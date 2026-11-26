using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Bounded pure-managed worker pool that prefetches scalar SetMetadata for multiple
    /// cache-missed Pass-0 sets concurrently without touching Unity or BepInEx APIs.
    /// The main thread still consumes and publishes results strictly in directory order.
    /// </summary>
    internal sealed class CatalogMetadataPool : IDisposable
    {
        internal sealed class Slot
        {
            internal readonly int Index;
            internal readonly string Directory;
            internal readonly string InfoFileName;
            private volatile bool _done;
            private volatile bool _cancelled;
            internal SetMetadata Data;
            internal string Error;
            internal double ElapsedMs;

            internal Slot(int index, string directory, string infoFileName)
            {
                Index = index;
                Directory = directory;
                InfoFileName = infoFileName;
            }

            internal bool Done { get { return _done; } }
            internal bool Cancelled { get { return _cancelled; } }

            internal void Complete(SetMetadata data, string error, double elapsedMs, bool cancelled)
            {
                Data = data;
                Error = error;
                ElapsedMs = elapsedMs;
                _cancelled = cancelled;
                _done = true;
            }
        }

        private const int MaxWorkers = 4;

        private readonly object _sync = new object();
        private readonly Queue<Slot> _queue = new Queue<Slot>();
        private readonly Dictionary<int, Slot> _byIndex = new Dictionary<int, Slot>();
        private readonly int _workerCount;
        private Thread[] _workers;
        private volatile bool _cancelled;

        internal CatalogMetadataPool(List<StartupCatalogScan.ScannedSet> misses)
        {
            if (misses == null) throw new ArgumentNullException("misses");
            for (int i = 0; i < misses.Count; i++)
            {
                StartupCatalogScan.ScannedSet set = misses[i];
                if (set == null || set.Directory == null || set.InfoFile == null) continue;
                Slot slot = new Slot(set.Index, set.Directory.FullName, set.InfoFile.Name);
                _queue.Enqueue(slot);
                _byIndex[set.Index] = slot;
            }
            int halfCores = Math.Max(1, Environment.ProcessorCount / 2);
            _workerCount = Math.Min(MaxWorkers, Math.Min(_byIndex.Count, halfCores));
        }

        internal int Count { get { return _byIndex.Count; } }
        internal int WorkerCount { get { return _workerCount; } }

        internal void Start()
        {
            if (_workerCount <= 0) return;
            _workers = new Thread[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                _workers[i] = new Thread(Work)
                {
                    IsBackground = true,
                    Name = "DIY catalog metadata worker " + i,
                    Priority = ThreadPriority.BelowNormal
                };
                _workers[i].Start();
            }
        }

        internal Slot GetSlot(int index)
        {
            Slot slot;
            return _byIndex.TryGetValue(index, out slot) ? slot : null;
        }

        private void Work()
        {
            while (true)
            {
                Slot slot;
                lock (_sync)
                {
                    if (_queue.Count == 0) return;
                    slot = _queue.Dequeue();
                }
                if (_cancelled)
                {
                    slot.Complete(null, "cancelled", 0, true);
                    continue;
                }
                Stopwatch timer = Stopwatch.StartNew();
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(slot.Directory);
                    FileInfo file = new FileInfo(Path.Combine(slot.Directory, slot.InfoFileName));
                    SetMetadata data = SerializedCatalogReader.Read(dir, file, () => _cancelled);
                    slot.Complete(data, null, timer.Elapsed.TotalMilliseconds, false);
                }
                catch (OperationCanceledException)
                {
                    slot.Complete(null, "cancelled", timer.Elapsed.TotalMilliseconds, true);
                }
                catch (Exception e)
                {
                    slot.Complete(null, e.GetType().Name + ": " + e.Message, timer.Elapsed.TotalMilliseconds, false);
                }
            }
        }

        public void Dispose()
        {
            _cancelled = true;
            lock (_sync)
            {
                while (_queue.Count != 0)
                {
                    _queue.Dequeue().Complete(null, "cancelled", 0, true);
                }
            }
        }
    }
}
