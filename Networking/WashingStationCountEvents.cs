using System;
using System.Reflection;
using HarmonyLib;

namespace DIYLevelFastInit
{
    /// <summary>Sends bounded sink counts in separate messages while retaining the authoritative count.</summary>
    internal static class WashingStationCountEvents
    {
        private static readonly MethodInfo SendAdd = AccessTools.Method(typeof(ServerWashingStation), "SendAddPlates");
        private static readonly MethodInfo SendCleaned = AccessTools.Method(typeof(ServerWashingStation), "SendCleanedPlate");
        private static readonly MethodInfo AddPrefix = AccessTools.Method(typeof(WashingStationCountEvents), nameof(SendAddPrefix));
        private static readonly MethodInfo CleanedPrefix = AccessTools.Method(typeof(WashingStationCountEvents), nameof(SendCleanedPrefix));

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(SendAdd, prefix: new HarmonyMethod(AddPrefix));
            harmony.Patch(SendCleaned, prefix: new HarmonyMethod(CleanedPrefix));
        }

        internal static void Uninstall()
        {
            Harmony harmony = new Harmony(FastInitPlugin.PluginGuid);
            harmony.Unpatch(SendAdd, AddPrefix);
            harmony.Unpatch(SendCleaned, CleanedPrefix);
        }

        private static bool SendAddPrefix(ServerWashingStation __instance, int ___m_plateCount)
        {
            SendCount(__instance, WashingStationMessage.MessageType.AddPlates, ___m_plateCount);
            return false;
        }

        private static bool SendCleanedPrefix(ServerWashingStation __instance, int ___m_plateCount)
        {
            SendCount(__instance, WashingStationMessage.MessageType.CleanedPlate, ___m_plateCount);
            return false;
        }

        private static void SendCount(ServerWashingStation station, WashingStationMessage.MessageType type, int count)
        {
            station.SendServerEvent(new WashingStationMessage
            {
                m_msgType = type,
                m_plateCount = Math.Min(count, 15)
            });
        }
    }
}
