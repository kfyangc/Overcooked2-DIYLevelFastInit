using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Replaces DIYLevelAssetBundleManager.Initialize(): the cheap part (common bundle,
    /// static fields, DLC data) stays synchronous, while the common* dependency bundles
    /// and all level-set info bundles load in a background coroutine. Also fixes the
    /// original's missing idempotence guard.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Overcooked2.exe")]
    [BepInDependency("dev.gua.overcooked.diylevel")]
    public class FastInitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "oc2.diylevel.fastinit";
        public const string PluginName = "DIYLevel FastInit";
        public const string PluginVersion = "1.3.0";

        internal static ManualLogSource Log;

        private static bool _started;

        private static readonly FieldInfo FiCommonBundle = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "commonBundle");
        private static readonly FieldInfo FiCover = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelCover");
        private static readonly FieldInfo FiSessionPrefab = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelGameSessionPrefab");
        private static readonly FieldInfo FiConfigSo = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "configTemplateSO");
        private static readonly FieldInfo FiInstance = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "Instance");
        private static readonly FieldInfo FiSetMenu = AccessTools.Field(typeof(DIYLevelEntryUI), "levelSetSelectionMenu");
        private static readonly MethodInfo MiAddSetButton = AccessTools.Method(typeof(DIYLevelEntryUI), "AddLevelSetButton");

        private static int _builtCount;

        internal static bool Loading;
        internal static int TotalSets;

        private static GUIStyle _progressStyle;

        private void Awake()
        {
            Log = Logger;
            MethodInfo original = AccessTools.Method(typeof(DIYLevelAssetBundleManager), "Initialize");
            if (original == null)
            {
                Log.LogError("DIYLevelAssetBundleManager.Initialize not found; FastInit inactive (original behavior kept).");
                return;
            }
            Harmony harmony = new Harmony(PluginGuid);
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(FastInitPlugin), nameof(InitializePrefix)));
            MethodInfo addUi = AccessTools.Method(typeof(DIYLevelEntryUI), "AddUI");
            if (addUi != null && FiSetMenu != null && MiAddSetButton != null)
            {
                harmony.Patch(addUi, postfix: new HarmonyMethod(typeof(FastInitPlugin), nameof(AddUIPostfix)));
            }
            else
            {
                Log.LogWarning("AddUI heal patch not applied (member missing): menu may need re-entry to show late-loaded sets");
            }
            Log.LogInfo("DIYLevel FastInit ready: level bundles load async, menu no longer blocks.");
        }

        private void OnGUI()
        {
            if (!Loading || TotalSets <= 0)
            {
                return;
            }
            try
            {
                if (_progressStyle == null)
                {
                    _progressStyle = new GUIStyle(GUI.skin.label);
                    _progressStyle.fontSize = 18;
                    _progressStyle.fontStyle = FontStyle.Bold;
                    _progressStyle.alignment = TextAnchor.UpperRight;
                }
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                int done = (infos != null) ? infos.Count : 0;
                string text = "DIY levels loading " + done + "/" + TotalSets;
                Rect rect = new Rect(Screen.width - 336, 6, 330, 26);
                _progressStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, _progressStyle);
                _progressStyle.normal.textColor = Color.white;
                GUI.Label(rect, text, _progressStyle);
            }
            catch (Exception)
            {
            }
        }

        // The original builds the level-set button list only when the menu is first created
        // (if (menu != null) return), so sets that finish loading later never show up;
        // re-sync the buttons after every AddUI call.
        private static void AddUIPostfix()
        {
            HealLevelSetButtons();
        }

        internal static void HealLevelSetButtons()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                if (infos == null || FiSetMenu == null || MiAddSetButton == null)
                {
                    return;
                }
                FrontendOptionsMenu menu = FiSetMenu.GetValue(null) as FrontendOptionsMenu;
                if (menu == null)
                {
                    return; // menu not created yet; AddUI itself will build it from the current list
                }
                if (_builtCount == infos.Count)
                {
                    return;
                }
                OC2DIYLevel.UIUtils.ClearAllMenuContent(menu);
                for (int i = 0; i < infos.Count; i++)
                {
                    MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                }
                _builtCount = infos.Count;
                Log.LogInfo("level set menu rebuilt: " + infos.Count + " entries");
                UpdateMenuHeaderProgress(menu);
            }
            catch (Exception e)
            {
                Log.LogWarning("HealLevelSetButtons failed: " + e.Message);
            }
        }

        internal static void NotifySetAdded()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                if (infos == null || FiSetMenu == null || MiAddSetButton == null)
                {
                    return;
                }
                FrontendOptionsMenu menu = FiSetMenu.GetValue(null) as FrontendOptionsMenu;
                if (menu == null)
                {
                    return;
                }
                if (_builtCount >= 0 && _builtCount < infos.Count)
                {
                    for (int i = _builtCount; i < infos.Count; i++)
                    {
                        MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                    }
                }
                _builtCount = infos.Count;
                UpdateMenuHeaderProgress(menu);
            }
            catch (Exception e)
            {
                Log.LogDebug("NotifySetAdded: " + e.Message);
            }
        }

        private static void UpdateMenuHeaderProgress(FrontendOptionsMenu menu)
        {
            try
            {
                Transform header = ((Component)menu).transform.Find("SettingsBody/HeaderBacker/Header");
                if (header == null)
                {
                    return;
                }
                T17Text text = header.GetComponent<T17Text>();
                if (text == null)
                {
                    return;
                }
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                int done = (infos != null) ? infos.Count : 0;
                if (Loading && TotalSets > 0)
                {
                    OC2DIYLevel.UIUtils.SetText(text, "More Levels (loading " + done + "/" + TotalSets + ")", "更多关卡（加载中 " + done + "/" + TotalSets + "）");
                }
                else
                {
                    OC2DIYLevel.UIUtils.SetText(text, "More Levels", "更多关卡");
                }
            }
            catch (Exception e)
            {
                Log.LogDebug("UpdateMenuHeaderProgress: " + e.Message);
            }
        }

        internal static void FinishLoading()
        {
            Loading = false;
            try
            {
                FrontendOptionsMenu menu = FiSetMenu.GetValue(null) as FrontendOptionsMenu;
                if (menu != null)
                {
                    UpdateMenuHeaderProgress(menu);
                }
            }
            catch (Exception)
            {
            }
        }

        // Harmony prefix: returns false to skip the original Initialize and run ours instead.
        private static bool InitializePrefix()
        {
            if (_started)
            {
                // The original has no idempotence guard: a second ByteLoad would reload
                // all 353 MB again (and duplicate the info entries in levelSetInfos).
                return false;
            }
            _started = true;
            try
            {
                string pluginDir = GetDiyPluginDir();
                if (!RunCheapInit(pluginDir))
                {
                    // Same failure conditions as the original (missing common file etc.); allow a retry.
                    _started = false;
                    return false;
                }
                GameObject host = new GameObject("DIYLevelFastInitHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<FastInitHost>().Run(pluginDir);
            }
            catch (Exception e)
            {
                _started = false;
                Log.LogError("FastInit init failed (DIY levels unavailable this attempt): " + e);
            }
            return false;
        }

        // Must resolve OC2DIYLevel.dll's own location: GetExecutingAssembly() here would
        // point at this plugin's directory instead.
        private static string GetDiyPluginDir()
        {
            return Path.GetDirectoryName(typeof(DIYLevelAssetBundleManager).Assembly.Location);
        }

        // The cheap part of the original Initialize: once done, IsInitialized is true and
        // every consumer patch behaves exactly as with the original.
        private static bool RunCheapInit(string pluginDir)
        {
            DIYLevelAssetBundleManager prevInst = FiInstance.GetValue(null) as DIYLevelAssetBundleManager;
            GameObject go = new GameObject("DIYLevelAssetBundleManager", new Type[] { typeof(DIYLevelAssetBundleManager) });
            UnityEngine.Object.DontDestroyOnLoad(go);
            if (prevInst != null && ((Component)prevInst).gameObject != go)
            {
                UnityEngine.Object.Destroy(((Component)prevInst).gameObject);
            }
            FiInstance.SetValue(null, go.GetComponent<DIYLevelAssetBundleManager>());

            string commonPath = Path.Combine(pluginDir, "common");
            if (!File.Exists(commonPath))
            {
                Log.LogWarning("missing file: " + commonPath);
                return false;
            }
            AssetBundle commonBundle = AssetBundle.LoadFromFile(commonPath);
            FiCover.SetValue(null, commonBundle.LoadAsset<Sprite>("diylevelcover"));
            FiSessionPrefab.SetValue(null, commonBundle.LoadAsset<GameObject>("DIYLevelGameSession"));
            FiConfigSo.SetValue(null, commonBundle.LoadAsset<PseudoPrefabSO>("LevelConfigTemplateSO"));
            FiCommonBundle.SetValue(null, commonBundle);

            DIYLevelAssetBundleManager.levelSetInfos = new List<KeyValuePair<string, LevelSetInfoSO>>();

            if (DIYLevelAssetBundleManager.diyDLCFrontendData == null)
            {
                DLCFrontendData data = ScriptableObject.CreateInstance<DLCFrontendData>();
                ((UnityEngine.Object)data).name = "DLC_DIYLevel";
                data.m_NameLocalizationKey = "\"More Levels\"";
                data.m_DescriptionLocalizationKey = OC2DIYLevel.UIUtils.GetLocalizedText("\"Enjoy extra levels from the community!\"", "\"游玩来自玩家社区的更多关卡！\"");
                data.m_PreviewImage = DIYLevelAssetBundleManager.GetCover();
                data.m_DLCID = 15;
                data.m_type = (DLCType)0;
                data.m_IsFreeDLC = true;
                data.m_IsSeasonPassDLC = false;
                DIYLevelAssetBundleManager.diyDLCFrontendData = data;
            }
            return true;
        }
    }

    internal class FastInitHost : MonoBehaviour
    {
        private sealed class PendingSet
        {
            public DirectoryInfo Dir;
            public FileInfo File;
            public AssetBundleCreateRequest Req;
        }

        private const float AsyncTimeoutSeconds = 15f;

        private bool _finished;

        public void Run(string pluginDir)
        {
            StartCoroutine(LoadHeavy(pluginDir));
        }

        private void OnDestroy()
        {
            if (!_finished)
            {
                FastInitPlugin.Log.LogWarning("FastInitHost destroyed before loading finished — coroutine was killed by something.");
            }
        }

        private static List<FileInfo> SafeGetFiles(DirectoryInfo dir, string pattern)
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

        private static List<DirectoryInfo> SafeGetDirs(string path)
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

        // Polls an async operation; the outer coroutine forwards wait.Current as its own
        // yield. Gives up after the timeout — the caller then checks isDone and falls
        // back to the sync path.
        private static IEnumerator WaitOp(AsyncOperation op, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup - start > AsyncTimeoutSeconds)
                {
                    FastInitPlugin.Log.LogWarning(what + ": async did not finish in " + AsyncTimeoutSeconds + "s, falling back to sync");
                    yield break;
                }
                yield return null;
            }
        }

        private IEnumerator LoadHeavy(string pluginDir)
        {
            FastInitPlugin.Loading = true;
            FastInitPlugin.Log.LogInfo("LoadHeavy started");
            // Plugins like WLoader may already keep commonW1/commonW2 resident in memory;
            // skipping them saves the 1-2 seconds of reading the whole file just to be
            // rejected as a duplicate by Unity.
            HashSet<string> alreadyLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
            {
                alreadyLoaded.Add(ab.name);
            }

            int loaded = 0, skipped = 0, failed = 0;
            foreach (FileInfo file in SafeGetFiles(new DirectoryInfo(pluginDir), "common*"))
            {
                if (file.Name == "common")
                {
                    continue;
                }
                if (alreadyLoaded.Contains(file.Name))
                {
                    skipped++;
                    FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                    continue;
                }
                AssetBundleCreateRequest req = AssetBundle.LoadFromFileAsync(file.FullName);
                IEnumerator wait = WaitOp(req, file.Name);
                while (wait.MoveNext())
                {
                    yield return wait.Current;
                }
                if (req.isDone && req.assetBundle != null)
                {
                    loaded++;
                    alreadyLoaded.Add(file.Name);
                }
                else
                {
                    FastInitPlugin.Log.LogWarning(file.Name + ": async failed (" + (req.isDone ? "null bundle" : "timeout") + "), trying sync");
                    AssetBundle sync = AssetBundle.LoadFromFile(file.FullName);
                    if (sync != null)
                    {
                        loaded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
            FastInitPlugin.Log.LogInfo("common bundles: " + loaded + " loaded, " + skipped + " skipped (dup), " + failed + " failed");

            // Parallel prefetch: queue the file reads of all info bundles onto worker threads
            // at once (one request per frame), then consume them in directory order —
            // disk read times overlap, far faster than one-by-one serial loading.
            List<PendingSet> pending = new List<PendingSet>();
            foreach (DirectoryInfo dir in SafeGetDirs(Path.Combine(pluginDir, "levels")))
            {
                List<FileInfo> infoFiles = SafeGetFiles(dir, "info*");
                if (infoFiles.Count == 0)
                {
                    FastInitPlugin.Log.LogWarning("missing info file: " + dir.Name);
                    continue;
                }
                PendingSet ps = new PendingSet();
                ps.Dir = dir;
                ps.File = infoFiles[0];
                ps.Req = AssetBundle.LoadFromFileAsync(infoFiles[0].FullName);
                pending.Add(ps);
                yield return null;
            }
            FastInitPlugin.TotalSets = pending.Count;
            FastInitPlugin.Log.LogInfo("prefetch queued: " + pending.Count + " info bundles");

            int sets = 0;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            foreach (PendingSet p in pending)
            {
                DirectoryInfo dir = p.Dir;
                AssetBundleCreateRequest cr = p.Req;
                FastInitPlugin.Log.LogInfo("consuming level set [" + dir.Name + "]");
                IEnumerator waitCr = WaitOp(cr, dir.Name);
                while (waitCr.MoveNext())
                {
                    yield return waitCr.Current;
                }
                AssetBundle bundle;
                if (cr.isDone && cr.assetBundle != null)
                {
                    bundle = cr.assetBundle;
                }
                else
                {
                    FastInitPlugin.Log.LogWarning(dir.Name + ": async failed (" + (cr.isDone ? "null bundle" : "timeout") + "), trying sync");
                    bundle = AssetBundle.LoadFromFile(p.File.FullName);
                }
                if (bundle == null)
                {
                    FastInitPlugin.Log.LogWarning("failed loading info bundle of " + dir.Name);
                    continue;
                }
                LevelSetInfoSO so = null;
                AssetBundleRequest ar = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
                IEnumerator waitAr = WaitOp(ar, dir.Name + " (LoadAssetAsync)");
                while (waitAr.MoveNext())
                {
                    yield return waitAr.Current;
                }
                if (ar.isDone)
                {
                    so = ar.asset as LevelSetInfoSO;
                }
                if (so == null)
                {
                    FastInitPlugin.Log.LogWarning(dir.Name + ": LoadAssetAsync incomplete, trying sync LoadAsset");
                    so = bundle.LoadAsset("LevelSetInfo", typeof(LevelSetInfoSO)) as LevelSetInfoSO;
                }
                if (so == null)
                {
                    FastInitPlugin.Log.LogWarning("missing LevelSetInfo in " + dir.Name);
                    continue;
                }
                infos.Add(new KeyValuePair<string, LevelSetInfoSO>(dir.FullName, so));
                sets++;
                FastInitPlugin.NotifySetAdded();
                FastInitPlugin.Log.LogInfo("level set [" + dir.Name + "] ready (" + sets + " total)");
            }

            _finished = true;
            FastInitPlugin.FinishLoading();
            FastInitPlugin.Log.LogInfo("FastInit complete: " + sets + " level sets available");

            // If we are in the frontend scene when loading completes, call AddUI once so the
            // menu picks up the full list immediately; the postfix heal rebuilds the stale
            // button snapshot. Every later DLC-menu entry self-checks as well.
            try
            {
                GameObject frontend = GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu");
                if (frontend != null)
                {
                    DIYLevelEntryUI.AddUI();
                }
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogDebug("AddUI refresh skipped: " + e.Message);
            }
        }
    }
}
