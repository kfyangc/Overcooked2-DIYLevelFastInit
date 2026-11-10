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

        internal static long MemoryBudgetBytes = 800L * 1024 * 1024;
        private readonly string[] _paths;
        private readonly bool _restore;
        private readonly long _memoryBudget;
        private readonly int _workerCount;
        private readonly object _sync = new object();
        private readonly Queue<string> _logs = new Queue<string>();
        private readonly List<PreparedFile> _prepared = new List<PreparedFile>();
        private readonly List<string> _activePaths = new List<string>();
        private readonly List<FileWork> _files = new List<FileWork>();
        private Thread _worker;
        private volatile BundleWorkScheduler _scheduler;
        private volatile bool _cancelled, _done;
        private int _completed, _failed, _replaced, _ready, _skipped, _restored;
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
                foreach (string path in _paths)
                {
                    if (_cancelled) return;
                    try
                    {
                        UnityFsRepacker.WorkPlan plan = UnityFsRepacker.Plan(path, _memoryBudget, _workerCount, _restore);
                        _files.Add(new FileWork { Path = path, Plan = plan });
                        Log("level bundle memory plan [" + Path.GetFileName(path) + "]: " + plan.ReservationBytes
                            + " bytes reserved, " + plan.WorkingBytes + " estimated working bytes, parallel=" + plan.Parallel
                            + (plan.Exclusive ? ", oversized file streams exclusively" : ""));
                    }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref _failed); Interlocked.Increment(ref _completed);
                        Log("level bundle planning failed; original retained [" + path + "]: " + error);
                    }
                }
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
                    + ": " + _replaced + " replaced, " + _ready + " already HC, " + _restored + " restored, "
                    + _skipped + " skipped, " + _failed + " failed (" + _completed + "/" + _paths.Length + ", "
                    + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms)");
                _done = true;
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
            try
            {
                if (_cancelled) return;
                UnityFsRepacker.CheckPlan(path, file.Plan);
                long preparedLength = 0, preparedModified = 0;
                InfoBundleOptimizer.Result result = _restore
                    ? InfoBundleOptimizer.Restore(path, () => _cancelled, Log)
                    : InfoBundleOptimizer.Optimize(path, () => _cancelled, Log, out preparedLength, out preparedModified, _scheduler, file.Plan);
                if (preparedLength > 0 && (result == InfoBundleOptimizer.Result.Replaced || result == InfoBundleOptimizer.Result.AlreadyOptimized))
                    lock (_sync) _prepared.Add(new PreparedFile { Path = path, Length = preparedLength, Modified = preparedModified });
                Log("level bundle preparation timing [" + Path.GetFileName(path) + "]: "
                    + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms, result=" + result);
                switch (result)
                {
                    case InfoBundleOptimizer.Result.Replaced: Interlocked.Increment(ref _replaced); break;
                    case InfoBundleOptimizer.Result.AlreadyOptimized: Interlocked.Increment(ref _ready); break;
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
                    _inFlight--; _reserved -= file.Plan.ReservationBytes;
                    try { StartFiles(); }
                    finally { Monitor.PulseAll(_sync); }
                }
            }
        }

        private void Log(string message) { lock (_sync) _logs.Enqueue(message); }

        internal void Drain(Action<string> log)
        {
            List<string> messages = new List<string>();
            lock (_sync) while (_logs.Count != 0) messages.Add(_logs.Dequeue());
            foreach (string message in messages) if (log != null) log(message);
        }

        public void Dispose() { _cancelled = true; }
    }
}
