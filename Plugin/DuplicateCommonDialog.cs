using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DIYLevelFastInit.ResourceFailures;
using HarmonyLib;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Detects duplicate loads of any common* asset bundle (including exact "common")
    /// across FastInit, WLoader takeover, and external AssetBundle.LoadFromFile* callers,
    /// logging both paths and showing a coalesced native warning dialog.
    /// </summary>
    internal static class DuplicateCommonDialog
    {
        private sealed class DuplicateEntry
        {
            internal readonly string BundleName;
            internal readonly string LoadedPath;
            internal readonly string DuplicatePath;

            internal DuplicateEntry(string bundleName, string loadedPath, string duplicatePath)
            {
                BundleName = bundleName;
                LoadedPath = loadedPath;
                DuplicatePath = duplicatePath;
            }
        }

        private sealed class PendingLoad
        {
            internal readonly string Path;
            internal readonly AssetBundleCreateRequest Request;

            internal PendingLoad(string path, AssetBundleCreateRequest request)
            {
                Path = path;
                Request = request;
            }
        }

        private const float CoalesceSeconds = 0.25f;

        private static readonly Dictionary<string, string> KnownPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PendingLoad> PendingLoads =
            new Dictionary<string, PendingLoad>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RecordedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<DuplicateEntry> PendingEntries = new List<DuplicateEntry>();

        private static T17DialogBox _activeDialog;
        private static float _showAfter;
        private static bool _errorLogged;

        internal static bool IsCommonBundleName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && name.StartsWith("common", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsResident(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                if (ab != null && string.Equals(ab.name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static void Install(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(DuplicateCommonDialog), nameof(ObserveLoadPrefix));
                HarmonyMethod asyncPostfix = new HarmonyMethod(typeof(DuplicateCommonDialog), nameof(ObserveAsyncLoadPostfix));
                int patched = 0;
                patched += PatchLoad(harmony, "LoadFromFile", new[] { typeof(string) }, prefix, null);
                patched += PatchLoad(harmony, "LoadFromFile", new[] { typeof(string), typeof(uint) }, prefix, null);
                patched += PatchLoad(harmony, "LoadFromFileAsync", new[] { typeof(string) }, prefix, asyncPostfix);
                patched += PatchLoad(harmony, "LoadFromFileAsync", new[] { typeof(string), typeof(uint) }, prefix, asyncPostfix);
                FastInitPlugin.Log.LogInfo("duplicate common* bundle observer ready (" + patched + " load hooks)");
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("duplicate common* bundle observer unavailable: " + e.Message);
            }
        }

        private static int PatchLoad(Harmony harmony, string name, Type[] args, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            MethodInfo method = AccessTools.Method(typeof(AssetBundle), name, args);
            if (method == null) return 0;
            harmony.Patch(method, prefix: prefix, postfix: postfix);
            return 1;
        }

        private static void ObserveLoadPrefix(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string name = Path.GetFileName(path);
                if (!IsCommonBundleName(name)) return;
                string full = NormalizePath(path);
                if (IsResident(name))
                {
                    RecordDuplicate(name, full);
                    return;
                }
                PendingLoad pending;
                if (PendingLoads.TryGetValue(name, out pending))
                {
                    if (pending.Request != null && !pending.Request.isDone)
                    {
                        if (!string.Equals(pending.Path, full, StringComparison.OrdinalIgnoreCase))
                        {
                            RecordDuplicate(name, full, pending.Path);
                            return;
                        }
                    }
                    else
                    {
                        PendingLoads.Remove(name);
                    }
                }
                KnownPaths[name] = full;
            }
            catch { }
        }

        private static void ObserveAsyncLoadPostfix(string path, AssetBundleCreateRequest __result)
        {
            try
            {
                if (__result == null || string.IsNullOrEmpty(path)) return;
                string name = Path.GetFileName(path);
                if (!IsCommonBundleName(name)) return;
                string full = NormalizePath(path);
                PendingLoad existing;
                if (PendingLoads.TryGetValue(name, out existing) && existing.Request != null && !existing.Request.isDone
                    && !string.Equals(existing.Path, full, StringComparison.OrdinalIgnoreCase))
                    return;
                PendingLoads[name] = new PendingLoad(full, __result);
                if (!KnownPaths.ContainsKey(name)) KnownPaths[name] = full;
            }
            catch { }
        }

        internal static void RecordLoaded(string bundleName, string path)
        {
            if (!IsCommonBundleName(bundleName) || string.IsNullOrEmpty(path)) return;
            string full = NormalizePath(path);
            if (!KnownPaths.ContainsKey(bundleName)) KnownPaths[bundleName] = full;
        }

        internal static void RecordDuplicate(string bundleName, string duplicatePath)
        {
            if (!IsCommonBundleName(bundleName)) return;
            string loadedPath;
            KnownPaths.TryGetValue(bundleName, out loadedPath);
            RecordDuplicate(bundleName, duplicatePath, loadedPath);
        }

        private static void RecordDuplicate(string bundleName, string duplicatePath, string loadedPath)
        {
            if (!IsCommonBundleName(bundleName)) return;
            string fullDuplicate = NormalizePath(duplicatePath);
            string fullLoaded = string.IsNullOrEmpty(loadedPath) ? null : NormalizePath(loadedPath);
            string key = bundleName + "\n" + (fullDuplicate ?? string.Empty);
            if (!RecordedKeys.Add(key)) return;

            PendingEntries.Add(new DuplicateEntry(bundleName, fullLoaded, fullDuplicate));
            _showAfter = Time.unscaledTime + CoalesceSeconds;

            string loadedDesc = string.IsNullOrEmpty(fullLoaded)
                ? "<other plugin>"
                : "\"" + fullLoaded + "\"" + FormatFileSize(fullLoaded);
            string duplicateDesc = string.IsNullOrEmpty(fullDuplicate)
                ? "<unknown>"
                : "\"" + fullDuplicate + "\"" + FormatFileSize(fullDuplicate);
            FastInitPlugin.Log.LogWarning("duplicate common bundle detected [" + bundleName + "]: loaded="
                + loadedDesc + ", duplicate=" + duplicateDesc);
        }

        internal static void Tick(bool otherStartupDialogActive)
        {
            try
            {
                if (_activeDialog != null)
                {
                    if (_activeDialog.gameObject != null && _activeDialog.gameObject.activeInHierarchy)
                    {
                        if (!FailureDialogLayout.IsApplied(_activeDialog))
                            FailureDialogLayout.Apply(_activeDialog);
                        return;
                    }
                    _activeDialog = null;
                }
                if (PendingEntries.Count == 0) return;
                if (Time.unscaledTime < _showAfter) return;
                if (otherStartupDialogActive || FailureGate.Active || PreparationFailureNotice.DialogActive) return;
                if (T17DialogBoxManager.Instance == null) return;
                T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
                if (dialog == null) return;

                List<DuplicateEntry> batch = new List<DuplicateEntry>(PendingEntries);

                SupportedLanguages language = Localization.GetLanguage();
                bool chinese = language == SupportedLanguages.Chinese || language == SupportedLanguages.ChineseTraditional;
                string title = chinese
                    ? "检测到重复的 common 资源包"
                    : "Duplicate common asset bundles detected";
                string message = BuildMessage(batch, chinese);

                dialog.Initialize(title, message, "Text.Button.Okay", null, null,
                    T17DialogBox.Symbols.Warning, false, false, false);
                dialog.OnConfirm = delegate { };
                _activeDialog = dialog;
                dialog.Show();
                PendingEntries.Clear();
                _errorLogged = false;
                FastInitPlugin.Log.LogInfo("duplicate common bundle warning dialog shown (" + batch.Count + " duplicate(s))");
            }
            catch (Exception error)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                FastInitPlugin.Log.LogWarning("Duplicate common bundle warning dialog is waiting: " + error);
            }
        }

        internal static void ResetForShutdown()
        {
            KnownPaths.Clear();
            PendingLoads.Clear();
            RecordedKeys.Clear();
            PendingEntries.Clear();
            _activeDialog = null;
            _showAfter = 0f;
            _errorLogged = false;
        }

        private static string BuildMessage(List<DuplicateEntry> entries, bool chinese)
        {
            // Group by (loadedDir, duplicateDir) so multiple duplicate bundles in the same
            // folder pair stay readable inside T17DialogBox's bounded vertical height.
            List<string> groupKeys = new List<string>();
            Dictionary<string, List<string>> groupNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string[]> groupDirs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < entries.Count; i++)
            {
                DuplicateEntry entry = entries[i];
                string loadedDir = DirectoryOf(entry.LoadedPath);
                string dupDir = DirectoryOf(entry.DuplicatePath);
                string key = loadedDir + "\n" + dupDir;
                List<string> names;
                if (!groupNames.TryGetValue(key, out names))
                {
                    names = new List<string>();
                    groupKeys.Add(key);
                    groupNames[key] = names;
                    groupDirs[key] = new[] { loadedDir, dupDir };
                }
                names.Add(entry.BundleName);
            }

            StringBuilder sb = new StringBuilder();
            if (chinese)
                sb.AppendLine("检测到以下 common* 资源包存在重复加载，可能导致关卡缺少菜谱或素材：");
            else
                sb.AppendLine("Duplicate common* asset bundle loads were detected, which may cause missing recipes or assets:");

            int shownGroups = Math.Min(groupKeys.Count, 3);
            for (int i = 0; i < shownGroups; i++)
            {
                string key = groupKeys[i];
                List<string> names = groupNames[key];
                names.Sort(StringComparer.OrdinalIgnoreCase);
                string[] dirs = groupDirs[key];
                string joined = string.Join(chinese ? "、" : ", ", names.ToArray());
                if (chinese)
                {
                    sb.Append("重复文件：").AppendLine(joined);
                    sb.Append(string.IsNullOrEmpty(dirs[0]) ? "已加载来源：其他插件" : "已加载目录：" + dirs[0]).AppendLine();
                    if (!string.IsNullOrEmpty(dirs[1]) && !string.Equals(dirs[0], dirs[1], StringComparison.OrdinalIgnoreCase))
                        sb.Append("重复目录：").AppendLine(dirs[1]);
                }
                else
                {
                    sb.Append("Duplicates: ").AppendLine(joined);
                    sb.Append(string.IsNullOrEmpty(dirs[0]) ? "Loaded by: another plugin" : "Loaded from: " + dirs[0]).AppendLine();
                    if (!string.IsNullOrEmpty(dirs[1]) && !string.Equals(dirs[0], dirs[1], StringComparison.OrdinalIgnoreCase))
                        sb.Append("Duplicate in: ").AppendLine(dirs[1]);
                }
            }
            if (groupKeys.Count > shownGroups)
            {
                sb.AppendLine(chinese
                    ? "……另有 " + (groupKeys.Count - shownGroups) + " 组重复目录，完整列表见 BepInEx 日志。"
                    : "...and " + (groupKeys.Count - shownGroups) + " more duplicate folder pair(s); see BepInEx log.");
            }
            sb.Append(chinese
                ? "请清理旧版或多余的重复文件后重启游戏（完整路径见 BepInEx 日志）。"
                : "Remove the old or redundant duplicate files and restart the game (see BepInEx log for full paths).");
            return sb.ToString();
        }

        private static string DirectoryOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetDirectoryName(path) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static string FormatFileSize(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Exists) return " (" + info.Length + " bytes)";
            }
            catch { }
            return string.Empty;
        }
    }
}
