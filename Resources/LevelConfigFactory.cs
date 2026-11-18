using System;
using System.Reflection;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>Binds the DIYLevel configuration factory and the templates it expects.</summary>
    internal static class LevelConfigFactory
    {
        private static readonly MethodInfo Current = AccessTools.Method(typeof(LevelConfigSetup), "SetupConfig",
            new[] { typeof(LevelInfoSO), typeof(int) });
        private static readonly MethodInfo Legacy = AccessTools.Method(typeof(LevelConfigSetup), "SetupConfig",
            new[] { typeof(PseudoPrefabSO), typeof(LevelInfoSO), typeof(int) });
        private static readonly FieldInfo HordeTemplate = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "hordeConfigTemplateSO");
        private static readonly FieldInfo LevelType = Current == null ? null : AccessTools.Field(typeof(LevelInfoSO), "levelType");
        private static readonly object Horde = LevelType == null ? null : Enum.Parse(LevelType.FieldType, "Horde");

        internal static bool UsesRuntimeTemplates { get { return Current != null; } }
        internal static MethodInfo SetupMethod { get { return Current ?? Legacy; } }

        internal static PseudoPrefabSO GetRuntimeTemplate(LevelInfoSO level)
        {
            FieldInfo field = Equals(LevelType.GetValue(level), Horde) ? HordeTemplate : FastInitPlugin.FiConfigSo;
            return (PseudoPrefabSO)field.GetValue(null);
        }

        internal static void InitializeTemplates(AssetBundle commonBundle)
        {
            if (!UsesRuntimeTemplates)
            {
                FastInitPlugin.FiConfigSo.SetValue(null, commonBundle.LoadAsset<PseudoPrefabSO>("LevelConfigTemplateSO"));
                return;
            }

            // DIYLevel 0.11.1 constructs these descriptors in the Initialize body we skip.
            FastInitPlugin.FiConfigSo.SetValue(null, CreateTemplate("LevelConfigTemplate", "bundle47",
                "Assets/Resources/datafile/levelconfigs/overcooked_2/Sushi_1_3_4P.asset"));
            HordeTemplate.SetValue(null, CreateTemplate("HordeLevelConfigTemplate", "bundle293",
                "Assets/downloadablecontent/dlc07/dlc_assets/data/levelconfigs/Battlements_01_4P.asset"));
        }

        internal static void Validate()
        {
            if (SetupMethod == null || !typeof(LevelConfigBase).IsAssignableFrom(SetupMethod.ReturnType))
                throw new MissingMethodException(typeof(LevelConfigSetup).FullName, "SetupConfig");
            if (FastInitPlugin.FiConfigSo == null || (UsesRuntimeTemplates && (HordeTemplate == null || LevelType == null)))
                throw new MissingFieldException("DIYLevel configuration template fields are missing");
        }

        internal static LevelConfigBase Create(LevelInfoSO level, int playerCount)
        {
            object[] arguments = UsesRuntimeTemplates ? new object[] { level, playerCount }
                : new object[] { FastInitPlugin.FiConfigSo.GetValue(null), level, playerCount };
            return (LevelConfigBase)SetupMethod.Invoke(null, arguments);
        }

        private static PseudoPrefabSO CreateTemplate(string name, string bundle, string assetPath)
        {
            PseudoPrefabSO template = ScriptableObject.CreateInstance<PseudoPrefabSO>();
            template.name = name + "SO";
            template.prefabName = name;
            template.bundleName = bundle;
            template.assetPath = assetPath;
            return template;
        }
    }
}
