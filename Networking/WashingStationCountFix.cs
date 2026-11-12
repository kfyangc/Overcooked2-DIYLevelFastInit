using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BitStream;
using HarmonyLib;

namespace DIYLevelFastInit
{
    /// <summary>Keeps the sink's outgoing four-bit count positive until the server finishes washing.</summary>
    internal static class WashingStationCountFix
    {
        private static readonly MethodInfo Serialise = AccessTools.Method(typeof(WashingStationMessage), "Serialise");
        private static readonly FieldInfo PlateCount = AccessTools.Field(typeof(WashingStationMessage), "m_plateCount");
        private static readonly MethodInfo WriteCount = AccessTools.Method(typeof(BitStreamWriter), "Write", new[] { typeof(uint), typeof(int) });
        private static readonly MethodInfo ClampCount = AccessTools.Method(typeof(WashingStationCountFix), nameof(GetWireCount));
        private static readonly MethodInfo Transpiler = AccessTools.Method(typeof(WashingStationCountFix), nameof(SerialiseTranspiler));

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(Serialise, transpiler: new HarmonyMethod(Transpiler));
        }

        internal static void Uninstall()
        {
            new Harmony(FastInitPlugin.PluginGuid).Unpatch(Serialise, Transpiler);
        }

        private static IEnumerable<CodeInstruction> SerialiseTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int countIndex = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!codes[i].LoadsField(PlateCount)) continue;
                if (countIndex >= 0) throw new NotSupportedException("Multiple washing station count loads in serializer.");
                countIndex = i;
            }
            if (countIndex < 0) throw new NotSupportedException("Washing station count load not found in serializer.");
            int writeIndex = countIndex + 1;
            if (writeIndex < codes.Count && codes[writeIndex].opcode == OpCodes.Conv_U4) writeIndex++;
            if (writeIndex + 1 >= codes.Count || codes[writeIndex].opcode != OpCodes.Ldc_I4_4
                || !codes[writeIndex + 1].Calls(WriteCount))
                throw new NotSupportedException("Washing station count serializer is not the expected four-bit write.");

            // Clamp the loaded value, leaving the reusable message and authoritative server count untouched.
            codes.Insert(countIndex + 1, new CodeInstruction(OpCodes.Call, ClampCount));
            return codes;
        }

        private static int GetWireCount(int count)
        {
            return Math.Min(count, 15);
        }
    }
}
