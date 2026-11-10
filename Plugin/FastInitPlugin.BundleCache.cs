using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {
        private void ConfigureBundleCache()
        {
            BundleFileCache.Shutdown();
            try
            {
                UseIndividualLevelAssets = Config.Bind("Loading", "EnableIndividualLevelAssets", true,
                    "Load the selected LevelInfo asset when its address is unambiguous; fall back to LevelSetInfo when needed. Restart after changing this option.").Value;
                UseDirectCatalogReader = Config.Bind("Loading", "EnableDirectCatalogReader", true,
                    "Build missing catalog files by reading scalar metadata without native info assets. Unsupported bundles fall back to the native scanner. Restart after changing this option.").Value;
                OptimizeInfoFilesOnStartup = Config.Bind("Loading", "OptimizeInfoBundlesOnStartup", true,
                    "Check all top-level UnityFS bundles in custom level sets, including info*, s_* and custom_recipes, and replace supported LZMA/LZ4/raw files with equivalent LZ4HC files before discovering levels. Verified replacements do not retain original files. Restart after changing this option.").Value;
                RestoreInfoFilesOnStartup = Config.Bind("Loading", "RestoreOriginalInfoBundlesOnStartup", false,
                    "Restore legacy backed-up original bundles on startup. New successful conversions retain no originals. Restoration takes precedence over optimization. Restart after changing this option.").Value;
                InfoBundleOptimizationBatch.MemoryBudgetBytes = Math.Max(16, Math.Min(2048, Config.Bind("Loading", "ConversionMemoryBudgetMiB", 800,
                    "Estimated conversion memory budget (16-2048 MiB). File size, decoder dictionaries and bounded HC buffers determine reservations. Queueing waits for released reservations; CPU workers remain capped at the logical processor count. This is not a process memory limit. Restart after changing this option.").Value)) * 1024L * 1024;
                Log.LogInfo("level bundles use source files directly; runtime LZ4 cache is disabled (legacy cache options are ignored)");
            }
            catch (Exception e) { Log.LogWarning("level bundle startup options unavailable; defaults retained: " + e.Message); }
        }

        internal static IEnumerator PrepareInfoBundleFiles(string pluginDir)
        {
            InfoBundleOptimizationBatch batch = null;
            try
            {
                if (!OptimizeInfoFilesOnStartup && !RestoreInfoFilesOnStartup) yield break;
                List<string> paths = new List<string>();
                foreach (DirectoryInfo dir in FastInitHost.SafeGetDirs(Path.Combine(pluginDir, "levels")))
                {
                    foreach (FileInfo file in FastInitHost.SafeGetFiles(dir, "*"))
                        if (LevelBundleFiles.IsBundle(file.FullName)) paths.Add(file.FullName);
                }
                batch = new InfoBundleOptimizationBatch(paths.ToArray(), RestoreInfoFilesOnStartup);
                _infoOptimization = batch;
                InfoFilesPreparing = true;
                InfoOptimizationProgress = (RestoreInfoFilesOnStartup ? "restoring " : "checking ") + "0/" + paths.Count;
                Log.LogInfo("startup level bundle " + (RestoreInfoFilesOnStartup ? "restoration" : "optimization")
                    + " started: " + paths.Count + " files");
                batch.Start();
                while (!batch.Done)
                {
                    UpdateInfoOptimization(batch);
                    yield return null;
                }
                UpdateInfoOptimization(batch);
                Log.LogInfo("startup level bundle preparation complete: " + batch.Completed + "/" + batch.Total);
            }
            finally
            {
                if (batch != null) batch.Dispose();
                if (batch == null || batch.Done)
                {
                    InfoFilesPreparing = false;
                    InfoOptimizationProgress = null;
                    if (ReferenceEquals(_infoOptimization, batch)) _infoOptimization = null;
                }
            }
        }

        private static void UpdateInfoOptimization(InfoBundleOptimizationBatch batch)
        {
            batch.Drain(delegate(string message) { Log.LogInfo(message); });
            if (batch.Done)
            {
                if (ReferenceEquals(_infoOptimization, batch))
                {
                    InfoFilesPreparing = false;
                    InfoOptimizationProgress = null;
                }
                return;
            }
            string current = batch.Current;
            InfoOptimizationProgress = (RestoreInfoFilesOnStartup ? "restoring " : "checking ")
                + batch.Completed + "/" + batch.Total
                + (string.IsNullOrEmpty(current) ? "" : ": " + Path.GetFileName(current));
        }

        private void Update()
        {
            if (_resourceFailureBlocker != null) _resourceFailureBlocker.Tick();
            InfoBundleOptimizationBatch batch = _infoOptimization;
            if (!_patchesReady || batch == null) return;
            UpdateInfoOptimization(batch);
            if (batch.Done && ReferenceEquals(_infoOptimization, batch))
            {
                batch.Dispose();
                _infoOptimization = null;
            }
        }
    }
}
