using System;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace DIYLevelFastInit.ResourceFailures
{
    internal static class BlockerRuntime
    {
        private static bool cleanupRequested;
        private static bool popupShown;
        private static bool popupWaitLogged;
        private static bool acknowledgementPending;
        private static FailureInfo acknowledgedFailure;
        private static T17DialogBox pendingPopup;
        private static MethodInfo forceClose;
        private static FieldInfo slotUpdate;
        private static int failureGeneration;
        private static bool forceCloseWatch;
        private static float forceCloseDeadline;
        private static FieldInfo pendingForceCloseField;

        internal static void RequestFailureHandling() { cleanupRequested = true; }

        internal static void Tick()
        {
            if (!FailureGate.Active) return;
            CapForceCloseWatch();
            if (cleanupRequested)
            {
                cleanupRequested = false;
                FastInitPlugin.CancelFailedResourcePreparation();
                CloseDiySaveDialogs();
            }
            if (acknowledgementPending)
            {
                if (!HasActiveDiySaveDialogs()) CompleteAcknowledgement();
                return;
            }
            if (!popupShown)
            {
                ShowPopup();
                return;
            }
            if (!FailureDialogLayout.IsApplied(pendingPopup) && pendingPopup != null && pendingPopup.gameObject.activeInHierarchy)
            {
                // The HostUtilities approach: capture the pooled instance, grow its panel by the message's real height
                // and restore everything when it is hidden. Applied once Show() has made the object active.
                FailureDialogLayout.Apply(pendingPopup);
            }
        }

        private static void CloseDiySaveDialogs()
        {
            try
            {
                SelectSaveDialog[] dialogs = UnityEngine.Object.FindObjectsOfType<SelectSaveDialog>();
                for (int i = 0; i < dialogs.Length; i++)
                {
                    SelectSaveDialog dialog = dialogs[i];
                    if (dialog == null || dialog.DLC != 15) continue;
                    if (slotUpdate == null) slotUpdate = typeof(SelectSaveDialog).GetField("m_slotUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (slotUpdate != null) slotUpdate.SetValue(dialog, null);
                    if (forceClose == null) forceClose = typeof(SelectSaveDialog).GetMethod("ForceClose", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (forceClose != null) forceClose.Invoke(dialog, null);
                    // The game's SelectSaveDialog.Update retries ForceClose forever while m_pendingForceClose stays set.
                    // Watch this attempt and cap the retry window instead of letting the log/CPU loop run unbounded.
                    forceCloseWatch = true;
                    forceCloseDeadline = Time.realtimeSinceStartup + 2f;
                    BlockerLog.Info("UI", "CLOSE", "closed failed DIYLevel save dialog instance=" + dialog.GetInstanceID());
                }
            }
            catch (Exception ex) { BlockerLog.Error("UI", "CLOSE_FAILED", ex.ToString()); }
        }

        private static void CapForceCloseWatch()
        {
            if (!forceCloseWatch) return;
            SelectSaveDialog[] dialogs = Resources.FindObjectsOfTypeAll<SelectSaveDialog>();
            if (pendingForceCloseField == null)
                pendingForceCloseField = typeof(SelectSaveDialog).GetField("m_pendingForceClose", BindingFlags.Instance | BindingFlags.NonPublic);
            bool anyPending = false;
            if (pendingForceCloseField != null)
            {
                for (int i = 0; i < dialogs.Length; i++)
                {
                    SelectSaveDialog dialog = dialogs[i];
                    if (dialog == null || dialog.DLC != 15) continue;
                    if ((bool)pendingForceCloseField.GetValue(dialog)) { anyPending = true; break; }
                }
            }
            if (!anyPending)
            {
                forceCloseWatch = false;
                return;
            }
            if (Time.realtimeSinceStartup < forceCloseDeadline) return;
            forceCloseWatch = false;
            int capped = 0;
            for (int i = 0; i < dialogs.Length; i++)
            {
                SelectSaveDialog dialog = dialogs[i];
                if (dialog == null || dialog.DLC != 15) continue;
                try
                {
                    if (pendingForceCloseField == null || !(bool)pendingForceCloseField.GetValue(dialog)) continue;
                    // Stop SelectSaveDialog.Update from logging "Retrying force close..." every frame; the failure
                    // popup already tells the player the load stopped, so a lingering hidden save panel is not useful.
                    pendingForceCloseField.SetValue(dialog, false);
                    if (dialog.gameObject.activeInHierarchy) dialog.Hide();
                    capped++;
                }
                catch (Exception ex) { BlockerLog.Warning("UI", "FORCE_CLOSE_CAP_FAILED", ex.ToString()); }
            }
            if (capped > 0)
                BlockerLog.Warning("UI", "FORCE_CLOSE_CAP", "stopped save-dialog force-close retries after 2s (dialogs=" + capped + ")");
        }

        private static bool HasActiveDiySaveDialogs()
        {
            SelectSaveDialog[] dialogs = UnityEngine.Object.FindObjectsOfType<SelectSaveDialog>();
            for (int i = 0; i < dialogs.Length; i++)
                if (dialogs[i] != null && dialogs[i].DLC == 15 && dialogs[i].gameObject.activeInHierarchy) return true;
            return false;
        }

        private static void ShowPopup()
        {
            FailureInfo info = FailureGate.First;
            if (info == null || T17DialogBoxManager.Instance == null) return;
            // Serialized popup queue: let the player confirm an open preparation-failure notice first; this popup is
            // retried every tick and shows immediately after the notice closes.
            if (PreparationFailureNotice.DialogActive) return;
            try
            {
                T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
                if (dialog == null)
                {
                    if (!popupWaitLogged)
                    {
                        popupWaitLogged = true;
                        BlockerLog.Warning("Popup", "WAIT", "native dialog pool has no available dialog or primary user");
                    }
                    return;
                }
                dialog.Initialize(ModLocalization.Current.Title, info.PopupText(), "Text.Button.Okay", null, null,
                    T17DialogBox.Symbols.Error, false, false, false);
                BlockerLog.Info("Popup", "TEXT", info.PopupText().Replace("\r", string.Empty).Replace("\n", " | "));
                int generation = failureGeneration;
                dialog.OnConfirm = delegate { ConfirmFailure(info, generation); };
                pendingPopup = dialog;
                dialog.Show();
                popupShown = true;
                BlockerLog.Info("Popup", "QUEUED", "native failure dialog queued for level=" + info.LevelName);
            }
            catch (Exception ex) { BlockerLog.Error("Popup", "FAILED", ex.ToString()); }
        }

        private static void ConfirmFailure(FailureInfo info, int generation)
        {
            if (generation != failureGeneration || !object.ReferenceEquals(FailureGate.First, info))
            {
                BlockerLog.Warning("Popup", "STALE_CONFIRM", "ignored confirmation callback for an obsolete failure");
                return;
            }
            acknowledgementPending = true;
            acknowledgedFailure = info;
            BlockerLog.Info("FailureGate", "ACK_WAIT_CLOSE", "confirmation received; waiting for the failed native save dialog to finish closing");
        }

        private static void CompleteAcknowledgement()
        {
            FailureInfo info = acknowledgedFailure;
            if (!FailureGate.ResetAfterConfirmation(info))
            {
                acknowledgementPending = false;
                acknowledgedFailure = null;
                return;
            }
            failureGeneration++;
            cleanupRequested = false;
            popupShown = false;
            popupWaitLogged = false;
            acknowledgementPending = false;
            acknowledgedFailure = null;
            pendingPopup = null;
            BlockerLog.Info("FailureGate", "ACKNOWLEDGED", "failed native save dialog closed; subsequent explicit level selections may be evaluated independently");
        }

        internal static void OnDialogShown(T17DialogBox dialog)
        {
            if (dialog == null || dialog != pendingPopup) return;
            FailureInfo info = FailureGate.First;
            BlockerLog.Info("Popup", "SHOWN", "native failure dialog became active for level=" + (info == null ? "unknown" : info.LevelName));
        }

        internal static void ResetForShutdown()
        {
            failureGeneration++;
            cleanupRequested = false;
            popupShown = false;
            popupWaitLogged = false;
            acknowledgementPending = false;
            acknowledgedFailure = null;
            pendingPopup = null;
            forceCloseWatch = false;
            forceCloseDeadline = 0f;
        }
    }
}
