using System;
using BepInEx;
using BepInEx.Bootstrap;
using DIYLevelFastInit.ResourceFailures;

namespace DIYLevelFastInit
{
    /// <summary>Stops duplicate FastInit hooks and defers the uninstall notice until native UI is ready.</summary>
    internal sealed class LegacyPluginConflict
    {
        internal const string LegacyGuid = "yang.oc2.diylevel.fastinit";
        private readonly string _path;
        private T17DialogBox _dialog;
        private bool _shown, _errorLogged;

        private LegacyPluginConflict(string path) { _path = path; }

        internal static LegacyPluginConflict Detect()
        {
            PluginInfo legacy;
            if (!Chainloader.PluginInfos.TryGetValue(LegacyGuid, out legacy)) return null;
            FastInitPlugin.Log.LogError("Legacy FastInit detected (" + LegacyGuid + "): " + legacy.Location
                + ". New FastInit hooks disabled. Uninstall the old DLL and restart the game.");
            return new LegacyPluginConflict(legacy.Location);
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
                string title = chinese ? "检测到旧版 DIYLevel 快速载入（启动速度优化）" : "Legacy DIYLevel FastInit detected";
                string message = chinese
                    ? "检测到旧版 DIYLevel 快速载入（启动速度优化），不能与新版共存。\n旧版路径：\n" + _path
                        + "\n请退出游戏，卸载上述旧版 DLL 后重启游戏。\n本次启动已停用新版，避免重复安装补丁。"
                    : "An old DIYLevel FastInit cannot coexist with this version.\nOld DLL path:\n" + _path
                        + "\nExit the game, uninstall the old DLL above, then restart.\nThe new version is disabled for this session to avoid duplicate hooks.";
                dialog.Initialize(title, message, "Text.Button.Okay", null, null,
                    T17DialogBox.Symbols.Error, false, false, false);
                dialog.OnConfirm = delegate { };
                _dialog = dialog;
                dialog.Show();
                _shown = true;
            }
            catch (Exception error)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                FastInitPlugin.Log.LogWarning("Legacy FastInit conflict dialog is waiting: " + error);
            }
        }
    }
}
