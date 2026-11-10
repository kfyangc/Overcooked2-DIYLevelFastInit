using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DIYLevelFastInit.BundleCodecs;

namespace DIYLevelFastInit
{
    /// <summary>Converts a configurable number of sets using one shared, bounded CPU worker pool.</summary>
    internal sealed class InfoBundleOptimizationBatch : IDisposable
    {
        internal sealed class PreparedFile
        {
            internal string Path;
            internal long Length, Modified;
        }

        private sealed class SetWork
        {
            internal string Directory;
            internal readonly List<string> Infos = new List<string>();
            internal readonly List<string> Scenes = new List<string>();
            internal int NextInfo, Remaining;
        }

        internal static int MaxConcurrentSets = 2;
        private readonly string[] _paths;
        private readonly bool _restore;
        private readonly int _setLimit, _workerCount;
        private readonly object _sync = new object();
        private readonly Queue<string> _logs = new Queue<string>();
        private readonly List<PreparedFile> _prepared = new List<PreparedFile>();
        private readonly List<string> _activePaths = new List<string>();
        private readonly List<SetWork> _sets = new List<SetWork>();
        private Thread _worker;
        private volatile BundleWorkScheduler _scheduler;
        private volatile bool _cancelled, _done;
        private int _completed, _failed, _replaced, _ready, _skipped, _restored;
        private int _activeSets, _nextSet, _peakSets;

        internal InfoBundleOptimizationBatch(string[] paths, bool restore)
            : this(paths, restore, MaxConcurrentSets, Math.Max(1, Environment.ProcessorCount)) { }

        internal InfoBundleOptimizationBatch(string[] paths, bool restore, int setLimit, int workers)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (setLimit < 1 || workers < 1) throw new ArgumentOutOfRangeException("setLimit");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> unique = new List<string>();
            foreach (string path in paths)
            {
                string full = Path.GetFullPath(path);
                if (seen.Add(full)) unique.Add(full);
            }
            _paths = unique.ToArray(); _restore = restore; _setLimit = setLimit; _workerCount = workers;
        }

        internal bool Done { get { return _done; } }
        internal int Completed { get { return Interlocked.CompareExchange(ref _completed, 0, 0); } }
        internal int Failed { get { return Interlocked.CompareExchange(ref _failed, 0, 0); } }
        internal PreparedFile[] PreparedFiles { get { lock (_sync) return _prepared.ToArray(); } }
        internal int Total { get { return _paths.Length; } }
        internal int PeakSets { get { lock (_sync) return _peakSets; } }
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
                Dictionary<string, SetWork> byDirectory = new Dictionary<string, SetWork>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in _paths)
                {
                    string directory = Path.GetDirectoryName(path);
                    SetWork set;
                    if (!byDirectory.TryGetValue(directory, out set))
                    {
                        set = new SetWork { Directory = directory };
                        byDirectory.Add(directory, set); _sets.Add(set);
                    }
                    if (Path.GetFileName(path).StartsWith("s_", StringComparison.OrdinalIgnoreCase)) set.Scenes.Add(path);
                    else set.Infos.Add(path);
                    set.Remaining++;
                }
                using (BundleWorkScheduler scheduler = new BundleWorkScheduler(_workerCount))
                {
                    _scheduler = scheduler;
                    Log("level bundle scheduling: " + _setLimit + " concurrent sets, " + _workerCount
                        + " shared CPU workers; info blocks parallel, scenes serial per file");
                    lock (_sync)
                    {
                        StartSets();
                        while (_activeSets != 0) Monitor.Wait(_sync);
                    }
                }
            }
            catch (Exception error) { Interlocked.Increment(ref _failed); Log("level bundle worker failed: " + error); }
            finally
            {
                Log("level bundle " + (_restore ? "restore" : "optimization") + (_cancelled ? " cancelled" : " complete")
                    + ": " + _replaced + " replaced, " + _ready + " already HC, " + _restored + " restored, "
                    + _skipped + " skipped, " + _failed + " failed (" + _completed + "/" + _paths.Length + ", "
                    + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms)");
                _done = true;
            }
        }

        private void StartSets()
        {
            while (!_cancelled && _activeSets < _setLimit && _nextSet < _sets.Count)
            {
                SetWork set = _sets[_nextSet++];
                _activeSets++; _peakSets = Math.Max(_peakSets, _activeSets);
                Log("level bundle set started: " + set.Directory);
                QueueNextInfo(set);
                foreach (string path in set.Scenes)
                {
                    string scene = path;
                    _scheduler.QueueFile(delegate { ProcessFile(set, scene, false); });
                }
            }
        }

        private void QueueNextInfo(SetWork set)
        {
            if (set.NextInfo >= set.Infos.Count) return;
            string info = set.Infos[set.NextInfo++];
            _scheduler.QueueInfo(delegate { ProcessFile(set, info, true); });
        }

        private void ProcessFile(SetWork set, string path, bool info)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            lock (_sync) _activePaths.Add(path);
            try
            {
                if (_cancelled) return;
                long preparedLength = 0, preparedModified = 0;
                InfoBundleOptimizer.Result result = _restore
                    ? InfoBundleOptimizer.Restore(path, () => _cancelled, Log)
                    : InfoBundleOptimizer.Optimize(path, () => _cancelled, Log, out preparedLength, out preparedModified, _scheduler);
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
            catch (Exception error) { Interlocked.Increment(ref _failed); Log("level bundle worker failed [" + path + "]: " + error); }
            finally
            {
                lock (_sync)
                {
                    _activePaths.Remove(path);
                    if (info)
                    {
                        if (_cancelled) { set.Remaining -= set.Infos.Count - set.NextInfo; set.NextInfo = set.Infos.Count; }
                        else QueueNextInfo(set);
                    }
                    if (--set.Remaining == 0)
                    {
                        _activeSets--;
                        Log("level bundle set finished: " + set.Directory);
                        StartSets();
                    }
                    Monitor.PulseAll(_sync);
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
