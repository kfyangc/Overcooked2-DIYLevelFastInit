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
                    "Check all top-level UnityFS bundles in custom level sets, including info*, s_* and custom_recipes, and replace LZMA-compressed files with equivalent LZ4HC files before discovering levels; fast raw/LZ4 layouts are kept as-is. Verified replacements do not retain original files. Restart after changing this option.").Value;
                OptimizeSharedCommonBundlesOnStartup = Config.Bind("Loading", "OptimizeSharedCommonBundlesOnStartup", true,
                    "Check shared dependency bundles (DIYLevel common0* and taken-over WLoader commonW*, excluding synchronous common) and replace LZMA-compressed files with equivalent 128 KiB LZ4HC files in place before loading them, so Unity reads only referenced chunks on demand instead of holding the full ~900 MiB decompressed payload in memory. Verified replacements do not retain original files. Restart after changing this option.").Value;
                RestoreInfoFilesOnStartup = Config.Bind("Loading", "RestoreOriginalInfoBundlesOnStartup", false,
                    "Restore legacy backed-up original bundles on startup. New successful conversions retain no originals. Restoration takes precedence over optimization. Restart after changing this option.").Value;
                InfoBundleOptimizationBatch.MemoryBudgetBytes = Math.Max(16, Math.Min(2048, Config.Bind("Loading", "ConversionMemoryBudgetMiB", 800,
                    "Estimated conversion memory budget (16-2048 MiB). File size, decoder dictionaries and bounded HC buffers determine reservations. Queueing waits for released reservations; CPU workers remain capped at the logical processor count. This is not a process memory limit. Restart after changing this option.").Value)) * 1024L * 1024;
                Log.LogInfo("level bundles use source files directly; runtime LZ4 cache is disabled (legacy cache options are ignored)");
                TakeOverWLoaderBundles = Config.Bind("Loading", "TakeOverWLoaderSharedBundles", true,
                    "Load the web loader's commonW* shared dependency bundles with FastInit scheduling instead of its serial background queue, so entering a web-exported level right after startup cannot race them. Restart after changing this option.").Value;
                WLoaderGateTimeoutSeconds = Config.Bind("Loading", "WLoaderSharedBundleWaitTimeoutSeconds", 60,
                    "Maximum seconds level preparation waits for the shared web dependency bundles before giving way. The wait also ends early when the resident count stops advancing for 20 seconds. Restart after changing this option.").Value;
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

        // Header-only probe mirroring the batch's own classification: does this set
        // need real conversion/restore work? Creates no work directory or lock files.
        internal static bool SetNeedsConversionWork(List<string> paths, bool restore)
        {
            foreach (string path in paths)
            {
                // Leftover recovery data takes the locked cleanup/hash path.
                if (Directory.Exists(Path.Combine(Path.GetDirectoryName(path), ".diylevel-fastinit"))) return true;
                if (restore) continue; // no work directory: NoBackup settles quickly
                // Only LZMA data converts; raw/LZ4/LZ4HC layouts settle quickly.
                if (BundleCodecs.UnityFsRepacker.NeedsConversion(path)) return true;
            }
            return false;
        }

        // A file is blocked only while the active batch still owns it; settled files
        // of other sets stay loadable while their conversion window is open.
        internal static bool InfoFilePreparing(string path)
        {
            InfoBundleOptimizationBatch batch = _infoOptimization;
            return batch != null && batch.IsPending(path);
        }

        // A set is blocked only while its own conversion batch is running.
        internal static bool InfoSetPreparing(string setDirectory)
        {
            return InfoFilesPreparing && setDirectory != null
                && string.Equals(PreparingSetDirectory, setDirectory, StringComparison.OrdinalIgnoreCase);
        }

        // Per-set startup conversion: this set's files are fully converted (workers
        // drained, work directory cleaned) before its catalog request runs, so the
        // set can publish to the level-set menu immediately (see FastInitHost.LoadHeavy).
        internal static IEnumerator PrepareSetInfoFiles(string setDirectory, List<string> paths)
        {
            string setName = Path.GetFileName(setDirectory);
            InfoBundleOptimizationBatch batch = null;
            try
            {
                batch = new InfoBundleOptimizationBatch(paths.ToArray(), RestoreInfoFilesOnStartup);
                _infoOptimization = batch;
                InfoFilesPreparing = true;
                PreparingSetDirectory = setDirectory;
                InfoOptimizationStartedRealtime = UnityEngine.Time.realtimeSinceStartup;
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
                if (!RestoreInfoFilesOnStartup && batch.Done)
                {
                    foreach (InfoBundleOptimizationBatch.PreparedFile prepared in batch.PreparedFiles)
                    {
                        CatalogIndexStore.Default.UpsertFastFile(
                            Path.GetDirectoryName(prepared.Path),
                            Path.GetFileName(prepared.Path),
                            prepared.Length,
                            prepared.Modified,
                            FastFileStamp.KindFastBundle);
                    }
                }
            }
            finally
            {
                if (batch != null) batch.Dispose();
                if (batch == null || batch.Done)
                {
                    InfoFilesPreparing = false;
                    PreparingSetDirectory = null;
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
                PreparingSetDirectory = null;
                InfoOptimizationProgress = null;
                return;
            }
            if (batch.Done)
            {
                batch.Dispose();
                _infoOptimization = null;
                InfoFilesPreparing = false;
                PreparingSetDirectory = null;
                InfoOptimizationProgress = null;
            }
        }

        private static void UpdateInfoOptimization(InfoBundleOptimizationBatch batch)
        {
            batch.Drain(delegate(string message) { Log.LogInfo(message); }, delegate(string message) { Log.LogDebug(message); });
            if (batch.Done)
            {
                if (ReferenceEquals(_infoOptimization, batch))
                {
                    InfoFilesPreparing = false;
                    PreparingSetDirectory = null;
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
            DuplicateCommonDialog.Tick(_blockerConflict != null && _blockerConflict.IsActive);
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
