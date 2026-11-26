using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DIYLevelFastInit.BundleCodecs;

namespace DIYLevelFastInit
{
    /// <summary>Admits file conversions within an estimated memory budget and one shared CPU pool.</summary>
    internal sealed class InfoBundleOptimizationBatch : IDisposable
    {
        internal sealed class PreparedFile
        {
            internal string Path;
            internal long Length, Modified;
        }

        private sealed class FileWork
        {
            internal string Path;
            internal UnityFsRepacker.WorkPlan Plan;
        }

        private struct LogEntry
        {
            internal string Message;
            internal bool Debug;
        }

        internal sealed class SettledFile
        {
            internal string Path;
            internal InfoBundleOptimizer.Result Result;
        }
        internal static long MemoryBudgetBytes = 800L * 1024 * 1024;
        private readonly string[] _paths;
        private readonly bool _restore;
        private readonly long _memoryBudget;
        private readonly int _workerCount;
        private readonly object _sync = new object();
        private readonly Queue<LogEntry> _logs = new Queue<LogEntry>();
        private readonly List<PreparedFile> _prepared = new List<PreparedFile>();
        private readonly List<string> _activePaths = new List<string>();
        private readonly List<FileWork> _files = new List<FileWork>();
        private readonly HashSet<string> _settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<SettledFile> _settledQueue = new Queue<SettledFile>();
        private readonly Dictionary<string, float> _fileProgress = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private Thread _worker;
        private volatile BundleWorkScheduler _scheduler;
        private volatile bool _cancelled, _done;
        private int _completed, _failed, _replaced, _ready, _skipped, _restored, _fast;
        private int _inFlight, _nextFile, _peakFiles, _memoryWaits;
        private long _reserved, _peakReserved;

        internal InfoBundleOptimizationBatch(string[] paths, bool restore)
            : this(paths, restore, MemoryBudgetBytes, Math.Max(1, Environment.ProcessorCount)) { }

        internal InfoBundleOptimizationBatch(string[] paths, bool restore, long memoryBudget, int workers)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (memoryBudget < 1 || workers < 1) throw new ArgumentOutOfRangeException("memoryBudget");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> unique = new List<string>();
            foreach (string path in paths)
            {
                string full = Path.GetFullPath(path);
                if (seen.Add(full)) unique.Add(full);
            }
            _paths = unique.ToArray(); _restore = restore; _memoryBudget = memoryBudget; _workerCount = workers;
        }

        internal bool Done { get { return _done; } }

        // True while this batch still intends to read or replace the file: planned,
        // queued or running. Settled files (completed, failed planning or foreign to
        // this batch) no longer block native readers.
        internal bool IsPending(string path)
        {
            if (string.IsNullOrEmpty(path) || _done) return false;
            string full = Path.GetFullPath(path);
            bool mine = false;
            foreach (string candidate in _paths)
                if (string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase)) { mine = true; break; }
            if (!mine) return false;
            lock (_sync) return !_settled.Contains(full);
        }

        internal bool TryDequeueSettled(out string path, out InfoBundleOptimizer.Result result)
        {
            lock (_sync)
            {
                if (_settledQueue.Count != 0)
                {
                    SettledFile settled = _settledQueue.Dequeue();
                    path = settled.Path;
                    result = settled.Result;
                    return true;
                }
            }
            path = null;
            result = InfoBundleOptimizer.Result.Failed;
            return false;
        }

        // Monotonic batch-wide work counter (settled files + active file fractions) so
        // callers waiting on the batch can display smooth progress and detect stalls.
        internal float OverallWorkUnits
        {
            get
            {
                lock (_sync)
                {
                    float sum = _settled.Count;
                    for (int i = 0; i < _activePaths.Count; i++)
                    {
                        float value;
                        if (_fileProgress.TryGetValue(_activePaths[i], out value)) sum += value;
                    }
                    return sum;
                }
            }
        }

        // Synchronous fallback: wait until this batch finishes touching files on disk.
        internal void WaitUntilDone()
        {
            lock (_sync)
            {
                while (!_done) Monitor.Wait(_sync);
            }
        }

        internal int Completed { get { return Interlocked.CompareExchange(ref _completed, 0, 0); } }
        internal int Failed { get { return Interlocked.CompareExchange(ref _failed, 0, 0); } }
        internal PreparedFile[] PreparedFiles { get { lock (_sync) return _prepared.ToArray(); } }
        internal int Total { get { return _paths.Length; } }
        internal int PeakFiles { get { lock (_sync) return _peakFiles; } }
        internal int MemoryWaits { get { lock (_sync) return _memoryWaits; } }
        internal long ReservedBytes { get { lock (_sync) return _reserved; } }
        internal long PeakReservedBytes { get { lock (_sync) return _peakReserved; } }
        internal int WorkerCount { get { return _workerCount; } }
        internal int PeakWorkers { get { return _scheduler == null ? 0 : _scheduler.PeakWorkers; } }
        internal int BlockJobsQueued { get { return _scheduler == null ? 0 : _scheduler.BlockJobsQueued; } }
        internal string Current
        {
            get
            {
                lock (_sync) return _activePaths.Count == 0 ? null : Path.GetFileName(_activePaths[0])
                    + (_activePaths.Count > 1 ? " (+" + (_activePaths.Count - 1) + " files)" : "");
            }
        }

        internal void Start()
        {
            lock (_sync)
            {
                if (_worker != null) throw new InvalidOperationException("Level bundle batch already started");
                try
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "DIY bundle batch coordinator", Priority = ThreadPriority.BelowNormal };
                    _worker.Start();
                }
                catch { _done = true; throw; }
            }
        }

        private void Work()
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                List<FileWork> planned = new List<FileWork>();
                foreach (string path in _paths)
                {
                    if (_cancelled) return;
                    try
                    {
                        UnityFsRepacker.WorkPlan plan = UnityFsRepacker.Plan(path, _memoryBudget, _workerCount, _restore);
                        planned.Add(new FileWork { Path = path, Plan = plan });
                        LogDebug("level bundle memory plan [" + Path.GetFileName(path) + "]: " + plan.ReservationBytes
                            + " bytes reserved, " + plan.WorkingBytes + " estimated working bytes, parallel=" + plan.Parallel
                            + (plan.Exclusive ? ", oversized file streams exclusively" : ""));
                    }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref _failed); Interlocked.Increment(ref _completed);
                        lock (_sync)
                        {
                            if (_settled.Add(path))
                            {
                                _fileProgress[path] = 1f;
                                _settledQueue.Enqueue(new SettledFile { Path = path, Result = InfoBundleOptimizer.Result.Failed });
                                Monitor.PulseAll(_sync);
                            }
                        }
                        Log("level bundle planning failed; original retained [" + path + "]: " + error);
                    }
                }
                // Publish the plan list only once complete, so locked readers never
                // observe a partially planned batch.
                lock (_sync) _files.AddRange(planned);
                using (BundleWorkScheduler scheduler = new BundleWorkScheduler(_workerCount))
                {
                    _scheduler = scheduler;
                    Log("level bundle scheduling: " + _memoryBudget + " byte estimated memory budget, " + _workerCount
                        + " shared CPU workers; parallel for multiple " + UnityFsRepacker.ChunkSize + " byte decoded blocks; no level-set limit");
                    lock (_sync)
                    {
                        StartFiles();
                        while (_inFlight != 0) Monitor.Wait(_sync);
                    }
                }
                HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in _paths)
                {
                    if (_cancelled) break;
                    string directory = Path.GetDirectoryName(path);
                    if (directories.Add(directory)) InfoBundleOptimizer.CleanupWorkDirectory(directory, Log);
                }
            }
            catch (Exception error) { _cancelled = true; Interlocked.Increment(ref _failed); Log("level bundle worker failed: " + error); }
            finally
            {
                Log("level bundle " + (_restore ? "restore" : "optimization") + (_cancelled ? " cancelled" : " complete")
                    + ": " + _replaced + " replaced, " + _ready + " already HC, " + _fast + " fast (raw/LZ4) kept, "
                    + _restored + " restored, " + _skipped + " skipped, " + _failed + " failed (" + _completed + "/"
                    + _paths.Length + ", " + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms)");
                lock (_sync)
                {
                    _done = true;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        private void StartFiles()
        {
            while (!_cancelled && _nextFile < _files.Count)
            {
                FileWork file = _files[_nextFile];
                if (file.Plan.ReservationBytes > _memoryBudget - _reserved)
                {
                    _memoryWaits++;
                    Log("level bundle waiting for conversion memory [" + Path.GetFileName(file.Path) + "]: "
                        + _reserved + "/" + _memoryBudget + " bytes reserved");
                    return;
                }
                _nextFile++; _inFlight++; _reserved += file.Plan.ReservationBytes;
                _peakFiles = Math.Max(_peakFiles, _inFlight); _peakReserved = Math.Max(_peakReserved, _reserved);
                try
                {
                    if (file.Plan.Parallel) _scheduler.QueueParallelFile(delegate { ProcessFile(file); });
                    else _scheduler.QueueFile(delegate { ProcessFile(file); });
                }
                catch { _inFlight--; _reserved -= file.Plan.ReservationBytes; _cancelled = true; throw; }
            }
        }

        private void ProcessFile(FileWork file)
        {
            string path = file.Path;
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            lock (_sync) _activePaths.Add(path);
            InfoBundleOptimizer.Result fileResult = InfoBundleOptimizer.Result.Failed;
            try
            {
                if (_cancelled) return;
                UnityFsRepacker.CheckPlan(path, file.Plan);
                long preparedLength = 0, preparedModified = 0;
                InfoBundleOptimizer.Result result = _restore
                    ? InfoBundleOptimizer.Restore(path, () => _cancelled, Log)
                    : InfoBundleOptimizer.Optimize(path, () => _cancelled, Log, out preparedLength, out preparedModified, _scheduler, file.Plan,
                        delegate(float fraction) { lock (_sync) _fileProgress[path] = fraction; });
                fileResult = result;
                if (preparedLength > 0 && (result == InfoBundleOptimizer.Result.Replaced
                    || result == InfoBundleOptimizer.Result.AlreadyOptimized
                    || result == InfoBundleOptimizer.Result.FastLayout))
                    lock (_sync) _prepared.Add(new PreparedFile { Path = path, Length = preparedLength, Modified = preparedModified });
                string timingLine = "level bundle preparation timing [" + Path.GetFileName(path) + "]: "
                    + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms, result=" + result;
                if (result == InfoBundleOptimizer.Result.Replaced || result == InfoBundleOptimizer.Result.Restored
                    || result == InfoBundleOptimizer.Result.Failed)
                    Log(timingLine);
                else
                    LogDebug(timingLine);
                switch (result)
                {
                    case InfoBundleOptimizer.Result.Replaced: Interlocked.Increment(ref _replaced); break;
                    case InfoBundleOptimizer.Result.AlreadyOptimized: Interlocked.Increment(ref _ready); break;
                    case InfoBundleOptimizer.Result.FastLayout: Interlocked.Increment(ref _fast); break;
                    case InfoBundleOptimizer.Result.Restored: Interlocked.Increment(ref _restored); break;
                    case InfoBundleOptimizer.Result.Failed: Interlocked.Increment(ref _failed); break;
                    default: Interlocked.Increment(ref _skipped); break;
                }
                Interlocked.Increment(ref _completed);
            }
            catch (OperationCanceledException) { _cancelled = true; }
            catch (Exception error)
            {
                Interlocked.Increment(ref _failed); Interlocked.Increment(ref _completed);
                Log("level bundle worker failed [" + path + "]: " + error);
            }
            finally
            {
                lock (_sync)
                {
                    _activePaths.Remove(path);
                    _fileProgress[path] = 1f;
                    if (_settled.Add(path))
                        _settledQueue.Enqueue(new SettledFile { Path = path, Result = fileResult });
                    _inFlight--; _reserved -= file.Plan.ReservationBytes;
                    try { StartFiles(); }
                    finally { Monitor.PulseAll(_sync); }
                }
            }
        }

        private void Log(string message) { lock (_sync) _logs.Enqueue(new LogEntry { Message = message, Debug = false }); }
        private void LogDebug(string message) { lock (_sync) _logs.Enqueue(new LogEntry { Message = message, Debug = true }); }

        internal void Drain(Action<string> log)
        {
            Drain(log, log);
        }

        internal void Drain(Action<string> infoLog, Action<string> debugLog)
        {
            List<LogEntry> entries = new List<LogEntry>();
            lock (_sync) while (_logs.Count != 0) entries.Add(_logs.Dequeue());
            foreach (LogEntry entry in entries)
            {
                Action<string> target = entry.Debug ? debugLog : infoLog;
                if (target != null) target(entry.Message);
            }
        }

        public void Dispose() { _cancelled = true; }
    }
}
