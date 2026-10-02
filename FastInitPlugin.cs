using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>
    /// 替换 OC2DIYLevel 的 DIYLevelAssetBundleManager.Initialize()：
    /// 原版在主菜单读档（MetaGameProgress.ByteLoad）时同步串行加载全部 common* 包
    /// 和 levels/ 下每个关卡集的 info 包（353MB），主线程冻结 20 秒以上。
    /// 本插件把廉价部分（common 主包 + 静态字段 + DLC 数据）保持同步，
    /// 其余 common* 依赖包与全部 info 包改为协程 + LoadFromFileAsync 后台加载，
    /// 并顺手修掉原版缺少的幂等保护（二次 ByteLoad 会整个重载一遍）。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Overcooked2.exe")]
    [BepInDependency("dev.gua.overcooked.diylevel")]
    public class FastInitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "yang.oc2.diylevel.fastinit";
        public const string PluginName = "DIYLevel FastInit";
        public const string PluginVersion = "1.2.0";

        internal static ManualLogSource Log;

        private static bool _started;

        private static readonly FieldInfo FiCommonBundle = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "commonBundle");
        private static readonly FieldInfo FiCover = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelCover");
        private static readonly FieldInfo FiSessionPrefab = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelGameSessionPrefab");
        private static readonly FieldInfo FiConfigSo = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "configTemplateSO");
        private static readonly FieldInfo FiInstance = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "Instance");
        private static readonly FieldInfo FiSetMenu = AccessTools.Field(typeof(DIYLevelEntryUI), "levelSetSelectionMenu");
        private static readonly MethodInfo MiAddSetButton = AccessTools.Method(typeof(DIYLevelEntryUI), "AddLevelSetButton");

        private static int _builtCount;

        private void Awake()
        {
            Log = Logger;
            MethodInfo original = AccessTools.Method(typeof(DIYLevelAssetBundleManager), "Initialize");
            if (original == null)
            {
                Log.LogError("DIYLevelAssetBundleManager.Initialize not found; FastInit inactive (original behavior kept).");
                return;
            }
            Harmony harmony = new Harmony(PluginGuid);
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(FastInitPlugin), nameof(InitializePrefix)));
            MethodInfo addUi = AccessTools.Method(typeof(DIYLevelEntryUI), "AddUI");
            if (addUi != null && FiSetMenu != null && MiAddSetButton != null)
            {
                harmony.Patch(addUi, postfix: new HarmonyMethod(typeof(FastInitPlugin), nameof(AddUIPostfix)));
            }
            else
            {
                Log.LogWarning("AddUI heal patch not applied (member missing): menu may need re-entry to show late-loaded sets");
            }
            Log.LogInfo("DIYLevel FastInit ready: level bundles load async, menu no longer blocks.");
        }

        /// <summary>原版 AddLevelSetSelectionUI 只在菜单首次创建时读取 levelSetInfos（有
        /// if(menu!=null) return 守卫），异步加载后补进的关卡集永远不会出现在按钮里。
        /// 这里在每次 AddUI 之后对比数量，不一致就清空重建按钮列表。</summary>
        private static void AddUIPostfix()
        {
            HealLevelSetButtons();
        }

        internal static void HealLevelSetButtons()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                if (infos == null || FiSetMenu == null || MiAddSetButton == null)
                {
                    return;
                }
                FrontendOptionsMenu menu = FiSetMenu.GetValue(null) as FrontendOptionsMenu;
                if (menu == null)
                {
                    return; // 菜单尚未创建，AddUI 内部会用当前列表全新构建
                }
                if (_builtCount == infos.Count)
                {
                    return;
                }
                OC2DIYLevel.UIUtils.ClearAllMenuContent(menu);
                for (int i = 0; i < infos.Count; i++)
                {
                    MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                }
                _builtCount = infos.Count;
                Log.LogInfo("level set menu rebuilt: " + infos.Count + " entries");
            }
            catch (Exception e)
            {
                Log.LogWarning("HealLevelSetButtons failed: " + e.Message);
            }
        }

        /// <summary>Harmony Prefix：返回 false 跳过原版 Initialize，改走本插件的实现。</summary>
        private static bool InitializePrefix()
        {
            if (_started)
            {
                // 原版没有幂等保护：再次 ByteLoad 会把 353MB 全部重载一遍（info 包还会重复入列）。
                return false;
            }
            _started = true;
            try
            {
                string pluginDir = GetDiyPluginDir();
                if (!RunCheapInit(pluginDir))
                {
                    // 与原版失败条件一致（缺 common 文件等）；放行下次重试。
                    _started = false;
                    return false;
                }
                GameObject host = new GameObject("DIYLevelFastInitHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<FastInitHost>().Run(pluginDir);
            }
            catch (Exception e)
            {
                _started = false;
                Log.LogError("FastInit init failed (DIY levels unavailable this attempt): " + e);
            }
            return false;
        }

        /// <summary>原版用 Assembly.GetExecutingAssembly().Location 定位插件目录；
        /// 在本插件里执行会指向错误的目录，必须解析 OC2DIYLevel.dll 自身的位置。</summary>
        private static string GetDiyPluginDir()
        {
            return Path.GetDirectoryName(typeof(DIYLevelAssetBundleManager).Assembly.Location);
        }

        /// <summary>同步完成原版 Initialize 中廉价的部分（原版第 50-68 行、105-116 行）。
        /// 完成后 IsInitialized 即为 true，所有消费方补丁的行为与原版一致。</summary>
        private static bool RunCheapInit(string pluginDir)
        {
            DIYLevelAssetBundleManager prevInst = FiInstance.GetValue(null) as DIYLevelAssetBundleManager;
            GameObject go = new GameObject("DIYLevelAssetBundleManager", new Type[] { typeof(DIYLevelAssetBundleManager) });
            UnityEngine.Object.DontDestroyOnLoad(go);
            if (prevInst != null && ((Component)prevInst).gameObject != go)
            {
                UnityEngine.Object.Destroy(((Component)prevInst).gameObject);
            }
            FiInstance.SetValue(null, go.GetComponent<DIYLevelAssetBundleManager>());

            string commonPath = Path.Combine(pluginDir, "common");
            if (!File.Exists(commonPath))
            {
                Log.LogWarning("missing file: " + commonPath);
                return false;
            }
            AssetBundle commonBundle = AssetBundle.LoadFromFile(commonPath);
            FiCover.SetValue(null, commonBundle.LoadAsset<Sprite>("diylevelcover"));
            FiSessionPrefab.SetValue(null, commonBundle.LoadAsset<GameObject>("DIYLevelGameSession"));
            FiConfigSo.SetValue(null, commonBundle.LoadAsset<PseudoPrefabSO>("LevelConfigTemplateSO"));
            FiCommonBundle.SetValue(null, commonBundle);

            DIYLevelAssetBundleManager.levelSetInfos = new List<KeyValuePair<string, LevelSetInfoSO>>();

            if (DIYLevelAssetBundleManager.diyDLCFrontendData == null)
            {
                DLCFrontendData data = ScriptableObject.CreateInstance<DLCFrontendData>();
                ((UnityEngine.Object)data).name = "DLC_DIYLevel";
                data.m_NameLocalizationKey = "\"More Levels\"";
                data.m_DescriptionLocalizationKey = OC2DIYLevel.UIUtils.GetLocalizedText("\"Enjoy extra levels from the community!\"", "\"游玩来自玩家社区的更多关卡！\"");
                data.m_PreviewImage = DIYLevelAssetBundleManager.GetCover();
                data.m_DLCID = 15;
                data.m_type = (DLCType)0;
                data.m_IsFreeDLC = true;
                data.m_IsSeasonPassDLC = false;
                DIYLevelAssetBundleManager.diyDLCFrontendData = data;
            }
            return true;
        }
    }

    /// <summary>协程宿主：逐包异步加载 common* 依赖包与各关卡集 info 包。
    /// 每步带日志；宿主被销毁时协程会无声死亡，OnDestroy 负责把这件事暴露出来；
    /// 异步请求超过超时时间仍未完成时回退到同步加载（原版已证明可行）。</summary>
    internal class FastInitHost : MonoBehaviour
    {
        private sealed class PendingSet
        {
            public DirectoryInfo Dir;
            public FileInfo File;
            public AssetBundleCreateRequest Req;
        }

        private const float AsyncTimeoutSeconds = 15f;

        private bool _finished;

        public void Run(string pluginDir)
        {
            StartCoroutine(LoadHeavy(pluginDir));
        }

        private void OnDestroy()
        {
            if (!_finished)
            {
                FastInitPlugin.Log.LogWarning("FastInitHost destroyed before loading finished — coroutine was killed by something.");
            }
        }

        private static List<FileInfo> SafeGetFiles(DirectoryInfo dir, string pattern)
        {
            try
            {
                return new List<FileInfo>(dir.GetFiles(pattern));
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("listing " + dir.FullName + ": " + e.Message);
                return new List<FileInfo>();
            }
        }

        private static List<DirectoryInfo> SafeGetDirs(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return new List<DirectoryInfo>();
                }
                return new List<DirectoryInfo>(new DirectoryInfo(path).GetDirectories());
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("listing " + path + ": " + e.Message);
                return new List<DirectoryInfo>();
            }
        }

        /// <summary>轮询等待异步请求（供外层协程用 wait.Current 转发 yield），超时后放弃等待，
        /// 由调用方检查 isDone 并回退同步路径。</summary>
        private static IEnumerator WaitOp(AsyncOperation op, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup - start > AsyncTimeoutSeconds)
                {
                    FastInitPlugin.Log.LogWarning(what + ": async did not finish in " + AsyncTimeoutSeconds + "s, falling back to sync");
                    yield break;
                }
                yield return null;
            }
        }

        private IEnumerator LoadHeavy(string pluginDir)
        {
            FastInitPlugin.Log.LogInfo("LoadHeavy started");
            // WLoader 等插件可能已把 commonW1/commonW2 常驻内存；跳过可以省去
            // 读取整个文件后才发现重复的那 1-2 秒。
            HashSet<string> alreadyLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
            {
                alreadyLoaded.Add(ab.name);
            }

            int loaded = 0, skipped = 0, failed = 0;
            foreach (FileInfo file in SafeGetFiles(new DirectoryInfo(pluginDir), "common*"))
            {
                if (file.Name == "common")
                {
                    continue;
                }
                if (alreadyLoaded.Contains(file.Name))
                {
                    skipped++;
                    FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                    continue;
                }
                AssetBundleCreateRequest req = AssetBundle.LoadFromFileAsync(file.FullName);
                IEnumerator wait = WaitOp(req, file.Name);
                while (wait.MoveNext())
                {
                    yield return wait.Current;
                }
                if (req.isDone && req.assetBundle != null)
                {
                    loaded++;
                    alreadyLoaded.Add(file.Name);
                }
                else
                {
                    FastInitPlugin.Log.LogWarning(file.Name + ": async failed (" + (req.isDone ? "null bundle" : "timeout") + "), trying sync");
                    AssetBundle sync = AssetBundle.LoadFromFile(file.FullName);
                    if (sync != null)
                    {
                        loaded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
            FastInitPlugin.Log.LogInfo("common bundles: " + loaded + " loaded, " + skipped + " skipped (dup), " + failed + " failed");

            // 并行预取：一次性把所有 info 包的文件读取排进工作线程（每帧发一个请求），
            // 再按目录顺序消费——磁盘读取时间重叠，总耗时远低于逐包串行。
            List<PendingSet> pending = new List<PendingSet>();
            foreach (DirectoryInfo dir in SafeGetDirs(Path.Combine(pluginDir, "levels")))
            {
                List<FileInfo> infoFiles = SafeGetFiles(dir, "info*");
                if (infoFiles.Count == 0)
                {
                    FastInitPlugin.Log.LogWarning("missing info file: " + dir.Name);
                    continue;
                }
                PendingSet ps = new PendingSet();
                ps.Dir = dir;
                ps.File = infoFiles[0];
                ps.Req = AssetBundle.LoadFromFileAsync(infoFiles[0].FullName);
                pending.Add(ps);
                yield return null;
            }
            FastInitPlugin.Log.LogInfo("prefetch queued: " + pending.Count + " info bundles");

            int sets = 0;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            foreach (PendingSet p in pending)
            {
                DirectoryInfo dir = p.Dir;
                AssetBundleCreateRequest cr = p.Req;
                FastInitPlugin.Log.LogInfo("consuming level set [" + dir.Name + "]");
                IEnumerator waitCr = WaitOp(cr, dir.Name);
                while (waitCr.MoveNext())
                {
                    yield return waitCr.Current;
                }
                AssetBundle bundle;
                if (cr.isDone && cr.assetBundle != null)
                {
                    bundle = cr.assetBundle;
                }
                else
                {
                    FastInitPlugin.Log.LogWarning(dir.Name + ": async failed (" + (cr.isDone ? "null bundle" : "timeout") + "), trying sync");
                    bundle = AssetBundle.LoadFromFile(p.File.FullName);
                }
                if (bundle == null)
                {
                    FastInitPlugin.Log.LogWarning("failed loading info bundle of " + dir.Name);
                    continue;
                }
                LevelSetInfoSO so = null;
                AssetBundleRequest ar = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
                IEnumerator waitAr = WaitOp(ar, dir.Name + " (LoadAssetAsync)");
                while (waitAr.MoveNext())
                {
                    yield return waitAr.Current;
                }
                if (ar.isDone)
                {
                    so = ar.asset as LevelSetInfoSO;
                }
                if (so == null)
                {
                    FastInitPlugin.Log.LogWarning(dir.Name + ": LoadAssetAsync incomplete, trying sync LoadAsset");
                    so = bundle.LoadAsset("LevelSetInfo", typeof(LevelSetInfoSO)) as LevelSetInfoSO;
                }
                if (so == null)
                {
                    FastInitPlugin.Log.LogWarning("missing LevelSetInfo in " + dir.Name);
                    continue;
                }
                infos.Add(new KeyValuePair<string, LevelSetInfoSO>(dir.FullName, so));
                sets++;
                FastInitPlugin.Log.LogInfo("level set [" + dir.Name + "] ready (" + sets + " total)");
            }

            _finished = true;
            FastInitPlugin.Log.LogInfo("FastInit complete: " + sets + " level sets available");

            // 加载完成后若在前端场景，补一次 AddUI 让菜单立即拿到完整列表；
            // Postfix 自愈会清掉旧的按钮快照并重建。之后每次进入 DLC 菜单也会自检。
            try
            {
                GameObject frontend = GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu");
                if (frontend != null)
                {
                    DIYLevelEntryUI.AddUI();
                }
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogDebug("AddUI refresh skipped: " + e.Message);
            }
        }
    }
}
