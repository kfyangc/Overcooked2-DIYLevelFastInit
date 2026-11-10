using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace DIYLevelFastInit.ResourceFailures
{
    // FastInit 1.7 reports on-demand preparation failures through its BepInEx ManualLogSource ("FastInitPlugin.Log")
    // on both the direct LoadPrefix path ("DIY level preparation failed [scene]: exception", rethrown) and the
    // server/warm FastInitHost.Guard path ("DIY level preparation [scene] failed: exception"). Those events never
    // reach Application.logMessageReceivedThreaded: BepInEx's UnityLogListener writes raw text into the log stream,
    // which does not raise the managed log callbacks. Detection therefore hooks BepInEx's own log bus through
    // PreparationLogListener — the same bus every ManualLogSource (and UnityLogSource forwarding Unity logs)
    // publishes to — so both paths are caught without patching FastInit internals.
    //
    // The parsed failure queues here and the native dialog is shown on the main thread from the plugin tick. Queued
    // notices are kept until the dialog is actually shown (a startup warm-scan failure fires long before the
    // frontend exists), deduplicated within a load attempt, and bounded while queued. A new explicit attempt can
    // report the same scene again. While the FailureGate popup is active the notices wait.
    internal static class PreparationFailureNotice
    {
        // FastInitHost.Guard catch: "DIY level preparation [scene] failed: reason"
        private const string SceneFirstTrigger = "DIY level preparation [";
        private const string SceneFirstMarker = "] failed:";
        // LoadPrefix catch: "DIY level preparation failed [scene]: reason"
        private const string FailedFirstTrigger = "DIY level preparation failed [";
        private const string FailedFirstMarker = "]:";
        private const int MaxPendingNotices = 5;
        private const int MaxReasonLength = 220;

        private static readonly object Gate = new object();
        private static readonly HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string[]> pending = new List<string[]>();
        private static T17DialogBox activeDialog;
        private static int shown;
        private static bool popupWaitLogged;

        internal static bool DialogActive
        {
            get
            {
                return activeDialog != null && activeDialog.gameObject != null
                    && activeDialog.gameObject.activeInHierarchy;
            }
        }

        internal static int ShownCount { get { return shown; } }

        internal static void BeginAttempt(string scene)
        {
            lock (Gate)
            {
                reported.Remove(scene);
                for (int i = pending.Count - 1; i >= 0; i--)
                    if (string.Equals(pending[i][0], scene, StringComparison.OrdinalIgnoreCase)) pending.RemoveAt(i);
            }
        }

        // Called from any thread by PreparationLogListener with the raw log text.
        internal static void Observe(LogLevel level, string sourceName, string message)
        {
            if (message == null) return;
            if ((level & (LogLevel.Error | LogLevel.Fatal)) == 0) return;
            // Self-trigger guard: BlockerLog publishes to the same bus, so its own lines must be skipped by source
            // (their wording also avoids the trigger phrases, but the source check makes it structural).
            if (!string.IsNullOrEmpty(sourceName) && sourceName == BlockerLog.SourceTag) return;
            string scene;
            string reason;
            if (!TryParse(message, out scene, out reason)) return;
            FailureInfo failure = FailureGate.First;
            if (failure != null && string.Equals(failure.SceneName, scene, StringComparison.OrdinalIgnoreCase)) return;
            lock (Gate)
            {
                if (pending.Count >= MaxPendingNotices) return;
                if (!reported.Add(scene)) return;
                pending.Add(new string[] { scene, reason });
            }
            BlockerLog.Warning("Preparation", "OBSERVED",
                "FastInit level preparation failed; scene=" + scene + " reason=" + reason);
        }

        // Both observed log shapes share the bracketed scene; the marker after the closing bracket decides the path.
        // Scene and reason must round-trip from arbitrary log text, so parsing is strict: a matching trigger without
        // its marker (for example FastInit's "already pending" debug line) is rejected.
        internal static bool TryParse(string condition, out string scene, out string reason)
        {
            scene = string.Empty;
            reason = string.Empty;
            if (string.IsNullOrEmpty(condition)) return false;
            int start = condition.IndexOf(SceneFirstTrigger, StringComparison.Ordinal);
            if (start >= 0)
            {
                int sceneStart = start + SceneFirstTrigger.Length;
                int sceneEnd = condition.IndexOf(']', sceneStart);
                if (sceneEnd > sceneStart && StartsWithAt(condition, SceneFirstMarker, sceneEnd))
                {
                    scene = condition.Substring(sceneStart, sceneEnd - sceneStart);
                    reason = FirstLine(condition, sceneEnd + SceneFirstMarker.Length);
                    return scene.Length > 0;
                }
            }
            start = condition.IndexOf(FailedFirstTrigger, StringComparison.Ordinal);
            if (start >= 0)
            {
                int sceneStart = start + FailedFirstTrigger.Length;
                int sceneEnd = condition.IndexOf(']', sceneStart);
                if (sceneEnd > sceneStart && StartsWithAt(condition, FailedFirstMarker, sceneEnd))
                {
                    scene = condition.Substring(sceneStart, sceneEnd - sceneStart);
                    reason = FirstLine(condition, sceneEnd + FailedFirstMarker.Length);
                    return scene.Length > 0;
                }
            }
            return false;
        }

        // Runs on the Unity main thread from the plugin tick.
        internal static void Tick()
        {
            try
            {
                if (activeDialog != null)
                {
                    if (!FailureDialogLayout.IsApplied(activeDialog)
                        && activeDialog.gameObject != null && activeDialog.gameObject.activeInHierarchy)
                        FailureDialogLayout.Apply(activeDialog);
                    // One notice at a time: the next queued failure waits for the player to confirm this dialog.
                    if (DialogActive) return;
                    activeDialog = null;
                }
                // The FailureGate popup owns the dialog flow while it is active; queued notices resume once the gate
                // is acknowledged and reset (BlockerRuntime.ShowPopup defers to DialogActive the same way).
                if (FailureGate.Active) return;
                string[] next = null;
                lock (Gate)
                {
                    if (pending.Count > 0) next = pending[0];
                }
                if (next == null) return;
                Show(next);
            }
            catch (Exception ex) { BlockerLog.Error("Preparation", "TICK_FAILED", ex.ToString()); }
        }

        private static void Show(string[] item)
        {
            if (T17DialogBoxManager.Instance == null)
            {
                // Keep the notice queued: a warm-scan failure fires long before the frontend exists.
                LogPopupWait("native dialog manager not ready yet");
                return;
            }
            T17DialogBox dialog = T17DialogBoxManager.GetDialog(false);
            if (dialog == null)
            {
                LogPopupWait("native dialog pool has no available dialog");
                return;
            }
            string scene = item[0];
            string message = ModLocalization.PreparationMessage(scene, item[1]);
            dialog.Initialize(ModLocalization.Current.PreparationTitle, message, "Text.Button.Okay", null, null,
                T17DialogBox.Symbols.Error, false, false, false);
            dialog.OnConfirm = delegate
            {
                BlockerLog.Info("Preparation", "CONFIRMED", "preparation-failure dialog confirmed: scene=" + scene);
            };
            activeDialog = dialog;
            dialog.Show();
            lock (Gate)
            {
                pending.RemoveAt(0);
                shown++;
            }
            popupWaitLogged = false;
            BlockerLog.Info("Preparation", "TEXT", message.Replace("\r", string.Empty).Replace("\n", " | "));
            BlockerLog.Info("Preparation", "SHOWN",
                "native preparation-failure dialog shown: scene=" + scene + " shown=" + shown);
        }

        private static void LogPopupWait(string reason)
        {
            if (popupWaitLogged) return;
            popupWaitLogged = true;
            BlockerLog.Warning("Preparation", "WAIT", "preparation-failure popup is waiting: " + reason);
        }

        private static string FirstLine(string text, int start)
        {
            if (start < 0 || start >= text.Length) return string.Empty;
            string rest = text.Substring(start);
            int newline = rest.IndexOfAny(LineBreaks);
            if (newline >= 0) rest = rest.Substring(0, newline);
            rest = rest.TrimStart(' ');
            if (rest.Length > MaxReasonLength) rest = rest.Substring(0, MaxReasonLength);
            return rest;
        }

        private static readonly char[] LineBreaks = new char[] { '\r', '\n' };

        private static bool StartsWithAt(string text, string marker, int index)
        {
            if (index < 0 || index + marker.Length > text.Length) return false;
            for (int i = 0; i < marker.Length; i++)
                if (text[index + i] != marker[i]) return false;
            return true;
        }

        internal static void ResetForShutdown()
        {
            lock (Gate)
            {
                reported.Clear();
                pending.Clear();
                shown = 0;
            }
            activeDialog = null;
            popupWaitLogged = false;
        }
    }

    // Registered on BepInEx.Logging.Logger.Listeners from the plugin Awake. Receives every log event — all
    // ManualLogSource writes (FastInit included) and Unity logs re-emitted by BepInEx's UnityLogSource — on whatever
    // thread the source logged from; Observe only queues, so thread safety is a lock, not a dispatch.
    internal sealed class PreparationLogListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs == null || eventArgs.Data == null) return;
                string message = eventArgs.Data as string;
                if (message == null) message = eventArgs.Data.ToString();
                PreparationFailureNotice.Observe(eventArgs.Level,
                    eventArgs.Source == null ? null : eventArgs.Source.SourceName, message);
            }
            catch { }
        }

        public void Dispose() { }
    }
}
