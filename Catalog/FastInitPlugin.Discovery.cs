using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

        internal static void FinishLoading()
        {
            ThumbnailCache.Invalidate();
            Loading = false;
            FrontendOptionsMenu menu = TryGetMenu();
            if (menu != null)
            {
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            OnDemandResources.QueueThumbnailWarmup();
        }

        private static bool SetupDirectoryPrefix(ref SceneDirectoryData __result)
        {
            __result = SceneDirectoryCache.GetDirectory();
            return false;
        }

        internal static IEnumerator LoadInfoSet(DirectoryInfo dir, InfoLoad result)
        {
            List<FileInfo> files = FastInitHost.SafeGetFiles(dir, "info*");
            if (files.Count == 0)
            {
                Log.LogWarning("missing info file: " + dir.Name);
                yield break;
            }
            result.InfoFile = files[0];
            result.So = LevelCatalogCache.TryRead(dir, result.InfoFile, result);
            if (result.So != null) yield break;
            Log.LogInfo("catalog cache miss [" + dir.Name + "]: " + result.CacheMiss);

            if (UseDirectCatalogReader)
            {
                CatalogMetadataRequest request = null;
                System.Diagnostics.Stopwatch metadataTimer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    try
                    {
                        request = new CatalogMetadataRequest(dir.FullName, result.InfoFile.Name);
                        _catalogRead = request;
                        request.Start();
                    }
                    catch (Exception e)
                    {
                        if (request != null) request.Dispose();
                        if (ReferenceEquals(_catalogRead, request)) _catalogRead = null;
                        request = null;
                        Log.LogWarning("catalog metadata worker unavailable [" + dir.Name + "]: " + e.Message);
                    }
                    if (request != null)
                    {
                        while (!request.Done) yield return null;
                        if (request.Cancelled) yield break;
                        result.MetadataReadMs = metadataTimer.Elapsed.TotalMilliseconds;
                        if (request.Data != null)
                        {
                            double pauseStarted = metadataTimer.Elapsed.TotalMilliseconds;
                            while (OnDemandResources.IsTransitioning || PseudoPrefabManager.isInCustomLevel) yield return null;
                            result.PublicationPauseMs = metadataTimer.Elapsed.TotalMilliseconds - pauseStarted;
                            if (request.Cancelled) yield break;
                            metadataTimer.Reset();
                            metadataTimer.Start();
                            try
                            {
                                FileInfo current = new FileInfo(result.InfoFile.FullName);
                                result.So = LevelCatalogCache.CreateAndStore(dir, current, request.Data);
                                result.InfoFile = current;
                                result.MetadataHit = true;
                                Log.LogInfo("catalog metadata read [" + dir.Name + "]: " + result.So.levelInfos.Length
                                    + " levels, no native info assets");
                            }
                            catch (Exception e) { Log.LogWarning("catalog metadata publication failed [" + dir.Name + "]: " + e.Message); }
                            result.ExtractMs = metadataTimer.Elapsed.TotalMilliseconds;
                            if (result.So != null) yield break;
                        }
                        else Log.LogInfo("catalog metadata fallback [" + dir.Name + "]: " + request.Error);
                    }
                }
                finally
                {
                    if (request != null) request.Dispose();
                    if (ReferenceEquals(_catalogRead, request)) _catalogRead = null;
                }
            }

            Box<AssetBundle> bundle = new Box<AssetBundle>();
            Box<LevelSetInfoSO> full = new Box<LevelSetInfoSO>();
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                yield return OnDemandResources.LoadCatalogBundle(result.InfoFile.FullName, bundle);
                result.BundleMs = timer.Elapsed.TotalMilliseconds;
                if (bundle.Value == null) yield break;
                timer.Reset();
                timer.Start();
                yield return FastInitHost.LoadInfoAsset(bundle.Value, dir.Name, full);
                result.AssetMs = timer.Elapsed.TotalMilliseconds;
                timer.Reset();
                timer.Start();
                if (full.Value != null)
                    result.So = LevelCatalogCache.Extract(dir, result.InfoFile, full.Value);
                else
                    Log.LogWarning("missing LevelSetInfo in " + dir.Name);
                result.ExtractMs = timer.Elapsed.TotalMilliseconds;
            }
            finally
            {
                full.Value = null;
                bundle.Value = null;
                OnDemandResources.ReleaseCatalogBundle();
            }
            timer.Reset();
            timer.Start();
            yield return OnDemandResources.WaitForCleanup();
            result.CleanupMs = timer.Elapsed.TotalMilliseconds;
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
    }
}
