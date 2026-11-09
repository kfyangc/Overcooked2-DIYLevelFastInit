using System;
using System.IO;
using System.Threading;

namespace DIYLevelFastInit
{
    /// <summary>Reads one disposable scalar catalog on a managed worker without Unity or logging calls.</summary>
    internal sealed class CatalogMetadataRequest : IDisposable
    {
        private readonly string _directory, _file;
        private volatile bool _cancelled, _done;
        private SetMetadata _data;
        private string _error;

        internal CatalogMetadataRequest(string directory, string file)
        {
            _directory = directory;
            _file = file;
        }

        internal bool Done { get { return _done; } }
        internal bool Cancelled { get { return _cancelled; } }
        internal SetMetadata Data { get { return _data; } }
        internal string Error { get { return _error; } }

        internal void Start()
        {
            new Thread(Work) { IsBackground = true, Name = "DIY catalog metadata reader", Priority = ThreadPriority.BelowNormal }.Start();
        }

        private void Work()
        {
            try
            {
                DirectoryInfo directory = new DirectoryInfo(_directory);
                _data = SerializedCatalogReader.Read(directory, new FileInfo(Path.Combine(_directory, _file)), () => _cancelled);
            }
            catch (OperationCanceledException) { _error = "cancelled"; }
            catch (Exception error) { _error = error.GetType().Name + ": " + error.Message; }
            finally { _done = true; }
        }

        public void Dispose() { _cancelled = true; }
    }
}
