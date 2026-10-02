# DIYLevelFastInit

《Overcooked! 2》 [OC2DIYLevel](https://github.com/gua248/Overcooked2-LevelEditor) 自定义关卡插件的启动加速补丁：**主菜单不再冻结 20 秒以上**，关卡包转为后台异步加载，并带实时进度指示。

[English README](README.md)

## 解决什么问题

OC2DIYLevel 插件在主菜单读档时（`MetaGameProgress.ByteLoad` 的 Harmony Postfix → `DIYLevelAssetBundleManager.Initialize()`）会在主线程同步执行：

- `AssetBundle.LoadFromFile` 加载全部 `common*` 依赖包与 `levels/` 下每个关卡集的 `info_*` 包（关卡多时，例如 135 个文件 / 353MB，约阻塞 20–30 秒，期间游戏看起来像卡死）；
- 无幂等保护，二次读档会把所有包整个重载一遍；
- `commonW1/commonW2` 若已被其它加载器（如 OC2DIYLevelRuntimeWLoader）常驻，仍会读完整文件后才被 Unity 拒绝，浪费 2–3 秒；
- 关卡集菜单的按钮列表是首次创建时的快照（`if (menu != null) return` 守卫），之后加载完成的条目永远不会出现。

关卡越多，冻结越久。

## 工作原理

本插件用 Harmony Prefix 替换 `DIYLevelAssetBundleManager.Initialize`：

1. **廉价部分保持同步**（`common` 主包、静态字段、DLC 数据，毫秒级），`IsInitialized` 立即为 true，所有下游补丁行为不变；
2. **重活转入协程**：`common*` 与全部 `info_*` 包用 `LoadFromFileAsync` 后台加载，info 包先并行预取（所有读取请求同时排队）再按目录顺序消费，保持关卡集顺序稳定（街机 MOD 的位置映射依赖此顺序）；
3. **已加载的包直接跳过**（按 bundle 名检测，省去重复读取）；任何异步请求 15 秒未完成自动回退同步加载（原版同步路径已验证可用）；
4. **修掉幂等隐患**：二次 `ByteLoad` 不再重载；
5. **UI 自愈**：`AddUI` Postfix 检测列表数量与菜单按钮数不一致时清空重建；加载过程中列表逐条追加、无闪烁；
6. **进度指示**：屏幕右上角角标 `DIY levels loading N/M`，"更多关卡"菜单标题实时显示 `更多关卡（加载中 N/M）`。

## 效果

- 启动后主菜单立即可交互，不再冻结；
- 关卡包在后台加载完毕（时长取决于体量，353MB 实测约 20 秒），期间角标显示进度；
- 加载完成后"更多关卡"列表完整可用。

## 安装 / 卸载

前置：BepInEx 5.4.x + [OC2DIYLevel 插件](https://github.com/gua248/Overcooked2-LevelEditor/releases)（`dev.gua.overcooked.diylevel`，0.10.0 实测）。

- 安装：把 `DIYLevelFastInit.dll` 放进 `BepInEx/plugins/`；
- 卸载：删除该文件即可，不修改 OC2DIYLevel 本体或存档。

## 构建

需要 .NET SDK 与一份装好 BepInEx + OC2DIYLevel 的游戏目录：

```bash
dotnet build -c Release -p:GameDir="C:\Path\To\Overcooked! 2"
```

`GameDir` 不传时使用 csproj 里的默认值（作者的机器路径，换机器请传参或改 csproj）。产物在 `bin/Release/DIYLevelFastInit.dll`。

## 声明

社区第三方补丁，与 Team17、gua248 无关。通过对 OC2DIYLevel 插件的互操作分析开发，不含其原始代码。
