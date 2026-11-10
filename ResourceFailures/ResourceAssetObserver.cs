using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;

namespace DIYLevelFastInit.ResourceFailures
{
    /// <summary>Observes typed asset calls at non-generic callers without patching Mono's shared generic bodies.</summary>
    internal static class ResourceAssetObserver
    {
        private static readonly OpCode[] Single = new OpCode[256], Double = new OpCode[256];
        private static readonly MethodInfo Wrapper = typeof(ResourceAssetObserver).GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic);

        static ResourceAssetObserver()
        {
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                OpCode code = (OpCode)field.GetValue(null);
                if (code.Size == 1) Single[(byte)code.Value] = code;
                else Double[(byte)code.Value] = code;
            }
        }

        internal static int Install(Harmony harmony, Assembly assembly)
        {
            int patched = 0;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types; }
            foreach (Type type in types)
            {
                if (type == null || type.ContainsGenericParameters) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.IsGenericMethod) continue;
                    // The untyped overload already has its resource finalizer.
                    if (method.DeclaringType.FullName == "OC2DIYLevel.PseudoPrefabManager" && method.Name == "LoadAsset") continue;
                    if (!CallsLoader(method)) continue;
                    harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ResourceAssetObserver), "ObserveCalls"));
                    patched++;
                    BlockerLog.Debug("Harmony", "TYPED_RESOURCE_CALLER", method.DeclaringType.FullName + "." + method.Name);
                }
            }
            BlockerLog.Info("Harmony", "TYPED_RESOURCES_READY", patched + " non-generic callers; generic LoadAsset bodies unchanged");
            return patched;
        }

        private static bool IsLoader(MethodInfo method)
        {
            return method != null && method.IsGenericMethod && method.Name == "LoadAsset"
                && method.DeclaringType == typeof(PseudoPrefabManager)
                && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(PseudoPrefabSO);
        }

        private static IEnumerable<CodeInstruction> ObserveCalls(IEnumerable<CodeInstruction> source)
        {
            foreach (CodeInstruction instruction in source)
            {
                MethodInfo target = instruction.operand as MethodInfo;
                if (instruction.opcode == OpCodes.Call && IsLoader(target))
                    instruction.operand = Wrapper.MakeGenericMethod(target.GetGenericArguments());
                yield return instruction;
            }
        }

        private static T Load<T>(PseudoPrefabSO reference) where T : UnityEngine.Object
        {
            T result = null;
            Exception failure = null;
            try { result = PseudoPrefabManager.LoadAsset<T>(reference); return result; }
            catch (Exception error) { failure = error; throw; }
            finally { BlockerPatches.ObserveAsset(reference, result, failure); }
        }

        private static bool CallsLoader(MethodInfo method)
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) return false;
            byte[] bytes = body.GetILAsByteArray();
            for (int position = 0; position < bytes.Length;)
            {
                byte first = bytes[position++];
                OpCode code = first == 254 ? Double[bytes[position++]] : Single[first];
                if (code.OperandType == OperandType.InlineMethod && code == OpCodes.Call)
                {
                    MethodInfo target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, position),
                        method.DeclaringType.GetGenericArguments(), method.GetGenericArguments()) as MethodInfo;
                    if (IsLoader(target)) return true;
                }
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: position++; break;
                    case OperandType.InlineVar: position += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: position += 8; break;
                    case OperandType.InlineSwitch:
                        position += 4 + BitConverter.ToInt32(bytes, position) * 4; break;
                    default: position += 4; break;
                }
            }
            return false;
        }
    }
}
