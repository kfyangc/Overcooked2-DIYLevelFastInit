using System;
using BepInEx;
using BepInEx.Bootstrap;
using DIYLevelFastInit.ResourceFailures;

namespace DIYLevelFastInit
{
    /// <summary>Native uninstall notice for a conflicting plugin DLL, deferred until native UI is ready.</summary>
    internal sealed class PluginConflictDialog
    {
        internal const string LegacyGuid = "yang.oc2.diylevel.fastinit";
        // The standalone blocker shipped the resource-failure hooks FastInit now integrates.
        internal const string StandaloneBlockerGuid = "com.ch3ngyz.plugin.overcooked2.diylevel.resourcereadfailureblocker";

        private readonly string _path;
        private readonly string _titleCn, _titleEn, _messageCn, _messageEn;
        private T17DialogBox _dialog;
        private bool _shown, _errorLogged;

        internal bool IsActive
        {
            get
            {
                return _dialog != null && _dialog.gameObject != null && _dialog.gameObject.activeInHierarchy;
            }
        }

        private PluginConflictDialog(string path, string titleCn, string titleEn, string messageCn, string messageEn)
        {
            _path = path;
            _titleCn = titleCn; _titleEn = titleEn;
            _messageCn = messageCn; _messageEn = messageEn;
        }

        // Legacy FastInit cannot coexist at all: both versions install the same core hooks.
        internal static PluginConflictDialog DetectLegacyFastInit()
        {
            PluginInfo legacy;
            if (!Chainloader.PluginInfos.TryGetValue(LegacyGuid, out legacy)) return null;
            FastInitPlugin.Log.LogError("Legacy FastInit detected (" + LegacyGuid + "): " + legacy.Location
                + ". New FastInit hooks disabled. Uninstall the old DLL and restart the game.");
            return new PluginConflictDialog(legacy.Location,
                "检测到旧版 DIYLevel 快速载入（启动速度优化）", "Legacy DIYLevel FastInit detected",
                "检测到旧版 DIYLevel 快速载入（启动速度优化），不能与新版共存。\n旧版路径：\n" + legacy.Location
                    + "\n请退出游戏，卸载上述旧版 DLL 后重启游戏。\n本次启动已停用新版，避免重复安装补丁。",
                "An old DIYLevel FastInit cannot coexist with this version.\nOld DLL path:\n" + legacy.Location
                    + "\nExit the game, uninstall the old DLL above, then restart.\nThe new version is disabled for this session to avoid duplicate hooks.");
        }

        // The standalone blocker only duplicates the integrated failure hooks: FastInit core
        // keeps running and the integrated blocker stays uninstalled until the DLL is removed.
        internal static PluginConflictDialog DetectStandaloneBlocker()
        {
            PluginInfo blocker;
            if (!Chainloader.PluginInfos.TryGetValue(StandaloneBlockerGuid, out blocker)) return null;
            FastInitPlugin.Log.LogError("Standalone ResourceReadFailureBlocker detected (" + StandaloneBlockerGuid + "): "
                + blocker.Location + ". Integrated failure hooks stay disabled; the rest of FastInit keeps running. Uninstall the standalone DLL and restart the game.");
            return new PluginConflictDialog(blocker.Location,
                "检测到独立版 DIYLevel 选档空白修复（ResourceReadFailureBlocker）",
                "Standalone DIYLevel ResourceReadFailureBlocker detected",
                "选档空白修复（ResourceReadFailureBlocker）已集成进 DIYLevel 快速载入（启动速度优化），独立版不能共存。\n独立版路径：\n" + blocker.Location
                    + "\n请退出游戏，卸载上述独立版 DLL 后重启游戏。\n本次启动已停用集成的失败拦截功能，避免重复安装补丁；快速载入等其他功能不受影响。",
                "ResourceReadFailureBlocker is integrated into DIYLevel FastInit and cannot coexist with the standalone DLL.\nStandalone DLL path:\n" + blocker.Location
                    + "\nExit the game, uninstall the standalone DLL above, then restart.\nThe integrated failure handling is disabled for this session to avoid duplicate hooks; the rest of FastInit keeps running.");
        }

        internal void Tick()
        {
            try
            {
                if (_shown)
                {
                    if (_dialog != null && _dialog.gameObject.activeInHierarchy && !FailureDialogLayout.IsApplied(_dialog))
                        FailureDialogLayout.Apply(_dialog);
                    return;
                }
                if (T17DialogBoxManager.Instance == null) return;
                T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
                if (dialog == null) return;
                SupportedLanguages language = Localization.GetLanguage();
                bool chinese = language == SupportedLanguages.Chinese || language == SupportedLanguages.ChineseTraditional;
                dialog.Initialize(chinese ? _titleCn : _titleEn, chinese ? _messageCn : _messageEn,
                    "Text.Button.Okay", null, null, T17DialogBox.Symbols.Error, false, false, false);
                dialog.OnConfirm = delegate { };
                _dialog = dialog;
                dialog.Show();
                _shown = true;
            }
            catch (Exception error)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                FastInitPlugin.Log.LogWarning("Plugin conflict dialog is waiting: " + error);
            }
        }
    }
}
