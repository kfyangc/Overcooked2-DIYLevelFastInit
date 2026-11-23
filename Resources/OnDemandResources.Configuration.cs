using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        internal static bool TryFindScenePath(string scene, out string path)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> catalog = DIYLevelAssetBundleManager.levelSetInfos;
            if (catalog != null)
                foreach (KeyValuePair<string, LevelSetInfoSO> set in catalog)
                    foreach (LevelInfoSO info in set.Value.levelInfos)
                        if (string.Equals(info.sceneName, scene, StringComparison.OrdinalIgnoreCase))
                        { path = set.Key; return true; }
            path = null;
            return false;
        }

        internal static bool TryFindInstalledScenePath(string scene, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(scene) || scene == "." || scene == ".."
                || scene.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;
            foreach (DirectoryInfo dir in FastInitHost.SafeGetDirs(Path.Combine(FastInitPlugin.GetDiyPluginDir(), "levels")))
                if (File.Exists(Path.Combine(dir.FullName, scene)) && FastInitHost.SafeGetFiles(dir, "info*").Count != 0)
                { path = dir.FullName; return true; }
            return false;
        }

        private static string FindScenePath(string scene)
        {
            string path;
            if (TryFindScenePath(scene, out path)) return path;
            throw new InvalidOperationException("DIY level not found in catalog: " + scene);
        }

        private static void PrepareLevel(string scene)
        {
            string path = FindScenePath(scene);
            _pendingScene = scene;
            CancelPreview();
            ThumbnailCache.ClearMemory();
            ReleaseUnused();
            LoadSet(path, scene);
            LevelInfoSO full = Resolve(path, scene);
            // Synchronous entry path (client replay/direct callers): no wait window
            // here, so unresolved references fail precisely before SetupConfig.
            string unresolvedDetail;
            int unresolved = CountUnresolvedReferences(full, out unresolvedDetail);
            if (unresolved > 0)
                throw new IOException("unresolved level references: " + scene + " (" + unresolvedDetail
                    + "; shared web bundles " + WLoaderInterop.StateDescription() + ")");
            SceneDirectoryData.SceneDirectoryEntry entry = PrepareSessionEntry(scene);
            entry.LoadScreenOverride = full.screenshot;
            foreach (SceneDirectoryData.PerPlayerCountDirectoryEntry variant in entry.SceneVarients) PrepareVariant(full, variant);
            FastInitPlugin.Log.LogInfo("on-demand config ready: " + scene + " (" + Levels.Count + " levels retained)");
        }

        // Counts references that SetupConfig dereferences, directly or through
        // PseudoPrefabManager/RecipeHelper. A null element is always a cross-bundle
        // reference that deserialized before its target bundle was resident; SetupConfig
        // turns each of them into its own crash (order dedup, LoadAsset(null), null
        // orders in the recipe list), so the entry gate treats them all alike.
        internal static int CountUnresolvedReferences(LevelInfoSO level)
        {
            string detail;
            return CountUnresolvedReferences(level, out detail);
        }

        internal static int CountUnresolvedReferences(LevelInfoSO level, out string detail)
        {
            detail = null;
            if (level == null) return 0;
            Dictionary<string, int> parts = new Dictionary<string, int>();
            Accumulate(parts, "recipes", CountNull(level.recipes, RecipeLimit(level)));
            Accumulate(parts, "recipeListSOs", CountNull(level.recipeListSOs, -1));
            RecipeListSO[] lists = level.recipeListSOs;
            if (lists != null)
                for (int i = 0; i < lists.Length; i++)
                    if (lists[i] != null) Accumulate(parts, "phaseRecipes", CountNull(lists[i].recipes, -1));
            Accumulate(parts, "includeRecipeMatchLists", CountNull(level.includeRecipeMatchLists, -1));
            Accumulate(parts, "optionalRecipeMatchListItems", CountNull(level.optionalRecipeMatchListItems, -1));
            Accumulate(parts, "allIngredients", CountNull(level.allIngredients, -1));
            Accumulate(parts, "allCookingSteps", CountNull(level.allCookingSteps, -1));
            bool horde = level.levelType == LevelInfoSO.LevelType.Horde;
            LevelConfigSetupPerPlayerCountSO[] variants = { level.config_1p, level.config_2p, level.config_3p, level.config_4p };
            foreach (LevelConfigSetupPerPlayerCountSO variant in variants)
            {
                if (variant == null) { Accumulate(parts, "playerConfigs", 1); continue; }
                Accumulate(parts, "scriptedRecipes", CountNull(variant.scriptedRecipes, -1));
                if (!horde) continue;
                HordeLevelConfigSO hordeConfig = variant.hordeLevelConfig;
                if (hordeConfig == null || hordeConfig.hordeWaveDataSOs == null) continue;
                foreach (HordeWaveDataSO wave in hordeConfig.hordeWaveDataSOs)
                {
                    if (wave == null) { Accumulate(parts, "hordeWaves", 1); continue; }
                    Accumulate(parts, "hordeEnemies", CountNull(wave.enemies, -1));
                    if (wave.recipeListSO == null) { Accumulate(parts, "hordeWaves", 1); continue; }
                    Accumulate(parts, "hordeRecipes", CountNull(wave.recipeListSO.recipes, -1));
                }
            }
            int unresolved = 0;
            foreach (KeyValuePair<string, int> part in parts) unresolved += part.Value;
            if (unresolved == 0) return 0;
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, int> part in parts)
            {
                if (text.Length != 0) text.Append(',');
                text.Append(part.Key).Append('=').Append(part.Value);
            }
            detail = text.ToString();
            return unresolved;
        }

        // SetupConfig takes debugRecipeCount recipes (Take clamps to the array length).
        private static int RecipeLimit(LevelInfoSO level)
        {
            ScriptableObject[] recipes = level.recipes;
            if (recipes == null) return 0;
            return level.debugRecipeCount == 0 ? recipes.Length : Math.Min(level.debugRecipeCount, recipes.Length);
        }

        private static int CountNull(ScriptableObject[] items, int limit)
        {
            if (items == null) return 0;
            if (limit < 0 || limit > items.Length) limit = items.Length;
            int unresolved = 0;
            for (int i = 0; i < limit; i++)
                if (items[i] == null) unresolved++;
            return unresolved;
        }

        private static void Accumulate(Dictionary<string, int> parts, string name, int count)
        {
            if (count <= 0) return;
            int known;
            parts.TryGetValue(name, out known);
            parts[name] = known + count;
        }

        private static SceneDirectoryData.SceneDirectoryEntry PrepareSessionEntry(string scene)
        {
            SceneDirectoryData directory = SceneDirectoryCache.GetDirectory();
            GameSession session = GameUtils.GetGameSession();
            if (session == null || session.DLC != 15 || !ReferenceEquals(session.Progress.GetSceneDirectory(), directory))
                session = DIYLevelAssetBundleManager.StartEmptySession(session != null && session.DLC == 15 ? session.SaveSlot : -1);
            foreach (SceneDirectoryData.SceneDirectoryEntry entry in directory.Scenes)
            {
                if (!string.Equals(entry.SceneVarients[0].SceneName, scene, StringComparison.OrdinalIgnoreCase)) continue;
                return entry;
            }
            throw new InvalidOperationException("Missing scene directory entry: " + scene);
        }

        private static void PrepareVariant(LevelInfoSO full, SceneDirectoryData.PerPlayerCountDirectoryEntry variant)
        {
            _directoryBuild = true;
            try
            {
                if (variant.LevelConfig == null)
                    variant.LevelConfig = LevelConfigFactory.Create(full, variant.PlayerCount);
                variant.Screenshot = full.screenshot;
            }
            finally { _directoryBuild = false; }
        }

        private static void ConfigPrefix(ref LevelInfoSO config, ref string __state)
        {
            __state = _buildingScene;
            string path;
            if (FindCatalogLevel(config, out path)) config = Resolve(path, config.sceneName);
            _buildingScene = config.sceneName;
        }

        private static void ConfigPostfix(CampaignLevelConfig __result)
        {
            Record(__result);
            Record(__result.m_recipeMatchingList);
            // SetupConfig clones only the first round's recipe list; other rounds may share assets.
            if (__result.m_rounds.Length != 0) Record(__result.m_rounds[0].m_recipes);
        }

        private static IEnumerable<CodeInstruction> TrackConfigAllocations(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo record = AccessTools.Method(typeof(OnDemandResources), nameof(Record));
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                MethodInfo method = instruction.operand as MethodInfo;
                if (instruction.opcode != OpCodes.Call || method == null || !method.IsGenericMethod) continue;
                // Track the cloned template and recipe lists even if setup fails before returning a config.
                if ((method.DeclaringType == typeof(ScriptableObject) && method.Name == "CreateInstance")
                    || (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "Instantiate"
                        && method.ReturnType == typeof(LevelConfigBase)))
                {
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Call, record);
                }
            }
        }

        private static Exception ConfigFinalizer(Exception __exception, string __state)
        {
            _buildingScene = __state;
            return __exception;
        }

        private static void CustomRecipePostfix(object __result, object[] __args)
        {
            if (__args[0] is CustomRecipeSO) Record(__result as UnityEngine.Object);
        }

        private static void OwnedPostfix(object __result) { Record(__result as UnityEngine.Object); }
        private static void ClonePostfix(GameObject __result) { Record(__result); }

        private static void Record(UnityEngine.Object value)
        {
            if (value == null) return;
            string scene = _buildingScene;
            if (scene == null && PseudoPrefabManager.isInCustomLevel && PseudoPrefabManager.stub != null)
                scene = PseudoPrefabManager.stub.levelInfo.sceneName;
            if (scene == null) return;
            LevelResources resources;
            if (!_directoryBuild && PseudoPrefabManager.isInCustomLevel && PseudoPrefabManager.stub != null)
            {
                Scene instance = PseudoPrefabManager.stub.gameObject.scene;
                if (!SceneObjects.TryGetValue(instance, out resources))
                {
                    resources = new LevelResources();
                    SceneObjects.Add(instance, resources);
                }
            }
            else if (!Levels.TryGetValue(scene, out resources))
            {
                resources = new LevelResources();
                Levels.Add(scene, resources);
            }
            resources.Objects.Add(value);
        }
    }
}
