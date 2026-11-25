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
        // Expected shared bundle names; compared case-insensitively at each use.
        private static readonly List<string> _names = new List<string>();
        private static bool _namesRead;
        // Names that finished loading, failed to load, or were already resident.
        private static readonly HashSet<string> _settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The takeover loading pass in LoadHeavy finished (all names settled).
        private static bool _drained;
        // Live takeover requests by bundle name; entry raises the priority of a level's ones.
        private static readonly Dictionary<string, AssetBundleCreateRequest> _takeoverLoads =
            new Dictionary<string, AssetBundleCreateRequest>(StringComparer.OrdinalIgnoreCase);
        // Entry-driven loads for needed bundles nothing else is loading (a failed startup
        // load); completion marks them settled either way.
        private static readonly Dictionary<string, AssetBundleCreateRequest> _healLoads =
            new Dictionary<string, AssetBundleCreateRequest>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _healed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // External CAB name -> dependency bundle file (WLoader commonW* and DIYLevel common*).
        private static readonly Dictionary<string, string> _cabBundles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _cabsIndexed;

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

        internal static IEnumerator LoadSharedBundles()
        {
            if (_mode == Mode.Off) yield break;
            CollectNames();
            if (_names.Count == 0)
            {
                _mode = Mode.Off;
                yield break;
            }
            if (_mode != Mode.Takeover) yield break; // observing: names only serve the wait
            int state = ReadDepState();
            if (state == 0)
            {
                // The web loader never activated its dependency pipeline (no web-exported
                // sets); release the suppression so a later activation still loads alone.
                Release("web loader dependency pipeline not active");
                yield break;
            }
            List<FileInfo> files = new List<FileInfo>(SharedFiles());
            int skipped = 0;
            List<FileInfo> pending = new List<FileInfo>();
            List<AssetBundleCreateRequest> requests = new List<AssetBundleCreateRequest>();
            FastInitPlugin.Log.LogInfo("WLoader shared bundles takeover: " + files.Count
                + " files (parallel) from " + _loaderDir);
            foreach (FileInfo file in files)
            {
                // Other loaders can finish a bundle while the preceding request is pending.
                if (IsResident(file.Name))
                {
                    skipped++;
                    _settled.Add(file.Name);
                    DuplicateCommonDialog.RecordDuplicate(file.Name, file.FullName);
                    FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                    continue;
                }
                AssetBundleCreateRequest request;
                try { request = AssetBundle.LoadFromFileAsync(file.FullName); }
                catch (Exception e)
                {
                    FastInitPlugin.Log.LogWarning("WLoader shared bundle request failed [" + file.Name + "]: " + e.Message);
                    request = null;
                }
                if (request == null)
                {
                    _settled.Add(file.Name);
                    continue;
                }
                _takeoverLoads[file.Name] = request;
                requests.Add(request);
                pending.Add(file);
            }
            float started = Time.realtimeSinceStartup;
            float nextNotice = started + 15f;
            while (true)
            {
                bool allDone = true;
                for (int i = 0; i < requests.Count; i++)
                    if (!requests[i].isDone) { allDone = false; break; }
                if (allDone) break;
                if (Time.realtimeSinceStartup >= nextNotice)
                {
                    FastInitPlugin.Log.LogWarning("WLoader shared bundles still pending after "
                        + (int)(Time.realtimeSinceStartup - started) + "s; waiting without duplicate requests");
                    nextNotice += 15f;
                }
                yield return null;
            }
            int loaded = 0, failed = 0;
            for (int i = 0; i < requests.Count; i++)
            {
                _takeoverLoads.Remove(pending[i].Name);
                AssetBundle bundle = requests[i].assetBundle;
                if (bundle == null)
                {
                    // Completed failed requests can safely fall back to a sync load.
                    try { bundle = AssetBundle.LoadFromFile(pending[i].FullName); }
                    catch (Exception e) { FastInitPlugin.Log.LogWarning("WLoader shared bundle sync retry failed [" + pending[i].Name + "]: " + e.Message); }
                }
                if (bundle != null)
                {
                    loaded++;
                    DuplicateCommonDialog.RecordLoaded(pending[i].Name, pending[i].FullName);
                }
                else failed++;
                // Failed files leave the wait list too: a corrupt dependency cannot be
                // fixed by waiting, and the recipe validation reports it precisely.
                _settled.Add(pending[i].Name);
                FastInitPlugin.Log.LogInfo("WLoader shared bundle settled [" + pending[i].Name + "]: loaded=" + (bundle != null));
            }
            _drained = true;
            FastInitPlugin.Log.LogInfo("WLoader shared bundles: " + loaded + " loaded, " + skipped + " skipped (dup), "
                + failed + " failed (" + (int)((Time.realtimeSinceStartup - started) * 1000) + " ms)");
        }

        // No fixed order: every request is issued in the same frame, so all packs load
        // concurrently and whichever pack a level needs is already under way.
        private static IEnumerable<FileInfo> SharedFiles()
        {
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(_loaderDir), "commonW*");
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return files;
        }

        private static void CollectNames()
        {
            if (_namesRead || _loaderDir == null) return;
            _namesRead = true;
            foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(_loaderDir), "commonW*"))
                _names.Add(file.Name);
        }

        // The dependency bundles a level actually references: its info asset (recipes,
        // include lists, optional items, ingredients, steps) and its scene bundle both
        // point at bundle CABs through external references. WLoader's commonW* packs load
        // in the background and are the usual race; DIYLevel's common01..04 and
        // common*_textures are preloaded before the catalog publishes, so they appear here
        // only when a startup load failed and entry has to heal them. Falls back to every
        // commonW* pack when the reference scan is unavailable.
        internal static HashSet<string> NeededBundles(string infoFile, string sceneFile)
        {
            HashSet<string> bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IndexBundleCabinets()) return AllSharedPacks();
            try
            {
                foreach (string cabinet in InfoBundleGraph.BundleReferences(infoFile)) AddBundle(cabinet, bundles);
                if (sceneFile != null && File.Exists(sceneFile))
                    foreach (string cabinet in InfoBundleGraph.BundleReferences(sceneFile)) AddBundle(cabinet, bundles);
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("dependency bundle reference scan unavailable; waiting for every shared pack: " + e.Message);
                return AllSharedPacks();
            }
            // A successful scan that attributes nothing means this level references no
            // dependency bundle: waiting cannot resolve it, so the reload decides.
            return bundles;
        }

        private static void AddBundle(string cabinet, HashSet<string> bundles)
        {
            string path;
            if (cabinet != null && _cabBundles.TryGetValue(cabinet, out path)) bundles.Add(path);
        }

        private static HashSet<string> AllSharedPacks()
        {
            CollectNames();
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_loaderDir != null)
                foreach (string name in _names) paths.Add(Path.Combine(_loaderDir, name));
            return paths;
        }

        // Reads each dependency bundle's directory once. A bundle whose header cannot be
        // read is never attributed, and NeededBundles falls back to every shared pack.
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
            try
            {
                foreach (string cabinet in InfoBundleGraph.CabinetNames(path))
                    _cabBundles[cabinet] = path;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("dependency bundle index unavailable [" + Path.GetFileName(path) + "]: " + e.Message);
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
        // raised first, and a needed bundle nothing is loading (a failed startup load) is
        // requested here, so entering a level pushes exactly its own bundles ahead of the
        // rest. progress receives the settled count and the average progress of the
        // pending requests, keeping the corner label moving instead of looking frozen.
        internal static IEnumerator AwaitBundles(HashSet<string> bundles, Action<int, float> progress)
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
            float lastProgress = start;
            if (progress != null) progress(settled, fraction);
            while (!Settled(bundles))
            {
                ReapHealLoads(bundles);
                int nowSettled = SettledCount(bundles);
                float nowFraction = PendingFraction(bundles);
                // A long decode still reports a rising request progress, so a cold disk
                // is not mistaken for a stalled load.
                if (nowSettled != settled || nowFraction > fraction + 0.01f)
                {
                    settled = nowSettled;
                    fraction = nowFraction;
                    lastProgress = Time.realtimeSinceStartup;
                    if (progress != null) progress(settled, fraction);
                }
                if (Time.realtimeSinceStartup - start >= timeout)
                {
                    FastInitPlugin.Log.LogWarning("dependency bundle wait timed out after " + (int)timeout
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
                if (Boost(_takeoverLoads, name) || Boost(_healLoads, name)) boosted++;
            }
            return boosted;
        }

        private static bool Boost(Dictionary<string, AssetBundleCreateRequest> loads, string name)
        {
            AssetBundleCreateRequest request;
            if (!loads.TryGetValue(name, out request) || request.isDone) return false;
            request.priority = 100;
            return true;
        }

        // Requests a needed bundle nothing is loading (a DIYLevel common bundle whose
        // startup load failed, for example); it stays resident for the session.
        private static int RequestMissing(HashSet<string> bundles)
        {
            int requested = 0;
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                if (IsSettled(name) || _takeoverLoads.ContainsKey(name) || _healLoads.ContainsKey(name)) continue;
                try
                {
                    _healLoads[name] = AssetBundle.LoadFromFileAsync(path);
                    requested++;
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
                _healed.Add(name);
                FastInitPlugin.Log.LogInfo("dependency bundle loaded for entry [" + name + "]: loaded=" + (bundle != null));
            }
        }

        // Average progress of the needed bundles that are still loading; 0 when none are.
        private static float PendingFraction(HashSet<string> bundles)
        {
            float sum = 0f;
            int count = 0;
            foreach (string path in bundles)
            {
                string name = Path.GetFileName(path);
                AddFraction(_takeoverLoads, name, ref sum, ref count);
                AddFraction(_healLoads, name, ref sum, ref count);
            }
            return count == 0 ? 0f : sum / count;
        }

        private static void AddFraction(Dictionary<string, AssetBundleCreateRequest> loads, string name, ref float sum, ref int count)
        {
            AssetBundleCreateRequest request;
            if (!loads.TryGetValue(name, out request) || request.isDone) return;
            sum += request.progress;
            count++;
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
            if (_mode == Mode.Off) return "inactive";
            if (_mode == Mode.Takeover) return "takeover " + (_drained ? "settled" : "pending")
                + " (" + _settled.Count + "/" + _names.Count + " files)";
            return "observing " + ResidentCount() + "/" + _names.Count + " resident";
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
