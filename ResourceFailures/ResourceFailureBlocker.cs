using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DIYLevelFastInit.ResourceFailures
{
    /// <summary>Owns the integrated resource-failure hooks, log listener and main-thread updates.</summary>
    internal sealed class ResourceFailureBlocker : IDisposable
    {
        internal const string LegacyGuid = "com.ch3ngyz.plugin.overcooked2.diylevel.resourcereadfailureblocker";
        private readonly object _sync = new object();
        private readonly Queue<Assembly> _assemblies = new Queue<Assembly>();
        private Harmony _harmony;
        private PreparationLogListener _listener;
        private bool _running, _legacyChecked;

        internal static ResourceFailureBlocker Create()
        {
            ResourceFailureBlocker blocker = new ResourceFailureBlocker();
            try
            {
                BlockerLog.Initialize(FastInitPlugin.PluginGuid + ".resource-failures", "FastInitResources");
                blocker._harmony = new Harmony(FastInitPlugin.PluginGuid + ".resource-failures");
                BlockerPatches.InstallGame(blocker._harmony);
                blocker._listener = new PreparationLogListener();
                BepInEx.Logging.Logger.Listeners.Add(blocker._listener);
                AppDomain.CurrentDomain.AssemblyLoad += blocker.AssemblyLoaded;
                blocker._running = true;
                BlockerLog.Info("Lifecycle", "READY", "integrated resource failure handling; original exceptions are preserved");
                return blocker;
            }
            catch (Exception error)
            {
                FastInitPlugin.Log.LogError("Resource failure hooks could not be installed: " + error);
                blocker.Dispose();
                return null;
            }
        }

        private void AssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        { lock (_sync) _assemblies.Enqueue(args.LoadedAssembly); }

        internal void Tick()
        {
            if (!_running) return;
            try
            {
                if (!_legacyChecked)
                {
                    _legacyChecked = true;
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(LegacyGuid))
                        FastInitPlugin.Log.LogWarning("ResourceReadFailureBlocker is integrated into FastInit. Remove the standalone ResourceReadFailureBlocker DLL before restarting to avoid duplicate and unsafe legacy hooks.");
                }
                for (int i = 0; i < 8; i++)
                {
                    Assembly assembly;
                    lock (_sync)
                    {
                        if (_assemblies.Count == 0) break;
                        assembly = _assemblies.Dequeue();
                    }
                    if (BlockerPatches.DiyPatchWindowOpen) BlockerPatches.InstallDiy(_harmony, assembly);
                }
                BlockerRuntime.Tick();
                PreparationFailureNotice.Tick();
            }
            catch (Exception error) { BlockerLog.Error("Lifecycle", "TICK_FAILED", error.ToString()); }
        }

        public void Dispose()
        {
            try
            {
                if (_running) AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
                _running = false;
                if (_listener != null) BepInEx.Logging.Logger.Listeners.Remove(_listener);
                if (_harmony != null) _harmony.UnpatchSelf();
            }
            catch (Exception error) { BlockerLog.Error("Lifecycle", "SHUTDOWN_FAILED", error.ToString()); }
            finally
            {
                lock (_sync) _assemblies.Clear();
                BlockerPatches.ResetForShutdown();
                FailureGate.ResetForShutdown();
                BlockerRuntime.ResetForShutdown();
                PreparationFailureNotice.ResetForShutdown();
                BlockerLog.Close();
            }
        }
    }
}
