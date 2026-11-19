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

        // One startup snapshot of every set's top-level bundle files, in directory
        // order; also seeds the global file counter used by the progress label.
        internal static List<string>[] CollectSetBundlePaths(List<DirectoryInfo> dirs)
        {
            List<string>[] result = new List<string>[dirs.Count];
            int total = 0;
            for (int i = 0; i < dirs.Count; i++)
            {
                List<string> paths = new List<string>();
                foreach (FileInfo file in FastInitHost.SafeGetFiles(dirs[i], "*"))
                    if (LevelBundleFiles.IsBundle(file.FullName)) paths.Add(file.FullName);
                result[i] = paths;
                total += paths.Count;
            }
            InfoOptimizationTotal = total;
            return result;
        }

        // Per-set startup conversion: this set's files are fully converted (workers
        // drained, work directory cleaned) before its catalog request runs, so the
        // set can publish to the level-set menu immediately (see FastInitHost.LoadHeavy).
        internal static IEnumerator PrepareSetInfoFiles(string setName, List<string> paths)
        {
            InfoBundleOptimizationBatch batch = null;
            try
            {
                batch = new InfoBundleOptimizationBatch(paths.ToArray(), RestoreInfoFilesOnStartup);
                _infoOptimization = batch;
                InfoFilesPreparing = true;
                InfoOptimizationProgress = (RestoreInfoFilesOnStartup ? "restoring " : "checking ")
                    + InfoOptimizationDone + "/" + InfoOptimizationTotal;
                Log.LogInfo("startup level bundle " + (RestoreInfoFilesOnStartup ? "restoration" : "optimization")
                    + " started [" + setName + "]: " + paths.Count + " files");
                batch.Start();
                while (!batch.Done)
                {
                    UpdateInfoOptimization(batch);
                    yield return null;
                }
                UpdateInfoOptimization(batch);
                Log.LogInfo("startup level bundle preparation complete [" + setName + "]: "
                    + batch.Completed + "/" + batch.Total + " files");
                InfoOptimizationDone += batch.Completed;
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

        // Defensive end-of-startup gate clear (empty levels dir, conversion options
        // off, or an aborted pipeline). A still-running batch keeps the gate.
        internal static void FinishStartupInfoPreparation()
        {
            InfoBundleOptimizationBatch batch = _infoOptimization;
            if (batch == null)
            {
                InfoFilesPreparing = false;
                InfoOptimizationProgress = null;
                return;
            }
            if (batch.Done)
            {
                batch.Dispose();
                _infoOptimization = null;
                InfoFilesPreparing = false;
                InfoOptimizationProgress = null;
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
                + (InfoOptimizationDone + batch.Completed) + "/" + InfoOptimizationTotal
                + (string.IsNullOrEmpty(current) ? "" : ": " + Path.GetFileName(current));
        }

        private void Update()
        {
            if (_legacyConflict != null)
            {
                _legacyConflict.Tick();
                return;
            }
            if (_blockerConflict != null) _blockerConflict.Tick();
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
