using System;
using System.Collections.Generic;
using System.Threading;

namespace DIYLevelFastInit.BundleCodecs
{
    /// <summary>Shares a fixed worker budget between files and independently encoded info blocks.</summary>
    internal sealed class BundleWorkScheduler : IDisposable
    {
        internal sealed class Job
        {
            internal Action Work;
            internal bool Done;
            internal Exception Error;
        }

        private readonly object _sync = new object();
        private readonly Queue<Job> _files = new Queue<Job>();
        private readonly Queue<Job> _infos = new Queue<Job>();
        private readonly Queue<Job> _blocks = new Queue<Job>();
        private readonly Thread[] _workers;
        private bool _stopping;
        private int _active, _peak, _blocksQueued;
        private int _encoding, _peakEncoding;
        private readonly HashSet<int> _encodingThreads = new HashSet<int>();
        [ThreadStatic] private static BundleWorkScheduler _current;
        [ThreadStatic] private static Lz4BlockEncoder _encoder;
        [ThreadStatic] private static int _depth;

        internal BundleWorkScheduler(int workers)
        {
            if (workers < 1) throw new ArgumentOutOfRangeException("workers");
            _workers = new Thread[workers];
            try
            {
                for (int i = 0; i < workers; i++)
                {
                    _workers[i] = new Thread(Work) { IsBackground = true, Name = "DIY bundle CPU worker " + i,
                        Priority = ThreadPriority.BelowNormal };
                    _workers[i].Start();
                }
            }
            catch { Dispose(); throw; }
        }

        internal int WorkerCount { get { return _workers.Length; } }
        internal int PeakWorkers { get { lock (_sync) return _peak; } }
        internal int BlockJobsQueued { get { lock (_sync) return _blocksQueued; } }
        internal int EncodingThreads { get { lock (_sync) return _encodingThreads.Count; } }
        internal int PeakBlockWorkers { get { lock (_sync) return _peakEncoding; } }

        internal Job QueueFile(Action work) { return Queue(work, false, false); }
        internal Job QueueInfo(Action work) { return Queue(work, false, true); }
        internal Job QueueBlock(Action work) { return Queue(work, true, false); }

        private Job Queue(Action work, bool block, bool info)
        {
            if (work == null) throw new ArgumentNullException("work");
            lock (_sync)
            {
                if (_stopping) throw new ObjectDisposedException("BundleWorkScheduler");
                Job job = new Job { Work = work };
                if (block) { _blocks.Enqueue(job); _blocksQueued++; }
                else if (info) _infos.Enqueue(job);
                else _files.Enqueue(job);
                Monitor.PulseAll(_sync);
                return job;
            }
        }

        internal void Wait(Job job, Func<bool> cancelled)
        {
            while (true)
            {
                Job help = null;
                lock (_sync)
                {
                    if (job.Done)
                    {
                        if (job.Error != null) throw job.Error;
                        return;
                    }
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    // A file worker can finish blocks while waiting without consuming another thread.
                    if (ReferenceEquals(_current, this) && _blocks.Count != 0) help = _blocks.Dequeue();
                    else if (ReferenceEquals(_current, this) && _files.Count != 0) help = _files.Dequeue();
                    else Monitor.Wait(_sync, 25);
                }
                if (help != null) Execute(help);
            }
        }

        internal int Encode(byte[] plain, int length, byte[] packed)
        {
            if (!ReferenceEquals(_current, this)) throw new InvalidOperationException("Block encoding requires a scheduler worker");
            if (_encoder == null) _encoder = new Lz4BlockEncoder();
            lock (_sync)
            {
                _encodingThreads.Add(Thread.CurrentThread.ManagedThreadId);
                _encoding++; _peakEncoding = Math.Max(_peakEncoding, _encoding);
            }
            try { return _encoder.Encode(plain, length, packed); }
            finally { lock (_sync) _encoding--; }
        }

        private void Work()
        {
            _current = this;
            try
            {
                while (true)
                {
                    Job job;
                    lock (_sync)
                    {
                        while (_blocks.Count == 0 && _infos.Count == 0 && _files.Count == 0)
                        {
                            if (_stopping) return;
                            Monitor.Wait(_sync);
                        }
                        job = _blocks.Count != 0 ? _blocks.Dequeue() : (_infos.Count != 0 ? _infos.Dequeue() : _files.Dequeue());
                    }
                    Execute(job);
                }
            }
            finally { _current = null; _encoder = null; _depth = 0; }
        }

        private void Execute(Job job)
        {
            bool outer = _depth++ == 0;
            if (outer) lock (_sync) { _active++; _peak = Math.Max(_peak, _active); }
            try { job.Work(); }
            catch (Exception error) { job.Error = error; }
            finally
            {
                _depth--;
                lock (_sync)
                {
                    if (outer) _active--;
                    job.Work = null;
                    job.Done = true;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync) { _stopping = true; Monitor.PulseAll(_sync); }
            foreach (Thread worker in _workers)
                if (worker != null && worker.IsAlive && worker != Thread.CurrentThread) worker.Join();
        }
    }
}
