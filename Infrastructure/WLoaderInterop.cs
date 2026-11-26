using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Coordinates in-place LZ4HC conversion and parallel background loading for
    /// DIYLevel common0* and taken-over WLoader commonW* shared dependency bundles.
    /// Once converted to 128 KiB LZ4HC, all shared packs load in ~1 second with
    /// minimal resident memory, so level entry simply waits until all shared packs
    /// have settled instead of building per-level common dependency graphs.
    /// </summary>
    internal static class WLoaderInterop
    {
        private enum Mode { Off, Observe, Takeover }

        private const string LoaderTypeName = "LevelRuntimeLoader";
        private const float StallSeconds = 20f;

        private static Mode _mode = Mode.Off;
        private static Harmony _harmony;
        private static string _loaderDir;
        private static FieldInfo _depStateField;
        // Expected WLoader shared bundle names; compared case-insensitively at each use.
        private static readonly List<string> _names = new List<string>();
        private static bool _namesRead;
        // Total shared bundles (DIYLevel common* + WLoader commonW*) scheduled in the parallel batch.
        private static int _expectedCount;
        // Incremented whenever a shared bundle newly settles so cached preview sets reload fresh.
        internal static int SettledRevision { get; private set; }
        // Names that finished loading, failed to load, or were already resident.
        private static readonly HashSet<string> _settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The parallel loading pass in LoadHeavy finished (all scheduled bundles settled).
        private static bool _drained;
        // Files scheduled by LoadSharedBundles (used by sync fallback if invoked before drain).
        private static readonly List<FileInfo> _scheduledFiles = new List<FileInfo>();
        // Live parallel background requests by bundle name; entry boosts their priority.
        private static readonly Dictionary<string, AssetBundleCreateRequest> _takeoverLoads =
            new Dictionary<string, AssetBundleCreateRequest>(StringComparer.OrdinalIgnoreCase);
        // Background LZ4HC conversion batch for shared common* / commonW* bundles; each bundle
        // streams directly into AssetBundle.LoadFromFileAsync as soon as its replacement settles.
        private static InfoBundleOptimizationBatch _conversionBatch;
        private static readonly HashSet<string> _convertingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static float _conversionStartedRealtime;
        private static bool _conversionRestore;
        private static bool _entryWaiting;
        private static bool _shutdown;

        internal static bool IsConvertingSharedBundles
        {
            get { return _conversionBatch != null && !_conversionBatch.Done; }
        }

        internal static float ConversionStartedRealtime
        {
            get { return _conversionStartedRealtime; }
        }

        internal static string OptimizationProgress
        {
            get
            {
                InfoBundleOptimizationBatch batch = _conversionBatch;
                if (batch == null || batch.Done) return null;
                string current = batch.Current;
                return (_conversionRestore ? "restoring " : "optimizing ")
                    + batch.Completed + "/" + batch.Total
                    + (string.IsNullOrEmpty(current) ? "" : ": " + current);
            }
        }

        // True once every shared dependency bundle (DIYLevel common0* and WLoader commonW*)
        // has settled. After the ~1s LZ4HC startup load, level entry short-circuits here.
        internal static bool AllReady
        {
            get
            {
                if (!_drained) return false;
                if (ObservePendingCount() > 0) return false;
                return true;
            }
        }

        internal static void Shutdown()
        {
            _shutdown = true;
            InfoBundleOptimizationBatch batch = _conversionBatch;
            if (batch != null) batch.Dispose();
            _convertingNames.Clear();
        }

        internal static void TryInstall()
        {
            if (!FastInitPlugin.TakeOverWLoaderBundles) return;
            try
            {
                Type loader = FindLoaderType();
                if (loader == null)
                {
                    FastInitPlugin.Log.LogInfo("web loader not found; shared bundle takeover off");
                    return;
                }
                _loaderDir = Path.GetDirectoryName(loader.Assembly.Location);
                _depStateField = loader.GetField("_depLoadState", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo probe = loader.GetMethod("IsBundleLoaded",
                    BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (probe == null || _depStateField == null)
                {
                    FastInitPlugin.Log.LogWarning("web loader member contract changed; observing its own shared bundle loads");
                    _mode = Mode.Observe;
                    return;
                }
                _harmony = new Harmony(FastInitPlugin.PluginGuid + ".wloader");
                _harmony.Patch(probe, prefix: new HarmonyMethod(typeof(WLoaderInterop), nameof(IsBundleLoadedPrefix)));
                _mode = Mode.Takeover;
                FastInitPlugin.Log.LogInfo("web loader shared bundle takeover armed: " + _loaderDir);
            }
            catch (Exception e)
            {
                _mode = Mode.Off;
                FastInitPlugin.Log.LogWarning("shared bundle takeover unavailable; web loader keeps its own scheduling: " + e.Message);
            }
        }

        // Harmony prefix: report FastInit-managed commonW* bundles as already loaded so
        // the web loader skips its own serial background queue. The probe is only called
        // after that loader decided to load its dependencies, so suppression never leaves
        // a bundle that nobody loads - FastInit loads them all in LoadHeavy.
        private static bool IsBundleLoadedPrefix(string __0, ref bool __result)
        {
            if (!IsCommonWName(__0)) return true;
            __result = true;
            return false;
        }

        private static bool IsCommonWName(string name)
        {
            if (name == null || name.Length <= 7
                || !name.StartsWith("commonW", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 7; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return true;
        }

        // True when path is one of the web loader's own commonW<digits> files (same
        // directory as Loader.dll). When WLoader is installed inside OC2DIYLevel/,
        // LoadSharedBundles skips these files in its DIYLevel common* loop so WLoader
        // ownership stays single-sourced without false duplicate warnings.
        internal static bool OwnsSharedBundle(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!IsCommonWName(Path.GetFileName(path))) return false;
            string loaderDir = ResolveLoadedLoaderDir();
            if (string.IsNullOrEmpty(loaderDir)) return false;
            return string.Equals(NormalizeDir(Path.GetDirectoryName(path)), NormalizeDir(loaderDir),
                StringComparison.OrdinalIgnoreCase);
        }

        // Inspects only already-loaded assemblies so calling this when takeover is off
        // never triggers an early Assembly.LoadFrom side effect.
        private static string ResolveLoadedLoaderDir()
        {
            if (!string.IsNullOrEmpty(_loaderDir)) return _loaderDir;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type match = FindLoaderTypeIn(assembly);
                if (match == null) continue;
                try
                {
                    string dir = Path.GetDirectoryName(match.Assembly.Location);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        _loaderDir = dir;
                        return _loaderDir;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string NormalizeDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        private static Type FindLoaderType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type match = FindLoaderTypeIn(assembly);
                if (match != null) return match;
            }
            // The web loader may instantiate after this plugin; load its assembly early
            // so the probe is patched before its first dependency scan runs.
            foreach (string candidate in LoaderDllCandidates())
            {
                try
                {
                    Type match = FindLoaderTypeIn(Assembly.LoadFrom(candidate));
                    if (match != null) return match;
                }
                catch { }
            }
            return null;
        }

        // The loader class lives in a namespace that varies across its releases
        // (e.g. OC2LevelRuntimeLoader.LevelRuntimeLoader); match by simple name so a
        // namespace change cannot hide it.
        private static Type FindLoaderTypeIn(Assembly assembly)
        {
            try
            {
                foreach (Type type in assembly.GetTypes())
                    if (type.IsClass && type.Name == LoaderTypeName) return type;
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> LoaderDllCandidates()
        {
            yield return Path.Combine(Paths.PluginPath, "Loader.dll");
            foreach (DirectoryInfo dir in FastInitHost.SafeGetDirs(Paths.PluginPath))
                yield return Path.Combine(dir.FullName, "Loader.dll");
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static void MarkSettled(string name)
        {
            if (_settled.Add(name)) SettledRevision++;
        }

        // Converts LZMA shared bundles (DIYLevel common0* and taken-over WLoader commonW*)
        // to 128 KiB LZ4HC in place before loading them so Unity maps only referenced chunks
        // instead of holding the full ~900 MiB decompressed payload in memory. Already-fast
        // bundles dispatch on frame 0; converting bundles stream into LoadFromFileAsync one
        // by one as soon as each verified replacement finishes.
        internal static IEnumerator LoadSharedBundles(string diyPluginDir)
        {
            List<FileInfo> batch = new List<FileInfo>();
            Dictionary<string, string> queuedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int wloaderFiles = 0, diyFiles = 0, skipped = 0;

            if (_mode != Mode.Off)
            {
                CollectNames();
                if (_names.Count == 0)
                {
                    _mode = Mode.Off;
                }
                else if (_mode == Mode.Takeover)
                {
                    int state = ReadDepState();
                    if (state == 0)
                    {
                        // The web loader never activated its dependency pipeline (no web-exported
                        // sets); release the suppression so a later activation still loads alone.
                        Release("web loader dependency pipeline not active");
                    }
                    else
                    {
                        foreach (FileInfo file in SharedFiles())
                        {
                            string full = NormalizePath(file.FullName);
                            if (queuedByName.ContainsKey(file.Name)) continue;
                            queuedByName[file.Name] = full;
                            batch.Add(file);
                            wloaderFiles++;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(diyPluginDir))
            {
                List<FileInfo> diyCommon = FastInitHost.SafeGetFiles(new DirectoryInfo(diyPluginDir), "common*");
                diyCommon.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (FileInfo file in diyCommon)
                {
                    if (string.Equals(file.Name, "common", StringComparison.OrdinalIgnoreCase)) continue;
                    if (OwnsSharedBundle(file.FullName)) continue;
                    string full = NormalizePath(file.FullName);
                    string existing;
                    if (queuedByName.TryGetValue(file.Name, out existing))
                    {
                        skipped++;
                        DuplicateCommonDialog.RecordDuplicate(file.Name, full, existing);
                        FastInitPlugin.Log.LogInfo("skip " + file.Name + ": duplicate of " + existing);
                        continue;
                    }
                    queuedByName[file.Name] = full;
                    batch.Add(file);
                    diyFiles++;
                }
            }

            _scheduledFiles.Clear();
            _scheduledFiles.AddRange(batch);
            _expectedCount = batch.Count;
            if (batch.Count == 0)
            {
                _drained = true;
                yield break;
            }

            List<FileInfo> readyNow = new List<FileInfo>();
            List<FileInfo> toConvert = new List<FileInfo>();
            List<KeyValuePair<string, AssetBundleCreateRequest>> externalPending =
                new List<KeyValuePair<string, AssetBundleCreateRequest>>();
            bool restore = FastInitPlugin.RestoreInfoFilesOnStartup;
            bool optimize = !restore && FastInitPlugin.OptimizeSharedCommonBundlesOnStartup;
            if (optimize)
            {
                string indexWarning;
                CatalogIndexStore.Default.EnsureLoaded(out indexWarning);
            }

            FastInitPlugin.Log.LogInfo("shared dependency bundles: " + batch.Count
                + " files (parallel: " + diyFiles + " DIYLevel, " + wloaderFiles + " WLoader)");
            foreach (FileInfo file in batch)
            {
                // Other loaders can finish a bundle before or during dispatch.
                if (IsResident(file.Name))
                {
                    skipped++;
                    MarkSettled(file.Name);
                    DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName);
                    FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                    continue;
                }
                AssetBundleCreateRequest extRequest;
                string extPath;
                if (DuplicateCommonDialog.TryGetPendingLoad(file.Name, out extRequest, out extPath))
                {
                    skipped++;
                    DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName, extPath);
                    FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loading by another plugin");
                    TrackExternalPending(externalPending, file.Name, extRequest);
                    continue;
                }
                if ((optimize || restore) && FileNeedsSharedWork(file, restore))
                    toConvert.Add(file);
                else
                    readyNow.Add(file);
            }

            List<FileInfo> pending = new List<FileInfo>();
            List<AssetBundleCreateRequest> requests = new List<AssetBundleCreateRequest>();
            List<bool> reaped = new List<bool>();
            int remaining = 0, loaded = 0, failed = 0;
            bool anyReplaced = false;
            Dictionary<string, FileInfo> convertingByPath = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

            float started = Time.realtimeSinceStartup;
            try
            {
                // Dispatch already-fast bundles immediately so warm launches incur zero wait.
                foreach (FileInfo file in readyNow)
                    DispatchSharedLoad(file, pending, requests, reaped, externalPending, ref remaining, ref skipped);

                if (toConvert.Count > 0 && !_shutdown)
                {
                    // Convert smallest files first so light packs finish within seconds while
                    // heavy commonW* packs stream concurrently behind them.
                    toConvert.Sort(CompareByLengthThenName);
                    string[] convertPaths = new string[toConvert.Count];
                    for (int i = 0; i < toConvert.Count; i++)
                    {
                        string full = NormalizePath(toConvert[i].FullName);
                        convertPaths[i] = full;
                        convertingByPath[full] = toConvert[i];
                        _convertingNames.Add(toConvert[i].Name);
                    }
                    _conversionRestore = restore;
                    _conversionStartedRealtime = Time.realtimeSinceStartup;
                    InfoBundleOptimizationBatch conversion = new InfoBundleOptimizationBatch(convertPaths, restore);
                    _conversionBatch = conversion;
                    FastInitPlugin.Log.LogInfo("shared dependency bundle " + (restore ? "restoration" : "LZ4HC optimization")
                        + " started: " + toConvert.Count + " file(s) (" + readyNow.Count + " already fast)");
                    conversion.Start();
                }

                float nextNotice = started + 15f;
                while (remaining > 0 || externalPending.Count > 0 || _convertingNames.Count > 0)
                {
                    InfoBundleOptimizationBatch conversion = _conversionBatch;
                    if (conversion != null)
                    {
                        DrainBatchLogs(conversion);
                        string settledPath;
                        InfoBundleOptimizer.Result settledResult;
                        while (conversion.TryDequeueSettled(out settledPath, out settledResult))
                        {
                            FileInfo convertedFile;
                            if (!convertingByPath.TryGetValue(settledPath, out convertedFile)) continue;
                            convertingByPath.Remove(settledPath);
                            _convertingNames.Remove(convertedFile.Name);
                            if (settledResult == InfoBundleOptimizer.Result.Replaced
                                || settledResult == InfoBundleOptimizer.Result.Restored)
                                anyReplaced = true;
                            if (!_shutdown)
                                DispatchSharedLoad(convertedFile, pending, requests, reaped, externalPending, ref remaining, ref skipped);
                        }
                        if (conversion.Done)
                        {
                            DrainBatchLogs(conversion);
                            if (!restore) RecordPreparedSharedStamps(conversion);
                            conversion.Dispose();
                            if (ReferenceEquals(_conversionBatch, conversion)) _conversionBatch = null;
                            if (convertingByPath.Count > 0)
                            {
                                foreach (FileInfo leftover in convertingByPath.Values)
                                {
                                    _convertingNames.Remove(leftover.Name);
                                    if (!_shutdown)
                                        DispatchSharedLoad(leftover, pending, requests, reaped, externalPending, ref remaining, ref skipped);
                                }
                                convertingByPath.Clear();
                            }
                            _convertingNames.Clear();
                            if (anyReplaced)
                            {
                                ThumbnailCache.Invalidate();
                                FastInitPlugin.Log.LogInfo("shared dependency bundle optimization updated source files; thumbnail stamp invalidated");
                            }
                        }
                    }

                    ReapExternalPending(externalPending);

                    for (int i = 0; i < requests.Count; i++)
                    {
                        if (reaped[i] || !requests[i].isDone) continue;
                        reaped[i] = true;
                        remaining--;
                        FileInfo file = pending[i];
                        _takeoverLoads.Remove(file.Name);
                        AssetBundle bundle = requests[i].assetBundle;
                        if (bundle == null)
                        {
                            // Completed failed requests can safely fall back to a sync load.
                            try { bundle = AssetBundle.LoadFromFile(file.FullName); }
                            catch (Exception e) { FastInitPlugin.Log.LogWarning("shared bundle sync retry failed [" + file.Name + "]: " + e.Message); }
                        }
                        if (bundle != null)
                        {
                            loaded++;
                            DuplicateCommonDialog.RecordLoaded(file.Name, file.FullName);
                        }
                        else failed++;
                        // Failed files leave the wait list too: a corrupt dependency cannot be
                        // fixed by waiting, and the recipe validation reports it precisely.
                        MarkSettled(file.Name);
                        FastInitPlugin.Log.LogInfo("shared bundle settled [" + file.Name + "]: "
                            + (int)((Time.realtimeSinceStartup - started) * 1000) + " ms, loaded=" + (bundle != null));
                    }
                    if (remaining == 0 && externalPending.Count == 0 && _convertingNames.Count == 0) break;
                    if (Time.realtimeSinceStartup >= nextNotice)
                    {
                        FastInitPlugin.Log.LogWarning("shared dependency bundles still pending after "
                            + (int)(Time.realtimeSinceStartup - started) + "s (" + (remaining + externalPending.Count) + " loading, "
                            + _convertingNames.Count + " optimizing); waiting without duplicate requests");
                        nextNotice += 15f;
                    }
                    yield return null;
                }
            }
            finally
            {
                InfoBundleOptimizationBatch conversion = _conversionBatch;
                if (conversion != null)
                {
                    DrainBatchLogs(conversion);
                    if (!restore && conversion.Done) RecordPreparedSharedStamps(conversion);
                    conversion.Dispose();
                    if (ReferenceEquals(_conversionBatch, conversion)) _conversionBatch = null;
                }
                _convertingNames.Clear();
                _drained = true;
                if (CatalogIndexStore.Default.IsDirty) LevelCatalogCache.FlushIndex();
            }
            FastInitPlugin.Log.LogInfo("shared dependency bundles: " + loaded + " loaded, " + skipped + " skipped (dup), "
                + failed + " failed (" + (int)((Time.realtimeSinceStartup - started) * 1000) + " ms)");
        }

        private static void DrainBatchLogs(InfoBundleOptimizationBatch conversion)
        {
            conversion.Drain(
                delegate(string message) { FastInitPlugin.Log.LogInfo(message); },
                delegate(string message) { FastInitPlugin.Log.LogDebug(message); });
        }

        private static void RecordPreparedSharedStamps(InfoBundleOptimizationBatch conversion)
        {
            foreach (InfoBundleOptimizationBatch.PreparedFile prepared in conversion.PreparedFiles)
            {
                CatalogIndexStore.Default.UpsertFastFile(
                    Path.GetDirectoryName(prepared.Path),
                    Path.GetFileName(prepared.Path),
                    prepared.Length,
                    prepared.Modified,
                    FastFileStamp.KindFastBundle);
            }
        }

        private static void TrackExternalPending(List<KeyValuePair<string, AssetBundleCreateRequest>> externalPending,
            string name, AssetBundleCreateRequest request)
        {
            if (request == null || request.isDone)
            {
                MarkSettled(name);
                return;
            }
            if (_entryWaiting) request.priority = 100;
            externalPending.Add(new KeyValuePair<string, AssetBundleCreateRequest>(name, request));
        }

        private static void ReapExternalPending(List<KeyValuePair<string, AssetBundleCreateRequest>> externalPending)
        {
            for (int i = externalPending.Count - 1; i >= 0; i--)
            {
                AssetBundleCreateRequest req = externalPending[i].Value;
                if (req != null && !req.isDone)
                {
                    if (_entryWaiting) req.priority = 100;
                    continue;
                }
                string name = externalPending[i].Key;
                externalPending.RemoveAt(i);
                MarkSettled(name);
            }
        }

        private static void DispatchSharedLoad(FileInfo file, List<FileInfo> pending, List<AssetBundleCreateRequest> requests,
            List<bool> reaped, List<KeyValuePair<string, AssetBundleCreateRequest>> externalPending,
            ref int remaining, ref int skipped)
        {
            if (_settled.Contains(file.Name)) return;
            if (IsResident(file.Name))
            {
                skipped++;
                MarkSettled(file.Name);
                DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName);
                FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                return;
            }
            AssetBundleCreateRequest extRequest;
            string extPath;
            if (DuplicateCommonDialog.TryGetPendingLoad(file.Name, out extRequest, out extPath))
            {
                skipped++;
                DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName, extPath);
                FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loading by another plugin");
                TrackExternalPending(externalPending, file.Name, extRequest);
                return;
            }
            AssetBundleCreateRequest request;
            try { request = AssetBundle.LoadFromFileAsync(file.FullName); }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("shared bundle request failed [" + file.Name + "]: " + e.Message);
                request = null;
            }
            if (request == null)
            {
                MarkSettled(file.Name);
                return;
            }
            if (_entryWaiting) request.priority = 100;
            _takeoverLoads[file.Name] = request;
            requests.Add(request);
            pending.Add(file);
            reaped.Add(false);
            remaining++;
        }

        private static bool FileNeedsSharedWork(FileInfo file, bool restore)
        {
            try
            {
                string path = file.FullName;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, ".diylevel-fastinit")))
                    return true;
                if (restore) return false;
                long length = file.Length;
                long modified = file.LastWriteTimeUtc.Ticks;
                byte cachedKind;
                if (CatalogIndexStore.Default.TryGetFastFile(dir, file.Name, length, modified, out cachedKind)
                    && cachedKind == FastFileStamp.KindFastBundle)
                    return false;
                if (BundleCodecs.UnityFsRepacker.NeedsConversion(path)) return true;
                CatalogIndexStore.Default.UpsertFastFile(dir, file.Name, length, modified, FastFileStamp.KindFastBundle);
                return false;
            }
            catch { return false; }
        }

        private static int CompareByLengthThenName(FileInfo a, FileInfo b)
        {
            long la = 0, lb = 0;
            try { la = a.Length; } catch { }
            try { lb = b.Length; } catch { }
            int cmp = la.CompareTo(lb);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<FileInfo> SharedFiles()
        {
            List<FileInfo> files = new List<FileInfo>();
            foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(_loaderDir), "commonW*"))
                if (IsCommonWName(file.Name)) files.Add(file);
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return files;
        }

        private static void CollectNames()
        {
            if (_namesRead) return;
            string loaderDir = ResolveLoadedLoaderDir();
            if (string.IsNullOrEmpty(loaderDir)) return;
            _namesRead = true;
            foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(loaderDir), "commonW*"))
                if (IsCommonWName(file.Name)) _names.Add(file.Name);
        }

        // Waits until every shared bundle has settled (no-op once startup drain completes).
        // Boosts in-flight requests to high priority and reports progress to the corner label.
        internal static IEnumerator AwaitAllReady(Action<int, int, float> progress, Func<bool> current)
        {
            if (AllReady) yield break;
            _entryWaiting = true;
            try
            {
                BoostActiveLoads();
                float start = Time.realtimeSinceStartup;
                float nextNotice = start + 5f;
                float timeout = FastInitPlugin.WLoaderGateTimeoutSeconds;
                int settled, total;
                float fraction = ReadOverallProgress(out settled, out total);
                float conversionWork = _conversionBatch != null ? _conversionBatch.OverallWorkUnits : 0f;
                float lastProgress = start;
                if (progress != null && total > 0) progress(settled, total, fraction);
                while (!AllReady)
                {
                    if (current != null && !current()) yield break;
                    int nowSettled, nowTotal;
                    float nowFraction = ReadOverallProgress(out nowSettled, out nowTotal);
                    bool converting = IsConvertingSharedBundles;
                    float nowConversionWork = converting && _conversionBatch != null ? _conversionBatch.OverallWorkUnits : conversionWork;
                    if (nowSettled != settled || nowTotal != total
                        || nowFraction > fraction + 0.01f || nowConversionWork > conversionWork + 0.01f)
                    {
                        bool uiChanged = nowSettled != settled || nowTotal != total || nowFraction > fraction + 0.01f;
                        settled = nowSettled;
                        total = nowTotal;
                        fraction = nowFraction;
                        conversionWork = nowConversionWork;
                        lastProgress = Time.realtimeSinceStartup;
                        if (uiChanged && progress != null && total > 0) progress(settled, total, fraction);
                    }
                    // First-launch LZ4HC conversion of heavy commonW* packs can legitimately run
                    // longer than the default 60s load timeout on slower CPUs; allow up to 300s
                    // while actively converting (no-progress stalls still trip at 20s).
                    float effectiveTimeout = converting ? Math.Max(timeout, 300f) : timeout;
                    if (Time.realtimeSinceStartup - start >= effectiveTimeout)
                    {
                        FastInitPlugin.Log.LogWarning("shared dependency wait timed out after " + (int)effectiveTimeout
                            + "s (" + settled + "/" + total + " ready at " + Percent(fraction) + "); giving way");
                        yield break;
                    }
                    if (Time.realtimeSinceStartup - lastProgress >= StallSeconds)
                    {
                        FastInitPlugin.Log.LogWarning("shared dependency bundles stopped progressing at " + settled + "/"
                            + total + " ready (" + Percent(fraction) + ") for " + (int)StallSeconds + "s; giving way");
                        yield break;
                    }
                    if (Time.realtimeSinceStartup >= nextNotice)
                    {
                        FastInitPlugin.Log.LogInfo("waiting for shared dependency bundles: " + settled + "/" + total
                            + " ready (" + Percent(fraction) + " of the rest)");
                        nextNotice += 5f;
                    }
                    yield return null;
                }
                FastInitPlugin.Log.LogInfo("shared dependency bundles ready after " + (int)((Time.realtimeSinceStartup - start) * 1000) + " ms");
            }
            finally { _entryWaiting = false; }
        }

        // Synchronous fallback entry path: waits for any active shared conversion batch
        // before joining in-flight async loads or synchronously loading remaining files.
        internal static void EnsureAllReadySync()
        {
            if (AllReady) return;
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            if (conversion != null && !conversion.Done) conversion.WaitUntilDone();
            for (int i = 0; i < _scheduledFiles.Count; i++)
                EnsureSingleSync(_scheduledFiles[i].Name, _scheduledFiles[i].FullName);
            if (_mode == Mode.Observe && _loaderDir != null && ReadDepState() != 0)
            {
                for (int i = 0; i < _names.Count; i++)
                    EnsureSingleSync(_names[i], Path.Combine(_loaderDir, _names[i]));
            }
        }

        private static void EnsureSingleSync(string name, string path)
        {
            if (IsResident(name) || _settled.Contains(name)) return;
            AssetBundle bundle = null;
            AssetBundleCreateRequest request;
            string extPath;
            if (_takeoverLoads.TryGetValue(name, out request)
                || DuplicateCommonDialog.TryGetPendingLoad(name, out request, out extPath))
            {
                try { bundle = request.assetBundle; }
                catch (Exception e) { FastInitPlugin.Log.LogWarning("sync join for shared bundle failed [" + name + "]: " + e.Message); }
            }
            if (bundle == null && !IsResident(name))
            {
                try { bundle = AssetBundle.LoadFromFile(path); }
                catch (Exception e) { FastInitPlugin.Log.LogWarning("sync load for shared bundle failed [" + name + "]: " + e.Message); }
            }
            if (bundle != null) DuplicateCommonDialog.RecordLoaded(name, path);
            MarkSettled(name);
        }

        private static void BoostActiveLoads()
        {
            foreach (AssetBundleCreateRequest request in _takeoverLoads.Values)
                if (request != null && !request.isDone) request.priority = 100;
            if (_mode == Mode.Observe)
            {
                for (int i = 0; i < _names.Count; i++)
                {
                    AssetBundleCreateRequest extRequest;
                    string extPath;
                    if (DuplicateCommonDialog.TryGetPendingLoad(_names[i], out extRequest, out extPath)
                        && extRequest != null && !extRequest.isDone)
                        extRequest.priority = 100;
                }
            }
        }

        private static float ReadOverallProgress(out int settled, out int total)
        {
            int observeTotal = (_mode == Mode.Observe && _names.Count > 0 && ReadDepState() != 0) ? _names.Count : 0;
            int observeSettled = observeTotal > 0 ? ResidentCount() : 0;
            total = _expectedCount + observeTotal;
            settled = _settled.Count + observeSettled;
            if (settled > total) settled = total;

            int remaining = total - settled;
            if (remaining <= 0) return 1f;

            float work = 0f;
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            if (conversion != null && !conversion.Done)
                work += Math.Max(0f, conversion.OverallWorkUnits - conversion.Completed) * 0.95f;
            foreach (AssetBundleCreateRequest request in _takeoverLoads.Values)
                if (request != null && !request.isDone) work += request.progress;
            if (observeTotal > 0)
            {
                for (int i = 0; i < _names.Count; i++)
                {
                    AssetBundleCreateRequest extRequest;
                    string extPath;
                    if (DuplicateCommonDialog.TryGetPendingLoad(_names[i], out extRequest, out extPath)
                        && extRequest != null && !extRequest.isDone)
                        work += extRequest.progress;
                }
            }
            float fraction = work / remaining;
            return fraction < 0f ? 0f : (fraction > 0.99f ? 0.99f : fraction);
        }

        private static int ObservePendingCount()
        {
            if (_mode != Mode.Observe || _names.Count == 0 || ReadDepState() == 0) return 0;
            int pending = _names.Count - ResidentCount();
            return pending > 0 ? pending : 0;
        }

        private static string Percent(float fraction)
        {
            return (int)(fraction * 100f) + "%";
        }

        internal static int ResidentCount()
        {
            int count = 0;
            foreach (string name in _names)
                if (IsResident(name)) count++;
            return count;
        }

        private static bool IsResident(string name)
        {
            foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                if (string.Equals(ab.name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string StateDescription()
        {
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            string shared = (_drained ? "settled" : "pending") + " (" + _settled.Count + "/" + _expectedCount + " files"
                + (conversion != null && !conversion.Done ? ", optimizing " + conversion.Completed + "/" + conversion.Total : "") + ")";
            if (_mode == Mode.Observe)
                return shared + "; WLoader observing " + ResidentCount() + "/" + _names.Count + " resident";
            return shared;
        }

        private static int ReadDepState()
        {
            try
            {
                object value = _depStateField != null ? _depStateField.GetValue(null) : null;
                return value is int ? (int)value : -1;
            }
            catch { return -1; }
        }

        // Drops the probe suppression; the web loader loads by itself again.
        internal static void Release(string reason)
        {
            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); } catch { }
                _harmony = null;
            }
            _mode = Mode.Observe;
            FastInitPlugin.Log.LogInfo("web loader shared bundle takeover released: " + reason);
        }
    }
}
