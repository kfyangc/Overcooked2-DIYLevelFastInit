using System;
using System.Collections.Generic;
using System.IO;

namespace DIYLevelFastInit
{
    internal static partial class InfoBundleGraph
    {
        /// <summary>Reads catalog byte ranges with one bounded decoded block and no shared graph-cache mutation.</summary>
        internal sealed class CatalogSource : IDisposable
        {
            internal sealed class FileEntry
            {
                internal string Name;
                internal long Offset, Size;
            }

            private readonly FileStream _stream;
            private readonly Source _source;
            private readonly long[] _starts;
            private readonly Func<bool> _cancelled;
            private byte[] _decoded;
            private int _decodedIndex = -1;
            internal readonly List<FileEntry> Files = new List<FileEntry>();
            internal long PackedBytesRead;

            internal CatalogSource(string path, Func<bool> cancelled)
            {
                _cancelled = cancelled;
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                try
                {
                    // A private parse keeps background catalog work out of Sources and References.
                    _source = ReadSource(new FileInfo(path));
                    if (_source.Length != _stream.Length) throw new IOException("Catalog source changed while opening");
                    _starts = new long[_source.Blocks.Count + 1];
                    for (int i = 0; i < _source.Blocks.Count; i++)
                    {
                        Block block = _source.Blocks[i];
                        if ((block.Compression != 0 && block.Compression != 2 && block.Compression != 3)
                            || block.Size > 8 * 1024 * 1024 || block.Size <= 0 || block.Packed <= 0
                            || block.Packed > 9 * 1024 * 1024
                            || (block.Compression == 0 && block.Packed != block.Size))
                            throw new IOException("Catalog reader requires bounded independent LZ4/raw blocks");
                        _starts[i + 1] = checked(_starts[i] + block.Size);
                    }
                    foreach (Node node in _source.Nodes)
                    {
                        if (node.Offset < 0 || node.Size < 0 || node.Offset > _starts[_starts.Length - 1] - node.Size)
                            throw new IOException("Catalog node exceeds the bundle payload");
                        if ((node.Flags & 4) != 0)
                            Files.Add(new FileEntry { Name = node.Name, Offset = node.Offset, Size = node.Size });
                    }
                }
                catch { _stream.Dispose(); throw; }
            }

            internal void CheckCancelled()
            {
                if (_cancelled != null && _cancelled()) throw new OperationCanceledException();
            }

            internal byte[] Read(FileEntry file, long start, int count)
            {
                CheckCancelled();
                if (start < 0 || count < 0 || count > 8 * 1024 * 1024 || start > file.Size - count)
                    throw new IOException("Catalog byte range exceeds the serialized file");
                byte[] result = new byte[count];
                long position = file.Offset + start;
                int written = 0;
                int index = Array.BinarySearch(_starts, position);
                if (index < 0) index = ~index - 1;
                while (written < count)
                {
                    CheckCancelled();
                    if (index < 0 || index >= _source.Blocks.Count) throw new IOException("Truncated catalog byte range");
                    Block block = _source.Blocks[index];
                    if (_decodedIndex != index)
                    {
                        _decoded = null;
                        _stream.Position = block.Offset;
                        _decoded = Decode(new BinaryReader(_stream), block.Packed, block.Size, block.Compression, block.Size);
                        _decodedIndex = index;
                        PackedBytesRead += block.Packed;
                    }
                    int from = checked((int)(position - _starts[index]));
                    int length = Math.Min(block.Size - from, count - written);
                    if (length <= 0) throw new IOException("Invalid catalog block range");
                    Buffer.BlockCopy(_decoded, from, result, written, length);
                    position += length;
                    written += length;
                    index++;
                }
                return result;
            }

            public void Dispose()
            {
                _decoded = null;
                _stream.Dispose();
            }
        }
    }
}
