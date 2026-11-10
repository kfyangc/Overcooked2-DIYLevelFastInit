using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit.ResourceFailures
{
    internal static class BlockerPatches
    {
        private const string SupportedGameMvid = "114d606a-29d5-472c-8ebe-c2c20fbe3be3";
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Declared = All | BindingFlags.DeclaredOnly;
        private static readonly HashSet<Assembly> DiyInstalled = new HashSet<Assembly>();
        private static readonly HashSet<MethodBase> IteratorInstalled = new HashSet<MethodBase>();
        private static Harmony activeHarmony;
        private static bool gameInstalled;
        private const float ForceCloseRetryLimit = 2f;
        private static readonly Dictionary<int, float> forceCloseRetryStart = new Dictionary<int, float>();
        private static FieldInfo pendingForceCloseField;

        internal static void InstallGame(Harmony harmony)
        {
            if (typeof(T17DialogBox).Assembly.ManifestModule.ModuleVersionId.ToString() != SupportedGameMvid)
                throw new NotSupportedException("Unsupported Assembly-CSharp MVID: " + typeof(T17DialogBox).Assembly.ManifestModule.ModuleVersionId);
            if (gameInstalled) return;
            activeHarmony = harmony;
            Patch(harmony, typeof(SelectSaveDialog).GetMethod("Show", Declared), "SelectSaveShowPrefix", null, null);
            Patch(harmony, typeof(T17DialogBox).GetMethod("OnAllowedToShow", Declared), null, "DialogShownPostfix", null);
            Patch(harmony, typeof(SelectSaveDialog).GetMethod("Update", Declared), "ForceCloseUpdatePrefix", null, null);
            Patch(harmony, typeof(SaveSlotElement).GetMethod("ServerLoadCampaign", Declared), "ServerLoadCampaignPrefix", null, null);
            Patch(harmony, typeof(SaveSlotElement).GetMethod("OnSaveGameReady", Declared), "SaveGameReadyPrefix", null, null);
            Patch(harmony, typeof(SelectSaveDialog).GetMethod("LoadReadySlot", Declared), "LoadReadySlotPrefix", null, null);
            Patch(harmony, typeof(FrontendDLCMenu).GetMethod("OnContinueGameButtonPressed", Declared), "DlcContinuePrefix", null, null);
            Patch(harmony, typeof(FrontendDLCMenu).GetMethod("OnNewGameButtonPressed", Declared), "DlcNewPrefix", null, null);
            Patch(harmony, typeof(FrontendDLCMenu).GetMethod("OnLoadGameButtonPressed", Declared), "DlcLoadPrefix", null, null);
            MethodInfo metaLoad = typeof(MetaGameProgress).GetMethod("ByteLoad", Declared);
            HarmonyMethod beforeDiyInitialize = Hook("MetaGameProgressByteLoadPostfix");
            beforeDiyInitialize.priority = Priority.First;
            harmony.Patch(metaLoad, null, beforeDiyInitialize);
            BlockerLog.Debug("Harmony", "PATCH_OK", "MetaGameProgress.ByteLoad [Postfix, Priority.First] before DIYLevel resource initialization");
            PatchIterator(harmony, typeof(SelectSaveDialog), "ShowSlotElements");
            PatchIterator(harmony, typeof(SaveSlotElement), "LoadSlotData");
            PatchIterator(harmony, typeof(SaveSlotElement), "TriggerSlotRoutine");
            PatchIterator(harmony, typeof(SaveSlotElement), "LoadSaveRoutine");
            PatchIterator(harmony, typeof(SaveSlotElement), "SaveNewGame");
            gameInstalled = true;
            BlockerLog.Info("Harmony", "GAME_READY", "patched native save dialog and DLC 15 safety boundaries");
        }

        // SelectSaveDialog.Update retries base.Close() every frame while m_pendingForceClose stays true, which can spam
        // "Retrying force close..." forever when the native close keeps failing. Prefix (before base.Close can clear the
        // flag again) records the first retry frame and caps the window at 2 seconds: clear the flag and hide the panel
        // so neither the log nor the per-frame Close() loop continues.
        private static void ForceCloseUpdatePrefix(SelectSaveDialog __instance)
        {
            if (__instance == null || __instance.DLC != 15) return;
            if (!FailureGate.Active) { forceCloseRetryStart.Clear(); return; }
            if (pendingForceCloseField == null)
                pendingForceCloseField = typeof(SelectSaveDialog).GetField("m_pendingForceClose", BindingFlags.Instance | BindingFlags.NonPublic);
            int id = __instance.GetInstanceID();
            if (pendingForceCloseField == null || !(bool)pendingForceCloseField.GetValue(__instance))
            { forceCloseRetryStart.Remove(id); return; }
            float started;
            if (!forceCloseRetryStart.TryGetValue(id, out started))
            {
                forceCloseRetryStart[id] = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - started < ForceCloseRetryLimit) return;
            forceCloseRetryStart.Remove(id);
            try
            {
                pendingForceCloseField.SetValue(__instance, false);
                __instance.Hide();
                BlockerLog.Warning("UI", "FORCE_CLOSE_CAP", "stopped save-dialog force-close retries after 2s instance=" + id);
            }
            catch (Exception ex) { BlockerLog.Warning("UI", "FORCE_CLOSE_CAP_FAILED", ex.ToString()); }
        }

        internal static void InstallDiy(Harmony harmony, Assembly assembly)
        {
            if (assembly == null || assembly.GetName().Name != "OC2DIYLevel" || DiyInstalled.Contains(assembly)) return;
            try
            {
                Type setup = assembly.GetType("OC2DIYLevel.LevelConfigSetup", false);
                Type bundles = assembly.GetType("OC2DIYLevel.DIYLevelAssetBundleManager", false);
                Type pseudo = assembly.GetType("OC2DIYLevel.PseudoPrefabManager", false);
                Type recipeHelper = assembly.GetType("OC2DIYLevel.RecipeHelper", false);
                Patch(harmony, Find(setup, "SetupConfig", 3), "SetupConfigPrefix", null, "SetupConfigFinalizer");
                Patch(harmony, Find(setup, "SetupSceneDirectoryData", 2), "SetupDirectoryPrefix", null, "SetupDirectoryFinalizer");
                Patch(harmony, Find(bundles, "Initialize", 0), "InitializePrefix", null, "InitializeFinalizer");
                Patch(harmony, Find(pseudo, "GetAssetBundle", 1), null, null, "BundleFinalizer");
                Patch(harmony, FindNonGenericLoadAsset(pseudo), null, null, "AssetFinalizer");
                BlockerLog.Debug("Harmony", "PATCH_OK", "PseudoPrefabManager.LoadAsset non-generic resource exception observer");
                ResourceAssetObserver.Install(harmony, assembly);
                foreach (string consumer in new[] { "GetIngredientOrderNode", "GetItemOrderNode", "GetIngredientPrefabForOptional" })
                    Patch(harmony, FindResourceConsumer(recipeHelper, consumer), "MissingObjectConsumerPrefix", null, "MissingObjectConsumerFinalizer");
                Patch(harmony, Find(assembly.GetType("OC2DIYLevel.DIYLevelEntryUI", false), "OnLevelSelected", 1), "DiyLevelSelectedPrefix", null, null);
                DiyInstalled.Add(assembly);
                BlockerLog.Info("Harmony", "DIY_READY", "installed resource observers and guarded level-selection entry for " + assembly.ManifestModule.ModuleVersionId);
            }
            catch (Exception ex) { BlockerLog.Error("Harmony", "DIY_FAILED", ex.ToString()); }
        }

        private static MethodInfo Find(Type type, string name, int count)
        {
            if (type == null) return null;
            foreach (MethodInfo method in type.GetMethods(Declared))
                if (method.Name == name && method.GetParameters().Length == count && !method.IsAbstract) return method;
            return null;
        }

        private static MethodInfo FindNonGenericLoadAsset(Type type)
        {
            if (type == null) return null;
            foreach (MethodInfo method in type.GetMethods(Declared))
                if (method.Name == "LoadAsset" && !method.IsGenericMethodDefinition && method.GetParameters().Length == 1) return method;
            return null;
        }

        private static MethodInfo FindDeclared(Type type, string name)
        {
            return type == null ? null : type.GetMethod(name, Declared);
        }

        private static MethodInfo FindResourceConsumer(Type type, string name)
        {
            if (type == null) return null;
            foreach (MethodInfo method in type.GetMethods(Declared))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == name && parameters.Length == 1
                    && parameters[0].ParameterType.FullName == "LevelEditorStub.PseudoPrefabSO") return method;
            }
            return null;
        }

        private static void PatchIterator(Harmony harmony, Type owner, string methodName)
        {
            if (owner == null) return;
            foreach (Type nested in owner.GetNestedTypes(Declared))
            {
                if (!nested.Name.StartsWith("<" + methodName + ">", StringComparison.Ordinal) || !typeof(IEnumerator).IsAssignableFrom(nested)) continue;
                MethodInfo move = nested.GetMethod("MoveNext", Declared);
                if (move == null || IteratorInstalled.Contains(move)) continue;
                harmony.Patch(move, null, null, null, Hook("IteratorFinalizer"), null);
                IteratorInstalled.Add(move);
                BlockerLog.Info("Harmony", "ITERATOR_READY", owner.FullName + "." + methodName + ".MoveNext");
                return;
            }
            BlockerLog.Warning("Harmony", "ITERATOR_MISSING", owner.FullName + "." + methodName + ".MoveNext");
        }

        private static void Patch(Harmony harmony, MethodInfo original, string prefix, string postfix, string finalizer)
        {
            if (original == null)
            {
                BlockerLog.Warning("Harmony", "TARGET_MISSING", "target not found");
                return;
            }
            HarmonyMethod pre = prefix == null ? null : Hook(prefix);
            if (pre != null && (prefix == "SelectSaveShowPrefix" || prefix == "ServerLoadCampaignPrefix"
                || prefix == "SaveGameReadyPrefix" || prefix == "LoadReadySlotPrefix" || prefix == "DiyLevelSelectedPrefix"
                || prefix.StartsWith("Dlc", StringComparison.Ordinal) || prefix == "InitializePrefix")) pre.priority = Priority.First;
            HarmonyMethod post = postfix == null ? null : Hook(postfix);
            HarmonyMethod fin = finalizer == null ? null : Hook(finalizer);
            harmony.Patch(original, pre, post, null, fin, null);
            BlockerLog.Debug("Harmony", "PATCH_OK", original.DeclaringType.FullName + "." + original.Name);
        }

        private static HarmonyMethod Hook(string name)
        { return new HarmonyMethod(typeof(BlockerPatches).GetMethod(name, Declared)); }

        private static void MetaGameProgressByteLoadPostfix()
        {
            // Preserve the author's installation window before DIY resource initialization.
            DiyPatchWindowOpen = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "OC2DIYLevel")
                {
                    if (activeHarmony != null) InstallDiy(activeHarmony, assembly);
                    return;
                }
            BlockerLog.Warning("Harmony", "DIY_NOT_LOADED", "MetaGameProgress.ByteLoad completed before OC2DIYLevel assembly was loaded");
        }

        internal static bool DiyPatchWindowOpen;

        internal static void ResetForShutdown()
        {
            DiyInstalled.Clear(); IteratorInstalled.Clear(); forceCloseRetryStart.Clear();
            activeHarmony = null; gameInstalled = DiyPatchWindowOpen = false;
        }

        internal static void ObserveAsset(object resource, object result, Exception error)
        {
            try { AssetFinalizer(resource, result, error); }
            catch (Exception observationError) { BlockerLog.Error("Resource", "OBSERVER_FAILED", observationError.ToString()); }
        }

        private static void DialogShownPostfix(T17DialogBox __instance)
        {
            BlockerRuntime.OnDialogShown(__instance);
        }

        private static bool SelectSaveShowPrefix(SelectSaveDialog __instance)
        {
            if (!FailureGate.Active || __instance == null || __instance.DLC != 15) return true;
            BlockerLog.Warning("UI", "SHOW_BLOCKED", "blocked DLC 15 save dialog after resource failure");
            return false;
        }

        private static bool ServerLoadCampaignPrefix(SaveSlotElement __instance, GameSession session)
        {
            if (!FailureGate.Active || session == null || session.DLC != 15) return true;
            BlockerLog.Warning("UI", "LOAD_BLOCKED", "blocked DIYLevel ServerLoadCampaign; failed load is not reported as success");
            BlockerRuntime.RequestFailureHandling();
            return false;
        }

        private static bool SaveGameReadyPrefix(SaveSlotElement __instance)
        {
            if (!FailureGate.Active || __instance == null || __instance.DLC != 15) return true;
            BlockerLog.Warning("UI", "READY_BLOCKED", "blocked SaveSlotElement.OnSaveGameReady; failed load cannot become ready");
            BlockerRuntime.RequestFailureHandling();
            return false;
        }

        private static bool LoadReadySlotPrefix(SelectSaveDialog __instance, ref bool __result)
        {
            if (!FailureGate.Active || __instance == null || __instance.DLC != 15) return true;
            __result = false;
            BlockerLog.Warning("UI", "READY_SLOT_BLOCKED", "blocked SelectSaveDialog.LoadReadySlot after resource failure");
            BlockerRuntime.RequestFailureHandling();
            return false;
        }

        private static bool DiyLevelSelectedPrefix(string __0)
        {
            if (!FailureGate.Active) return true;
            BlockerLog.Warning("UI", "LEVEL_SELECTION_BLOCKED", "blocked DIYLevelEntryUI.OnLevelSelected scene=" + __0);
            BlockerRuntime.RequestFailureHandling();
            return false;
        }

        private static bool DlcContinuePrefix(DLCFrontendData dlcData)
        { return !BlockedDlc(dlcData, "CONTINUE"); }
        private static bool DlcNewPrefix(DLCFrontendData dlcData)
        { return !BlockedDlc(dlcData, "NEW"); }
        private static bool DlcLoadPrefix(DLCFrontendData dlcData)
        { return !BlockedDlc(dlcData, "LOAD"); }

        private static bool BlockedDlc(DLCFrontendData data, string operation)
        {
            if (!FailureGate.Active || data == null || data.m_DLCID != 15) return false;
            BlockerLog.Warning("UI", operation + "_BLOCKED", "blocked DIYLevel DLC entry after resource failure");
            BlockerRuntime.RequestFailureHandling();
            return true;
        }

        private static void SetupConfigPrefix(object __0, object __1, int __2, out SetupTrace __state)
        {
            __state = new SetupTrace { Previous = CurrentTrace, Directory = CurrentDirectory, LevelInfo = __1, Resource = __0, PlayerCount = __2 };
            CurrentTrace = __state;
            // Directory previews run one setup per level and player count; their start lines are redundant with the
            // directory summary, so only standalone setups (real level loads) log a start line.
            if (__state.Directory == null) BlockerLog.Debug("Resource", "SETUP_START", __state.Describe());
        }

        private static Exception SetupConfigFinalizer(Exception __exception, SetupTrace __state)
        {
            try
            {
                if (__exception != null)
                {
                    bool missingResourceConsumed = __state != null && __state.ConsumerFailureException != null
                        && object.ReferenceEquals(__exception, __state.ConsumerFailureException);
                    bool missingTemplate = !missingResourceConsumed && __state != null
                        && (object.ReferenceEquals(__exception, __state.TemplateFailureException)
                            || (__state.TemplateMissing && IsMissingResourceConsumerException(__exception)));
                    bool missingReference = !missingResourceConsumed && !missingTemplate && __state != null
                        && __state.MissingResourceReference && IsMissingResourceConsumerException(__exception);
                    if (!FailureGate.Active && missingResourceConsumed)
                    {
                        ActivateResourceFailure("RecipeHelper resource consumer failed", __state.LevelInfo,
                            __state.PlayerCount, __state.ConsumerFailureResource, __exception);
                    }
                    else if (!FailureGate.Active && missingTemplate)
                    {
                        ActivateResourceFailure("DIYLevel template resource missing", __state.LevelInfo,
                            __state.PlayerCount, __state.Resource, __exception);
                    }
                    else if (!FailureGate.Active && missingReference)
                    {
                        ActivateResourceFailureDetailed("DIYLevel resource reference missing", __state.LevelInfo,
                            __state.PlayerCount, __state.ConsumerFailureResource, __exception,
                            __state.MissingReferenceCaller, Describe(__state.Resource));
                    }
                    BlockerLog.Error("Resource", "SETUP_FAILED", (__state == null ? string.Empty : __state.Describe())
                        + " resourceReadFailure=" + (missingResourceConsumed || missingTemplate || missingReference)
                        + " missingResourceConsumed=" + missingResourceConsumed + " missingTemplate=" + missingTemplate
                        + " missingReference=" + missingReference
                        + " missingReferenceCaller=" + (__state == null ? string.Empty : __state.MissingReferenceCaller)
                        + " exception=" + __exception);
                }
                if (__state != null && __state.Nulls.Loads > 0 && __state.Directory == null)
                {
                    // Outside a directory scan the same misses repeat for every player count, so they are
                    // aggregated instead of writing one line per load attempt.
                    BlockerLog.Debug("Resource", "ASSET_NULL_IGNORED", __state.Describe() + " " + __state.Nulls.Describe()
                        + " tolerated=true");
                }
            }
            finally { CurrentTrace = __state == null ? null : __state.Previous; }
            return __exception;
        }

        private static void SetupDirectoryPrefix(object __0, object __1, out DirectoryTrace __state)
        {
            __state = new DirectoryTrace { Previous = CurrentDirectory, Resource = __0, Levels = __1 as Array };
            CurrentDirectory = __state;
            BlockerLog.Debug("Resource", "DIRECTORY_START", "levelCount=" + (__state.Levels == null ? "null" : __state.Levels.Length.ToString()));
        }

        private static Exception SetupDirectoryFinalizer(Exception __exception, DirectoryTrace __state)
        {
            try
            {
                if (__exception != null)
                    BlockerLog.Error("Resource", "DIRECTORY_FAILED", "resourceFailure=" + FailureGate.Active + " exception=" + __exception);
                if (__state != null && __state.Nulls.Loads > 0)
                {
                    // One summary for the whole directory scan: the same optional recipe assets are probed again
                    // for every level and player count, and every miss is tolerated by DIYLevel.
                    BlockerLog.Debug("Resource", "ASSET_NULL_IGNORED", "scope=directory levelCount="
                        + (__state.Levels == null ? "null" : __state.Levels.Length.ToString()) + " " + __state.Nulls.Describe()
                        + " tolerated=true");
                }
            }
            finally { CurrentDirectory = __state == null ? null : __state.Previous; }
            return __exception;
        }

        private static bool InitializePrefix()
        {
            if (!FailureGate.Active) return true;
            BlockerLog.Warning("Resource", "INITIALIZE_BLOCKED", "blocked repeated DIYLevel initialization after failure");
            return false;
        }

        private static Exception InitializeFinalizer(Exception __exception)
        {
            if (__exception != null)
                BlockerLog.Error("Resource", "INITIALIZE_FAILED", "outside the save-slot resource-read boundary; original exception preserved: " + __exception);
            return __exception;
        }

        private static Exception BundleFinalizer(string __0, object __result, Exception __exception)
        {
            if (__exception != null)
            {
                string detail = "bundle=" + (__0 ?? "null") + " result=" + (__result == null ? "null" : __result.ToString());
                BlockerLog.Error("Resource", "BUNDLE_FAILED", detail + " setupTrace=" + (CurrentTrace != null) + " exception=" + __exception);
            }
            else if (__result == null)
            {
                BlockerLog.Debug("Resource", "BUNDLE_NULL", "bundle=" + (__0 ?? "null") + " returned null without a load exception");
            }
            return __exception;
        }

        private static void MissingObjectConsumerPrefix(object __0, MethodBase __originalMethod, out ResourceConsumerTrace __state)
        {
            MethodInfo consumerMethod = __originalMethod as MethodInfo;
            __state = new ResourceConsumerTrace
            {
                Previous = CurrentResourceConsumer,
                Resource = __0,
                Consumer = consumerMethod == null ? string.Empty : consumerMethod.Name,
                ExpectedResultType = consumerMethod == null ? null : consumerMethod.ReturnType
            };
            CurrentResourceConsumer = __state;
        }

        private static Exception MissingObjectConsumerFinalizer(Exception __exception, ResourceConsumerTrace __state)
        {
            try
            {
                bool expectedType = __state != null && __state.LoadObserved && __state.LoadResult != null
                    && ((__state.ExpectedResultType != null && __state.ExpectedResultType.IsInstanceOfType(__state.LoadResult))
                        || __state.LoadResult is GameObject);
                bool missingOrWrongTypeConsumed = __state != null && __state.LoadObserved && __exception != null
                    && ((!expectedType && IsMissingResourceConsumerException(__exception))
                        || object.ReferenceEquals(__exception, __state.LoadException));
                if (missingOrWrongTypeConsumed && CurrentTrace != null)
                {
                    CurrentTrace.ConsumerFailureResource = __state.Resource;
                    CurrentTrace.ConsumerFailureException = __exception;
                    BlockerLog.Error("Resource", "MISSING_RESOURCE_CONSUMED", "consumer=" + __state.Consumer
                        + " result=" + (__state.LoadResult == null ? "null" : __state.LoadResult.GetType().FullName)
                        + " consumerResource=" + Describe(__state.Resource) + " exception=" + __exception);
                }
                else if (missingOrWrongTypeConsumed && PseudoPrefabManager.isInCustomLevel)
                {
                    object manager = PseudoPrefabManager.stub;
                    FieldInfo level = manager == null ? null : manager.GetType().GetField("levelInfo", All);
                    object info = level == null ? null : level.GetValue(manager);
                    if (info != null) ActivateResourceFailure("RecipeHelper." + __state.Consumer, info, 0, __state.Resource, __exception);
                }
            }
            catch (Exception error) { BlockerLog.Error("Resource", "CONSUMER_OBSERVER_FAILED", error.ToString()); }
            finally { CurrentResourceConsumer = __state == null ? null : __state.Previous; }
            return __exception;
        }

        private static Exception AssetFinalizer(object __0, object __result, Exception __exception)
        {
            ResourceConsumerTrace activeConsumer = CurrentResourceConsumer;
            if (activeConsumer != null && object.ReferenceEquals(__0, activeConsumer.Resource))
            {
                activeConsumer.LoadObserved = true;
                activeConsumer.LoadResult = __result;
                activeConsumer.LoadException = __exception;
            }
            if (CurrentTrace != null && object.ReferenceEquals(__0, CurrentTrace.Resource) && __exception != null)
                CurrentTrace.TemplateFailureException = __exception;
            if (__0 == null && CurrentTrace != null)
            {
                // A level can reference a missing PseudoPrefabSO outright. LoadAsset(null) then throws before it can
                // report a null asset, so the null resource descriptor itself is the missing prerequisite. Record the
                // first non-blocker frame so the popup can name who asked for the missing reference.
                CurrentTrace.MissingResourceReference = true;
                if (string.IsNullOrEmpty(CurrentTrace.MissingReferenceCaller))
                    CurrentTrace.MissingReferenceCaller = CaptureCaller();
            }
            if (__exception != null)
            {
                BlockerLog.Error("Resource", "ASSET_FAILED", "reference=" + Describe(__0)
                    + " setupTrace=" + (CurrentTrace != null) + " exception=" + __exception);
            }
            else if (__result == null)
            {
                ResourceConsumerTrace consumer = CurrentResourceConsumer;
                bool consumedByRecipeHelper = CurrentTrace != null && consumer != null
                    && object.ReferenceEquals(__0, consumer.Resource);
                bool missingTemplate = CurrentTrace != null && object.ReferenceEquals(__0, CurrentTrace.Resource);
                if (consumedByRecipeHelper)
                {
                    consumer.LoadObserved = true;
                    consumer.LoadResult = null;
                    BlockerLog.Debug("Resource", "ASSET_NULL", "reference=" + Describe(__0)
                        + " matched active RecipeHelper resource consumer");
                }
                else if (CurrentTrace == null)
                    BlockerLog.Debug("Resource", "ASSET_NULL", "reference=" + Describe(__0) + " outside SetupConfig");
                else if (missingTemplate)
                {
                    CurrentTrace.TemplateMissing = true;
                    BlockerLog.Debug("Resource", "ASSET_NULL", "reference=" + Describe(__0)
                        + " matched SetupConfig template; null result alone does not activate FailureGate");
                }
                else
                    CurrentTrace.RecordIgnoredNull(__0);
            }
            else
            {
                ResourceConsumerTrace consumer = CurrentResourceConsumer;
                if (CurrentTrace != null && consumer != null && object.ReferenceEquals(__0, consumer.Resource))
                {
                    consumer.LoadObserved = true;
                    consumer.LoadResult = __result;
                    if (consumer.ExpectedResultType != null && !consumer.ExpectedResultType.IsInstanceOfType(__result)
                        && !(__result is GameObject))
                        BlockerLog.Warning("Resource", "CONSUMER_TYPE_MISMATCH", "consumer=" + consumer.Consumer
                            + " actual=" + __result.GetType().FullName + " expected=" + consumer.ExpectedResultType.FullName
                            + " reference=" + Describe(__0));
                }
            }
            return __exception;
        }

        [ThreadStatic] private static SetupTrace CurrentTrace;
        [ThreadStatic] private static DirectoryTrace CurrentDirectory;
        [ThreadStatic] private static ResourceConsumerTrace CurrentResourceConsumer;

        private sealed class ResourceConsumerTrace
        {
            internal ResourceConsumerTrace Previous;
            internal object Resource;
            internal object LoadResult;
            internal Exception LoadException;
            internal Type ExpectedResultType;
            internal string Consumer;
            internal bool LoadObserved;
        }

        private sealed class SetupTrace
        {
            internal SetupTrace Previous;
            internal DirectoryTrace Directory;
            internal object LevelInfo;
            internal object Resource;
            internal object ConsumerFailureResource;
            internal Exception ConsumerFailureException;
            internal bool TemplateMissing;
            internal Exception TemplateFailureException;
            internal bool MissingResourceReference;
            internal string MissingReferenceCaller;
            internal int PlayerCount;
            internal readonly NullLoadAggregate Nulls = new NullLoadAggregate();
            internal void RecordIgnoredNull(object resource)
            {
                Nulls.Add(resource);
                if (Directory != null) Directory.Nulls.Add(resource);
            }
            internal string Describe()
            { return "level=" + Text(LevelInfo, "levelName") + " scene=" + Text(LevelInfo, "sceneName") + " players=" + PlayerCount + " template=" + BlockerPatches.Describe(Resource); }
        }

        private sealed class DirectoryTrace
        {
            internal DirectoryTrace Previous;
            internal object Resource;
            internal Array Levels;
            internal readonly NullLoadAggregate Nulls = new NullLoadAggregate();
        }

        private sealed class NullLoadAggregate
        {
            internal int Loads;
            internal readonly HashSet<string> Resources = new HashSet<string>();
            internal readonly List<string> Samples = new List<string>();
            internal void Add(object resource)
            {
                Loads++;
                string key = BlockerPatches.Describe(resource);
                if (Resources.Add(key) && Samples.Count < 5) Samples.Add(key);
            }
            internal string Describe()
            {
                return "ignoredNullLoads=" + Loads + " distinct=" + Resources.Count
                    + " samples=" + string.Join("; ", Samples.ToArray());
            }
        }

        private static bool IsMissingResourceConsumerException(Exception error)
        {
            return error is NullReferenceException || error is ArgumentException;
        }

        private static void ActivateResourceFailure(string phase, object levelInfo, int playerCount,
            object resource, Exception error)
        {
            try
            {
                FailureGate.Activate(phase, levelInfo, playerCount, resource, error, "未知 DIYLevel");
            }
            catch (Exception activationError)
            {
                BlockerLog.Error("FailureGate", "ACTIVATE_FAILED",
                    "activation failed; original resource exception is preserved: " + activationError);
            }
        }

        private static void ActivateResourceFailureDetailed(string phase, object levelInfo, int playerCount,
            object resource, Exception error, string referenceCaller, string templateResource)
        {
            try
            {
                FailureGate.ActivateDetailed(phase, levelInfo, playerCount, resource, error, "未知 DIYLevel",
                    true, referenceCaller, templateResource);
            }
            catch (Exception activationError)
            {
                BlockerLog.Error("FailureGate", "ACTIVATE_FAILED",
                    "activation failed; original resource exception is preserved: " + activationError);
            }
        }

        // First frame outside this plugin: for a null resource descriptor this names the DIYLevel method that asked for
        // it (for example PseudoPrefabManager.SetAssetRef), which is the only remaining clue for the popup. Harmony's
        // dynamic wrapper and the patched LoadAsset method itself are skipped so the reported caller is meaningful.
        private static string CaptureCaller()
        {
            try
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(false);
                string generatedFallback = null;
                string otherFallback = null;
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    System.Diagnostics.StackFrame frame = trace.GetFrame(i);
                    if (frame == null) continue;
                    MethodBase method = frame.GetMethod();
                    if (method == null || method.DeclaringType == null) continue;
                    Assembly assembly = method.DeclaringType.Assembly;
                    if (assembly == typeof(BlockerPatches).Assembly) continue;
                    if (method.DeclaringType.FullName.StartsWith("HarmonyLib.", StringComparison.Ordinal)) continue;
                    string name = CleanMethodName(method.Name);
                    if (name == "LoadAsset" || name.StartsWith("LoadAsset<", StringComparison.Ordinal)
                        || name.StartsWith("LoadAsset`", StringComparison.Ordinal)) continue;
                    string label = method.DeclaringType.Name + "." + name;
                    string assemblyName = assembly.GetName().Name;
                    bool gameFrame = assemblyName == "OC2DIYLevel" || assemblyName == "Assembly-CSharp";
                    if (gameFrame && !IsCompilerGenerated(method)) return label;
                    if (IsCompilerGenerated(method))
                    {
                        if (generatedFallback == null) generatedFallback = label;
                        continue;
                    }
                    if (otherFallback == null && assemblyName != "mscorlib"
                        && !assemblyName.StartsWith("System", StringComparison.Ordinal))
                        otherFallback = label;
                }
                if (otherFallback != null) return otherFallback;
                if (generatedFallback != null) return generatedFallback;
            }
            catch (Exception ex)
            {
                BlockerLog.Warning("Resource", "CALLER_CAPTURE_FAILED", ex.ToString());
            }
            return string.Empty;
        }

        private static bool IsCompilerGenerated(MethodBase method)
        {
            if (method == null) return false;
            string typeName = method.DeclaringType == null ? string.Empty : method.DeclaringType.Name;
            string methodName = method.Name ?? string.Empty;
            return typeName.IndexOf('<') >= 0 || methodName.IndexOf('<') >= 0
                || methodName.IndexOf("b__", StringComparison.Ordinal) >= 0;
        }

        private static string CleanMethodName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int marker = name.IndexOf("::", StringComparison.Ordinal);
            int close = name.IndexOf('>', marker < 0 ? 0 : marker);
            if (name.StartsWith("DMD<", StringComparison.Ordinal) && marker >= 0 && close > marker)
                return name.Substring(marker + 2, close - marker - 2);
            return name;
        }

        private static string Describe(object value)
        { return value == null ? "null" : "bundle=" + Text(value, "bundleName") + " asset=" + Text(value, "assetPath"); }

        private static string Text(object value, string field)
        {
            if (value == null) return string.Empty;
            try
            {
                UnityEngine.Object unity = value as UnityEngine.Object;
                if (field == "name" && unity != null) return unity.name;
                FieldInfo info = value.GetType().GetField(field, All);
                object result = info == null ? null : info.GetValue(value);
                return result == null ? string.Empty : result.ToString();
            }
            catch { return string.Empty; }
        }

        private static Exception IteratorFinalizer(Exception __exception, object __instance)
        {
            if (!FailureGate.ShouldSuppressIteratorException(__exception)) return __exception;
            ClearIteratorOwner(__instance);
            BlockerRuntime.RequestFailureHandling();
            return __exception;
        }

        private static void ClearIteratorOwner(object iterator)
        {
            if (iterator == null) return;
            try
            {
                foreach (FieldInfo field in iterator.GetType().GetFields(All))
                {
                    object owner = field.GetValue(iterator);
                    if (owner is SelectSaveDialog)
                    {
                        FieldInfo update = typeof(SelectSaveDialog).GetField("m_slotUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (update != null) update.SetValue(owner, null);
                        BlockerLog.Info("UI", "ITERATOR_STOP", "cleared SelectSaveDialog.m_slotUpdate after failure");
                        return;
                    }
                    SaveSlotElement slot = owner as SaveSlotElement;
                    if (slot != null)
                    {
                        Type type = typeof(SaveSlotElement);
                        FieldInfo routine = type.GetField("m_slotClickedRoutine", BindingFlags.Instance | BindingFlags.NonPublic);
                        FieldInfo processing = type.GetField("m_processingSlotTrigger", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (routine != null) routine.SetValue(slot, null);
                        if (processing != null) processing.SetValue(slot, false);
                        MethodInfo release = type.GetMethod("ReleaseActiveSuppression", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (release != null) release.Invoke(slot, new object[] { true });
                        BlockerLog.Info("UI", "ITERATOR_STOP", "cleared SaveSlotElement trigger state after failure");
                        return;
                    }
                }
            }
            catch (Exception error) { BlockerLog.Error("UI", "ITERATOR_STOP_FAILED", error.ToString()); }
        }
    }
}
