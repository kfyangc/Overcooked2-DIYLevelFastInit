using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{

    internal class FastInitHost : MonoBehaviour
    {
        private const float AsyncTimeoutSeconds = 15f;

        private bool _finished;

        public void Run(string pluginDir)
        {
            StartCoroutine(Guard(LoadHeavy(pluginDir), "initial catalog load"));
        }

        internal static IEnumerator Guard(IEnumerator routine, string what)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(routine);
            try
            {
                while (stack.Count != 0)
                {
                    IEnumerator current = stack.Peek();
                    bool next = false;
                    object value = null;
                    Exception failure = null;
                    try
                    {
                        next = current.MoveNext();
                        if (next) value = current.Current;
                    }
                    catch (Exception e) { failure = e; }
                    if (failure != null)
                    {
                        FastInitPlugin.Log.LogError(what + " failed: " + failure);
                        yield break;
                    }
                    if (!next)
                    {
                        stack.Pop();
                        IDisposable disposable = current as IDisposable;
                        if (disposable != null) disposable.Dispose();
                        continue;
                    }
                    IEnumerator nested = value as IEnumerator;
                    if (nested != null) stack.Push(nested);
                    else yield return value;
                }
            }
            finally
            {
                while (stack.Count != 0)
                {
                    IDisposable disposable = stack.Pop() as IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
            }
        }

        private void OnDestroy()
        {
            if (!_finished)
            {
                FastInitPlugin.Log.LogWarning("FastInitHost destroyed before loading finished — coroutine was killed by something.");
            }
        }

        internal static List<FileInfo> SafeGetFiles(DirectoryInfo dir, string pattern)
        {
            try
            {
                return new List<FileInfo>(dir.GetFiles(pattern));
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("listing " + dir.FullName + ": " + e.Message);
                return new List<FileInfo>();
            }
        }

        internal static List<DirectoryInfo> SafeGetDirs(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return new List<DirectoryInfo>();
                }
                return new List<DirectoryInfo>(new DirectoryInfo(path).GetDirectories());
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("listing " + path + ": " + e.Message);
                return new List<DirectoryInfo>();
            }
        }

        // Unity requests cannot be cancelled; keep waiting rather than racing a duplicate load.
        internal static IEnumerator WaitOp(AsyncOperation op, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup - start > AsyncTimeoutSeconds)
                {
                    FastInitPlugin.Log.LogWarning(what + ": async still pending after " + AsyncTimeoutSeconds + "s; waiting without a duplicate request");
                    start = Time.realtimeSinceStartup;
                }
                yield return null;
            }
        }

        // A completed failed request can safely fall back to a synchronous load.
        internal static IEnumerator FinishBundleLoad(AssetBundleCreateRequest req, string path, string what, Box<AssetBundle> result)
        {
            yield return WaitOp(req, what);
            AssetBundle bundle = req.assetBundle;
            if (bundle == null)
            {
                FastInitPlugin.Log.LogWarning(what + ": async failed (" + (req.isDone ? "null bundle" : "timeout") + "), trying sync");
                bundle = AssetBundle.LoadFromFile(path);
            }
            result.Value = bundle;
        }

        // Start an async file load and await it (see FinishBundleLoad).
        internal static IEnumerator LoadBundleFile(string path, string what, Box<AssetBundle> result)
        {
            return FinishBundleLoad(AssetBundle.LoadFromFileAsync(path), path, what, result);
        }

        // Async LevelSetInfo load with a sync LoadAsset fallback; null on failure.
        internal static IEnumerator LoadInfoAsset(AssetBundle bundle, string what, Box<LevelSetInfoSO> result)
        {
            AssetBundleRequest ar = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
            yield return WaitOp(ar, what);
            LevelSetInfoSO so = ar.isDone ? ar.asset as LevelSetInfoSO : null;
            if (so == null)
            {
                FastInitPlugin.Log.LogWarning(what + ": LoadAssetAsync incomplete, trying sync LoadAsset");
                so = bundle.LoadAsset("LevelSetInfo", typeof(LevelSetInfoSO)) as LevelSetInfoSO;
            }
            result.Value = so;
        }

        private static void FlushPendingMenu(ref bool menuDirty, ref bool menuNeedsHeal,
            System.Diagnostics.Stopwatch phase, StartupTimings timings)
        {
            if (!menuDirty) return;
            double menuStarted = phase.Elapsed.TotalMilliseconds;
            if (menuNeedsHeal) FastInitPlugin.HealLevelSetButtons();
            else FastInitPlugin.NotifySetAdded();
            timings.MenuMs += phase.Elapsed.TotalMilliseconds - menuStarted;
            menuDirty = false;
            menuNeedsHeal = false;
        }

        private IEnumerator LoadHeavy(string pluginDir)
        {
            FastInitPlugin.Loading = true;
            System.Diagnostics.Stopwatch startup = System.Diagnostics.Stopwatch.StartNew();
            StartupTimings timings = new StartupTimings();
            double preparationMs = 0, commonMs = 0, catalogMs = 0;
            bool complete = false;
            CatalogMetadataPool metadataPool = null;
            try
            {
                FastInitPlugin.Log.LogInfo("LoadHeavy started");
                // Load DIYLevel common* (excluding "common") and WLoader commonW* shared
                // dependency bundles concurrently on a background coroutine with equal
                // priority so catalog publication is never blocked; level entry computes
                // each level's transitive dependency closure and prioritizes/awaits only
                // the bundles that level needs (WLoaderInterop).
                System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
                StartCoroutine(Guard(WLoaderInterop.LoadSharedBundles(pluginDir), "shared dependency bundles"));
                commonMs = phase.Elapsed.TotalMilliseconds;

                phase.Reset();
                phase.Start();
                System.Diagnostics.Stopwatch slice = System.Diagnostics.Stopwatch.StartNew();
                string levelsDir = Path.Combine(pluginDir, "levels");
                bool convertFiles = FastInitPlugin.OptimizeInfoFilesOnStartup || FastInitPlugin.RestoreInfoFilesOnStartup;
                StartupCatalogScan scan = new StartupCatalogScan(levelsDir,
                    FastInitPlugin.OptimizeInfoFilesOnStartup, FastInitPlugin.RestoreInfoFilesOnStartup);
                FastInitPlugin._startupScan = scan;
                StartupCatalogScan.ScannedSet[] scannedSets;
                try
                {
                    scan.Start();
                    while (!scan.Done) yield return null;
                    if (scan.Cancelled) yield break;
                    foreach (string warning in scan.Warnings) FastInitPlugin.Log.LogWarning(warning);
                    scannedSets = scan.Sets;
                    FastInitPlugin.Log.LogInfo("startup catalog scan: " + scannedSets.Length + " sets, "
                        + scan.TotalBundleFiles + " bundles, scanMs=" + StartupTimings.Ms(scan.TotalScanMs)
                        + " (indexLoadMs=" + StartupTimings.Ms(scan.IndexLoadMs)
                        + ", dirScanMs=" + StartupTimings.Ms(scan.DirScanMs)
                        + ", headerProbeMs=" + StartupTimings.Ms(scan.HeaderProbeMs)
                        + ", cacheLookupMs=" + StartupTimings.Ms(scan.CacheLookupMs)
                        + ", fastHits=" + scan.FastFileHits + ", headerProbes=" + scan.HeaderProbes
                        + ", unifiedIndex=" + scan.IndexLoadedFromDisk + ")");
                    FastInitPlugin.InfoOptimizationTotal = scan.TotalBundleFiles;
                }
                finally
                {
                    scan.Dispose();
                    if (ReferenceEquals(FastInitPlugin._startupScan, scan)) FastInitPlugin._startupScan = null;
                }
                FastInitPlugin.TotalSets = scannedSets.Length;
                FastInitPlugin.InfoOptimizationDone = 0;
                FastInitPlugin.InfoFilesPreparing = false;
                Dictionary<string, int> publishedIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int sets = 0;
                bool menuDirty = false;
                bool menuNeedsHeal = false;
                if (FastInitPlugin.UseDirectCatalogReader)
                {
                    List<StartupCatalogScan.ScannedSet> pass0Misses = new List<StartupCatalogScan.ScannedSet>();
                    for (int i = 0; i < scannedSets.Length; i++)
                    {
                        StartupCatalogScan.ScannedSet candidate = scannedSets[i];
                        if (!candidate.NeedsConversionWork && candidate.InfoFile != null && candidate.CachedMetadata == null)
                            pass0Misses.Add(candidate);
                    }
                    if (pass0Misses.Count >= 2)
                    {
                        try
                        {
                            metadataPool = new CatalogMetadataPool(pass0Misses);
                            FastInitPlugin._catalogPool = metadataPool;
                            metadataPool.Start();
                            FastInitPlugin.Log.LogInfo("catalog metadata prefetch pool started: " + metadataPool.Count
                                + " missed sets across " + metadataPool.WorkerCount + " workers");
                        }
                        catch (Exception e)
                        {
                            if (metadataPool != null) metadataPool.Dispose();
                            if (ReferenceEquals(FastInitPlugin._catalogPool, metadataPool)) FastInitPlugin._catalogPool = null;
                            metadataPool = null;
                            FastInitPlugin.Log.LogWarning("catalog metadata prefetch pool unavailable: " + e.Message);
                        }
                    }
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 1 && metadataPool != null)
                    {
                        metadataPool.Dispose();
                        if (ReferenceEquals(FastInitPlugin._catalogPool, metadataPool)) FastInitPlugin._catalogPool = null;
                        metadataPool = null;
                    }
                    for (int index = 0; index < scannedSets.Length; index++)
                    {
                        StartupCatalogScan.ScannedSet scanned = scannedSets[index];
                        if (scanned.NeedsConversionWork != (pass == 1)) continue;
                        DirectoryInfo dir = scanned.Directory;
                        // Conversion is pure managed I/O with no Unity requests; keep it ahead of
                        // the transition wait so scene changes do not stall the pipeline.
                        // Pass 0 already-fast sets publish immediately without spinning up a batch;
                        // only Pass 1 sets needing real conversion/restore run PrepareSetInfoFiles.
                        if (pass == 0)
                        {
                            if (convertFiles) FastInitPlugin.InfoOptimizationDone += scanned.BundlePaths.Count;
                            if (scanned.CachedMetadata == null)
                                FlushPendingMenu(ref menuDirty, ref menuNeedsHeal, phase, timings);
                        }
                        else
                        {
                            FlushPendingMenu(ref menuDirty, ref menuNeedsHeal, phase, timings);
                            if (convertFiles && scanned.BundlePaths.Count > 0)
                            {
                                double convertStarted = phase.Elapsed.TotalMilliseconds;
                                yield return Guard(FastInitPlugin.PrepareSetInfoFiles(dir.FullName, scanned.BundlePaths), "info prepare " + dir.Name);
                                preparationMs += phase.Elapsed.TotalMilliseconds - convertStarted;
                            }
                        }
                        double pauseStarted = phase.Elapsed.TotalMilliseconds;
                        while (OnDemandResources.IsTransitioning || PseudoPrefabManager.isInCustomLevel)
                            yield return null;
                        timings.PausedMs += phase.Elapsed.TotalMilliseconds - pauseStarted;
                        InfoLoad load = new InfoLoad();
                        if (pass == 0)
                        {
                            if (scanned.InfoFile == null)
                            {
                                FastInitPlugin.Log.LogWarning("missing info file: " + dir.Name);
                            }
                            else
                            {
                                CatalogMetadataPool.Slot slot = metadataPool != null ? metadataPool.GetSlot(scanned.Index) : null;
                                yield return Guard(FastInitPlugin.LoadInfoSet(dir, load, scanned.InfoFile,
                                    scanned.CachedMetadata, scanned.CacheMiss, scanned.CacheReadMs, false, slot),
                                    "catalog " + dir.Name);
                            }
                        }
                        else
                        {
                            yield return Guard(FastInitPlugin.LoadInfoSet(dir, load, null, null, null, 0, false),
                                "catalog " + dir.Name);
                        }
                        timings.Add(dir.Name, load);
                        if (load.So == null) continue;
                        // Insert at the directory-order position; quick sets append while
                        // slow sets slot in before already-published later directories.
                        List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                        int insertAt = 0;
                        while (insertAt < infos.Count)
                        {
                            int publishedAt;
                            if (!publishedIndex.TryGetValue(infos[insertAt].Key, out publishedAt) || publishedAt >= index) break;
                            insertAt++;
                        }
                        KeyValuePair<string, LevelSetInfoSO> entry =
                            new KeyValuePair<string, LevelSetInfoSO>(dir.FullName, load.So);
                        if (insertAt == infos.Count)
                        {
                            infos.Add(entry);
                        }
                        else
                        {
                            infos.Insert(insertAt, entry);
                            menuNeedsHeal = true;
                        }
                        menuDirty = true;
                        publishedIndex[dir.FullName] = index;
                        double snapshotStarted = phase.Elapsed.TotalMilliseconds;
                        if (pass == 0 && scanned.Snapshot != null && load.InfoFile != null
                            && string.Equals(scanned.Snapshot.InfoFileName, load.InfoFile.Name, StringComparison.OrdinalIgnoreCase)
                            && scanned.Snapshot.InfoLength == load.InfoFile.Length
                            && scanned.Snapshot.InfoMtimeUtc.Ticks == load.InfoFile.LastWriteTimeUtc.Ticks)
                        {
                            FastInitPlugin.Snapshots[dir.FullName] = scanned.Snapshot;
                        }
                        else
                        {
                            FastInitPlugin.Snapshots[dir.FullName] = FastInitPlugin.ReadSnapshot(dir, load.InfoFile);
                        }
                        timings.SnapshotMs += phase.Elapsed.TotalMilliseconds - snapshotStarted;
                        sets++;
                        FastInitPlugin.Log.LogInfo("level set [" + dir.Name + "] ready (" + sets + " total, metadata only)");
                        if (slice.Elapsed.TotalMilliseconds >= 12)
                        {
                            FlushPendingMenu(ref menuDirty, ref menuNeedsHeal, phase, timings);
                            double yieldStarted = phase.Elapsed.TotalMilliseconds;
                            yield return null;
                            timings.YieldWaitMs += phase.Elapsed.TotalMilliseconds - yieldStarted;
                            slice.Reset();
                            slice.Start();
                        }
                    }
                }
                FlushPendingMenu(ref menuDirty, ref menuNeedsHeal, phase, timings);
                catalogMs = phase.Elapsed.TotalMilliseconds;

                FastInitPlugin.Log.LogInfo("FastInit complete: " + sets + " level sets available");

                // If we are in the frontend scene when loading completes, call AddUI once so the
                // menu picks up the full list immediately; the postfix heal rebuilds the stale
                // button snapshot. Every later DLC-menu entry self-checks as well.
                try
                {
                    double menuStarted = startup.Elapsed.TotalMilliseconds;
                    GameObject frontend = GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu");
                    if (frontend != null)
                    {
                        DIYLevelEntryUI.AddUI();
                    }
                    timings.MenuMs += startup.Elapsed.TotalMilliseconds - menuStarted;
                }
                catch (Exception e)
                {
                    FastInitPlugin.Log.LogDebug("AddUI refresh skipped: " + e.Message);
                }
                complete = true;
            }
            finally
            {
                if (metadataPool != null)
                {
                    metadataPool.Dispose();
                    if (ReferenceEquals(FastInitPlugin._catalogPool, metadataPool)) FastInitPlugin._catalogPool = null;
                }
                // Per-set conversion interleaves with the catalog loop, so infoPreparationMs
                // is a subset of catalogMs, not a phase preceding it.
                FastInitPlugin.FinishStartupInfoPreparation();
                LevelCatalogCache.FlushIndex();
                FastInitPlugin.Log.LogInfo("startup timing: complete=" + complete + ", totalMs=" + StartupTimings.Ms(startup.Elapsed.TotalMilliseconds)
                    + ", infoPreparationMs=" + StartupTimings.Ms(preparationMs) + ", commonMs=" + StartupTimings.Ms(commonMs)
                    + ", catalogMs=" + StartupTimings.Ms(catalogMs) + ", " + timings.Summary());
                _finished = true;
                FastInitPlugin.FinishLoading();
            }
        }
    }
}
