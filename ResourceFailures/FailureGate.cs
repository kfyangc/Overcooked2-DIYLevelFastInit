using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace DIYLevelFastInit.ResourceFailures
{
    internal sealed class FailureInfo
    {
        internal string LevelName;
        internal string SceneName;
        internal string LevelSetDirectory;
        internal string LevelSetRoot;
        internal string LevelFile;
        internal object SourceLevelInfo;
        internal int PlayerCount;
        internal string Phase;
        internal string Resource;
        internal string ExceptionText;
        internal string ExceptionMessage;
        internal string ExceptionType;
        internal bool ReferenceMissing;
        internal string ReferenceCaller;
        internal string TemplateResource;

        internal string PopupText()
        {
            LocalizedText text = ModLocalization.Current;
            string setDirectory = Safe(LevelSetDirectory, text.UnknownSet);
            string levelSetPath = CombinePath(Safe(LevelSetRoot, "levels"), setDirectory);
            string levelPath = CombinePath(levelSetPath, Safe(LevelFile, text.UnknownLevel));
            string levelLine = string.Format(text.LevelSetPattern, setDirectory, levelSetPath)
                + string.Format(text.LevelPattern, Safe(LevelName, text.UnknownLevel), levelPath);
            string resourceLine;
            string templateLine = string.Empty;
            if (ReferenceMissing)
            {
                // The failing descriptor itself is null, so there is no asset path to print. Say exactly that, name the
                // caller that asked for it, and attach the level-config template the setup was building from.
                resourceLine = text.ResourceLabel + (string.IsNullOrEmpty(ReferenceCaller)
                    ? text.NullReferenceNoCaller
                    : string.Format(text.NullReferencePattern, ReferenceCaller));
                if (!string.IsNullOrEmpty(TemplateResource))
                    templateLine = "\n" + text.TemplateLabel + ResourceSummary(TemplateResource, text);
            }
            else resourceLine = text.ResourceLabel + ResourceSummary(Resource, text);
            return text.Header + "\n" + levelLine + "\n" + resourceLine + templateLine + "\n" + FormatError(text);
        }

        private string FormatError(LocalizedText text)
        {
            string kind = Safe(ExceptionType, text.UnknownResource);
            int separator = kind.LastIndexOf('.');
            if (separator >= 0) kind = kind.Substring(separator + 1);
            string message = (ExceptionMessage ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 220) message = message.Substring(0, 220);
            return string.Format(text.ErrorLine, kind + (message.Length == 0 ? string.Empty : ": " + message));
        }

        private static string Safe(string value, string fallback)
        { return string.IsNullOrEmpty(value) ? fallback : value; }

        private static string ResourceSummary(string value, LocalizedText text)
        {
            if (string.IsNullOrEmpty(value)) return text.UnknownResource;
            int bundleStart = value.IndexOf("bundle=", StringComparison.Ordinal);
            int assetStart = value.IndexOf(" asset=", StringComparison.Ordinal);
            if (bundleStart < 0 || assetStart < 0) return NormalizePath(value);
            string bundle = value.Substring(bundleStart + 7, assetStart - bundleStart - 7).Trim();
            string asset = NormalizePath(value.Substring(assetStart + 7).Trim());
            const string longPrefix = "Assets/Resources/datafile/levelconfigs/overcooked_2/";
            if (asset.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
                asset = asset.Substring(longPrefix.Length);
            if (string.IsNullOrEmpty(bundle)) bundle = text.UnknownBundle;
            if (string.IsNullOrEmpty(asset)) asset = text.UnknownResource;
            return bundle + "/" + asset;
        }

        private static string NormalizePath(string value)
        { return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\\', '/'); }

        private static string CombinePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left)) return NormalizePath(right);
            if (string.IsNullOrEmpty(right)) return NormalizePath(left);
            return NormalizePath(left).TrimEnd('/') + "/" + NormalizePath(right).TrimStart('/');
        }

        internal static string LevelFileName(string sceneName, string levelName)
        {
            string value = string.IsNullOrEmpty(sceneName) ? levelName : sceneName;
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return NormalizePath(value).TrimStart('/');
        }

        internal static string FindLevelSetDirectory(object levelInfo)
        {
            if (levelInfo == null) return string.Empty;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type manager = assemblies[i].GetType("OC2DIYLevel.DIYLevelAssetBundleManager", false);
                    if (manager == null) continue;
                    FieldInfo field = manager.GetField("levelSetInfos", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    IEnumerable entries = field == null ? null : field.GetValue(null) as IEnumerable;
                    if (entries == null) return FallbackLevelSetDirectory(levelInfo);
                    foreach (object entry in entries)
                    {
                        if (entry == null) continue;
                        Type pairType = entry.GetType();
                        PropertyInfo valueProperty = pairType.GetProperty("Value");
                        PropertyInfo keyProperty = pairType.GetProperty("Key");
                        object levelSet = valueProperty == null ? null : valueProperty.GetValue(entry, null);
                        if (levelSet == null) continue;
                        if (!LevelSetContains(levelSet, levelInfo)) continue;
                        object key = keyProperty == null ? null : keyProperty.GetValue(entry, null);
                        string path = key == null ? string.Empty : key.ToString();
                        path = NormalizePath(path).TrimEnd('/');
                        int slash = path.LastIndexOf('/');
                        string directory = slash < 0 ? path : path.Substring(slash + 1);
                        if (!string.IsNullOrEmpty(directory)) return directory;
                        break;
                    }
                    return FallbackLevelSetDirectory(levelInfo);
                }
            }
            catch (Exception ex)
            {
                BlockerLog.Warning("FailureGate", "LEVEL_SET_LOOKUP", ex.ToString());
            }
            return FallbackLevelSetDirectory(levelInfo);
        }

        // A background warm scan can see LevelInfoSO instances that are not the ones registered in levelSetInfos, and it
        // can run before that list exists. Match by identity first, then by the level's own names, and finally by the
        // "info_<set>" asset name so the popup never shows "未知" for a level that is right in front of it.
        private static bool LevelSetContains(object levelSet, object levelInfo)
        {
            if (object.ReferenceEquals(levelSet, levelInfo)) return true;
            Type setType = levelSet.GetType();
            FieldInfo levelsField = setType.GetField("levelInfos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            IEnumerable levels = levelsField == null ? null : levelsField.GetValue(levelSet) as IEnumerable;
            if (levels == null) return false;
            string wantedLevel = MemberText(levelInfo, "levelName");
            string wantedScene = MemberText(levelInfo, "sceneName");
            foreach (object level in levels)
            {
                if (object.ReferenceEquals(level, levelInfo)) return true;
                if (level == null) continue;
                if (!string.IsNullOrEmpty(wantedLevel) && !string.IsNullOrEmpty(wantedScene)
                    && string.Equals(MemberText(level, "levelName"), wantedLevel, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(MemberText(level, "sceneName"), wantedScene, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string MemberText(object value, string field)
        {
            if (value == null) return string.Empty;
            try
            {
                FieldInfo info = value.GetType().GetField(field,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object result = info == null ? null : info.GetValue(value);
                return result == null ? string.Empty : result.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string FallbackLevelSetDirectory(object levelInfo)
        {
            try
            {
                UnityEngine.Object unity = levelInfo as UnityEngine.Object;
                string name = unity == null ? string.Empty : unity.name;
                if (string.IsNullOrEmpty(name)) return string.Empty;
                const string infoPrefix = "info_";
                if (name.StartsWith(infoPrefix, StringComparison.OrdinalIgnoreCase)) return name.Substring(infoPrefix.Length);
            }
            catch (Exception ex)
            {
                BlockerLog.Warning("FailureGate", "LEVEL_SET_FALLBACK", ex.ToString());
            }
            return string.Empty;
        }
    }

    internal static class FailureGate
    {
        private static readonly object Gate = new object();
        private static FailureInfo first;

        internal static bool Active { get { lock (Gate) { return first != null; } } }
        internal static FailureInfo First { get { lock (Gate) { return first; } } }

        internal static void Activate(string phase, object levelInfo, int playerCount, object resource, Exception error, string fallbackLevel)
        {
            ActivateDetailed(phase, levelInfo, playerCount, resource, error, fallbackLevel, false, null, null);
        }

        internal static void ActivateDetailed(string phase, object levelInfo, int playerCount, object resource,
            Exception error, string fallbackLevel, bool referenceMissing, string referenceCaller, string templateResource)
        {
            FailureInfo info = new FailureInfo();
            info.SourceLevelInfo = levelInfo;
            info.LevelName = Text(levelInfo, "levelName");
            info.SceneName = Text(levelInfo, "sceneName");
            if (levelInfo is string && string.IsNullOrEmpty(info.LevelName)) info.LevelName = (string)levelInfo;
            info.LevelSetDirectory = FailureInfo.FindLevelSetDirectory(levelInfo);
            info.LevelSetRoot = "levels";
            info.LevelFile = FailureInfo.LevelFileName(info.SceneName, info.LevelName);
            info.PlayerCount = playerCount;
            info.Phase = phase;
            info.Resource = ResourceText(resource);
            info.ReferenceMissing = referenceMissing;
            info.ReferenceCaller = referenceCaller;
            info.TemplateResource = templateResource;
            info.ExceptionText = error == null ? string.Empty : error.ToString();
            info.ExceptionMessage = error == null ? string.Empty : error.Message;
            info.ExceptionType = error == null ? "null result" : error.GetType().FullName;
            if (string.IsNullOrEmpty(info.LevelName)) info.LevelName = fallbackLevel;
            Activate(info, phase, error);
        }

        internal static void Activate(string phase, string message, Exception error)
        {
            FailureInfo info = new FailureInfo();
            info.LevelName = "DIYLevel 初始化";
            info.LevelSetRoot = "levels";
            info.LevelFile = string.Empty;
            info.Phase = phase;
            info.ExceptionText = (message ?? string.Empty) + (error == null ? string.Empty : Environment.NewLine + error);
            info.ExceptionMessage = error == null ? string.Empty : error.Message;
            info.ExceptionType = error == null ? "resource unavailable" : error.GetType().FullName;
            Activate(info, phase, error);
        }

        private static void Activate(FailureInfo info, string phase, Exception error)
        {
            bool firstFailure = false;
            lock (Gate)
            {
                if (first == null)
                {
                    first = info;
                    firstFailure = true;
                }
            }
            if (firstFailure)
            {
                BlockerLog.Error("FailureGate", "ACTIVATE", "DIYLevel blocked; level=" + info.LevelName
                    + " scene=" + info.SceneName + " phase=" + info.Phase
                    + " resource=" + info.Resource + " exception=" + info.ExceptionText);
                BlockerRuntime.RequestFailureHandling();
            }
            else
            {
                FailureInfo original = First;
                if (original != null
                    && (original.ExceptionType == "null result" || original.ExceptionType == "resource unavailable"))
                {
                    if (error != null)
                    {
                        original.ExceptionType = error.GetType().FullName;
                        original.ExceptionText = error.ToString();
                        original.ExceptionMessage = error.Message;
                    }
                    if (phase == "LevelConfigSetup.SetupConfig" && !string.IsNullOrEmpty(info.Resource))
                        original.Resource = info.Resource;
                    original.Phase = phase;
                    BlockerLog.Error("FailureGate", "ENRICHED", "retained the outer level-config resource context and attached the propagating exception: " + (error == null ? "none" : error.ToString()));
                }
                else BlockerLog.Warning("FailureGate", "ADDITIONAL", "additional failure after gate activation: " + info.ExceptionText);
            }
        }

        internal static bool ShouldSuppressIteratorException(Exception error)
        {
            if (error == null || !Active) return false;
            BlockerLog.Error("FailureGate", "ITERATOR", "terminating failed UI iterator while preserving original exception: " + error);
            return true;
        }

        internal static bool ResetAfterConfirmation(FailureInfo expected)
        {
            if (expected == null) return false;
            lock (Gate)
            {
                if (!object.ReferenceEquals(first, expected)) return false;
                first = null;
                return true;
            }
        }

        internal static void ResetForShutdown() { lock (Gate) first = null; }

        private static string Text(object value, string field)
        {
            if (value == null) return string.Empty;
            try
            {
                UnityEngine.Object unity = value as UnityEngine.Object;
                if (field == "name" && unity != null) return unity.name;
                System.Reflection.FieldInfo info = value.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                object result = info == null ? null : info.GetValue(value);
                return result == null ? string.Empty : result.ToString();
            }
            catch (Exception ex)
            {
                BlockerLog.Warning("FailureGate", "REFLECTION", "field=" + field + " error=" + ex);
                return string.Empty;
            }
        }

        private static string ResourceText(object resource)
        {
            if (resource == null) return string.Empty;
            string bundle = Text(resource, "bundleName");
            string path = Text(resource, "assetPath");
            if (string.IsNullOrEmpty(bundle) && string.IsNullOrEmpty(path)) return resource.ToString();
            return "bundle=" + bundle + " asset=" + path;
        }
    }
}
