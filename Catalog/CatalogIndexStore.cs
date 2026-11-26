using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace DIYLevelFastInit
{
    /// <summary>Cached fingerprint for one top-level file known to need no LZMA conversion.</summary>
    internal sealed class FastFileStamp
    {
        internal const byte KindNotBundle = 0;
        internal const byte KindFastBundle = 1;

        internal string Name;
        internal long Length;
        internal long Modified;
        internal byte Kind;
    }

    /// <summary>
    /// Stores all level-set scalar catalogs and verified fast-bundle fingerprints in a single
    /// versioned binary file so warm startups perform one sequential read instead of N per-set reads.
    /// Pure managed I/O: never calls Unity or BepInEx APIs so background workers can query it safely.
    /// </summary>
    internal sealed class CatalogIndexStore
    {
        private sealed class Entry
        {
            internal string Directory;
            internal SetMetadata Metadata;
            internal Dictionary<string, FastFileStamp> FastFiles =
                new Dictionary<string, FastFileStamp>(StringComparer.OrdinalIgnoreCase);
        }

        internal const int Magic = 0x33434944; // "DIC3"
        internal const int Schema = 3;
        private const int MaxEntries = 20000;
        private const int MaxFileBytes = 32 * 1024 * 1024;

        private static readonly object DefaultLock = new object();
        private static CatalogIndexStore _default;

        private readonly string _path;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;
        private bool _loadedFromDisk;
        private bool _dirty;
        private int _version;

        internal CatalogIndexStore(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            _path = path;
        }

        internal static CatalogIndexStore Default
        {
            get
            {
                if (_default != null) return _default;
                lock (DefaultLock)
                {
                    if (_default == null)
                    {
                        string path = Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), "catalog.index");
                        _default = new CatalogIndexStore(path);
                    }
                    return _default;
                }
            }
        }

        internal string IndexPath { get { return _path; } }

        internal bool LoadedFromDisk
        {
            get { lock (_sync) return _loadedFromDisk; }
        }

        internal bool IsDirty
        {
            get { lock (_sync) return _dirty; }
        }

        internal int EntryCount
        {
            get { lock (_sync) return _entries.Count; }
        }

        internal void EnsureLoaded(out string warning)
        {
            warning = null;
            lock (_sync)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (!File.Exists(_path)) return;
                    long fileLength = new FileInfo(_path).Length;
                    if (fileLength <= 0 || fileLength > MaxFileBytes)
                    {
                        warning = "catalog index ignored: file size out of bounds (" + fileLength + " bytes)";
                        return;
                    }
                    byte[] bytes = File.ReadAllBytes(_path);
                    Dictionary<string, Entry> loaded = ReadEntries(bytes);
                    _entries.Clear();
                    foreach (KeyValuePair<string, Entry> kv in loaded) _entries[kv.Key] = kv.Value;
                    _loadedFromDisk = true;
                }
                catch (Exception e)
                {
                    _entries.Clear();
                    _loadedFromDisk = false;
                    warning = "catalog index ignored (" + e.GetType().Name + "): " + e.Message;
                }
            }
        }

        internal bool TryGetMetadata(DirectoryInfo dir, FileInfo file, out SetMetadata data, out string miss)
        {
            data = null;
            miss = null;
            string key = NormalizeDir(dir.FullName);
            SetMetadata candidate;
            lock (_sync)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry) || entry.Metadata == null)
                {
                    miss = "missing index entry";
                    return false;
                }
                candidate = entry.Metadata;
            }
            if (!string.Equals(candidate.directory, dir.FullName, StringComparison.OrdinalIgnoreCase)) miss = "directory changed";
            else if (candidate.infoFile != file.Name) miss = "info filename changed";
            else if (candidate.length != file.Length) miss = "info length changed";
            else if (candidate.modified != file.LastWriteTimeUtc.Ticks) miss = "info timestamp changed";
            else if (!candidate.IsValid(dir, file)) miss = "invalid metadata";
            if (miss != null) return false;
            data = candidate;
            return true;
        }

        internal bool TryGetFastFile(string directoryPath, string fileName, long length, long modified, out byte kind)
        {
            kind = 0;
            if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(fileName)) return false;
            string key = NormalizeDir(directoryPath);
            lock (_sync)
            {
                Entry entry;
                FastFileStamp stamp;
                if (!_entries.TryGetValue(key, out entry) || !entry.FastFiles.TryGetValue(fileName, out stamp))
                    return false;
                if (stamp.Length != length || stamp.Modified != modified) return false;
                kind = stamp.Kind;
                return true;
            }
        }

        internal void PutMetadata(string directoryPath, SetMetadata data)
        {
            if (string.IsNullOrEmpty(directoryPath) || data == null) return;
            string key = NormalizeDir(directoryPath);
            lock (_sync)
            {
                Entry entry = GetOrCreateEntryLocked(key, directoryPath);
                entry.Metadata = data;
                _dirty = true;
                _version++;
            }
        }

        internal void SetFastFiles(string directoryPath, List<FastFileStamp> files)
        {
            if (string.IsNullOrEmpty(directoryPath) || files == null) return;
            string key = NormalizeDir(directoryPath);
            lock (_sync)
            {
                Entry entry = GetOrCreateEntryLocked(key, directoryPath);
                if (SameFastFiles(entry.FastFiles, files)) return;
                entry.FastFiles = new Dictionary<string, FastFileStamp>(files.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Count; i++)
                {
                    FastFileStamp stamp = files[i];
                    if (stamp == null || string.IsNullOrEmpty(stamp.Name)) continue;
                    entry.FastFiles[stamp.Name] = new FastFileStamp
                    {
                        Name = stamp.Name,
                        Length = stamp.Length,
                        Modified = stamp.Modified,
                        Kind = stamp.Kind
                    };
                }
                _dirty = true;
                _version++;
            }
        }

        internal void UpsertFastFile(string directoryPath, string fileName, long length, long modified, byte kind)
        {
            if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(fileName)) return;
            string key = NormalizeDir(directoryPath);
            lock (_sync)
            {
                Entry entry = GetOrCreateEntryLocked(key, directoryPath);
                FastFileStamp existing;
                if (entry.FastFiles.TryGetValue(fileName, out existing)
                    && existing.Length == length && existing.Modified == modified && existing.Kind == kind)
                {
                    return;
                }
                entry.FastFiles[fileName] = new FastFileStamp
                {
                    Name = fileName,
                    Length = length,
                    Modified = modified,
                    Kind = kind
                };
                _dirty = true;
                _version++;
            }
        }

        internal void RemoveDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return;
            string key = NormalizeDir(directoryPath);
            lock (_sync)
            {
                if (_entries.Remove(key))
                {
                    _dirty = true;
                    _version++;
                }
            }
        }

        internal void PruneMissingDirectories(HashSet<string> existingNormalizedDirs)
        {
            PruneMissingDirectories(null, existingNormalizedDirs);
        }

        // When parentDir is provided, only direct children of parentDir are pruned so
        // fast-bundle stamps stored for shared common* directories outside levels/ survive.
        internal void PruneMissingDirectories(string parentDir, HashSet<string> existingNormalizedDirs)
        {
            if (existingNormalizedDirs == null) return;
            string normalizedParent = string.IsNullOrEmpty(parentDir) ? null : NormalizeDir(parentDir);
            lock (_sync)
            {
                List<string> stale = null;
                foreach (string key in _entries.Keys)
                {
                    if (normalizedParent != null)
                    {
                        string parent;
                        try { parent = NormalizeDir(Path.GetDirectoryName(key)); }
                        catch { parent = string.Empty; }
                        if (!string.Equals(parent, normalizedParent, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (!existingNormalizedDirs.Contains(key))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(key);
                    }
                }
                if (stale == null) return;
                for (int i = 0; i < stale.Count; i++) _entries.Remove(stale[i]);
                _dirty = true;
                _version++;
            }
        }

        internal bool Flush(out string warning)
        {
            warning = null;
            List<Entry> snapshot;
            int version;
            lock (_sync)
            {
                if (!_dirty) return false;
                version = _version;
                snapshot = SnapshotEntriesLocked();
            }
            string temporary = null;
            try
            {
                string parent = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                byte[] payload;
                using (MemoryStream memory = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(memory, Encoding.UTF8))
                {
                    WriteEntries(writer, snapshot);
                    writer.Flush();
                    payload = memory.ToArray();
                }
                if (payload.Length > MaxFileBytes)
                    throw new IOException("Catalog index exceeds size limit (" + payload.Length + " bytes)");
                temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, payload);
                byte[] readback = File.ReadAllBytes(temporary);
                Dictionary<string, Entry> verified = ReadEntries(readback);
                if (verified.Count != snapshot.Count)
                    throw new IOException("Catalog index readback count mismatch");
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                temporary = null;
                lock (_sync)
                {
                    if (_version == version) _dirty = false;
                    _loaded = true;
                    _loadedFromDisk = true;
                }
                return true;
            }
            catch (Exception e)
            {
                warning = "catalog index write skipped: " + e.Message;
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal static string NormalizeDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private Entry GetOrCreateEntryLocked(string key, string directoryPath)
        {
            Entry entry;
            if (!_entries.TryGetValue(key, out entry))
            {
                entry = new Entry { Directory = directoryPath };
                _entries[key] = entry;
            }
            else if (string.IsNullOrEmpty(entry.Directory))
            {
                entry.Directory = directoryPath;
            }
            return entry;
        }

        private List<Entry> SnapshotEntriesLocked()
        {
            List<Entry> result = new List<Entry>(_entries.Count);
            foreach (Entry source in _entries.Values)
            {
                if (source.Metadata == null && source.FastFiles.Count == 0) continue;
                Entry copy = new Entry
                {
                    Directory = source.Directory,
                    Metadata = source.Metadata,
                    FastFiles = new Dictionary<string, FastFileStamp>(source.FastFiles.Count, StringComparer.OrdinalIgnoreCase)
                };
                foreach (KeyValuePair<string, FastFileStamp> kv in source.FastFiles)
                {
                    FastFileStamp s = kv.Value;
                    copy.FastFiles[kv.Key] = new FastFileStamp
                    {
                        Name = s.Name,
                        Length = s.Length,
                        Modified = s.Modified,
                        Kind = s.Kind
                    };
                }
                result.Add(copy);
            }
            return result;
        }

        private static bool SameFastFiles(Dictionary<string, FastFileStamp> current, List<FastFileStamp> next)
        {
            int validCount = 0;
            for (int i = 0; i < next.Count; i++)
            {
                FastFileStamp stamp = next[i];
                if (stamp == null || string.IsNullOrEmpty(stamp.Name)) continue;
                validCount++;
                FastFileStamp existing;
                if (!current.TryGetValue(stamp.Name, out existing)) return false;
                if (existing.Length != stamp.Length || existing.Modified != stamp.Modified || existing.Kind != stamp.Kind)
                    return false;
            }
            return validCount == current.Count;
        }

        private static void WriteEntries(BinaryWriter writer, List<Entry> entries)
        {
            writer.Write(Magic);
            writer.Write(Schema);
            writer.Write(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                LevelCatalogCache.WriteText(writer, entry.Directory);
                writer.Write(entry.Metadata != null ? (byte)1 : (byte)0);
                if (entry.Metadata != null) LevelCatalogCache.WriteData(writer, entry.Metadata);
                writer.Write(entry.FastFiles.Count);
                foreach (FastFileStamp stamp in entry.FastFiles.Values)
                {
                    LevelCatalogCache.WriteText(writer, stamp.Name);
                    writer.Write(stamp.Length);
                    writer.Write(stamp.Modified);
                    writer.Write(stamp.Kind);
                }
            }
        }

        private static Dictionary<string, Entry> ReadEntries(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxFileBytes) throw new IOException("Invalid catalog index size");
            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Schema)
                    throw new IOException("Unsupported catalog index format");
                int count = LevelCatalogCache.ReadCount(reader);
                if (count > MaxEntries) throw new IOException("Catalog index entry count exceeds limit");
                Dictionary<string, Entry> result = new Dictionary<string, Entry>(count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string directory = LevelCatalogCache.ReadText(reader);
                    if (string.IsNullOrEmpty(directory)) throw new IOException("Empty directory in catalog index");
                    byte hasMeta = reader.ReadByte();
                    if (hasMeta != 0 && hasMeta != 1) throw new IOException("Invalid metadata flag in catalog index");
                    SetMetadata metadata = hasMeta == 1 ? LevelCatalogCache.ReadDataBody(reader) : null;
                    int fileCount = LevelCatalogCache.ReadCount(reader);
                    Dictionary<string, FastFileStamp> fastFiles =
                        new Dictionary<string, FastFileStamp>(fileCount, StringComparer.OrdinalIgnoreCase);
                    for (int j = 0; j < fileCount; j++)
                    {
                        string name = LevelCatalogCache.ReadText(reader);
                        long length = reader.ReadInt64();
                        long modified = reader.ReadInt64();
                        byte kind = reader.ReadByte();
                        if (string.IsNullOrEmpty(name) || length < 0
                            || (kind != FastFileStamp.KindNotBundle && kind != FastFileStamp.KindFastBundle))
                            throw new IOException("Invalid fast-bundle stamp in catalog index");
                        fastFiles[name] = new FastFileStamp
                        {
                            Name = name,
                            Length = length,
                            Modified = modified,
                            Kind = kind
                        };
                    }
                    string key = NormalizeDir(directory);
                    if (result.ContainsKey(key)) throw new IOException("Duplicate directory in catalog index");
                    result.Add(key, new Entry { Directory = directory, Metadata = metadata, FastFiles = fastFiles });
                }
                if (stream.Position != stream.Length) throw new IOException("Trailing catalog index data");
                return result;
            }
        }
    }
}
