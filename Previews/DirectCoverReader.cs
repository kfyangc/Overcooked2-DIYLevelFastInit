using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DIYLevelFastInit
{
    internal enum DirectCoverStatus
    {
        Ok,
        NoScreenshot,
        Fallback
    }

    internal sealed class DirectCoverData
    {
        internal DirectCoverStatus Status;
        internal int Width;
        internal int Height;
        internal int TextureFormat;
        internal float RectX;
        internal float RectY;
        internal float RectWidth;
        internal float RectHeight;
        internal byte[] RawData;
        internal bool Downscaled;
        internal long SourceLength;
        internal long SourceModified;
        internal string FallbackReason;
        internal double IndexMs;
        internal double ReadMs;
        internal double ResizeMs;
    }

    /// <summary>
    /// Reads cover Sprite/Texture2D payloads directly from bounded LZ4/raw UnityFS info bundles
    /// on a background managed thread without touching Unity or BepInEx APIs.
    /// </summary>
    internal static class DirectCoverReader
    {
        internal const int MaxWidth = 640;
        internal const int MaxHeight = 360;

        private const int MaxMetadata = 8 * 1024 * 1024;
        private const int MaxBlockSize = 8 * 1024 * 1024;
        private const int MaxPackedBlockSize = 9 * 1024 * 1024;
        private const int MaxObject = 1024 * 1024;
        private const int MaxRawBytes = 32 * 1024 * 1024;
        private const int MaxTextureDimension = 8192;
        private const int MaxIndexCacheEntries = 64;
        private const int MaxItems = 20000;

        // Unity 2017 TextureFormat enum values.
        internal const int FormatRgb24 = 3;
        internal const int FormatRgba32 = 4;
        internal const int FormatArgb32 = 5;
        internal const int FormatDxt1 = 10;
        internal const int FormatDxt5 = 12;
        internal const int FormatBgra32 = 14;

        private sealed class BlockInfo
        {
            internal long Offset;
            internal int Size;
            internal int Packed;
            internal int Compression;
        }

        private sealed class NodeInfo
        {
            internal string Name;
            internal long Offset;
            internal long Size;
            internal int Flags;
        }

        private sealed class LevelTarget
        {
            internal DirectCoverStatus Status;
            internal string FallbackReason;
            internal long DataOffset;
            internal int DataSize;
            internal int Width;
            internal int Height;
            internal int TextureFormat;
            internal float RectX;
            internal float RectY;
            internal float RectWidth;
            internal float RectHeight;
        }

        private sealed class SetIndex
        {
            internal string Path;
            internal long Length;
            internal long Modified;
            internal BlockInfo[] Blocks;
            internal long[] BlockStarts;
            internal readonly Dictionary<string, LevelTarget> Levels =
                new Dictionary<string, LevelTarget>(StringComparer.OrdinalIgnoreCase);
            internal long Used;
        }

        private sealed class Field
        {
            internal string Name;
            internal string Type;
            internal int Depth;
            internal int Size;
            internal int Flags;
            internal int Meta;
            internal int End;
        }

        private sealed class ObjectType
        {
            internal int ClassId;
            internal Field[] Fields;

            internal bool Has(string name)
            {
                for (int i = 1; i < Fields.Length; i = Fields[i].End)
                    if (Fields[i].Name == name) return true;
                return false;
            }
        }

        private sealed class ObjectEntry
        {
            internal ObjectType Type;
            internal long Offset;
            internal int Size;
        }

        private struct Pointer
        {
            internal int File;
            internal long Id;
        }

        private struct RectData
        {
            internal float X;
            internal float Y;
            internal float Width;
            internal float Height;
        }

        private struct SpriteInfo
        {
            internal RectData Rect;
            internal RectData TextureRect;
            internal Pointer Texture;
            internal uint SettingsRaw;
        }

        private struct TextureInfo
        {
            internal int Width;
            internal int Height;
            internal int Format;
            internal int MipCount;
            internal long InlineDataRelOffset;
            internal int InlineDataLength;
            internal long StreamOffset;
            internal uint StreamSize;
            internal string StreamPath;
        }

        private sealed class BlockReader : IDisposable
        {
            private readonly FileStream _stream;
            private readonly BinaryReader _reader;
            private readonly BlockInfo[] _blocks;
            private readonly long[] _starts;
            private readonly Func<bool> _cancelled;
            private byte[] _decoded;
            private int _decodedIndex = -1;

            internal BlockReader(string path, BlockInfo[] blocks, long[] starts, Func<bool> cancelled)
            {
                _blocks = blocks;
                _starts = starts;
                _cancelled = cancelled;
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                _reader = new BinaryReader(_stream);
            }

            internal void CheckCancelled()
            {
                if (_cancelled != null && _cancelled()) throw new OperationCanceledException();
            }

            internal byte[] ReadRange(long start, int count)
            {
                CheckCancelled();
                if (start < 0 || count < 0 || count > MaxRawBytes || start > _starts[_starts.Length - 1] - count)
                    throw new IOException("Cover byte range exceeds bundle payload");
                byte[] result = new byte[count];
                if (count == 0) return result;
                long position = start;
                int written = 0;
                int index = Array.BinarySearch(_starts, position);
                if (index < 0) index = ~index - 1;
                while (written < count)
                {
                    CheckCancelled();
                    if (index < 0 || index >= _blocks.Length) throw new IOException("Truncated cover block range");
                    BlockInfo block = _blocks[index];
                    if (_decodedIndex != index)
                    {
                        _decoded = null;
                        _stream.Position = block.Offset;
                        _decoded = InfoBundleGraph.Decode(_reader, block.Packed, block.Size, block.Compression, block.Size);
                        _decodedIndex = index;
                    }
                    int from = checked((int)(position - _starts[index]));
                    int take = Math.Min(block.Size - from, count - written);
                    if (take <= 0) throw new IOException("Invalid cover block slice");
                    Buffer.BlockCopy(_decoded, from, result, written, take);
                    position += take;
                    written += take;
                    index++;
                }
                return result;
            }

            public void Dispose()
            {
                _decoded = null;
                _reader.Close();
                _stream.Dispose();
            }
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly object IndexLock = new object();
        private static readonly Dictionary<string, SetIndex> IndexCache =
            new Dictionary<string, SetIndex>(StringComparer.OrdinalIgnoreCase);
        private static long _indexUse;
        private static int _activeReads;

        internal static int ActiveReads
        {
            get { return Thread.VolatileRead(ref _activeReads); }
        }

        internal static void ClearCache()
        {
            lock (IndexLock) IndexCache.Clear();
        }

        internal static DirectCoverData ReadCover(string infoPath, string sceneName, Func<bool> cancelled)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                return ReadCoverCore(infoPath, sceneName, cancelled);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return new DirectCoverData
                {
                    Status = DirectCoverStatus.Fallback,
                    FallbackReason = e.GetType().Name + ": " + e.Message
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        private static DirectCoverData ReadCoverCore(string infoPath, string sceneName, Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            if (string.IsNullOrEmpty(infoPath) || string.IsNullOrEmpty(sceneName))
                return Fallback("missing path or scene");

            string fullPath = Path.GetFullPath(infoPath);
            FileInfo file = new FileInfo(fullPath);
            if (!file.Exists) return Fallback("missing info file");
            long length = file.Length;
            long modified = file.LastWriteTimeUtc.Ticks;

            Stopwatch timer = Stopwatch.StartNew();
            SetIndex index = TryGetCachedIndex(fullPath, length, modified);
            double indexMs = 0;
            if (index == null)
            {
                index = BuildIndex(fullPath, length, modified, cancelled);
                StoreCachedIndex(index);
                indexMs = timer.Elapsed.TotalMilliseconds;
            }

            LevelTarget target;
            if (!index.Levels.TryGetValue(sceneName, out target))
                return Fallback("scene not in direct cover index", indexMs);

            if (target.Status == DirectCoverStatus.NoScreenshot)
            {
                return new DirectCoverData
                {
                    Status = DirectCoverStatus.NoScreenshot,
                    SourceLength = length,
                    SourceModified = modified,
                    IndexMs = indexMs
                };
            }
            if (target.Status != DirectCoverStatus.Ok)
                return Fallback(target.FallbackReason ?? "unsupported cover target", indexMs);

            double readStart = timer.Elapsed.TotalMilliseconds;
            byte[] raw;
            using (BlockReader reader = new BlockReader(fullPath, index.Blocks, index.BlockStarts, cancelled))
            {
                raw = reader.ReadRange(target.DataOffset, target.DataSize);
            }
            FileInfo verify = new FileInfo(fullPath);
            if (!verify.Exists || verify.Length != length || verify.LastWriteTimeUtc.Ticks != modified)
                return Fallback("info file changed during direct cover read", indexMs);
            double readMs = timer.Elapsed.TotalMilliseconds - readStart;

            int outWidth = target.Width;
            int outHeight = target.Height;
            int outFormat = target.TextureFormat;
            float outRectX = target.RectX;
            float outRectY = target.RectY;
            float outRectW = target.RectWidth;
            float outRectH = target.RectHeight;
            bool downscaled = false;
            double resizeMs = 0;

            if (IsUncompressed(outFormat))
            {
                bool needsCrop = outRectX > 0.5f || outRectY > 0.5f
                    || Math.Abs(outRectW - outWidth) > 0.5f || Math.Abs(outRectH - outHeight) > 0.5f;
                bool needsScale = outRectW > MaxWidth || outRectH > MaxHeight;
                bool needsSwizzle = outFormat == FormatArgb32 || outFormat == FormatBgra32;
                if (needsCrop || needsScale || needsSwizzle)
                {
                    double resizeStart = timer.Elapsed.TotalMilliseconds;
                    raw = CropAndDownscaleUncompressed(
                        raw, outWidth, outHeight, outFormat,
                        outRectX, outRectY, outRectW, outRectH,
                        cancelled, out outWidth, out outHeight, out outFormat);
                    outRectX = 0f;
                    outRectY = 0f;
                    outRectW = outWidth;
                    outRectH = outHeight;
                    downscaled = needsScale || needsCrop;
                    resizeMs = timer.Elapsed.TotalMilliseconds - resizeStart;
                }
            }

            return new DirectCoverData
            {
                Status = DirectCoverStatus.Ok,
                Width = outWidth,
                Height = outHeight,
                TextureFormat = outFormat,
                RectX = outRectX,
                RectY = outRectY,
                RectWidth = outRectW,
                RectHeight = outRectH,
                RawData = raw,
                Downscaled = downscaled,
                SourceLength = length,
                SourceModified = modified,
                IndexMs = indexMs,
                ReadMs = readMs,
                ResizeMs = resizeMs
            };
        }

        private static DirectCoverData Fallback(string reason, double indexMs = 0)
        {
            return new DirectCoverData
            {
                Status = DirectCoverStatus.Fallback,
                FallbackReason = reason,
                IndexMs = indexMs
            };
        }

        private static SetIndex TryGetCachedIndex(string fullPath, long length, long modified)
        {
            lock (IndexLock)
            {
                SetIndex existing;
                if (IndexCache.TryGetValue(fullPath, out existing)
                    && existing.Length == length && existing.Modified == modified)
                {
                    existing.Used = ++_indexUse;
                    return existing;
                }
                return null;
            }
        }

        private static void StoreCachedIndex(SetIndex index)
        {
            lock (IndexLock)
            {
                index.Used = ++_indexUse;
                IndexCache[index.Path] = index;
                while (IndexCache.Count > MaxIndexCacheEntries)
                {
                    SetIndex oldest = null;
                    foreach (SetIndex entry in IndexCache.Values)
                        if (oldest == null || entry.Used < oldest.Used) oldest = entry;
                    if (oldest == null) break;
                    IndexCache.Remove(oldest.Path);
                }
            }
        }

        private static SetIndex BuildIndex(string fullPath, long length, long modified, Func<bool> cancelled)
        {
            BlockInfo[] blocks;
            long[] blockStarts;
            NodeInfo[] nodes;
            ReadUnityFsHeader(fullPath, length, out blocks, out blockStarts, out nodes);

            SetIndex index = new SetIndex
            {
                Path = fullPath,
                Length = length,
                Modified = modified,
                Blocks = blocks,
                BlockStarts = blockStarts
            };

            Dictionary<string, NodeInfo> nodesByName = new Dictionary<string, NodeInfo>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Length; i++)
                if (!nodesByName.ContainsKey(nodes[i].Name)) nodesByName.Add(nodes[i].Name, nodes[i]);

            using (BlockReader reader = new BlockReader(fullPath, blocks, blockStarts, cancelled))
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    NodeInfo node = nodes[i];
                    if ((node.Flags & 4) == 0) continue;
                    ParseSerializedFile(reader, node, nodesByName, index);
                }
            }

            if (index.Levels.Count == 0)
                throw new IOException("No LevelInfoSO entries found for direct cover index");

            return index;
        }

        private static void ReadUnityFsHeader(string path, long expectedLength,
            out BlockInfo[] blocks, out long[] blockStarts, out NodeInfo[] nodes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (stream.Length != expectedLength) throw new IOException("Info bundle changed while opening");
                if (ReadCString(reader, 64) != "UnityFS" || ReadBigU32(reader) != 6)
                    throw new IOException("Unsupported UnityFS header");
                ReadCString(reader, 128);
                ReadCString(reader, 128);
                if (ReadBigI64(reader) != expectedLength) throw new IOException("Invalid UnityFS size");
                int packed = BoundedSize(ReadBigU32(reader), MaxMetadata);
                int plain = BoundedSize(ReadBigU32(reader), MaxMetadata);
                uint flags = ReadBigU32(reader);
                long dataStart = stream.Position + ((flags & 128) == 0 ? packed : 0);
                if ((flags & ~255U) != 0 || (flags & 64) == 0) throw new IOException("Unsupported UnityFS flags");
                if ((flags & 128) != 0) stream.Position = expectedLength - packed;
                byte[] headerBytes = InfoBundleGraph.Decode(reader, packed, plain, (int)(flags & 63), plain);

                using (BinaryReader table = new BinaryReader(new MemoryStream(headerBytes, false)))
                {
                    table.ReadBytes(16);
                    int blockCount = BoundedSize(ReadBigU32(table), 100000);
                    blocks = new BlockInfo[blockCount];
                    blockStarts = new long[blockCount + 1];
                    long fileOffset = dataStart;
                    for (int i = 0; i < blockCount; i++)
                    {
                        int uSize = BoundedSize(ReadBigU32(table), MaxBlockSize);
                        int cSize = BoundedSize(ReadBigU32(table), MaxPackedBlockSize);
                        int comp = (table.ReadByte() << 8 | table.ReadByte()) & 63;
                        if ((comp != 0 && comp != 2 && comp != 3) || uSize <= 0 || cSize <= 0
                            || (comp == 0 && cSize != uSize) || fileOffset > expectedLength - cSize)
                            throw new IOException("Direct cover reader requires bounded independent LZ4/raw blocks");
                        blocks[i] = new BlockInfo { Offset = fileOffset, Size = uSize, Packed = cSize, Compression = comp };
                        blockStarts[i + 1] = checked(blockStarts[i] + uSize);
                        fileOffset += cSize;
                    }
                    long totalUnpacked = blockStarts[blockCount];
                    int nodeCount = BoundedSize(ReadBigU32(table), 20000);
                    nodes = new NodeInfo[nodeCount];
                    for (int i = 0; i < nodeCount; i++)
                    {
                        long nOffset = ReadBigI64(table);
                        long nSize = ReadBigI64(table);
                        int nFlags = (int)ReadBigU32(table);
                        string nName = ReadCString(table, 4096);
                        if (nOffset < 0 || nSize < 0 || nOffset > totalUnpacked - nSize)
                            throw new IOException("Invalid UnityFS node bounds");
                        nodes[i] = new NodeInfo { Name = nName, Offset = nOffset, Size = nSize, Flags = nFlags };
                    }
                }
            }
        }

        private static void ParseSerializedFile(BlockReader blockReader, NodeInfo node,
            Dictionary<string, NodeInfo> nodesByName, SetIndex index)
        {
            byte[] header = blockReader.ReadRange(node.Offset, 20);
            int metadataSize = Big32(header, 0);
            int fileSize = Big32(header, 4);
            int version = Big32(header, 8);
            int dataOffset = Big32(header, 12);
            if (version != 17 || header[16] != 0 || fileSize != node.Size || metadataSize < 1
                || metadataSize > MaxMetadata || dataOffset < 20 || dataOffset > MaxMetadata
                || metadataSize > dataOffset - 20 || dataOffset > node.Size)
                throw new IOException("Unsupported serialized file header");

            Dictionary<long, ObjectEntry> objects = new Dictionary<long, ObjectEntry>();
            using (BinaryReader reader = new BinaryReader(new MemoryStream(blockReader.ReadRange(node.Offset + 20, metadataSize), false)))
            {
                ReadCString(reader, 128);
                reader.ReadInt32();
                if (reader.ReadByte() != 1) throw new IOException("Serialized type trees are absent");
                int typeCount = Count(reader, 4096);
                ObjectType[] types = new ObjectType[typeCount];
                for (int i = 0; i < typeCount; i++)
                {
                    blockReader.CheckCancelled();
                    int classId = reader.ReadInt32();
                    reader.ReadByte();
                    reader.ReadInt16();
                    if (classId == 114) SkipBytes(reader, 16);
                    SkipBytes(reader, 16);
                    types[i] = new ObjectType { ClassId = classId, Fields = ReadTree(reader) };
                }
                int objectCount = Count(reader, 200000);
                for (int i = 0; i < objectCount; i++)
                {
                    Align(reader);
                    long id = reader.ReadInt64();
                    long offset = dataOffset + (long)reader.ReadUInt32();
                    uint objLen = reader.ReadUInt32();
                    int typeIdx = reader.ReadInt32();
                    if (typeIdx < 0 || typeIdx >= types.Length || offset < dataOffset || offset > node.Size - objLen || id == 0)
                        throw new IOException("Invalid serialized object table");
                    int classId = types[typeIdx].ClassId;
                    if (classId == 114 || classId == 115 || classId == 213 || classId == 28)
                    {
                        objects[id] = new ObjectEntry { Type = types[typeIdx], Offset = offset, Size = checked((int)objLen) };
                    }
                }
            }

            Dictionary<long, string> scripts = new Dictionary<long, string>();
            foreach (ObjectEntry obj in objects.Values)
            {
                if (obj.Type.ClassId != 114 || !obj.Type.Has("sceneName") || !obj.Type.Has("screenshot")) continue;
                blockReader.CheckCancelled();
                if (obj.Size > MaxObject) continue;

                string sceneName;
                Pointer screenshotPtr;
                Pointer scriptPtr;
                if (!TryReadLevelCoverFields(blockReader, node, obj, out sceneName, out screenshotPtr, out scriptPtr))
                    continue;
                if (!IsExpectedScript(blockReader, node, objects, scripts, scriptPtr, "LevelInfoSO"))
                    continue;
                if (string.IsNullOrEmpty(sceneName)) continue;

                LevelTarget target = ResolveTarget(blockReader, node, nodesByName, objects, screenshotPtr);
                index.Levels[sceneName] = target;
            }
        }

        private static bool TryReadLevelCoverFields(BlockReader blockReader, NodeInfo node, ObjectEntry obj,
            out string sceneName, out Pointer screenshot, out Pointer script)
        {
            sceneName = null;
            screenshot = default(Pointer);
            script = default(Pointer);
            bool hasScene = false, hasScreenshot = false, hasScript = false;
            Field[] fields = obj.Type.Fields;
            using (BinaryReader reader = new BinaryReader(new MemoryStream(blockReader.ReadRange(node.Offset + obj.Offset, obj.Size), false)))
            {
                for (int i = 1; i < fields.Length; i = fields[i].End)
                {
                    string name = fields[i].Name;
                    if (name == "m_Script")
                    {
                        script = ReadPointer(reader, fields, i);
                        hasScript = true;
                    }
                    else if (name == "sceneName" && fields[i].Type == "string")
                    {
                        sceneName = ReadString(reader);
                        hasScene = true;
                    }
                    else if (name == "screenshot")
                    {
                        screenshot = ReadPointer(reader, fields, i);
                        hasScreenshot = true;
                    }
                    else
                    {
                        SkipField(reader, fields, i);
                    }
                }
            }
            return hasScene && hasScreenshot && hasScript;
        }

        private static bool IsExpectedScript(BlockReader blockReader, NodeInfo node,
            Dictionary<long, ObjectEntry> objects, Dictionary<long, string> scripts,
            Pointer pointer, string expectedClass)
        {
            if (pointer.File != 0 || pointer.Id == 0) return false;
            string className;
            if (!scripts.TryGetValue(pointer.Id, out className))
            {
                ObjectEntry scriptObj;
                if (!objects.TryGetValue(pointer.Id, out scriptObj) || scriptObj.Type.ClassId != 115 || scriptObj.Size > MaxObject)
                    return false;
                string cls = null, ns = null, asm = null;
                Field[] fields = scriptObj.Type.Fields;
                using (BinaryReader reader = new BinaryReader(new MemoryStream(blockReader.ReadRange(node.Offset + scriptObj.Offset, scriptObj.Size), false)))
                {
                    for (int i = 1; i < fields.Length; i = fields[i].End)
                    {
                        string name = fields[i].Name;
                        if (name == "m_ClassName" && fields[i].Type == "string") cls = ReadString(reader);
                        else if (name == "m_Namespace" && fields[i].Type == "string") ns = ReadString(reader);
                        else if (name == "m_AssemblyName" && fields[i].Type == "string") asm = ReadString(reader);
                        else SkipField(reader, fields, i);
                    }
                }
                if (ns != "LevelEditorStub" || Path.GetFileNameWithoutExtension(asm ?? "") != "LevelEditorStub")
                    className = string.Empty;
                else
                    className = cls ?? string.Empty;
                scripts[pointer.Id] = className;
            }
            return className == expectedClass;
        }

        private static LevelTarget ResolveTarget(BlockReader blockReader, NodeInfo serializedNode,
            Dictionary<string, NodeInfo> nodesByName, Dictionary<long, ObjectEntry> objects, Pointer spritePtr)
        {
            if (spritePtr.Id == 0)
                return new LevelTarget { Status = DirectCoverStatus.NoScreenshot };
            if (spritePtr.File != 0)
                return FallbackTarget("external sprite reference (file " + spritePtr.File + ")");
            ObjectEntry spriteObj;
            if (!objects.TryGetValue(spritePtr.Id, out spriteObj) || spriteObj.Type.ClassId != 213)
                return FallbackTarget("local sprite object missing");
            if (spriteObj.Size > MaxObject)
                return FallbackTarget("sprite object exceeds size limit");

            SpriteInfo sprite = ReadSprite(blockReader, serializedNode, spriteObj);
            bool packed = (sprite.SettingsRaw & 1U) != 0;
            uint packingRotation = (sprite.SettingsRaw >> 2) & 0xFU;
            if (packed && packingRotation != 0)
                return FallbackTarget("rotated packed sprite");
            if (sprite.Texture.Id == 0)
                return new LevelTarget { Status = DirectCoverStatus.NoScreenshot };
            if (sprite.Texture.File != 0)
                return FallbackTarget("external texture reference (file " + sprite.Texture.File + ")");

            ObjectEntry texObj;
            if (!objects.TryGetValue(sprite.Texture.Id, out texObj) || texObj.Type.ClassId != 28)
                return FallbackTarget("local texture object missing");
            if (texObj.Size > MaxRawBytes + MaxObject)
                return FallbackTarget("texture object exceeds size limit");

            TextureInfo tex = ReadTexture(blockReader, serializedNode, texObj);
            if (tex.Width < 1 || tex.Width > MaxTextureDimension || tex.Height < 1 || tex.Height > MaxTextureDimension)
                return FallbackTarget("texture dimensions out of bounds");

            int mip0Bytes;
            string formatError;
            if (!TryComputeMip0Bytes(tex.Width, tex.Height, tex.Format, out mip0Bytes, out formatError))
                return FallbackTarget(formatError);
            if (mip0Bytes <= 0 || mip0Bytes > MaxRawBytes)
                return FallbackTarget("texture payload exceeds size limit");

            RectData rect = (sprite.TextureRect.Width > 0f && sprite.TextureRect.Height > 0f)
                ? sprite.TextureRect : sprite.Rect;
            if (rect.Width <= 0f || rect.Height <= 0f
                || rect.X < -0.5f || rect.Y < -0.5f
                || rect.X + rect.Width > tex.Width + 0.5f
                || rect.Y + rect.Height > tex.Height + 0.5f)
                return FallbackTarget("sprite rect out of texture bounds");

            float clampedX = Math.Max(0f, rect.X);
            float clampedY = Math.Max(0f, rect.Y);
            float clampedW = Math.Min(rect.Width, tex.Width - clampedX);
            float clampedH = Math.Min(rect.Height, tex.Height - clampedY);
            if (clampedW <= 0f || clampedH <= 0f)
                return FallbackTarget("empty clamped sprite rect");

            long dataOffset;
            if (tex.StreamSize > 0)
            {
                if (string.IsNullOrEmpty(tex.StreamPath))
                    return FallbackTarget("missing stream path");
                string nodeName = StreamNodeName(tex.StreamPath);
                NodeInfo resNode;
                if (!nodesByName.TryGetValue(nodeName, out resNode))
                    return FallbackTarget("external stream file: " + nodeName);
                if (tex.StreamOffset < 0 || tex.StreamSize < (uint)mip0Bytes || tex.StreamOffset > resNode.Size - mip0Bytes)
                    return FallbackTarget("stream data out of bounds");
                dataOffset = resNode.Offset + tex.StreamOffset;
            }
            else if (tex.InlineDataLength >= mip0Bytes && tex.InlineDataRelOffset >= 0)
            {
                dataOffset = serializedNode.Offset + texObj.Offset + tex.InlineDataRelOffset;
            }
            else
            {
                return FallbackTarget("texture has no readable image data");
            }

            return new LevelTarget
            {
                Status = DirectCoverStatus.Ok,
                DataOffset = dataOffset,
                DataSize = mip0Bytes,
                Width = tex.Width,
                Height = tex.Height,
                TextureFormat = tex.Format,
                RectX = clampedX,
                RectY = clampedY,
                RectWidth = clampedW,
                RectHeight = clampedH
            };
        }

        private static LevelTarget FallbackTarget(string reason)
        {
            return new LevelTarget { Status = DirectCoverStatus.Fallback, FallbackReason = reason };
        }

        private static SpriteInfo ReadSprite(BlockReader blockReader, NodeInfo node, ObjectEntry obj)
        {
            SpriteInfo info = new SpriteInfo();
            Field[] fields = obj.Type.Fields;
            using (BinaryReader reader = new BinaryReader(new MemoryStream(blockReader.ReadRange(node.Offset + obj.Offset, obj.Size), false)))
            {
                for (int i = 1; i < fields.Length; i = fields[i].End)
                {
                    string name = fields[i].Name;
                    if (name == "m_Rect")
                    {
                        info.Rect = ReadRect(reader, fields, i);
                    }
                    else if (name == "m_RD")
                    {
                        for (int child = i + 1; child < fields[i].End; child = fields[child].End)
                        {
                            string childName = fields[child].Name;
                            if (childName == "texture") info.Texture = ReadPointer(reader, fields, child);
                            else if (childName == "textureRect") info.TextureRect = ReadRect(reader, fields, child);
                            else if (childName == "settingsRaw" && fields[child].Size == 4)
                            {
                                info.SettingsRaw = reader.ReadUInt32();
                                if ((fields[child].Meta & 0x4000) != 0) Align(reader);
                            }
                            else SkipField(reader, fields, child);
                        }
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else
                    {
                        SkipField(reader, fields, i);
                    }
                }
            }
            return info;
        }

        private static TextureInfo ReadTexture(BlockReader blockReader, NodeInfo node, ObjectEntry obj)
        {
            TextureInfo info = new TextureInfo { InlineDataRelOffset = -1 };
            Field[] fields = obj.Type.Fields;
            using (BinaryReader reader = new BinaryReader(new MemoryStream(blockReader.ReadRange(node.Offset + obj.Offset, obj.Size), false)))
            {
                for (int i = 1; i < fields.Length; i = fields[i].End)
                {
                    string name = fields[i].Name;
                    if (name == "m_Width" && fields[i].Size == 4)
                    {
                        info.Width = reader.ReadInt32();
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else if (name == "m_Height" && fields[i].Size == 4)
                    {
                        info.Height = reader.ReadInt32();
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else if (name == "m_TextureFormat" && fields[i].Size == 4)
                    {
                        info.Format = reader.ReadInt32();
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else if (name == "m_MipCount" && fields[i].Size == 4)
                    {
                        info.MipCount = reader.ReadInt32();
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else if (name == "image data" && fields[i].Type == "TypelessData")
                    {
                        int len = Count(reader, MaxRawBytes);
                        info.InlineDataRelOffset = reader.BaseStream.Position;
                        info.InlineDataLength = len;
                        SkipBytes(reader, len);
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else if (name == "m_StreamData")
                    {
                        for (int child = i + 1; child < fields[i].End; child = fields[child].End)
                        {
                            string childName = fields[child].Name;
                            if (childName == "offset")
                            {
                                if (fields[child].Size == 4) info.StreamOffset = reader.ReadUInt32();
                                else if (fields[child].Size == 8) info.StreamOffset = reader.ReadInt64();
                                else SkipField(reader, fields, child);
                                if ((fields[child].Meta & 0x4000) != 0) Align(reader);
                            }
                            else if (childName == "size" && fields[child].Size == 4)
                            {
                                info.StreamSize = reader.ReadUInt32();
                                if ((fields[child].Meta & 0x4000) != 0) Align(reader);
                            }
                            else if (childName == "path" && fields[child].Type == "string")
                            {
                                info.StreamPath = ReadString(reader);
                            }
                            else
                            {
                                SkipField(reader, fields, child);
                            }
                        }
                        if ((fields[i].Meta & 0x4000) != 0) Align(reader);
                    }
                    else
                    {
                        SkipField(reader, fields, i);
                    }
                }
            }
            return info;
        }

        private static string StreamNodeName(string streamPath)
        {
            int slash = Math.Max(streamPath.LastIndexOf('/'), streamPath.LastIndexOf('\\'));
            return slash >= 0 ? streamPath.Substring(slash + 1) : streamPath;
        }

        private static bool IsUncompressed(int format)
        {
            return format == FormatRgb24 || format == FormatRgba32 || format == FormatArgb32 || format == FormatBgra32;
        }

        private static bool TryComputeMip0Bytes(int width, int height, int format, out int bytes, out string error)
        {
            bytes = 0;
            error = null;
            try
            {
                switch (format)
                {
                    case FormatRgb24:
                        bytes = checked(width * height * 3);
                        return true;
                    case FormatRgba32:
                    case FormatArgb32:
                    case FormatBgra32:
                        bytes = checked(width * height * 4);
                        return true;
                    case FormatDxt1:
                        if ((width & 3) != 0 || (height & 3) != 0)
                        {
                            error = "DXT1 dimensions not multiple of 4";
                            return false;
                        }
                        bytes = checked((width / 4) * (height / 4) * 8);
                        return true;
                    case FormatDxt5:
                        if ((width & 3) != 0 || (height & 3) != 0)
                        {
                            error = "DXT5 dimensions not multiple of 4";
                            return false;
                        }
                        bytes = checked((width / 4) * (height / 4) * 16);
                        return true;
                    default:
                        error = "unsupported texture format " + format;
                        return false;
                }
            }
            catch (OverflowException)
            {
                error = "texture size overflow";
                return false;
            }
        }

        /// <summary>
        /// Crops to the sprite rectangle and area-averages uncompressed textures down to at most
        /// 640x360 off the main thread so large RGB24/RGBA32 covers shrink ~10x before GPU upload.
        /// Unity Texture2D raw buffers and Sprite textureRect are both bottom-up.
        /// </summary>
        internal static byte[] CropAndDownscaleUncompressed(
            byte[] src, int srcW, int srcH, int srcFormat,
            float rectX, float rectY, float rectW, float rectH,
            Func<bool> cancelled, out int dstW, out int dstH, out int dstFormat)
        {
            int cropX = Math.Max(0, Math.Min(srcW - 1, (int)Math.Floor(rectX)));
            int cropY = Math.Max(0, Math.Min(srcH - 1, (int)Math.Floor(rectY)));
            int cropW = Math.Max(1, Math.Min(srcW - cropX, (int)Math.Round(rectW)));
            int cropH = Math.Max(1, Math.Min(srcH - cropY, (int)Math.Round(rectH)));

            float scale = Math.Min(1f, Math.Min((float)MaxWidth / cropW, (float)MaxHeight / cropH));
            dstW = Math.Max(1, (int)Math.Round(cropW * scale));
            dstH = Math.Max(1, (int)Math.Round(cropH * scale));

            int srcBpp = srcFormat == FormatRgb24 ? 3 : 4;
            dstFormat = srcFormat == FormatRgb24 ? FormatRgb24 : FormatRgba32;
            int dstBpp = dstFormat == FormatRgb24 ? 3 : 4;

            // Channel offsets inside each source pixel for R, G, B, A.
            int offR, offG, offB, offA;
            if (srcFormat == FormatRgb24) { offR = 0; offG = 1; offB = 2; offA = -1; }
            else if (srcFormat == FormatRgba32) { offR = 0; offG = 1; offB = 2; offA = 3; }
            else if (srcFormat == FormatArgb32) { offA = 0; offR = 1; offG = 2; offB = 3; }
            else { offB = 0; offG = 1; offR = 2; offA = 3; } // BGRA32

            byte[] dst = new byte[checked(dstW * dstH * dstBpp)];
            int srcStride = srcW * srcBpp;

            // Precompute horizontal source pixel spans [xStart, xEnd) per destination column.
            int[] xStarts = new int[dstW];
            int[] xEnds = new int[dstW];
            for (int x = 0; x < dstW; x++)
            {
                int x0 = cropX + (int)((long)x * cropW / dstW);
                int x1 = cropX + (int)((long)(x + 1) * cropW / dstW);
                if (x1 <= x0) x1 = Math.Min(cropX + cropW, x0 + 1);
                xStarts[x] = x0;
                xEnds[x] = x1;
            }

            int dstIndex = 0;
            for (int y = 0; y < dstH; y++)
            {
                if ((y & 31) == 0 && cancelled != null && cancelled()) throw new OperationCanceledException();
                int y0 = cropY + (int)((long)y * cropH / dstH);
                int y1 = cropY + (int)((long)(y + 1) * cropH / dstH);
                if (y1 <= y0) y1 = Math.Min(cropY + cropH, y0 + 1);
                int rows = y1 - y0;

                if (dstBpp == 3)
                {
                    for (int x = 0; x < dstW; x++)
                    {
                        int x0 = xStarts[x];
                        int x1 = xEnds[x];
                        int count = rows * (x1 - x0);
                        int sumR = 0, sumG = 0, sumB = 0;
                        for (int sy = y0; sy < y1; sy++)
                        {
                            int p = sy * srcStride + x0 * 3;
                            int pEnd = sy * srcStride + x1 * 3;
                            while (p < pEnd)
                            {
                                sumR += src[p];
                                sumG += src[p + 1];
                                sumB += src[p + 2];
                                p += 3;
                            }
                        }
                        int half = count >> 1;
                        dst[dstIndex++] = (byte)((sumR + half) / count);
                        dst[dstIndex++] = (byte)((sumG + half) / count);
                        dst[dstIndex++] = (byte)((sumB + half) / count);
                    }
                }
                else
                {
                    for (int x = 0; x < dstW; x++)
                    {
                        int x0 = xStarts[x];
                        int x1 = xEnds[x];
                        int count = rows * (x1 - x0);
                        int sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                        for (int sy = y0; sy < y1; sy++)
                        {
                            int p = sy * srcStride + x0 * 4;
                            int pEnd = sy * srcStride + x1 * 4;
                            while (p < pEnd)
                            {
                                sumR += src[p + offR];
                                sumG += src[p + offG];
                                sumB += src[p + offB];
                                sumA += src[p + offA];
                                p += 4;
                            }
                        }
                        int half = count >> 1;
                        dst[dstIndex++] = (byte)((sumR + half) / count);
                        dst[dstIndex++] = (byte)((sumG + half) / count);
                        dst[dstIndex++] = (byte)((sumB + half) / count);
                        dst[dstIndex++] = (byte)((sumA + half) / count);
                    }
                }
            }
            return dst;
        }

        private static Pointer ReadPointer(BinaryReader reader, Field[] fields, int index)
        {
            if (fields[index].End != index + 3 || fields[index + 1].Size != 4 || fields[index + 2].Size != 8)
                throw new IOException("Unexpected pointer layout");
            Pointer result = new Pointer { File = reader.ReadInt32(), Id = reader.ReadInt64() };
            if ((fields[index].Meta & 0x4000) != 0) Align(reader);
            return result;
        }

        private static RectData ReadRect(BinaryReader reader, Field[] fields, int index)
        {
            RectData rect = new RectData();
            for (int child = index + 1; child < fields[index].End; child = fields[child].End)
            {
                string name = fields[child].Name;
                if (fields[child].Size == 4 && fields[child].Type == "float")
                {
                    float val = reader.ReadSingle();
                    if (name == "x") rect.X = val;
                    else if (name == "y") rect.Y = val;
                    else if (name == "width") rect.Width = val;
                    else if (name == "height") rect.Height = val;
                    if ((fields[child].Meta & 0x4000) != 0) Align(reader);
                }
                else
                {
                    SkipField(reader, fields, child);
                }
            }
            if ((fields[index].Meta & 0x4000) != 0) Align(reader);
            return rect;
        }

        private static int ArrayData(Field[] fields, int index)
        {
            int array = index + 1;
            if (array >= fields[index].End || (fields[array].Flags & 1) == 0 || fields[array].End != fields[index].End
                || array + 2 >= fields[array].End || fields[array + 1].Size != 4
                || fields[array + 1].End != array + 2 || fields[array + 2].End != fields[array].End)
                throw new IOException("Unexpected array layout");
            return array + 2;
        }

        private static void SkipField(BinaryReader reader, Field[] fields, int index)
        {
            Field field = fields[index];
            if (field.Type == "string")
            {
                int count = Count(reader, MaxObject);
                SkipBytes(reader, count);
                Align(reader);
            }
            else if (field.Type == "TypelessData")
            {
                int count = Count(reader, MaxRawBytes);
                SkipBytes(reader, count);
            }
            else if (index + 1 < field.End && (fields[index + 1].Flags & 1) != 0)
            {
                int data = ArrayData(fields, index);
                int count = Count(reader, MaxRawBytes);
                if (fields[data].End == data + 1 && fields[data].Size >= 0 && (fields[data].Meta & 0x4000) == 0)
                    SkipBytes(reader, checked(count * fields[data].Size));
                else
                {
                    if (count > MaxItems) throw new IOException("Array item count exceeds limits");
                    for (int i = 0; i < count; i++) SkipField(reader, fields, data);
                }
                if ((fields[index + 1].Meta & 0x4000) != 0) Align(reader);
            }
            else if (field.End > index + 1)
            {
                for (int child = index + 1; child < field.End; child = fields[child].End)
                    SkipField(reader, fields, child);
            }
            else if (field.Size >= 0)
            {
                SkipBytes(reader, field.Size);
            }
            else
            {
                throw new IOException("Unsupported variable field: " + field.Type);
            }
            if ((field.Meta & 0x4000) != 0) Align(reader);
        }

        private static Field[] ReadTree(BinaryReader reader)
        {
            int count = Count(reader, 65536);
            int stringSize = Count(reader, MaxMetadata);
            if (count == 0 || (long)count * 24 + stringSize > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new IOException("Invalid type tree extent");
            Field[] fields = new Field[count];
            uint[] names = new uint[count];
            uint[] types = new uint[count];
            for (int i = 0; i < count; i++)
            {
                reader.ReadInt16();
                int depth = reader.ReadByte();
                int flags = reader.ReadByte();
                types[i] = reader.ReadUInt32();
                names[i] = reader.ReadUInt32();
                int size = reader.ReadInt32();
                reader.ReadInt32();
                int meta = reader.ReadInt32();
                if (depth > 64 || (i == 0 ? depth != 0 : depth == 0 || depth > fields[i - 1].Depth + 1))
                    throw new IOException("Invalid type tree depth");
                fields[i] = new Field { Depth = depth, Flags = flags, Size = size, Meta = meta, End = count };
            }
            byte[] strings = reader.ReadBytes(stringSize);
            Stack<int> stack = new Stack<int>();
            for (int i = 0; i < count; i++)
            {
                while (stack.Count != 0 && fields[stack.Peek()].Depth >= fields[i].Depth)
                    fields[stack.Pop()].End = i;
                stack.Push(i);
                fields[i].Name = TreeString(strings, names[i]);
                fields[i].Type = TreeString(strings, types[i]);
            }
            return fields;
        }

        private static string TreeString(byte[] strings, uint offset)
        {
            if ((offset & 0x80000000) != 0)
            {
                switch (offset & 0x7fffffff)
                {
                    case 49: return "Array";
                    case 76: return "bool";
                    case 106: return "data";
                    case 161: return "float";
                    case 222: return "int";
                    case 427: return "m_Name";
                    case 490: return "m_Script";
                    case 659: return "PPtr<Sprite>";
                    case 702: return "PPtr<Texture2D>";
                    case 753: return "Rectf";
                    case 795: return "size";
                    case 807: return "SInt32";
                    case 814: return "SInt64";
                    case 840: return "string";
                    case 874: return "Texture2D";
                    case 894: return "TypelessData";
                    case 914: return "UInt32";
                    case 921: return "UInt64";
                    case 928: return "UInt8";
                    case 934: return "unsigned int";
                    default: return "#" + (offset & 0x7fffffff);
                }
            }
            if (offset >= strings.Length) throw new IOException("Invalid type tree string offset");
            int end = (int)offset;
            while (end < strings.Length && strings[end] != 0 && end - offset <= 4096) end++;
            if (end == strings.Length || end - offset > 4096) throw new IOException("Invalid type tree string");
            return Utf8.GetString(strings, (int)offset, end - (int)offset);
        }

        private static int Big32(byte[] data, int offset)
        {
            uint value = (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
            if (value > int.MaxValue) throw new IOException("Header size exceeds limits");
            return (int)value;
        }

        private static uint ReadBigU32(BinaryReader reader)
        {
            return (uint)(reader.ReadByte() << 24 | reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte());
        }

        private static long ReadBigI64(BinaryReader reader)
        {
            return ((long)ReadBigU32(reader) << 32) | ReadBigU32(reader);
        }

        private static int BoundedSize(uint value, int limit)
        {
            if (value > (uint)limit) throw new IOException("UnityFS field exceeds limits");
            return (int)value;
        }

        private static int Count(BinaryReader reader, int limit)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > limit) throw new IOException("Serialized count exceeds limits");
            return count;
        }

        private static void SkipBytes(BinaryReader reader, int count)
        {
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new IOException("Truncated serialized field");
            reader.BaseStream.Position += count;
        }

        private static void Align(BinaryReader reader)
        {
            int padding = (int)((4 - reader.BaseStream.Position % 4) % 4);
            SkipBytes(reader, padding);
        }

        private static string ReadCString(BinaryReader reader, int limit)
        {
            List<byte> bytes = new List<byte>();
            byte value;
            while ((value = reader.ReadByte()) != 0)
            {
                if (bytes.Count == limit) throw new IOException("C-string exceeds limits");
                bytes.Add(value);
            }
            return Utf8.GetString(bytes.ToArray());
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = Count(reader, MaxObject);
            if (length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new IOException("Truncated string field");
            string text = Utf8.GetString(reader.ReadBytes(length));
            Align(reader);
            return text;
        }
    }

    /// <summary>Runs one cancellable direct cover read on the managed thread pool.</summary>
    internal sealed class DirectCoverRequest : IDisposable
    {
        private readonly string _infoPath;
        private readonly string _sceneName;
        private volatile bool _cancelled;
        private volatile bool _done;
        private DirectCoverData _result;

        internal DirectCoverRequest(string infoPath, string sceneName)
        {
            _infoPath = infoPath;
            _sceneName = sceneName;
        }

        internal bool Done { get { return _done; } }
        internal bool Cancelled { get { return _cancelled; } }
        internal DirectCoverData Result { get { return _result; } }

        internal void Start()
        {
            ThreadPool.QueueUserWorkItem(Work);
        }

        private void Work(object unused)
        {
            try
            {
                if (_cancelled) return;
                _result = DirectCoverReader.ReadCover(_infoPath, _sceneName, () => _cancelled);
            }
            catch (OperationCanceledException)
            {
                _result = null;
            }
            catch (Exception e)
            {
                _result = new DirectCoverData
                {
                    Status = DirectCoverStatus.Fallback,
                    FallbackReason = e.GetType().Name + ": " + e.Message
                };
            }
            finally
            {
                _done = true;
            }
        }

        public void Dispose()
        {
            _cancelled = true;
        }
    }
}
