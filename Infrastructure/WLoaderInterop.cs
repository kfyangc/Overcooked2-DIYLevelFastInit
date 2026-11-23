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
            List<FileInfo> files = new List<FileInfo>(OrderedSharedFiles());
            List<string> priority = ParsePriorityList();
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
                request.priority = PriorityRank(file.Name, priority) < priority.Count ? 100 : 0;
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
                AssetBundle bundle = requests[i].assetBundle;
                if (bundle == null)
                {
                    // Completed failed requests can safely fall back to a sync load.
                    try { bundle = AssetBundle.LoadFromFile(pending[i].FullName); }
                    catch (Exception e) { FastInitPlugin.Log.LogWarning("WLoader shared bundle sync retry failed [" + pending[i].Name + "]: " + e.Message); }
                }
                if (bundle != null) loaded++;
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

        private static IEnumerable<FileInfo> OrderedSharedFiles()
        {
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(_loaderDir), "commonW*");
            List<string> priority = ParsePriorityList();
            files.Sort((a, b) =>
            {
                int rankA = PriorityRank(a.Name, priority), rankB = PriorityRank(b.Name, priority);
                if (rankA != rankB) return rankA - rankB;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return files;
        }

        private static List<string> ParsePriorityList()
        {
            List<string> priority = new List<string>();
            string setting = FastInitPlugin.WLoaderPriorityBundles;
            if (string.IsNullOrEmpty(setting)) return priority;
            foreach (string part in setting.Split(','))
            {
                string name = part.Trim();
                if (name.Length != 0) priority.Add(name);
            }
            return priority;
        }

        private static int PriorityRank(string name, List<string> priority)
        {
            for (int i = 0; i < priority.Count; i++)
                if (string.Equals(name, priority[i], StringComparison.OrdinalIgnoreCase)) return i;
            return priority.Count;
        }

        private static void CollectNames()
        {
            if (_namesRead || _loaderDir == null) return;
            _namesRead = true;
            foreach (FileInfo file in FastInitHost.SafeGetFiles(new DirectoryInfo(_loaderDir), "commonW*"))
                _names.Add(file.Name);
        }

        internal static bool Settled()
        {
            if (_mode == Mode.Off || _names.Count == 0) return true;
            if (_mode == Mode.Takeover) return _drained;
            foreach (string name in _names)
                if (!IsResident(name)) return false;
            return true;
        }

        internal static bool WaitingUseful()
        {
            return _mode != Mode.Off && _names.Count != 0 && !Settled();
        }

        // Waits until the shared bundles settle or the configured timeout elapses
        // (then gives way; the loading coroutine runs independently of LoadHeavy).
        internal static IEnumerator AwaitSettled()
        {
            if (!WaitingUseful()) yield break;
            float start = Time.realtimeSinceStartup;
            float nextNotice = start + 5f;
            float timeout = FastInitPlugin.WLoaderGateTimeoutSeconds;
            while (!Settled())
            {
                if (Time.realtimeSinceStartup - start >= timeout)
                {
                    FastInitPlugin.Log.LogWarning("shared web bundle wait timed out after " + (int)timeout + "s; giving way");
                    yield break;
                }
                if (Time.realtimeSinceStartup >= nextNotice)
                {
                    FastInitPlugin.Log.LogInfo("waiting for shared web bundles: " + ResidentCount() + "/" + _names.Count + " resident");
                    nextNotice += 5f;
                }
                yield return null;
            }
            FastInitPlugin.Log.LogInfo("shared web bundles settled after " + (int)((Time.realtimeSinceStartup - start) * 1000) + " ms");
        }

        private static int ResidentCount()
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
