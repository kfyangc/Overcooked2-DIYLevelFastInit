using System;
using System.Collections.Generic;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {
        // Quick per-set checks last milliseconds; surface the checking line only
        // once a batch runs past this so the corner label never flickers.
        private const float InfoProgressDelaySeconds = 0.5f;

        private void OnGUI()
        {
            bool catalogLoading = !QuietDirectoryRefresh && Loading && (Syncing || TotalSets > 0);
            string infoPreparing = QuietDirectoryRefresh ? null : InfoOptimizationProgress;
            if (infoPreparing != null
                && Time.realtimeSinceStartup - InfoOptimizationStartedRealtime < InfoProgressDelaySeconds)
            {
                infoPreparing = null;
            }
            string levelLoading = OnDemandResources.LevelLoadProgress;
            bool thumbnails = !QuietDirectoryRefresh && OnDemandResources.WarmingThumbnails
                && !OnDemandResources.IsTransitioning && !PseudoPrefabManager.isInCustomLevel;
            if (!catalogLoading && !thumbnails && levelLoading == null && infoPreparing == null)
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
                // Foreground level entry outranks background conversion progress.
                string text = levelLoading != null ? "DIY level preparing " + levelLoading
                    : infoPreparing != null ? "DIY info " + infoPreparing
                    : catalogLoading ? (Syncing ? "DIY levels refreshing" : "DIY levels loading " + done + "/" + TotalSets)
                    : "DIY previews caching " + OnDemandResources.ThumbnailDone + "/" + OnDemandResources.ThumbnailTotal;
                Rect rect = new Rect(Screen.width - 336, 6, 330, 26);
                // The label used to assume 330px was enough; longer states (entry dependency
                // progress) were clipped. Size the rect to the text instead, so no wording
                // change can truncate it again; the screen width remains the hard bound.
                GUIContent content = new GUIContent(text);
                float width = Math.Min(Screen.width - 16f, _progressStyle.CalcSize(content).x + 12f);
                if (width > rect.width) rect = new Rect(Screen.width - width - 6f, rect.y, width, rect.height);
                _progressStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), content, _progressStyle);
                _progressStyle.normal.textColor = Color.white;
                GUI.Label(rect, content, _progressStyle);
            }
            catch (Exception)
            {
            }
        }
    }
}
