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

        private IEnumerator LoadHeavy(string pluginDir)
        {
            FastInitPlugin.Loading = true;
            System.Diagnostics.Stopwatch startup = System.Diagnostics.Stopwatch.StartNew();
            StartupTimings timings = new StartupTimings();
            double preparationMs = 0, commonMs = 0, catalogMs = 0;
            bool complete = false;
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
                List<DirectoryInfo> dirs = SafeGetDirs(Path.Combine(pluginDir, "levels"));
                FastInitPlugin.TotalSets = dirs.Count;
                // Per-set pipeline: convert one set's bundles, then catalog and publish it
                // right away, so the level-set menu fills in while later sets still prepare.
                bool convertFiles = FastInitPlugin.OptimizeInfoFilesOnStartup || FastInitPlugin.RestoreInfoFilesOnStartup;
                List<string>[] setFiles = convertFiles ? FastInitPlugin.CollectSetBundlePaths(dirs) : null;
                FastInitPlugin.InfoOptimizationDone = 0;
                // Two rounds: sets needing no conversion work publish first (directory
                // order within a round), so already-prepared sets are not stuck behind
                // slow conversions. Entries insert at their directory position below,
                // keeping the final levelSetInfos order equal to directory order.
                bool[] needsWork = new bool[dirs.Count];
                if (setFiles != null)
                    for (int i = 0; i < dirs.Count; i++)
                        needsWork[i] = FastInitPlugin.SetNeedsConversionWork(setFiles[i], FastInitPlugin.RestoreInfoFilesOnStartup);
                Dictionary<string, int> publishedIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int sets = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int index = 0; index < dirs.Count; index++)
                    {
                        if (needsWork[index] != (pass == 1)) continue;
                        DirectoryInfo dir = dirs[index];
                        // Conversion is pure managed I/O with no Unity requests; keep it ahead of
                        // the transition wait so scene changes do not stall the pipeline.
                        if (setFiles != null && setFiles[index].Count > 0)
                        {
                            double convertStarted = phase.Elapsed.TotalMilliseconds;
                            yield return Guard(FastInitPlugin.PrepareSetInfoFiles(dir.FullName, setFiles[index]), "info prepare " + dir.Name);
                            preparationMs += phase.Elapsed.TotalMilliseconds - convertStarted;
                        }
                        double pauseStarted = phase.Elapsed.TotalMilliseconds;
                        while (OnDemandResources.IsTransitioning || PseudoPrefabManager.isInCustomLevel)
                            yield return null;
                        timings.PausedMs += phase.Elapsed.TotalMilliseconds - pauseStarted;
                        InfoLoad load = new InfoLoad();
                        yield return Guard(FastInitPlugin.LoadInfoSet(dir, load), "catalog " + dir.Name);
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
                        double menuStarted = phase.Elapsed.TotalMilliseconds;
                        if (insertAt == infos.Count)
                        {
                            infos.Add(entry);
                            FastInitPlugin.NotifySetAdded();
                        }
                        else
                        {
                            infos.Insert(insertAt, entry);
                            FastInitPlugin.HealLevelSetButtons();
                        }
                        publishedIndex[dir.FullName] = index;
                        timings.MenuMs += phase.Elapsed.TotalMilliseconds - menuStarted;
                        double snapshotStarted = phase.Elapsed.TotalMilliseconds;
                        FastInitPlugin.Snapshots[dir.FullName] = FastInitPlugin.ReadSnapshot(dir, load.InfoFile);
                        timings.SnapshotMs += phase.Elapsed.TotalMilliseconds - snapshotStarted;
                        sets++;
                        FastInitPlugin.Log.LogInfo("level set [" + dir.Name + "] ready (" + sets + " total, metadata only)");
                        if (slice.Elapsed.TotalMilliseconds >= 4)
                        {
                            double yieldStarted = phase.Elapsed.TotalMilliseconds;
                            yield return null;
                            timings.YieldWaitMs += phase.Elapsed.TotalMilliseconds - yieldStarted;
                            slice.Reset();
                            slice.Start();
                        }
                    }
                }
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
                // Per-set conversion interleaves with the catalog loop, so infoPreparationMs
                // is a subset of catalogMs, not a phase preceding it.
                FastInitPlugin.FinishStartupInfoPreparation();
                FastInitPlugin.Log.LogInfo("startup timing: complete=" + complete + ", totalMs=" + StartupTimings.Ms(startup.Elapsed.TotalMilliseconds)
                    + ", infoPreparationMs=" + StartupTimings.Ms(preparationMs) + ", commonMs=" + StartupTimings.Ms(commonMs)
                    + ", catalogMs=" + StartupTimings.Ms(catalogMs) + ", " + timings.Summary());
                _finished = true;
                FastInitPlugin.FinishLoading();
            }
        }
    }
}
