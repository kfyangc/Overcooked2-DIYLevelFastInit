using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.CustomArcade;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

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
            if (OnDemandResources.IsTransitioning)
            {
                Log.LogInfo("sync skipped: a DIY level load is in progress");
                return;
            }
            if (OnDemandResources.HasDiySaveSelection || LoadingScreenFlow.IsLoading)
            {
                Log.LogInfo("sync skipped: save selection or scene loading is active");
                return;
            }
            // Sessions can linger in the frontend; PrepareLevel replaces stale directories.
            if (GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu") == null)
            {
                Log.LogInfo("sync skipped: not in the frontend scene");
                return;
            }
            Instance.StartCoroutine(FastInitHost.Guard(Instance.SyncRoutine(), "catalog sync"));
        }

        private IEnumerator SyncRoutine()
        {
            Loading = true;
            Syncing = true;
            QuietDirectoryRefresh = true;
            bool completed = false;
            try
            {
                Log.LogInfo("sync started");
                OnDemandResources.ResetFrontend();
                yield return OnDemandResources.WaitForPreview();
                OnDemandResources.ResetFrontend();
                while (!OnDemandResources.CatalogFilesAreIdle)
                {
                    if (!RefreshContextIsSafe()) yield break;
                    yield return null;
                    OnDemandResources.ResetFrontend();
                }
                Box<LevelDirectorySnapshot> scan = new Box<LevelDirectorySnapshot>();
                yield return ScanDirectoryForSync(scan);
                if (scan.Value == null || !RefreshContextIsSafe()) yield break;
                LevelDirectorySnapshot observed = scan.Value;
                QuietDirectoryRefresh = observed.SameEntriesAs(_notifiedDirectory);
                _notifiedDirectory = observed;
                List<DirectoryInfo> dirs = new List<DirectoryInfo>(observed.Directories);
                TotalSets = dirs.Count;
                List<DirectoryInfo> added = new List<DirectoryInfo>();
                List<DirectoryInfo> infoChanged = new List<DirectoryInfo>();
                List<DirectoryInfo> contentChanged = new List<DirectoryInfo>();
                List<string> removed = new List<string>();
                int unchanged = 0;
                bool diffFailed = false;
                // Diff only — no yields allowed inside this try (C# iterators).
                try
                {
                    Dictionary<string, SetSnapshot> fresh = observed.Sets;
                    foreach (DirectoryInfo dir in dirs)
                    {
                        SetSnapshot snap = fresh[dir.FullName];
                        if (snap.InfoFileName == null)
                        {
                            Log.LogWarning("sync: missing info file: " + dir.Name);
                            continue;
                        }
                        SetSnapshot old;
                        bool hasSnapshot = Snapshots.TryGetValue(dir.FullName, out old);
                        bool inList = IndexOfSet(DIYLevelAssetBundleManager.levelSetInfos, dir.FullName) >= 0;
                        if (!inList)
                        {
                            added.Add(dir);
                        }
                        else if (!hasSnapshot || !LevelDirectorySnapshot.SameInfo(old, snap))
                        {
                            infoChanged.Add(dir);
                        }
                        else if (!LevelDirectorySnapshot.SameSet(old, snap))
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
                        SetSnapshot current;
                        if (!fresh.TryGetValue(kv.Key, out current) || current.InfoFileName == null)
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

                List<DirectoryInfo> changed = new List<DirectoryInfo>(added);
                changed.AddRange(infoChanged);
                changed.AddRange(contentChanged);
                foreach (DirectoryInfo dir in changed)
                {
                    int index = IndexOfSet(DIYLevelAssetBundleManager.levelSetInfos, dir.FullName);
                    if (index >= 0) SweepSetBundles(DIYLevelAssetBundleManager.levelSetInfos[index].Value);
                }
                Box<bool> conversionFailed = new Box<bool>();
                yield return PrepareChangedBundleFiles(observed, changed, conversionFailed);
                if (!RefreshContextIsSafe()) yield break;
                for (int index = contentChanged.Count - 1; index >= 0; index--)
                {
                    DirectoryInfo dir = contentChanged[index];
                    if (!LevelDirectorySnapshot.SameInfo(Snapshots[dir.FullName], observed.Sets[dir.FullName]))
                    {
                        contentChanged.RemoveAt(index);
                        infoChanged.Add(dir);
                    }
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
                    Box<bool> done = new Box<bool>();
                    yield return ReloadSetRoutine(dir, done);
                    if (done.Value)
                    {
                        reloaded++;
                        Snapshots[dir.FullName] = ConsumedSnapshot(observed.Sets[dir.FullName], Snapshots[dir.FullName]);
                        if (!LevelDirectorySnapshot.SameInfo(Snapshots[dir.FullName], observed.Sets[dir.FullName])) failed++;
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
                            Snapshots[dir.FullName] = observed.Sets[dir.FullName];
                            swept++;
                            Log.LogInfo("sync: swept resident bundles of [" + dir.Name + "]");
                        }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Log.LogWarning("sync: content sweep of [" + dir.Name + "] failed: " + e.Message);
                    }
                }
                foreach (DirectoryInfo dir in added)
                {
                    Box<bool> done = new Box<bool>();
                    yield return LoadSetRoutine(dir, done);
                    if (done.Value)
                    {
                        addedCount++;
                        Snapshots[dir.FullName] = ConsumedSnapshot(observed.Sets[dir.FullName], Snapshots[dir.FullName]);
                        if (!LevelDirectorySnapshot.SameInfo(Snapshots[dir.FullName], observed.Sets[dir.FullName])) failed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                try
                {
                    List<KeyValuePair<string, LevelSetInfoSO>> ordered = new List<KeyValuePair<string, LevelSetInfoSO>>();
                    foreach (DirectoryInfo dir in dirs)
                    {
                        int index = IndexOfSet(DIYLevelAssetBundleManager.levelSetInfos, dir.FullName);
                        if (index >= 0) ordered.Add(DIYLevelAssetBundleManager.levelSetInfos[index]);
                    }
                    DIYLevelAssetBundleManager.levelSetInfos = ordered;
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
                    ResetArcadeMenu();
                }
                if (conversionFailed.Value)
                {
                    failed++;
                    foreach (DirectoryInfo dir in changed)
                    {
                        SetSnapshot previous;
                        if (!Snapshots.TryGetValue(dir.FullName, out previous)) continue;
                        SetSnapshot retry = ConsumedSnapshot(previous, previous);
                        retry.Files = null;
                        Snapshots[dir.FullName] = retry;
                    }
                }
                Log.LogInfo("sync: " + addedCount + " added, " + removedCount + " removed, "
                    + reloaded + " info-reloaded, " + swept + " content-swept, "
                    + unchanged + " unchanged" + (failed > 0 ? ", " + failed + " failed" : ""));
                if (failed == 0)
                {
                    AcceptDirectorySnapshot(observed);
                    completed = true;
                }
                else
                {
                    _refreshRetry = true;
                    _directoryChanged = true;
                    _directoryPoller.Rescan();
                }
            }
            finally
            {
                if (!completed)
                {
                    _refreshRetry = true;
                    _directoryChanged = true;
                }
                Syncing = false;
                FinishLoading();
            }
        }

        private static int IndexOfSet(List<KeyValuePair<string, LevelSetInfoSO>> infos, string key)
        {
            return infos.FindIndex(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // The arcade settings menu snapshots selector options at build time; destroy it so
        // the next T17TabPanel.OnTabSelected rebuilds it with fresh options. Nulling the
        // static is not enough: AddCustomArcadeSettingsUI early-returns while the menu
        // GameObject exists, so stale selector rows would survive.
        private static void ResetArcadeMenu()
        {
            try
            {
                FrontendOptionsMenu arcadeMenu = CustomArcadeEntryUI.customArcadeSettingsMenu;
                if (arcadeMenu == null)
                {
                    return;
                }
                if (arcadeMenu.gameObject.activeInHierarchy)
                {
                    Log.LogInfo("sync: arcade settings menu is open, deferring its rebuild");
                    return;
                }
                UnityEngine.Object.Destroy(arcadeMenu.gameObject);
                CustomArcadeEntryUI.customArcadeSettingsMenu = null;
                CustomArcadeEntryUI.selectorOptions.Clear();
                Log.LogInfo("arcade settings menu will rebuild on next open");
            }
            catch (Exception e)
            {
                Log.LogDebug("arcade menu reset failed: " + e.Message);
            }
        }
    }
}
