using System;
using System.Threading;

namespace DIYLevelFastInit
{
    /// <summary>Publishes one shallow metadata scan every five seconds from a single managed worker.</summary>
    internal sealed class LevelDirectoryPoller : IDisposable
    {
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;
        private bool _cancelled, _stopped;
        private int _generation;
        private LevelDirectorySnapshot _latest;
        private string _error;
        private bool _ready;

        internal LevelDirectoryPoller(string path)
        {
            _path = path;
            _worker = new Thread(Work) { IsBackground = true, Name = "DIY level directory poller", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }

        internal void Rescan()
        {
            lock (_sync)
            {
                if (_cancelled || _stopped) return;
                _generation++;
                _latest = null;
                _error = null;
                _ready = false;
                _wake.Set();
            }
        }

        internal bool TryTake(out LevelDirectorySnapshot snapshot, out string error)
        {
            lock (_sync)
            {
                snapshot = _latest;
                error = _error;
                if (!_ready) return false;
                _latest = null;
                _error = null;
                _ready = false;
                return true;
            }
        }

        private void Work()
        {
            try
            {
                while (true)
                {
                    int generation;
                    lock (_sync)
                    {
                        if (_cancelled) return;
                        generation = _generation;
                    }
                    LevelDirectorySnapshot snapshot = null;
                    string error = null;
                    try { snapshot = LevelDirectorySnapshot.Read(_path); }
                    catch (Exception exception) { error = exception.Message; }
                    lock (_sync)
                    {
                        if (_cancelled) return;
                        if (generation == _generation)
                        {
                            _latest = snapshot;
                            _error = error;
                            _ready = true;
                        }
                    }
                    _wake.WaitOne(5000, false);
                }
            }
            finally
            {
                lock (_sync)
                {
                    _stopped = true;
                    _wake.Close();
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _cancelled = true;
                if (!_stopped) _wake.Set();
            }
        }
    }
}
