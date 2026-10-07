# DIYLevel FastInit 热重载方案(最终结构)

主前端一键 sync:重扫 `levels/` 目录,按目录粒度 diff,只动发生变化的关卡集。新增目录按排序位置插入,消失的目录卸载移除,info 变化的集 sweep 后原位重载,未变化的集零打扰。触发入口是 More Levels 菜单第一项的刷新按钮(实测后按用户要求移除了热键)。`common` 与 `common*` 常驻不卸载。零新增 Harmony hook。

## 0. 已核实的事实

全部来自反编译源码或实测,不依赖推测。

| # | 事实 | 出处 |
|---|------|------|
| 1 | `AssetBundles.AssetBundleManager.UnloadAssetBundle(string)` 是公开静态方法,内部按 refcount 递减,归零时 `Unload(true)` 并移出注册表;依赖包由 `UnloadDependencies` 连带递减清理 | 游戏 `Assembly-CSharp.dll`,反编译存档 `..\game_AssetBundleManager.cs:295-338` |
| 2 | 每次关卡加载完成后 `UpdateLoadedSceneBundles` 自动卸载名字不匹配当前场景的 streamed scene bundle;退出关卡回前端后场景包仍可能驻留 | 同上 `:393-422` |
| 3 | 重玩同一关累积 refcount(`LoadAssetBundleInternal` 命中已加载包时 `m_ReferencedCount++`),单次 `UnloadAssetBundle` 卸不干净 | 同上 `:262-293` |
| 4 | `m_LoadingErrors` 一旦写入永不清除,会让 `GetLoadedAssetBundle` 对该名字永远返回 null | 同上 `:37,134-139,438` |
| 5 | gua 的三个游戏侧补丁(`LoadAssetBundleInternal`/`LoadDependencies`/`GetStreamingAssetsPath` 前缀)每次被调现场遍历 `levelSetInfos` | OC2DIYLevel `Patch.cs:1469-1537,1551` |
| 6 | `PseudoPrefabManager` 按 `PseudoPrefabSO.bundleName` 惰性解析,`editedMaterials` 进出关卡时清空,无常驻缓存 | `PseudoPrefabManager.cs:278-312,39,48` |
| 7 | DIY 存档按 `sceneName` 字符串键控,存档时保留已不存在的旧条目;arcade 配置按 `levelSetUID` 字符串键控,未知 uid 回退 classic | `DIYLevelSaveManager.cs:62-75,109-130`;`CustomArcadeConfig.cs:33,56-74,99-104` |
| 8 | info bundle 全工程无 `Unload` 调用。文件锁不是普遍事实:实测游戏运行中可删除含 info 文件的整个目录,删除后菜单照常显示(SO 已整读进内存),进关失败(场景包路径 `levelSetInfos.Key` 指向已删目录)。是否持句柄取决于构建压缩方式(LZMA 整包解压进内存后句柄即关;未压缩/LZ4 内存映射则持到 `Unload`)。设计不依赖锁 | 全源码检索 `Unload` 零命中;用户实测 |
| 9 | 场景包在游戏注册表中的键是小写 `sceneName`(`LoadLevel` 做 `ToLowerInvariant()`);依赖包键为 info 中 `dependencies` 原文 | `game_AssetBundleManager.cs:354`;gua `Patch.cs:1503` |
| 10 | `UIUtils.AddButton(menu, name, title, titleZH, onClick)` 从 GameOptions 模板实例化 `T17Button` 挂到 Content 末尾,置顶需 `SetAsFirstSibling`;`ClearAllMenuContent` 销毁容器全部子项 | OC2DIYLevel `UIUtils.cs:242-296` |
| 11 | arcade 设置菜单只构建一次(`AddCustomArcadeSettingsUI` 对已存在的菜单对象早退),选择器选项是构建时刻快照;增删集后位置偏移,旋钮按活列表解析会静默选错集。**置空静态字段无效**(早退会重新找回现存 GameObject);必须销毁菜单 GameObject,下一次 `T17TabPanel.OnTabSelected` 才会用新选项完整重建,`CreateMenu` 顺带清 `UIUtils.allSelectors` | `CustomArcadeEntryUI.cs:38-55`;`CustomArcade\Patch.cs:349-354`;`UIUtils.cs:71` |

## 1. 行为定义

sync(刷新)对每个目录判定四类状态并分别处理:

| 状态 | 判定 | 动作 |
|------|------|------|
| 新增 | 目录存在,不在 `levelSetInfos` | `LoadSet`,按排序位置插入 |
| 消失 | 在 `levelSetInfos`,目录不存在 | `UnloadSet` 移除 |
| info 变化 | 目录都在,info 文件 mtime/size 变化 | `ReloadSet`,原位替换同一索引 |
| 内容变化 | info 未变,目录内其他文件 mtime 变化 | 只 sweep 该集场景/依赖驻留包,info 不重载 |
| 未变化 | 其余 | 什么都不做 |

删除目录后的死条目(菜单可见、进关失败,事实 8)由 sync 移除。

守卫条件,任一命中则拒绝并 `LogInfo` 原因:

- 初始加载或上一次 sync 进行中(`Loading == true`)
- 在 DIY 对局中(`PseudoPrefabManager.isInCustomLevel`)
- `GameUtils.GetGameSession() != null`

已知代价,写日志提示,不做迁移:改 `sceneName` 该关旧进度孤儿化;`sceneName` 跨集重复串档(OC2DIYLevel 固有行为)。

## 2. 数据结构

```csharp
// FastInitHost 内,启动与 LoadSet/ReloadSet 时维护
internal static readonly Dictionary<string, AssetBundle> InfoBundles;      // 目录全路径 -> info 包
private static readonly Dictionary<string, SetSnapshot> Snapshots;         // 目录全路径 -> 快照

private sealed class SetSnapshot
{
    public string InfoFileName;
    public DateTime InfoMtimeUtc;
    public long InfoLength;
    public DateTime DirMaxMtimeUtc;   // 目录内全部文件的最大 mtime,用于内容变化检测
}
```

现状缺陷:LoadHeavy 加载 info 包后丢弃引用,sync 无法卸载。改造为加载时写入 `InfoBundles` 和 `Snapshots`。

## 3. 三个原语

### UnloadSet(dirPath, so)

1. 收集该集 bundle 名单:so 的每个 `levelInfos` 的 `sceneName`(原文与小写双变体)与 `dependencies` 原文,加入 `HashSet<string>`
2. 对名单逐个查 `AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name, out _)`,非 null 则 `UnloadAssetBundle(name)`;整表反复 sweep 直到全为 null 或 16 轮上限(应对 refcount 累积,事实 3)
3. `InfoBundles[dirPath].Unload(false)`(释放内存,顺带清任何残余句柄),移除字典与快照条目
4. 从 `levelSetInfos` 移除对应项

依赖包物理上位于各集自己的目录(gua 的 `LoadDependencies` 从重定向后的集目录加载)。跨集同名依赖由 refcount 保证安全,被连带卸载的集下次进关自动重载。

### ReloadSet(dirPath, 旧 so)

1. 用旧 so 执行 UnloadSet 的步骤 1-3(sweep 名单必须来自旧 so,改名后的新 sceneName 与旧驻留包无关)
2. 异步加载新 info 包与 `LevelSetInfo`,复用 `WaitOp` 超时回退模式
3. `levelSetInfos[i]` 原位替换为同 Key 新值对,索引不变——三个操作里唯一零位置副作用的
4. 更新 `InfoBundles`、`Snapshots`

### LoadSet(dirPath)

1. 异步加载 info 包与 `LevelSetInfo`
2. 按 `StringComparer.OrdinalIgnoreCase` 的目录路径序找到插入点,`levelSetInfos.Insert`。禁止盲目追加:目录序是字母序,新目录可能排在中间
3. 写入 `InfoBundles`、`Snapshots`

## 4. Sync 协程

入口 `TrySyncReload`:过守卫后,置 `Loading = true`、`TotalSets = rescan` 的目录数(OnGUI 进度复用),新建 host 挂协程。步骤:

1. `SafeGetDirs` 重扫 `levels/`,对每个目录 `GetFiles("info*")` 取第一个,读 mtime/size 与目录最大 mtime,构建新快照集
2. 与 `levelSetInfos` 现键及旧快照 diff,得出四类清单
3. 依次应用:先 `UnloadSet` 全部消失项,再 `ReloadSet` 全部 info 变化项,再 sweep 全部内容变化项,最后 `LoadSet` 全部新增项。每项 try/catch,失败只记日志不阻断
4. `levelSetInfos` 按目录路径做一次稳定排序(幂等,消除任何顺序漂移)
5. `Resources.UnloadUnusedAssets()`(回收被替换/移除集的旧 SO 与截图)
6. 有任何变化:`_builtCount = 0` 强制 `HealLevelSetButtons` 重建菜单;arcade 侧在菜单 GameObject 非 active 时将其销毁(置空静态无效,见事实 12),并清 `selectorOptions`,下次切标签页自动重建;菜单开着则本轮跳过并记日志
7. `FinishLoading()`,日志摘要 `sync: N added, M removed, K info-reloaded, J content-swept, T unchanged`

未变化时提前退出,只记 `sync: no changes`,不动菜单。

## 5. 刷新按钮

`EnsureReloadButton(menu)`:按名查重,`UIUtils.AddButton` 后 `SetAsFirstSibling`,`interactable = !Loading`。调用点三处:

1. `HealLevelSetButtons` 全量重建末尾
2. `NotifySetAdded` 增量追加后(初始加载期间逐集补按钮,把刷新按钮顶回第一位)
3. `FinishLoading`:恢复置灰状态

文案 `"Reload level sets" / "刷新关卡列表"`,点击调 `TrySyncReload`。第一位与 `NotifySetAdded` 的尾部追加天然互不干扰。实测后按用户要求移除了热键,按钮是唯一入口。

## 6. 启动路径改造

只改 `LoadHeavy` 一处:加载 info 包成功后写入 `InfoBundles[dir]` 和 `Snapshots[dir]`(含 info 文件名、mtime、size、目录最大 mtime)。prefetch 并行结构、目录序、common* 幂等跳过全部保持。`InitializePrefix` 与 `RunCheapInit` 不动——sync 不重建 common,不存在全量重载路径。

## 7. csproj 与版本

无新增程序集引用(热键移除后不再需要 Input 模块)。`PluginVersion` 与 csproj `<Version>` 同步升 `1.4.0`。

## 8. 实现坑位

- 卸载名单匹配用原文与小写双变体(事实 9);sweep 达到轮次上限仍有驻留包时 `LogWarning` 名单
- 跨集同名 sceneName 时,sweep 一个集会连带卸掉另一个集的同名驻留包;前端无会话,对方下次进关自动重载,只产生日志噪音
- 快照以 `infoFiles[0]` 为准;info 文件改名或增删会表现为 info 变化,触发一次多余重载,无害
- 硬盘上正在被替换的文件若恰有驻留包锁(未压缩/LZ4 构建,事实 8),替换在资源管理器侧报"正在使用";先按一次 sync(sweep 驻留包)再替换即可
- net35/C# 7.3:try/catch 内无 `yield`;`UnloadUnusedAssets` 的 `AsyncOperation` 协程直接 `yield return`;`List<T>.Sort` 用 `Comparison<T>` 委托
- 保存选择对话框打开的瞬间触发 sync:守卫拦不住(此时尚无 GameSession),与随后的进关存在竞态。`selectedLevel` 只写不清,无法用作守卫;窗口极窄,接受为已知限制
- 日志关键词沿用 `DIYLevel FastInit`

## 9. hook 面增量

| 目标 | 类型 | 归属 | 增量 |
|------|------|------|------|
| `DIYLevelAssetBundleManager.Initialize` | Harmony prefix | gua | 0,沿用现有 |
| `DIYLevelEntryUI.AddUI` | Harmony postfix | gua | 0,沿用现有 |
| 游戏 `AssetBundleManager` 卸载 | 公开静态调用 | 游戏本体 | 0 hook,0 反射 |
| `CustomArcadeEntryUI` 两个 public static | 置空触发重建 | gua | 0 hook,0 反射 |

gua 反射契约维持 AGENTS.md 记录的 7 个成员,不扩大。

## 10. 验证清单

每轮 grep `LogOutput.log` 中 `DIYLevel FastInit`。

1. 冷启动回归:`common bundles` / `level set [name] ready` / `FastInit complete` 齐全
2. 新增目录 → sync → 按排序位置插入,菜单与 arcade 选择器同步更新
3. 复现事实 8:删目录 → sync 前菜单仍显示该集但进不去;sync 后条目消失
4. 改某集 info(加一关)→ sync → 该集原位更新,日志 `reloaded 1`,其余集未动
5. 玩过某关后替换其场景文件 → sync → 重玩该关吃到新内容
6. 只改场景文件不动 info → sync → 日志只 sweep 不重载 info
7. 刷新按钮为菜单第一项:点击触发 sync,sync 期间置灰
8. sync 前后存档星级逐关一致(不含改名场景)

## 11. 分期与风险

Phase 1:第 2-7 节全量,核心一步到位。

Phase 2(可选):`FileSystemWatcher` 自动 sync(3 秒防抖)、`AddUI` 时自动 sync 检测(消除死条目窗口)、`m_LoadingErrors` 反射清理(事实 4)、sceneName 改名/重复警告。

风险与回退:

- 个别 bundle 名卸载行为异常:per-name try/catch,失败退化为该包下次进关可能读到旧内容,重启后必然一致,不影响其余集
- reload 后进关黑屏:公开 API 卸载保证注册表一致(事实 1),理论不成立;若出现,回退方案是砍掉 sweep,只做 info 级重载
- gua 插件更新:反射契约未扩大,风险水位不变
