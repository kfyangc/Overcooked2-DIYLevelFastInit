using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {
        private void EnsureDirectoryPolling()
        {
            if (_destroying || !_started || _directoryPoller != null) return;
            try
            {
                _observedDirectory = new LevelDirectorySnapshot(new DirectoryInfo[0], Snapshots);
                _notifiedDirectory = _observedDirectory;
                _directoryPoller = new LevelDirectoryPoller(Path.Combine(GetDiyPluginDir(), "levels"));
                Log.LogInfo("automatic level refresh: checking shallow directory metadata every 5 seconds");
            }
            catch (Exception error) { Log.LogWarning("directory polling unavailable: " + error.Message); }
        }

        private void LateUpdate()
        {
            if (!_patchesReady || _destroying || _directoryPoller == null || Loading || InfoFilesPreparing) return;
            LevelDirectorySnapshot snapshot;
            string error;
            if (_directoryPoller.TryTake(out snapshot, out error))
            {
                if (snapshot != null)
                {
                    _directoryChanged = _refreshRetry || !snapshot.SameAs(_observedDirectory);
                    _directoryScanFailed = false;
                }
                else if (!_directoryScanFailed)
                {
                    _directoryScanFailed = true;
                    Log.LogWarning("directory poll failed; keeping catalog and retrying: " + error);
                }
            }
            if (!_directoryChanged || Time.realtimeSinceStartup < _nextAutomaticSync) return;
            _nextAutomaticSync = Time.realtimeSinceStartup + 5f;
            if (!CanRefreshDirectory()) return;
            Log.LogInfo("level directory changed; starting automatic refresh");
            StartCoroutine(FastInitHost.Guard(SyncRoutine(), "automatic catalog sync"));
        }

        private static bool CanRefreshDirectory()
        {
            return !Loading && !InfoFilesPreparing && DIYLevelAssetBundleManager.IsInitialized
                && RefreshContextIsSafe();
        }

        private static bool RefreshContextIsSafe()
        {
            return !PseudoPrefabManager.isInCustomLevel && !OnDemandResources.IsTransitioning
                && !OnDemandResources.HasDiySaveSelection && !LoadingScreenFlow.IsLoading
                && GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu") != null;
        }

        private IEnumerator ScanDirectoryForSync(Box<LevelDirectorySnapshot> result)
        {
            EnsureDirectoryPolling();
            if (_directoryPoller == null) yield break;
            _directoryPoller.Rescan();
            string error;
            LevelDirectorySnapshot snapshot;
            while (!_directoryPoller.TryTake(out snapshot, out error))
            {
                if (_destroying) yield break;
                yield return null;
            }
            if (snapshot == null) Log.LogWarning("sync scan failed; keeping catalog: " + error);
            result.Value = snapshot;
        }

        private void AcceptDirectorySnapshot(LevelDirectorySnapshot snapshot)
        {
            _observedDirectory = snapshot;
            _directoryChanged = false;
            _refreshRetry = false;
            _directoryPoller.Rescan();
        }

        private IEnumerator PrepareChangedBundleFiles(LevelDirectorySnapshot source, List<DirectoryInfo> changed, Box<bool> failed)
        {
            if (!OptimizeInfoFilesOnStartup || RestoreInfoFilesOnStartup || changed.Count == 0) yield break;
            List<string> paths = new List<string>();
            foreach (DirectoryInfo directory in changed)
                foreach (LevelDirectorySnapshot.FileStamp file in source.Sets[directory.FullName].Files)
                    if (file.Name.StartsWith("info", StringComparison.OrdinalIgnoreCase)
                        || file.Name.StartsWith("s_", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.Combine(directory.FullName, file.Name));
            if (paths.Count == 0) yield break;
            InfoBundleOptimizationBatch batch = new InfoBundleOptimizationBatch(paths.ToArray(), false);
            try
            {
                _infoOptimization = batch;
                InfoFilesPreparing = true;
                Log.LogInfo("refresh bundle optimization started: " + paths.Count + " files");
                batch.Start();
                while (!batch.Done)
                {
                    if (!RefreshContextIsSafe())
                    {
                        failed.Value = true;
                        batch.Dispose();
                    }
                    UpdateInfoOptimization(batch);
                    yield return null;
                }
                UpdateInfoOptimization(batch);
                failed.Value = failed.Value || batch.Failed != 0 || batch.Completed != batch.Total;
                foreach (InfoBundleOptimizationBatch.PreparedFile file in batch.PreparedFiles)
                {
                    SetSnapshot set = source.Sets[Path.GetDirectoryName(file.Path)];
                    string name = Path.GetFileName(file.Path);
                    foreach (LevelDirectorySnapshot.FileStamp stamp in set.Files)
                        if (string.Equals(stamp.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            stamp.Length = file.Length;
                            stamp.Modified = file.Modified;
                            if (set.InfoFileName == stamp.Name)
                            {
                                set.InfoLength = stamp.Length;
                                set.InfoMtimeUtc = new DateTime(stamp.Modified, DateTimeKind.Utc);
                            }
                            break;
                        }
                }
            }
            finally
            {
                batch.Dispose();
                if (batch.Done)
                {
                    InfoFilesPreparing = false;
                    InfoOptimizationProgress = null;
                    if (ReferenceEquals(_infoOptimization, batch)) _infoOptimization = null;
                }
            }
        }

        private static SetSnapshot ConsumedSnapshot(SetSnapshot prepared, SetSnapshot consumed)
        {
            return new SetSnapshot { DirectoryName = prepared.DirectoryName, Files = prepared.Files,
                InfoFileName = consumed.InfoFileName, InfoLength = consumed.InfoLength, InfoMtimeUtc = consumed.InfoMtimeUtc };
        }
    }
}
