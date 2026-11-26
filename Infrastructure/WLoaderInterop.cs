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
    /// Takes over the web loader's commonW* shared dependency bundles: its enqueue
    /// probe (IsBundleLoaded) reports them as resident, LoadHeavy loads them with
    /// FastInit scheduling, and level preparation can wait for them to settle.
    /// </summary>
    internal static class WLoaderInterop
    {
        private enum Mode { Off, Observe, Takeover }

        private const string LoaderTypeName = "LevelRuntimeLoader";

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
        // Live parallel background requests by bundle name; entry raises the priority of a level's ones.
        private static readonly Dictionary<string, AssetBundleCreateRequest> _takeoverLoads =
            new Dictionary<string, AssetBundleCreateRequest>(StringComparer.OrdinalIgnoreCase);
        // Entry-driven loads for needed bundles nothing else is loading (a failed or
        // unscheduled startup load); completion marks them settled either way.
        private static readonly Dictionary<string, AssetBundleCreateRequest> _healLoads =
            new Dictionary<string, AssetBundleCreateRequest>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _healed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // External CAB name -> normalized dependency bundle file (WLoader commonW* and DIYLevel common*).
        private static readonly Dictionary<string, string> _cabBundles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Normalized shared bundle path -> outgoing CAB references cached before in-place conversion
        // so NeededBundles never has to open a shared bundle while InfoBundleOptimizer replaces it.
        private static readonly Dictionary<string, string[]> _sharedBundleRefs =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _indexedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Bundles whose headers could not be indexed; conservatively included in NeededBundles.
        private static readonly List<string> _unindexedBundles = new List<string>();
        private static bool _cabsIndexed;
        // Background LZ4HC conversion batch for shared common* / commonW* bundles; each bundle
        // streams directly into AssetBundle.LoadFromFileAsync as soon as its replacement settles.
        private static InfoBundleOptimizationBatch _conversionBatch;
        private static readonly HashSet<string> _convertingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _prioritizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static float _conversionStartedRealtime;
        private static bool _conversionRestore;
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

        internal static void Shutdown()
        {
            _shutdown = true;
            InfoBundleOptimizationBatch batch = _conversionBatch;
            if (batch != null) batch.Dispose();
            _convertingNames.Clear();
            _prioritizedNames.Clear();
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
        // FastInitHost skips these files in its serial common* loop so WLoaderInterop
        // (or WLoader itself when takeover is disabled) owns them without stalling
        // catalog startup or triggering false duplicate-bundle warnings.
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

            _expectedCount = batch.Count;
            if (batch.Count == 0)
            {
                _drained = true;
                yield break;
            }

            List<FileInfo> readyNow = new List<FileInfo>();
            List<FileInfo> toConvert = new List<FileInfo>();
            bool restore = FastInitPlugin.RestoreInfoFilesOnStartup;
            bool optimize = !restore && FastInitPlugin.OptimizeSharedCommonBundlesOnStartup;

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
                if (_healLoads.ContainsKey(file.Name)) continue;
                AssetBundleCreateRequest extRequest;
                string extPath;
                if (DuplicateCommonDialog.TryGetPendingLoad(file.Name, out extRequest, out extPath))
                {
                    skipped++;
                    DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName, extPath);
                    FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loading by another plugin");
                    continue;
                }
                if ((optimize || restore) && FileNeedsSharedWork(file.FullName, restore))
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
                    DispatchSharedLoad(file, pending, requests, reaped, ref remaining, ref skipped);

                if (toConvert.Count > 0 && !_shutdown)
                {
                    // Mark converting names synchronously BEFORE the first yield so entry's
                    // RequestMissing can never race PreIndexSharedBundles on an LZMA file.
                    for (int i = 0; i < toConvert.Count; i++)
                        _convertingNames.Add(toConvert[i].Name);

                    // Index every shared bundle's CAB names and outgoing CAB edges BEFORE
                    // starting in-place replacement so NeededBundles reads from memory and
                    // never contends with File.Replace on disk.
                    yield return PreIndexSharedBundles(diyPluginDir);
                    if (_shutdown) yield break;

                    // Convert smallest files first so light shared bundles become ready within
                    // seconds while heavy commonW* packs stream concurrently behind them.
                    toConvert.Sort(CompareByLengthThenName);
                    string[] convertPaths = new string[toConvert.Count];
                    for (int i = 0; i < toConvert.Count; i++)
                    {
                        string full = NormalizePath(toConvert[i].FullName);
                        convertPaths[i] = full;
                        convertingByPath[full] = toConvert[i];
                    }
                    _conversionRestore = restore;
                    _conversionStartedRealtime = Time.realtimeSinceStartup;
                    InfoBundleOptimizationBatch conversion = new InfoBundleOptimizationBatch(convertPaths, restore);
                    _conversionBatch = conversion;
                    if (_prioritizedNames.Count > 0)
                    {
                        List<string> urgent = new List<string>();
                        foreach (FileInfo file in toConvert)
                            if (_prioritizedNames.Contains(file.Name)) urgent.Add(file.FullName);
                        if (urgent.Count > 0) conversion.Prioritize(urgent);
                    }
                    FastInitPlugin.Log.LogInfo("shared dependency bundle " + (restore ? "restoration" : "LZ4HC optimization")
                        + " started: " + toConvert.Count + " file(s) (" + readyNow.Count + " already fast)");
                    conversion.Start();
                }

                float nextNotice = started + 15f;
                while (remaining > 0 || _convertingNames.Count > 0)
                {
                    InfoBundleOptimizationBatch conversion = _conversionBatch;
                    if (conversion != null)
                    {
                        conversion.Drain(delegate(string message) { FastInitPlugin.Log.LogInfo(message); });
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
                                DispatchSharedLoad(convertedFile, pending, requests, reaped, ref remaining, ref skipped);
                        }
                        if (conversion.Done)
                        {
                            conversion.Drain(delegate(string message) { FastInitPlugin.Log.LogInfo(message); });
                            conversion.Dispose();
                            if (ReferenceEquals(_conversionBatch, conversion)) _conversionBatch = null;
                            if (convertingByPath.Count > 0)
                            {
                                foreach (FileInfo leftover in convertingByPath.Values)
                                {
                                    _convertingNames.Remove(leftover.Name);
                                    if (!_shutdown)
                                        DispatchSharedLoad(leftover, pending, requests, reaped, ref remaining, ref skipped);
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
                    if (remaining == 0 && _convertingNames.Count == 0) break;
                    if (Time.realtimeSinceStartup >= nextNotice)
                    {
                        FastInitPlugin.Log.LogWarning("shared dependency bundles still pending after "
                            + (int)(Time.realtimeSinceStartup - started) + "s (" + remaining + " loading, "
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
                    conversion.Drain(delegate(string message) { FastInitPlugin.Log.LogInfo(message); });
                    conversion.Dispose();
                    if (ReferenceEquals(_conversionBatch, conversion)) _conversionBatch = null;
                }
                _convertingNames.Clear();
                _drained = true;
            }
            FastInitPlugin.Log.LogInfo("shared dependency bundles: " + loaded + " loaded, " + skipped + " skipped (dup), "
                + failed + " failed (" + (int)((Time.realtimeSinceStartup - started) * 1000) + " ms)");
        }

        private static void DispatchSharedLoad(FileInfo file, List<FileInfo> pending, List<AssetBundleCreateRequest> requests,
            List<bool> reaped, ref int remaining, ref int skipped)
        {
            if (IsResident(file.Name))
            {
                skipped++;
                MarkSettled(file.Name);
                DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName);
                FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                return;
            }
            if (_healLoads.ContainsKey(file.Name)) return;
            AssetBundleCreateRequest extRequest;
            string extPath;
            if (DuplicateCommonDialog.TryGetPendingLoad(file.Name, out extRequest, out extPath))
            {
                skipped++;
                DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName, extPath);
                FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loading by another plugin");
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
            if (_prioritizedNames.Contains(file.Name)) request.priority = 100;
            _takeoverLoads[file.Name] = request;
            requests.Add(request);
            pending.Add(file);
            reaped.Add(false);
            remaining++;
        }

        private static bool FileNeedsSharedWork(string path, bool restore)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, ".diylevel-fastinit")))
                    return true;
                if (restore) return false;
                return BundleCodecs.UnityFsRepacker.NeedsConversion(path);
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

        private static IEnumerator PreIndexSharedBundles(string diyPluginDir)
        {
            if (_cabsIndexed) yield break;
            CollectNames();
            List<string> toIndex = new List<string>();
            if (_loaderDir != null)
                foreach (string name in _names) toIndex.Add(Path.Combine(_loaderDir, name));
            if (!string.IsNullOrEmpty(diyPluginDir))
                foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(diyPluginDir), "common*"))
                    if (!string.Equals(file.Name, "common", StringComparison.OrdinalIgnoreCase))
                        toIndex.Add(file.FullName);

            System.Diagnostics.Stopwatch slice = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < toIndex.Count; i++)
            {
                if (_shutdown) yield break;
                IndexCabinets(toIndex[i]);
                if (slice.Elapsed.TotalMilliseconds >= 4)
                {
                    yield return null;
                    slice.Reset();
                    slice.Start();
                }
            }
            _cabsIndexed = true;
        }

        // No fixed order: every request is issued in the same frame, so all packs load
        // concurrently and whichever pack a level needs is already under way.
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

        // Transitive closure of shared dependency bundles (DIYLevel common0* and WLoader
        // commonW*) referenced by a level's info bundles, cross-set info dependencies,
        // scene bundle, and any shared bundle -> shared bundle edges (e.g. common03 ->
        // common01 -> common01_textures, commonW3 -> commonW1 -> commonW2). Falls back
        // to every known shared pack when the reference scan is unavailable.
        internal static HashSet<string> NeededBundles(IEnumerable<string> seedBundles, string sceneFile)
        {
            HashSet<string> needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IndexBundleCabinets()) return AllSharedPacks();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> queue = new Queue<string>();
            if (seedBundles != null)
            {
                foreach (string seed in seedBundles)
                {
                    if (string.IsNullOrEmpty(seed) || !File.Exists(seed)) continue;
                    string full = NormalizePath(seed);
                    if (visited.Add(full)) queue.Enqueue(full);
                }
            }
            if (!string.IsNullOrEmpty(sceneFile) && File.Exists(sceneFile))
            {
                string fullScene = NormalizePath(sceneFile);
                if (visited.Add(fullScene)) queue.Enqueue(fullScene);
            }
            try
            {
                while (queue.Count != 0)
                {
                    string current = queue.Dequeue();
                    string[] references;
                    if (!_sharedBundleRefs.TryGetValue(current, out references))
                        references = InfoBundleGraph.BundleReferences(current);
                    foreach (string cabinet in references)
                    {
                        string target;
                        if (cabinet == null || !_cabBundles.TryGetValue(cabinet, out target)) continue;
                        if (needed.Add(target) && visited.Add(target))
                            queue.Enqueue(target);
                    }
                }
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("dependency bundle reference scan unavailable; waiting for every shared pack: " + e.Message);
                return AllSharedPacks();
            }
            // Conservatively include any shared bundle whose own directory table could not be indexed.
            for (int i = 0; i < _unindexedBundles.Count; i++) needed.Add(_unindexedBundles[i]);
            return needed;
        }

        private static HashSet<string> AllSharedPacks()
        {
            CollectNames();
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_loaderDir != null)
            {
                foreach (string name in _names)
                {
                    string full = NormalizePath(Path.Combine(_loaderDir, name));
                    if (seenNames.Add(name)) paths.Add(full);
                }
            }
            string diy = FastInitPlugin.GetDiyPluginDir();
            if (!string.IsNullOrEmpty(diy))
            {
                foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(diy), "common*"))
                {
                    if (string.Equals(file.Name, "common", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seenNames.Add(file.Name)) paths.Add(NormalizePath(file.FullName));
                }
            }
            return paths;
        }

        // Reads each dependency bundle's directory and outgoing CAB references once.
        // WLoader packs are indexed first so cross-directory duplicates attribute to
        // WLoader's authoritative copy.
        private static bool IndexBundleCabinets()
        {
            if (_cabsIndexed) return _cabBundles.Count != 0;
            _cabsIndexed = true;
            CollectNames();
            if (_loaderDir != null)
                foreach (string name in _names) IndexCabinets(Path.Combine(_loaderDir, name));
            string diy = FastInitPlugin.GetDiyPluginDir();
            if (!string.IsNullOrEmpty(diy))
                foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(diy), "common*"))
                    if (!string.Equals(file.Name, "common", StringComparison.OrdinalIgnoreCase))
                        IndexCabinets(file.FullName);
            return _cabBundles.Count != 0;
        }

        private static void IndexCabinets(string path)
        {
            string normalized = NormalizePath(path);
            string name = Path.GetFileName(normalized);
            if (!_indexedPaths.Add(normalized) || !_indexedNames.Add(name)) return;
            try
            {
                foreach (string cabinet in InfoBundleGraph.CabinetNames(normalized))
                    if (!_cabBundles.ContainsKey(cabinet)) _cabBundles[cabinet] = normalized;
                _sharedBundleRefs[normalized] = InfoBundleGraph.BundleReferences(normalized);
            }
            catch (Exception e)
            {
                _unindexedBundles.Add(normalized);
                FastInitPlugin.Log.LogWarning("dependency bundle index unavailable [" + name + "]: " + e.Message);
            }
        }

        internal static bool AllSettled(HashSet<string> bundles)
        {
            return bundles == null || bundles.Count == 0 || Settled(bundles);
        }

        // Synchronous fallback entry path: waits for any in-flight conversion of a needed
        // file before joining its async load or loading synchronously.
        internal static void EnsureBundlesSync(HashSet<string> bundles)
        {
            if (bundles == null || bundles.Count == 0) return;
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            if (conversion != null && !conversion.Done) conversion.Prioritize(bundles);
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                if (IsSettled(name)) continue;
                if (conversion != null && conversion.IsPending(path))
                    conversion.WaitUntilSettled(path);
                if (IsSettled(name)) continue;
                AssetBundle bundle = null;
                AssetBundleCreateRequest request;
                string extPath;
                if (_takeoverLoads.TryGetValue(name, out request)
                    || _healLoads.TryGetValue(name, out request)
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
        }

        internal static string DescribeBundles(HashSet<string> bundles)
        {
            List<string> names = new List<string>();
            foreach (string path in bundles) names.Add(Path.GetFileName(path));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", names.ToArray());
        }

        // A bundle load that makes no progress for this long is treated as stalled so
        // the entry fails with a precise message instead of hanging indefinitely.
        private const float StallSeconds = 20f;

        // Waits until the needed bundles settle, the configured timeout elapses, or their
        // loading progress stops moving. Still-pending needed requests get their priority
        // raised first, and a needed bundle nothing is loading is requested immediately,
        // so entering a level pushes exactly its own bundles ahead of the rest.
        internal static IEnumerator AwaitBundles(HashSet<string> bundles, Action<int, float> progress, Func<bool> current)
        {
            if (bundles == null || bundles.Count == 0) yield break;
            ReapHealLoads(bundles);
            int prioritized = Prioritize(bundles);
            int requested = RequestMissing(bundles);
            if (prioritized != 0 || requested != 0)
                FastInitPlugin.Log.LogInfo("dependency bundles for entry: " + DescribeBundles(bundles)
                    + " (" + prioritized + " prioritized, " + requested + " requested now)");
            float start = Time.realtimeSinceStartup;
            float nextNotice = start + 5f;
            float timeout = FastInitPlugin.WLoaderGateTimeoutSeconds;
            int settled = SettledCount(bundles);
            float fraction = PendingFraction(bundles);
            float conversionWork = _conversionBatch != null ? _conversionBatch.OverallWorkUnits : 0f;
            float lastProgress = start;
            if (progress != null) progress(settled, fraction);
            while (!Settled(bundles))
            {
                if (current != null && !current()) yield break;
                ReapHealLoads(bundles);
                int nowSettled = SettledCount(bundles);
                float nowFraction = PendingFraction(bundles);
                bool convertingNeeded = AnyConverting(bundles);
                float nowConversionWork = convertingNeeded && _conversionBatch != null ? _conversionBatch.OverallWorkUnits : conversionWork;
                // A long decode still reports a rising request progress, so a cold disk
                // is not mistaken for a stalled load; likewise active conversion progress
                // in the shared batch keeps the stall timer alive while queued files wait.
                if (nowSettled != settled || nowFraction > fraction + 0.01f || nowConversionWork > conversionWork + 0.01f)
                {
                    bool uiChanged = nowSettled != settled || nowFraction > fraction + 0.01f;
                    settled = nowSettled;
                    fraction = nowFraction;
                    conversionWork = nowConversionWork;
                    lastProgress = Time.realtimeSinceStartup;
                    if (uiChanged && progress != null) progress(settled, fraction);
                }
                // First-launch LZ4HC conversion of heavy commonW* packs can legitimately run
                // longer than the default 60s load timeout on slower CPUs; allow up to 300s
                // while needed bundles are actively converting (no-progress stalls still trip at 20s).
                float effectiveTimeout = convertingNeeded ? Math.Max(timeout, 300f) : timeout;
                if (Time.realtimeSinceStartup - start >= effectiveTimeout)
                {
                    FastInitPlugin.Log.LogWarning("dependency bundle wait timed out after " + (int)effectiveTimeout
                        + "s (" + settled + "/" + bundles.Count + " ready at " + Percent(fraction) + "); giving way");
                    yield break;
                }
                if (Time.realtimeSinceStartup - lastProgress >= StallSeconds)
                {
                    FastInitPlugin.Log.LogWarning("dependency bundles stopped progressing at " + settled + "/"
                        + bundles.Count + " ready (" + Percent(fraction) + ") for " + (int)StallSeconds + "s; giving way");
                    yield break;
                }
                if (Time.realtimeSinceStartup >= nextNotice)
                {
                    FastInitPlugin.Log.LogInfo("waiting for dependency bundles: " + settled + "/" + bundles.Count
                        + " ready (" + Percent(fraction) + " of the rest)");
                    nextNotice += 5f;
                }
                yield return null;
            }
            FastInitPlugin.Log.LogInfo("dependency bundles ready after " + (int)((Time.realtimeSinceStartup - start) * 1000) + " ms");
        }

        private static string Percent(float fraction)
        {
            return (int)(fraction * 100f) + "%";
        }

        private static int Prioritize(HashSet<string> bundles)
        {
            int boosted = 0;
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                _prioritizedNames.Add(name);
                if (Boost(_takeoverLoads, name) || Boost(_healLoads, name))
                {
                    boosted++;
                    continue;
                }
                AssetBundleCreateRequest extRequest;
                string extPath;
                if (DuplicateCommonDialog.TryGetPendingLoad(name, out extRequest, out extPath))
                {
                    extRequest.priority = 100;
                    boosted++;
                }
            }
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            if (conversion != null && !conversion.Done)
                boosted += conversion.Prioritize(bundles);
            return boosted;
        }

        private static bool IsConverting(string name, string path)
        {
            if (_convertingNames.Contains(name)) return true;
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            return conversion != null && conversion.IsPending(path);
        }

        private static bool AnyConverting(HashSet<string> bundles)
        {
            if (bundles == null) return false;
            foreach (string path in bundles)
                if (IsConverting(Path.GetFileName(path), path)) return true;
            return false;
        }

        private static bool Boost(Dictionary<string, AssetBundleCreateRequest> loads, string name)
        {
            AssetBundleCreateRequest request;
            if (!loads.TryGetValue(name, out request) || request.isDone) return false;
            request.priority = 100;
            return true;
        }

        // Requests a needed bundle nothing is loading (an unscheduled or failed startup
        // load, for example); it stays resident for the session.
        private static int RequestMissing(HashSet<string> bundles)
        {
            int requested = 0;
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                AssetBundleCreateRequest extRequest;
                string extPath;
                if (IsSettled(name) || IsConverting(name, path)
                    || _takeoverLoads.ContainsKey(name) || _healLoads.ContainsKey(name)
                    || DuplicateCommonDialog.TryGetPendingLoad(name, out extRequest, out extPath)) continue;
                try
                {
                    AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(path);
                    if (request != null)
                    {
                        request.priority = 100;
                        _healLoads[name] = request;
                        requested++;
                    }
                }
                catch (Exception e)
                {
                    FastInitPlugin.Log.LogWarning("dependency bundle request failed [" + name + "]: " + e.Message);
                }
            }
            return requested;
        }

        private static void ReapHealLoads(HashSet<string> bundles)
        {
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                AssetBundleCreateRequest request;
                if (!_healLoads.TryGetValue(name, out request) || !request.isDone) continue;
                _healLoads.Remove(name);
                AssetBundle bundle = request.assetBundle;
                if (bundle != null) DuplicateCommonDialog.RecordLoaded(name, path);
                if (_healed.Add(name)) SettledRevision++;
                FastInitPlugin.Log.LogInfo("dependency bundle loaded for entry [" + name + "]: loaded=" + (bundle != null));
            }
        }

        // Average progress of the needed bundles that are still converting or loading; 0 when none are.
        private static float PendingFraction(HashSet<string> bundles)
        {
            float sum = 0f;
            int count = 0;
            InfoBundleOptimizationBatch conversion = _conversionBatch;
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                if (IsSettled(name)) continue;
                if (IsConverting(name, path))
                {
                    sum += (conversion != null ? conversion.GetProgress(path) : 0f) * 0.95f;
                    count++;
                    continue;
                }
                if (AddFraction(_takeoverLoads, name, ref sum, ref count)) continue;
                if (AddFraction(_healLoads, name, ref sum, ref count)) continue;
                AssetBundleCreateRequest extRequest;
                string extPath;
                if (DuplicateCommonDialog.TryGetPendingLoad(name, out extRequest, out extPath))
                {
                    sum += extRequest.progress;
                    count++;
                }
            }
            return count == 0 ? 0f : sum / count;
        }

        private static bool AddFraction(Dictionary<string, AssetBundleCreateRequest> loads, string name, ref float sum, ref int count)
        {
            AssetBundleCreateRequest request;
            if (!loads.TryGetValue(name, out request) || request.isDone) return false;
            sum += request.progress;
            count++;
            return true;
        }

        private static bool Settled(HashSet<string> bundles)
        {
            foreach (string path in bundles)
                if (!IsSettled(Path.GetFileName(path))) return false;
            return true;
        }

        private static int SettledCount(HashSet<string> bundles)
        {
            int count = 0;
            foreach (string path in bundles)
                if (IsSettled(Path.GetFileName(path))) count++;
            return count;
        }

        private static bool IsSettled(string name)
        {
            return IsResident(name) || _settled.Contains(name) || _healed.Contains(name);
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
