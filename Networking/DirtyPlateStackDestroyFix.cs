using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>Destroys whole dirty stacks without the recursive message's fixed descendant array.</summary>
    internal static class DirtyPlateStackDestroyFix
    {
        private static readonly Type Messenger = typeof(NetworkUtils).Assembly.GetType("ServerMessenger", true);
        private static readonly MethodInfo DestroyEntities = AccessTools.Method(Messenger, "DestroyEntities");
        private static readonly MethodInfo Prefix = AccessTools.Method(typeof(DirtyPlateStackDestroyFix), nameof(DestroyEntitiesPrefix));
        private static readonly Func<GameObject, bool> DestroyEntity =
            (Func<GameObject, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, bool>), AccessTools.Method(Messenger, "DestroyEntity"));

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(DestroyEntities, prefix: new HarmonyMethod(Prefix));
        }

        internal static void Uninstall()
        {
            new Harmony(FastInitPlugin.PluginGuid).Unpatch(DestroyEntities, Prefix);
        }

        private static bool DestroyEntitiesPrefix(GameObject __0, ref bool __result)
        {
            if (__0 == null || __0.GetComponent(typeof(DirtyPlateStack)) == null) return true;
            // Dirty plates are consumed as a whole stack; the original client destroys its children with the root.
            __result = DestroyEntity(__0);
            return false;
        }
    }
}
