using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Performs one background pass over the levels directory: loads the unified catalog index,
    /// enumerates each set directory once, checks bundle headers only for uncached files, and
    /// preloads cached scalar metadata for Pass-0 sets without touching Unity or BepInEx APIs.
    /// </summary>
    internal sealed class StartupCatalogScan : IDisposable
    {
        internal sealed class ScannedSet
        {
            internal int Index;
            internal DirectoryInfo Directory;
            internal SetSnapshot Snapshot;
            internal FileInfo InfoFile;
            internal List<string> BundlePaths;
            internal bool NeedsConversionWork;
            internal SetMetadata CachedMetadata;
            internal string CacheMiss;
            internal double CacheReadMs;
        }

        private readonly string _levelsDir;
        private readonly bool _optimizeFiles;
        private readonly bool _restoreFiles;
        private volatile bool _cancelled, _done;
        private ScannedSet[] _sets = new ScannedSet[0];
        private List<string> _warnings = new List<string>();
        private int _totalBundleFiles;
        private int _fastFileHits;
        private int _headerProbes;
        private double _totalScanMs, _indexLoadMs, _dirScanMs, _headerProbeMs, _cacheLookupMs;
        private bool _indexLoadedFromDisk;

        internal StartupCatalogScan(string levelsDir, bool optimizeFiles, bool restoreFiles)
        {
            _levelsDir = levelsDir;
            _optimizeFiles = optimizeFiles;
            _restoreFiles = restoreFiles;
        }

        internal bool Done { get { return _done; } }
        internal bool Cancelled { get { return _cancelled; } }
        internal ScannedSet[] Sets { get { return _sets; } }
        internal List<string> Warnings { get { return _warnings; } }
        internal int TotalBundleFiles { get { return _totalBundleFiles; } }
        internal int FastFileHits { get { return _fastFileHits; } }
        internal int HeaderProbes { get { return _headerProbes; } }
        internal double TotalScanMs { get { return _totalScanMs; } }
        internal double IndexLoadMs { get { return _indexLoadMs; } }
        internal double DirScanMs { get { return _dirScanMs; } }
        internal double HeaderProbeMs { get { return _headerProbeMs; } }
        internal double CacheLookupMs { get { return _cacheLookupMs; } }
        internal bool IndexLoadedFromDisk { get { return _indexLoadedFromDisk; } }

        internal void Start()
        {
            new Thread(Work)
            {
                IsBackground = true,
                Name = "DIY startup catalog scanner",
                Priority = ThreadPriority.BelowNormal
            }.Start();
        }

        public void Dispose()
        {
            _cancelled = true;
        }

        private void Work()
        {
            Stopwatch total = Stopwatch.StartNew();
            try
            {
                Stopwatch step = Stopwatch.StartNew();
                string indexWarning;
                CatalogIndexStore store = CatalogIndexStore.Default;
                store.EnsureLoaded(out indexWarning);
                _indexLoadMs = step.Elapsed.TotalMilliseconds;
                _indexLoadedFromDisk = store.LoadedFromDisk;
                if (indexWarning != null) _warnings.Add(indexWarning);
                if (_cancelled) return;

                step.Reset();
                step.Start();
                DirectoryInfo[] dirs = ReadDirectories(_levelsDir);
                if (_cancelled) return;

                bool convertFiles = _optimizeFiles || _restoreFiles;
                ScannedSet[] scanned = new ScannedSet[dirs.Length];
                HashSet<string> existingNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < dirs.Length; i++)
                {
                    if (_cancelled) return;
                    DirectoryInfo dir = dirs[i];
                    existingNormalized.Add(CatalogIndexStore.NormalizeDir(dir.FullName));
                    scanned[i] = ScanDirectory(store, dir, i, convertFiles);
                }

                _dirScanMs = step.Elapsed.TotalMilliseconds - _headerProbeMs - _cacheLookupMs;
                if (_dirScanMs < 0) _dirScanMs = 0;

                if (!_cancelled && Directory.Exists(_levelsDir))
                {
                    store.PruneMissingDirectories(existingNormalized);
                }
                _sets = scanned;
            }
            catch (Exception e)
            {
                _warnings.Add("startup catalog scan failed: " + e.Message);
            }
            finally
            {
                _totalScanMs = total.Elapsed.TotalMilliseconds;
                _done = true;
            }
        }

        private DirectoryInfo[] ReadDirectories(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return new DirectoryInfo[0];
                return new DirectoryInfo(path).GetDirectories();
            }
            catch (Exception e)
            {
                _warnings.Add("listing " + path + ": " + e.Message);
                return new DirectoryInfo[0];
            }
        }

        private ScannedSet ScanDirectory(CatalogIndexStore store, DirectoryInfo dir, int index, bool convertFiles)
        {
            ScannedSet result = new ScannedSet
            {
                Index = index,
                Directory = dir,
                BundlePaths = new List<string>()
            };

            FileInfo[] files;
            try
            {
                files = dir.GetFiles();
            }
            catch (Exception e)
            {
                _warnings.Add("listing " + dir.FullName + ": " + e.Message);
                result.Snapshot = new SetSnapshot { DirectoryName = dir.Name, Files = new LevelDirectorySnapshot.FileStamp[0] };
                return result;
            }

            LevelDirectorySnapshot.FileStamp[] stamps = new LevelDirectorySnapshot.FileStamp[files.Length];
            SetSnapshot snapshot = new SetSnapshot { DirectoryName = dir.Name };
            bool hasWorkDir = convertFiles && Directory.Exists(Path.Combine(dir.FullName, ".diylevel-fastinit"));
            bool needsWork = hasWorkDir;
            List<FastFileStamp> verifiedFast = (_optimizeFiles && !_restoreFiles) ? new List<FastFileStamp>(files.Length) : null;

            for (int i = 0; i < files.Length; i++)
            {
                if (_cancelled) break;
                FileInfo file = files[i];
                long length = file.Length;
                long modified = file.LastWriteTimeUtc.Ticks;
                LevelDirectorySnapshot.FileStamp stamp = new LevelDirectorySnapshot.FileStamp
                {
                    Name = file.Name,
                    Length = length,
                    Modified = modified
                };
                stamps[i] = stamp;

                if (result.InfoFile == null && file.Name.StartsWith("info", StringComparison.OrdinalIgnoreCase))
                {
                    result.InfoFile = file;
                    snapshot.InfoFileName = file.Name;
                    snapshot.InfoLength = length;
                    snapshot.InfoMtimeUtc = new DateTime(modified, DateTimeKind.Utc);
                }

                if (!convertFiles) continue;
                if (_restoreFiles)
                {
                    if (!hasWorkDir) continue;
                    // Restore mode only needs bundle paths when a .diylevel-fastinit work dir exists.
                    byte cachedKind;
                    if (store.TryGetFastFile(dir.FullName, file.Name, length, modified, out cachedKind))
                    {
                        _fastFileHits++;
                        if (cachedKind == FastFileStamp.KindFastBundle) result.BundlePaths.Add(file.FullName);
                    }
                    else
                    {
                        Stopwatch probe = Stopwatch.StartNew();
                        _headerProbes++;
                        if (LevelBundleFiles.IsBundle(file.FullName)) result.BundlePaths.Add(file.FullName);
                        _headerProbeMs += probe.Elapsed.TotalMilliseconds;
                    }
                    continue;
                }

                // Optimize mode: reuse verified fast-bundle / non-bundle stamps when (length, mtime) match.
                byte kind;
                if (store.TryGetFastFile(dir.FullName, file.Name, length, modified, out kind))
                {
                    _fastFileHits++;
                    if (kind == FastFileStamp.KindFastBundle) result.BundlePaths.Add(file.FullName);
                    verifiedFast.Add(new FastFileStamp { Name = file.Name, Length = length, Modified = modified, Kind = kind });
                    continue;
                }

                Stopwatch headerTimer = Stopwatch.StartNew();
                _headerProbes++;
                if (!LevelBundleFiles.IsBundle(file.FullName))
                {
                    verifiedFast.Add(new FastFileStamp
                    {
                        Name = file.Name,
                        Length = length,
                        Modified = modified,
                        Kind = FastFileStamp.KindNotBundle
                    });
                }
                else
                {
                    result.BundlePaths.Add(file.FullName);
                    if (BundleCodecs.UnityFsRepacker.NeedsConversion(file.FullName))
                    {
                        needsWork = true;
                    }
                    else
                    {
                        verifiedFast.Add(new FastFileStamp
                        {
                            Name = file.Name,
                            Length = length,
                            Modified = modified,
                            Kind = FastFileStamp.KindFastBundle
                        });
                    }
                }
                _headerProbeMs += headerTimer.Elapsed.TotalMilliseconds;
            }

            Array.Sort(stamps, delegate(LevelDirectorySnapshot.FileStamp first, LevelDirectorySnapshot.FileStamp second)
            {
                return StringComparer.Ordinal.Compare(first.Name, second.Name);
            });
            snapshot.Files = stamps;
            result.Snapshot = snapshot;
            result.NeedsConversionWork = needsWork;
            _totalBundleFiles += result.BundlePaths.Count;

            if (!_cancelled && verifiedFast != null && !needsWork)
            {
                store.SetFastFiles(dir.FullName, verifiedFast);
            }

            // Pass-0 sets will not be modified by conversion, so resolve their cached metadata now.
            if (!_cancelled && !needsWork && result.InfoFile != null)
            {
                Stopwatch cacheTimer = Stopwatch.StartNew();
                string warning;
                result.CachedMetadata = LevelCatalogCache.ReadCachedData(dir, result.InfoFile, out result.CacheMiss, out warning);
                result.CacheReadMs = cacheTimer.Elapsed.TotalMilliseconds;
                _cacheLookupMs += result.CacheReadMs;
                if (warning != null) _warnings.Add(warning);
            }

            return result;
        }
    }
}
