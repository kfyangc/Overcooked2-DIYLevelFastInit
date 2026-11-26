using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        internal static void InstallPreviewHooks(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("OC2StageThumbnailPreview.Patches.PreviewController");
            if (type == null) return;
            MethodInfo method = AccessTools.Method(type, "UpdatePreview", new[] { typeof(Image), typeof(LevelInfoSO) });
            if (method == null) return;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(OnDemandResources), nameof(PreviewPrefix)));
            _thumbnailAdapter = true;
            FastInitPlugin.Log.LogInfo("on-demand thumbnails: asynchronous StageThumbnailPreview adapter installed");
        }

        private static bool PreviewPrefix(Image __0, LevelInfoSO level)
        {
            if (FastInitPlugin.Syncing) return false;
            if (__0 == null) return true;
            if (level == null)
            {
                if (_previewRequest != null && ReferenceEquals(_previewRequest.Image, __0))
                {
                    _previewRequest = null;
                    CancelDirectCoverRequest();
                }
                ThumbnailCache.Hide(__0);
                return true;
            }
            if (FastInitPlugin.Syncing || IsTransitioning || PseudoPrefabManager.isInCustomLevel) return true;
            string path;
            if (!FindCatalogLevel(level, out path)) return !FastInitPlugin.InfoFilesPreparing;
            // Published sets preview while another set's conversion batch runs; the
            // set still being converted keeps its previews suppressed.
            if (FastInitPlugin.InfoSetPreparing(path)) return false;
            if (_previewRequest != null && ReferenceEquals(_previewRequest.Image, __0)
                && ReferenceEquals(_previewRequest.Level, level)) return false;

            // Serve memory LRU hits synchronously on the same frame with zero I/O or flicker.
            ThumbnailCache.Entry cached = ThumbnailCache.TryGetMemory(path, level.sceneName);
            if (cached != null)
            {
                CancelDirectCoverRequest();
                _previewRequest = new PreviewRequest { Image = __0, Level = level, Path = path, Started = true };
                _previewPath = null;
                _lastPreviewTime = Time.realtimeSinceStartup;
                ThumbnailCache.Show(cached, __0);
                ReleaseUnused();
                FastInitPlugin.Log.LogInfo("thumbnail cache hit: " + level.sceneName + " (memory, 0 ms)");
                return false;
            }

            CancelDirectCoverRequest();
            ThumbnailCache.Hide(__0);
            _previewRequest = new PreviewRequest { Image = __0, Level = level, Path = path };
            _previewPath = path;
            _lastPreviewTime = Time.realtimeSinceStartup;
            if (_previewRunning || FastInitPlugin.Instance == null) return false;
            _previewRunning = true;
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(PreviewRoutine(), "thumbnail queue"));
            return false;
        }

        private static bool PreviewIsCurrent(PreviewRequest request)
        {
            return ReferenceEquals(_previewRequest, request) && request.Image != null && request.Level != null
                && request.Image.transform.parent != null && request.Image.transform.parent.gameObject.activeInHierarchy
                && !FastInitPlugin.InfoSetPreparing(request.Path) && !FastInitPlugin.Syncing && !IsTransitioning && !PseudoPrefabManager.isInCustomLevel;
        }

        private static IEnumerator PreviewRoutine()
        {
            try
            {
                while (_previewRequest != null)
                {
                    PreviewRequest request = _previewRequest;
                    if (request == null) break;
                    if (!PreviewIsCurrent(request))
                    {
                        if (ReferenceEquals(_previewRequest, request)) _previewRequest = null;
                        break;
                    }
                    if (request.Started) break;
                    request.Started = true;
                    if (TryShowCachedPreview(request)) continue;

                    Box<bool> handled = new Box<bool>();
                    yield return FastInitHost.Guard(
                        LoadDirectPreview(request, handled),
                        "direct thumbnail " + Path.GetFileName(request.Path));
                    if (handled.Value || !PreviewIsCurrent(request)) continue;

                    // Rare fallback: LZMA/external-sprite/unsupported texture uses native async load.
                    _previewLoading = true;
                    try
                    {
                        while (_warmLoading && PreviewIsCurrent(request)) yield return null;
                        if (!PreviewIsCurrent(request)) continue;
                        ReleaseUnused();
                        while (_cleanupQueued && PreviewIsCurrent(request)) yield return null;
                        while (_catalogBundles != null && PreviewIsCurrent(request)) yield return null;
                        if (!PreviewIsCurrent(request)) continue;
                        yield return FastInitHost.Guard(LoadPreview(request), "thumbnail " + Path.GetFileName(request.Path));
                    }
                    finally { _previewLoading = false; }
                }
            }
            finally
            {
                CancelDirectCoverRequest();
                _previewRunning = false;
                _previewBundles = null;
                ReleaseUnused();
            }
        }

        private static IEnumerator LoadDirectPreview(PreviewRequest request, Box<bool> handled)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string infoPath = ResolveInfoPath(request.Path);
            if (infoPath == null) yield break;

            DirectCoverRequest direct = new DirectCoverRequest(infoPath, request.Level.sceneName);
            _directCoverRequest = direct;
            try
            {
                direct.Start();
                while (!direct.Done && PreviewIsCurrent(request)) yield return null;
                if (!PreviewIsCurrent(request))
                {
                    direct.Dispose();
                    yield break;
                }

                DirectCoverData result = direct.Result;
                if (result == null) yield break;

                if (result.Status == DirectCoverStatus.Ok || result.Status == DirectCoverStatus.NoScreenshot)
                {
                    double uploadStart = timer.Elapsed.TotalMilliseconds;
                    ThumbnailCache.Entry entry = null;
                    try { entry = ThumbnailCache.StoreDirect(request.Path, request.Level.sceneName, result); }
                    catch (Exception e)
                    {
                        FastInitPlugin.Log.LogWarning("thumbnail direct upload failed [" + request.Level.sceneName + "]: " + e.Message);
                    }
                    if (entry != null)
                    {
                        double uploadMs = timer.Elapsed.TotalMilliseconds - uploadStart;
                        ThumbnailCache.Show(entry, request.Image);
                        _previewPath = null;
                        handled.Value = true;
                        if (result.Status == DirectCoverStatus.NoScreenshot)
                        {
                            FastInitPlugin.Log.LogInfo("thumbnail ready: " + request.Level.sceneName
                                + " (direct, no screenshot, " + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms)");
                        }
                        else
                        {
                            FastInitPlugin.Log.LogInfo("thumbnail ready: " + request.Level.sceneName
                                + " (direct, " + StartupTimings.Ms(timer.Elapsed.TotalMilliseconds) + " ms; index="
                                + StartupTimings.Ms(result.IndexMs) + ", read=" + StartupTimings.Ms(result.ReadMs)
                                + ", resize=" + StartupTimings.Ms(result.ResizeMs) + ", upload=" + StartupTimings.Ms(uploadMs) + ")");
                        }
                        yield break;
                    }
                }
                else if (result.Status == DirectCoverStatus.Fallback)
                {
                    FastInitPlugin.Log.LogInfo("thumbnail direct fallback [" + request.Level.sceneName + "]: " + result.FallbackReason);
                }
            }
            finally
            {
                direct.Dispose();
                if (ReferenceEquals(_directCoverRequest, direct)) _directCoverRequest = null;
            }
        }

        private static string ResolveInfoPath(string setPath)
        {
            SetSnapshot snap;
            if (FastInitPlugin.Snapshots.TryGetValue(setPath, out snap) && snap != null && snap.InfoFileName != null)
                return Path.Combine(setPath, snap.InfoFileName);
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(setPath), "info*");
            return files.Count > 0 ? files[0].FullName : null;
        }

        private static IEnumerator LoadPreview(PreviewRequest request)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Box<LevelInfoSO> full = new Box<LevelInfoSO>();
            try
            {
                if (TryShowCachedPreview(request)) yield break;
                List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(request.Path), "info*");
                if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", request.Path);
                _previewBundles = InfoBundleGraph.Required(files[0].FullName, null);
                ReleaseUnused();
                foreach (string path in _previewBundles)
                {
                    if (!PreviewIsCurrent(request)) yield break;
                    yield return EnsureInfoBundleAsync(path);
                }
                if (!PreviewIsCurrent(request)) yield break;
                yield return EnsureLevelInfoAsync(request.Path, request.Level.sceneName, full, 0, () => PreviewIsCurrent(request));
                if (!PreviewIsCurrent(request)) yield break;
                if (full.Value == null) throw new IOException("Level no longer in preview info: " + request.Level.sceneName);
                ThumbnailCache.Entry thumbnail = null;
                try { thumbnail = ThumbnailCache.Capture(request.Path, request.Level.sceneName, full.Value.screenshot); }
                catch (Exception e) { FastInitPlugin.Log.LogWarning("thumbnail cache capture skipped: " + e.Message); }
                if (thumbnail != null)
                {
                    ThumbnailCache.Show(thumbnail, request.Image);
                    _previewPath = null;
                }
                else
                {
                    request.Image.sprite = full.Value.screenshot;
                    request.Image.gameObject.SetActive(full.Value.screenshot != null);
                }
                FastInitPlugin.Log.LogInfo("thumbnail ready: " + request.Level.sceneName + " (async, " + timer.ElapsedMilliseconds + " ms elapsed)");
            }
            finally
            {
                full.Value = null;
                _previewBundles = null;
                ReleaseUnused();
            }
        }

        private static void CancelDirectCoverRequest()
        {
            DirectCoverRequest request = _directCoverRequest;
            _directCoverRequest = null;
            if (request != null) request.Dispose();
        }

        private static void CancelPreview()
        {
            CancelDirectCoverRequest();
            _previewRequest = null;
            _previewPath = null;
            ThumbnailCache.Hide(null);
        }

        internal static void ShutdownPreviews()
        {
            CancelPreview();
            ThumbnailCache.ClearMemory();
            DirectCoverReader.ClearCache();
        }

        private static bool TryShowCachedPreview(PreviewRequest request)
        {
            if (!PreviewIsCurrent(request)) return false;
            bool memoryHit;
            ThumbnailCache.Entry cached = ThumbnailCache.TryGet(request.Path, request.Level.sceneName, out memoryHit);
            if (cached == null) return false;
            ThumbnailCache.Show(cached, request.Image);
            _previewPath = null;
            ReleaseUnused();
            FastInitPlugin.Log.LogInfo("thumbnail cache hit: " + request.Level.sceneName + " (memory, 0 ms)");
            return true;
        }

        internal static IEnumerator WaitForPreview()
        {
            while (_previewRunning || _warmRunning || DirectCoverReader.ActiveReads > 0) yield return null;
        }

        private static bool FindCatalogLevel(LevelInfoSO level, out string path)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> catalog = DIYLevelAssetBundleManager.levelSetInfos;
            if (catalog != null)
                foreach (KeyValuePair<string, LevelSetInfoSO> set in catalog)
                    foreach (LevelInfoSO item in set.Value.levelInfos)
                        if (ReferenceEquals(item, level)) { path = set.Key; return true; }
            path = null;
            return false;
        }
    }
}
