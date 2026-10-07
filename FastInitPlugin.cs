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
using OC2DIYLevel.CustomArcade;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Replaces DIYLevelAssetBundleManager.Initialize(): the cheap part (common bundle,
    /// static fields, DLC data) stays synchronous, while the common* dependency bundles
    /// and all level-set info bundles load in a background coroutine. Also fixes the
    /// original's missing idempotence guard, and adds hot sync (F9 or the menu button):
    /// per-directory diff of levels/ that only reloads changed level sets.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Overcooked2.exe")]
    [BepInDependency("dev.gua.overcooked.diylevel")]
    public class FastInitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "oc2.diylevel.fastinit";
        public const string PluginName = "DIYLevel FastInit";
        public const string PluginVersion = "1.4.0";

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

        internal static FastInitPlugin Instance;

        // Per-directory info bundle refs and on-load snapshots: sync unloads via these
        // and diffs against them, so untouched sets are never reloaded.
        internal static readonly Dictionary<string, AssetBundle> InfoBundles =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, SetSnapshot> Snapshots =
            new Dictionary<string, SetSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static GUIStyle _progressStyle;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
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
                // The frontend rebuilds its menus on every level exit, so a freshly recreated
                // menu can match the cached count while missing the reload button; re-assert
                // it on every heal before the count check can short-circuit.
                EnsureReloadButton(menu);
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
                EnsureReloadButton(menu);
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
                EnsureReloadButton(menu);
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
                    EnsureReloadButton(menu);
                }
            }
            catch (Exception)
            {
            }
        }

        // ---- Hot sync: per-directory diff, only reload what changed ----

        internal static void TrySyncReload()
        {
            if (Loading)
            {
                Log.LogInfo("sync skipped: initial load or previous sync still running");
                return;
            }
            if (!DIYLevelAssetBundleManager.IsInitialized)
            {
                Log.LogInfo("sync skipped: DIY level manager not initialized");
                return;
            }
            if (PseudoPrefabManager.isInCustomLevel)
            {
                Log.LogInfo("sync skipped: inside a DIY level");
                return;
            }
            // No GameSession guard here: gua's session is DontDestroyOnLoad and lingers
            // in the frontend after a level exit, but every level entry runs through
            // StartEmptySession, which destroys it and rebuilds the scene directory from
            // the current list — a stale session is harmless to sync. The button itself
            // only exists in the frontend menu, so no level load can be in flight.
            if (GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu") == null)
            {
                Log.LogInfo("sync skipped: not in the frontend scene");
                return;
            }
            Instance.StartCoroutine(Instance.SyncRoutine());
        }

        private IEnumerator SyncRoutine()
        {
            Loading = true;
            try
            {
                Log.LogInfo("sync started");
                string levelsDir = Path.Combine(GetDiyPluginDir(), "levels");
                List<DirectoryInfo> dirs = FastInitHost.SafeGetDirs(levelsDir);
                TotalSets = dirs.Count;
                if (dirs.Count == 0 && DIYLevelAssetBundleManager.levelSetInfos != null
                    && DIYLevelAssetBundleManager.levelSetInfos.Count > 0)
                {
                    // A transiently empty scan must not unload every loaded set.
                    Log.LogWarning("sync: levels/ scan returned nothing while sets are loaded; aborting");
                    yield break;
                }
                List<DirectoryInfo> added = new List<DirectoryInfo>();
                List<DirectoryInfo> infoChanged = new List<DirectoryInfo>();
                List<DirectoryInfo> contentChanged = new List<DirectoryInfo>();
                List<string> removed = new List<string>();
                int unchanged = 0;
                bool diffFailed = false;
                try
                {
                    Dictionary<string, SetSnapshot> fresh = new Dictionary<string, SetSnapshot>(StringComparer.OrdinalIgnoreCase);
                    foreach (DirectoryInfo dir in dirs)
                    {
                        List<FileInfo> infoFiles = FastInitHost.SafeGetFiles(dir, "info*");
                        if (infoFiles.Count == 0)
                        {
                            Log.LogWarning("sync: missing info file: " + dir.Name);
                            continue;
                        }
                        SetSnapshot snap = ReadSnapshot(dir, infoFiles[0]);
                        fresh[dir.FullName] = snap;
                        SetSnapshot old;
                        bool hasSnapshot = Snapshots.TryGetValue(dir.FullName, out old);
                        bool inList = IndexOfSet(DIYLevelAssetBundleManager.levelSetInfos, dir.FullName) >= 0;
                        if (!inList)
                        {
                            added.Add(dir);
                        }
                        else if (!hasSnapshot)
                        {
                            // Loaded before snapshots existed; leave it alone.
                            unchanged++;
                        }
                        else if (old.InfoFileName != snap.InfoFileName
                            || old.InfoMtimeUtc != snap.InfoMtimeUtc
                            || old.InfoLength != snap.InfoLength)
                        {
                            infoChanged.Add(dir);
                        }
                        else if (old.DirMaxMtimeUtc != snap.DirMaxMtimeUtc)
                        {
                            contentChanged.Add(dir);
                        }
                        else
                        {
                            unchanged++;
                        }
                    }
                    foreach (KeyValuePair<string, LevelSetInfoSO> kv in DIYLevelAssetBundleManager.levelSetInfos)
                    {
                        if (!fresh.ContainsKey(kv.Key))
                        {
                            removed.Add(kv.Key);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("sync diff failed: " + e);
                    diffFailed = true;
                }
                if (diffFailed)
                {
                    yield break;
                }

                int removedCount = 0, reloaded = 0, swept = 0, addedCount = 0, failed = 0;
                foreach (string key in removed)
                {
                    try
                    {
                        UnloadSet(key);
                        removedCount++;
                        Log.LogInfo("sync: set removed: " + key);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Log.LogWarning("sync: unload failed for [" + key + "]: " + e.Message);
                    }
                }
                foreach (DirectoryInfo dir in infoChanged)
                {
                    Box done = new Box();
                    IEnumerator step = ReloadSetRoutine(dir, done);
                    while (step.MoveNext())
                    {
                        yield return step.Current;
                    }
                    if (done.Value)
                    {
                        reloaded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                foreach (DirectoryInfo dir in contentChanged)
                {
                    try
                    {
                        List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                        int idx = IndexOfSet(infos, dir.FullName);
                        if (idx >= 0)
                        {
                            SweepSetBundles(infos[idx].Value);
                            swept++;
                            Log.LogInfo("sync: swept resident bundles of [" + dir.Name + "]");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("sync: content sweep of [" + dir.Name + "] failed: " + e.Message);
                    }
                }
                foreach (DirectoryInfo dir in added)
                {
                    Box done = new Box();
                    IEnumerator step = LoadSetRoutine(dir, done);
                    while (step.MoveNext())
                    {
                        yield return step.Current;
                    }
                    if (done.Value)
                    {
                        addedCount++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                try
                {
                    DIYLevelAssetBundleManager.levelSetInfos.Sort(delegate (KeyValuePair<string, LevelSetInfoSO> a, KeyValuePair<string, LevelSetInfoSO> b)
                    {
                        return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                    });
                }
                catch (Exception e)
                {
                    Log.LogDebug("sync sort failed: " + e.Message);
                }

                yield return Resources.UnloadUnusedAssets();

                if (addedCount + removedCount + reloaded + swept > 0)
                {
                    _builtCount = 0;
                    HealLevelSetButtons();
                    try
                    {
                        FrontendOptionsMenu arcadeMenu = CustomArcadeEntryUI.customArcadeSettingsMenu;
                        if (arcadeMenu != null)
                        {
                            // Nulling the static is not enough: AddCustomArcadeSettingsUI
                            // early-returns while the menu GameObject exists, so the stale
                            // selector rows would survive. Destroy the object; the next
                            // T17TabPanel.OnTabSelected rebuilds it with fresh options.
                            if (arcadeMenu.gameObject.activeInHierarchy)
                            {
                                Log.LogInfo("sync: arcade settings menu is open, deferring its rebuild");
                            }
                            else
                            {
                                UnityEngine.Object.Destroy(arcadeMenu.gameObject);
                                CustomArcadeEntryUI.customArcadeSettingsMenu = null;
                                CustomArcadeEntryUI.selectorOptions.Clear();
                                Log.LogInfo("arcade settings menu will rebuild on next open");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogDebug("arcade menu reset failed: " + e.Message);
                    }
                }
                Log.LogInfo("sync: " + addedCount + " added, " + removedCount + " removed, "
                    + reloaded + " info-reloaded, " + swept + " content-swept, "
                    + unchanged + " unchanged" + (failed > 0 ? ", " + failed + " failed" : ""));
            }
            finally
            {
                FinishLoading();
            }
        }

        private static int IndexOfSet(List<KeyValuePair<string, LevelSetInfoSO>> infos, string key)
        {
            return infos.FindIndex(delegate (KeyValuePair<string, LevelSetInfoSO> kv)
            {
                return string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Remove one set: sweep its resident scene/dependency bundles, release the info
        // bundle, drop the list entry and snapshot.
        private static void UnloadSet(string key)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            int idx = IndexOfSet(infos, key);
            if (idx < 0)
            {
                return;
            }
            try
            {
                SweepSetBundles(infos[idx].Value);
            }
            catch (Exception e)
            {
                Log.LogWarning("sync: sweep of [" + key + "] failed: " + e.Message);
            }
            AssetBundle bundle;
            if (InfoBundles.TryGetValue(key, out bundle) && bundle != null)
            {
                bundle.Unload(false);
            }
            InfoBundles.Remove(key);
            Snapshots.Remove(key);
            infos.RemoveAt(idx);
        }

        // Reload one set whose info changed: sweep with the OLD scene names, unload the
        // old info bundle, load the new one, replace the entry at the same index.
        private IEnumerator ReloadSetRoutine(DirectoryInfo dir, Box done)
        {
            string key = dir.FullName;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            int idx = IndexOfSet(infos, key);
            if (idx < 0)
            {
                done.Value = false;
                yield break;
            }
            try
            {
                SweepSetBundles(infos[idx].Value);
                AssetBundle oldBundle;
                if (InfoBundles.TryGetValue(key, out oldBundle) && oldBundle != null)
                {
                    oldBundle.Unload(false);
                }
                InfoBundles.Remove(key);
            }
            catch (Exception e)
            {
                Log.LogWarning("sync: sweep of [" + dir.Name + "] failed: " + e.Message);
            }
            List<FileInfo> infoFiles = FastInitHost.SafeGetFiles(dir, "info*");
            if (infoFiles.Count == 0)
            {
                // Unload(false) kept the old SO alive, so the stale set still renders.
                Log.LogWarning("sync: info file vanished from [" + dir.Name + "], keeping old set");
                done.Value = false;
                yield break;
            }
            FileInfo infoFile = infoFiles[0];
            AssetBundleCreateRequest req = AssetBundle.LoadFromFileAsync(infoFile.FullName);
            IEnumerator wait = FastInitHost.WaitOp(req, infoFile.Name);
            while (wait.MoveNext())
            {
                yield return wait.Current;
            }
            AssetBundle bundle = req.assetBundle;
            if (bundle == null)
            {
                Log.LogWarning("sync [" + dir.Name + "]: async load failed, trying sync");
                bundle = AssetBundle.LoadFromFile(infoFile.FullName);
            }
            if (bundle == null)
            {
                Log.LogWarning("sync: failed loading info bundle of [" + dir.Name + "], keeping old set");
                done.Value = false;
                yield break;
            }
            AssetBundleRequest ar = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
            IEnumerator waitAr = FastInitHost.WaitOp(ar, dir.Name + " (LevelSetInfo)");
            while (waitAr.MoveNext())
            {
                yield return waitAr.Current;
            }
            LevelSetInfoSO so = ar.asset as LevelSetInfoSO;
            if (so == null)
            {
                so = bundle.LoadAsset("LevelSetInfo", typeof(LevelSetInfoSO)) as LevelSetInfoSO;
            }
            if (so == null)
            {
                Log.LogWarning("sync: missing LevelSetInfo in [" + dir.Name + "], keeping old set");
                bundle.Unload(false);
                done.Value = false;
                yield break;
            }
            infos[idx] = new KeyValuePair<string, LevelSetInfoSO>(key, so);
            InfoBundles[key] = bundle;
            Snapshots[key] = ReadSnapshot(dir, infoFile);
            Log.LogInfo("sync: level set [" + dir.Name + "] reloaded in place");
            done.Value = true;
        }

        // Load one new directory and insert it at its sorted position (directory order
        // must hold: the arcade mod resolves selections by list position).
        private IEnumerator LoadSetRoutine(DirectoryInfo dir, Box done)
        {
            List<FileInfo> infoFiles = FastInitHost.SafeGetFiles(dir, "info*");
            if (infoFiles.Count == 0)
            {
                Log.LogWarning("sync: missing info file: " + dir.Name);
                done.Value = false;
                yield break;
            }
            FileInfo infoFile = infoFiles[0];
            AssetBundleCreateRequest req = AssetBundle.LoadFromFileAsync(infoFile.FullName);
            IEnumerator wait = FastInitHost.WaitOp(req, infoFile.Name);
            while (wait.MoveNext())
            {
                yield return wait.Current;
            }
            AssetBundle bundle = req.assetBundle;
            if (bundle == null)
            {
                Log.LogWarning("sync [" + dir.Name + "]: async load failed, trying sync");
                bundle = AssetBundle.LoadFromFile(infoFile.FullName);
            }
            if (bundle == null)
            {
                Log.LogWarning("sync: failed loading info bundle of " + dir.Name);
                done.Value = false;
                yield break;
            }
            AssetBundleRequest ar = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
            IEnumerator waitAr = FastInitHost.WaitOp(ar, dir.Name + " (LevelSetInfo)");
            while (waitAr.MoveNext())
            {
                yield return waitAr.Current;
            }
            LevelSetInfoSO so = ar.asset as LevelSetInfoSO;
            if (so == null)
            {
                so = bundle.LoadAsset("LevelSetInfo", typeof(LevelSetInfoSO)) as LevelSetInfoSO;
            }
            if (so == null)
            {
                Log.LogWarning("sync: missing LevelSetInfo in " + dir.Name);
                bundle.Unload(false);
                done.Value = false;
                yield break;
            }
            string key = dir.FullName;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            if (IndexOfSet(infos, key) >= 0)
            {
                Log.LogWarning("sync: [" + dir.Name + "] already loaded, skipping insert");
                bundle.Unload(false);
                done.Value = false;
                yield break;
            }
            int insertAt = infos.Count;
            for (int i = 0; i < infos.Count; i++)
            {
                if (string.Compare(infos[i].Key, key, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    insertAt = i;
                    break;
                }
            }
            infos.Insert(insertAt, new KeyValuePair<string, LevelSetInfoSO>(key, so));
            InfoBundles[key] = bundle;
            Snapshots[key] = ReadSnapshot(dir, infoFile);
            Log.LogInfo("sync: level set [" + dir.Name + "] added");
            done.Value = true;
        }

        private static void SweepSetBundles(LevelSetInfoSO so)
        {
            if (so == null || so.levelInfos == null)
            {
                return;
            }
            List<string> sceneNames = new List<string>();
            foreach (LevelInfoSO info in so.levelInfos)
            {
                if (info == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(info.sceneName))
                {
                    sceneNames.Add(info.sceneName);
                }
            }
            // Scene (main) bundles only. NEVER sweep names from info.dependencies:
            // those are VANILLA game bundle names (gua's LoadDependencies expands them
            // via the game's own manifest), and unloading one the frontend still uses
            // is a native access violation. Dependency bundles release correctly via
            // the game's refcounts when the main bundle unloads (UnloadDependencies).
            SweepBundles(sceneNames);
        }

        // Repeatedly UnloadAssetBundle the names (verbatim and lowercase variants: the
        // game lowercases scene bundle keys) until nothing resolves, cap 16 passes for
        // refcounts accumulated by replays.
        private static void SweepBundles(List<string> names)
        {
            for (int pass = 0; pass < 16; pass++)
            {
                bool touched = false;
                foreach (string name in names)
                {
                    touched |= TryUnloadBundle(name);
                    touched |= TryUnloadBundle(name.ToLowerInvariant());
                }
                if (!touched)
                {
                    return;
                }
            }
            foreach (string name in names)
            {
                string err;
                if (AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name, out err) != null
                    || AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name.ToLowerInvariant(), out err) != null)
                {
                    Log.LogWarning("sync: [" + name + "] still resident after 16 sweep passes");
                }
            }
        }

        private static bool TryUnloadBundle(string name)
        {
            try
            {
                string err;
                if (AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name, out err) == null)
                {
                    return false;
                }
                AssetBundles.AssetBundleManager.UnloadAssetBundle(name);
                return true;
            }
            catch (Exception e)
            {
                Log.LogDebug("sync: unload of [" + name + "]: " + e.Message);
                return false;
            }
        }

        // The reload button sits FIRST in the level set menu; find-by-name keeps it
        // from ever duplicating across incremental appends and full rebuilds.
        internal static void EnsureReloadButton(FrontendOptionsMenu menu)
        {
            try
            {
                if (menu == null)
                {
                    return;
                }
                Transform content = menu.transform.Find("SettingsBody/ContentPC/Viewport/Content");
                if (content == null)
                {
                    return;
                }
                T17Button btn;
                Transform existing = content.Find("FastInit_Reload");
                if (existing != null)
                {
                    btn = existing.GetComponent<T17Button>();
                }
                else
                {
                    btn = OC2DIYLevel.UIUtils.AddButton(menu, "FastInit_Reload", "Reload level sets", "刷新关卡列表", TrySyncReload);
                }
                if (btn == null)
                {
                    return;
                }
                btn.gameObject.transform.SetAsFirstSibling();
                ((Button)btn).interactable = !Loading;
            }
            catch (Exception e)
            {
                Log.LogDebug("EnsureReloadButton: " + e.Message);
            }
        }

        internal static SetSnapshot ReadSnapshot(DirectoryInfo dir, FileInfo infoFile)
        {
            SetSnapshot snap = new SetSnapshot();
            try
            {
                snap.InfoFileName = infoFile.Name;
                snap.InfoMtimeUtc = infoFile.LastWriteTimeUtc;
                snap.InfoLength = infoFile.Length;
                DateTime max = DateTime.MinValue;
                foreach (FileInfo f in FastInitHost.SafeGetFiles(dir, "*"))
                {
                    if (f.LastWriteTimeUtc > max)
                    {
                        max = f.LastWriteTimeUtc;
                    }
                }
                snap.DirMaxMtimeUtc = max;
            }
            catch (Exception e)
            {
                Log.LogDebug("snapshot [" + dir.Name + "]: " + e.Message);
            }
            return snap;
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

    // One-shot success flag passed into nested load coroutines.
    internal sealed class Box
    {
        public bool Value;
    }

    // Per-set disk fingerprint taken at load time; sync diffs against it.
    internal sealed class SetSnapshot
    {
        public string InfoFileName;
        public DateTime InfoMtimeUtc;
        public long InfoLength;
        public DateTime DirMaxMtimeUtc;
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

        // Polls an async operation; the outer coroutine forwards wait.Current as its own
        // yield. Gives up after the timeout — the caller then checks isDone and falls
        // back to the sync path.
        internal static IEnumerator WaitOp(AsyncOperation op, string what)
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
                FastInitPlugin.InfoBundles[dir.FullName] = bundle;
                FastInitPlugin.Snapshots[dir.FullName] = FastInitPlugin.ReadSnapshot(dir, p.File);
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
