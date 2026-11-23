# 1.9.8

## 中文

修复快速进入使用 Web 导出关卡时，共享依赖包尚未加载完成导致的进图失败（弹"必需的 DIYLevel 资源加载或使用失败"）。

- 接管 WLoader 的 `commonW*` 共享依赖包加载：不再等它自己的串行后台队列，FastInit 在启动编目后并行加载全部共享包，WLoader 侧检查直接跳过，不会重复加载；关卡集列表的发布不受影响。
- 进图只等**该关卡引用的依赖包**：入口时解析关卡 info bundle 与场景 bundle 的跨包引用，映射到具体的 `commonW*` 与 DIYLevel `common01..04`/`common*_textures` 文件，只等尚未就绪的那些；仍未完成的请求会提升优先级，其余包继续后台加载、不阻塞进图。实测 star_6 只需 commonW1/2/3，不再等无关的 commonW4/5/6；不引用共享包的关卡（如 test_level、oc1_story）零等待。
- DIYLevel 的 `common*` 在发布关卡列表前已加载且整局不卸载，正常不会参与等待；若某次启动加载失败，入口会**自行补发加载**，而不是直接报资源缺失。
- 等待过程可见且不会卡死：右上角角标显示 `common asset files x/y 百分比`（百分比取自加载请求的真实进度，标签宽度随文字自适应，不再截断）；总超时（默认 60 秒，`WLoaderSharedBundleWaitTimeoutSeconds` 可调）或连续 20 秒进度无变化时放弃等待，转精确失败弹窗。
- 引用校验覆盖 SetupConfig 消费的全部数组：配方、阶段配方（`recipeListSOs`）、`includeRecipeMatchLists`、可选配方、食材、烹饪步骤、每人数脚本配方、持久战波次/敌人/配方。共享包就绪后重载关卡信息使引用重新解析；只有重载后仍无法解析（共享包缺失或损坏）才报错，错误信息列出未解析字段及数量，如 `includeRecipeMatchLists=8`。
- WLoader 版本升级后类名带命名空间也能正确接管（按类型简单名查找）；接口契约变化时自动退化为仅观察、由其自行加载，不影响进图。
- 新增配置（`[Loading]` 段，改后重启生效）：
  - `TakeOverWLoaderSharedBundles`（默认 `true`）：接管共享包加载；关掉即恢复 WLoader 原生行为。
  - `WLoaderSharedBundleWaitTimeoutSeconds`（默认 `60`）：进图等待所需依赖包的总超时。

## English

Fixes level entry failing with "required DIYLevel resource failed" when a Web-exported level is entered before the shared web dependency packs finish loading.

- Takes over the WLoader `commonW*` shared dependency loads: instead of waiting for its serial background queue, FastInit loads every shared pack in parallel right after the startup catalogue, and WLoader skips its own loads so nothing is loaded twice. Publication of the level-set list is not delayed.
- Entry waits only for the dependency bundles **that level references**: the level's info bundle and scene bundle cross-bundle references are mapped to specific `commonW*` and DIYLevel `common01..04`/`common*_textures` files, and only the ones not ready yet are waited for. Still-pending requests get raised priority and other packs keep loading in the background without blocking entry. Here star_6 needs commonW1/2/3 and no longer waits for the unrelated commonW4/5/6; levels that reference no shared pack (test_level, oc1_story) wait for nothing.
- DIYLevel `common*` bundles are loaded before the level list is published and stay resident for the session, so they normally never participate in the wait; if a startup load failed, entry requests the missing bundle itself instead of failing with a resource error.
- The wait is visible and bounded: the corner label shows `common asset files x/y percent` (percent from the requests' own progress; the label sizes itself to its text and is no longer clipped). The wait ends on the configured timeout (default 60 s, `WLoaderSharedBundleWaitTimeoutSeconds`) or 20 s without progress, turning into the precise failure dialog instead of a silent freeze.
- Validation covers every reference array SetupConfig consumes: recipes, phase recipes (`recipeListSOs`), `includeRecipeMatchLists`, optional items, ingredients, cooking steps, per-player scripted recipes, and horde waves/enemies/recipes. Once the bundles are ready the level info is reloaded so the references resolve; a failure is reported only when they still cannot be resolved, and the message lists the unresolved fields and counts, e.g. `includeRecipeMatchLists=8`.
- The takeover also works when the loader type moves into a namespace (looked up by simple type name); if the loader's contract changes, it degrades to observing the loader's own loads instead of interfering.
- New settings (`[Loading]`, applied on restart):
  - `TakeOverWLoaderSharedBundles` (default `true`): take over shared pack loading; disable to restore native WLoader scheduling.
  - `WLoaderSharedBundleWaitTimeoutSeconds` (default `60`): maximum entry wait for the needed dependency bundles.

# 1.9.7

## 中文

启动检查不再阻塞已就绪的关卡集：无需转换的集合优先出现在"更多关卡"，转换期间即可进入已发布的关卡。

- 无需转换的关卡集（此前已检查过或本就是 raw/LZ4 布局）优先发布：启动先按目录序编目发布所有无需转换的集合，再处理需要 LZMA 转换的集合，已就绪的集合不再被排在后面的大转换卡住。最终"更多关卡"列表顺序仍与目录顺序一致（需要转换的集合稍后插入其目录位置，菜单相应重建）。
- 仅 LZMA 数据仍触发转换（原生 LZMA 解压慢）；raw/LZ4 布局解压已足够快，保留原文件不再转换为 LZ4HC（日志计为 fast 项）。触发转换的产物仍为 LZ4HC。
- 转换（`DIY info checking`）期间即可进入、预览已发布的关卡集：门禁从"整个检查期间全局阻塞"改为按文件/按集合阻塞，只有活动批次仍持有的文件才等待。跨集合依赖若被游戏占用导致替换失败，仍保留原文件并下次启动重试（内容等价，无副作用）。
- 修复右上角角标在 checking 与加载状态间疯狂闪烁：checking 文案加入 0.5 秒防抖，进图进度优先级提升到后台 checking 之上。

## English

Startup checking no longer blocks already-prepared level sets: sets needing no conversion appear in "More Levels" first, and published sets can be entered while conversion still runs.

- Sets needing no conversion (previously checked, or already raw/LZ4 layouts) publish first: startup catalogues and publishes every no-work set in directory order before processing sets that still need LZMA conversion, so ready sets are no longer stuck behind large conversions queued ahead of them. The completed "More Levels" list still matches directory order (later conversions insert at their directory position, rebuilding the open menu).
- Only LZMA data still triggers conversion (native LZMA decode is slow); raw/LZ4 layouts decode fast enough and are kept as-is instead of being rewritten to LZ4HC (logged as a separate fast count). Conversions that do trigger still produce LZ4HC.
- Published sets can be entered and previewed while conversion (`DIY info checking`) is still running: gating moves from a global block for the whole checking phase to per-file/per-set blocking, waiting only for files the active batch still owns. A cross-set dependency held open by the game that makes a replacement fail still keeps the original and retries next startup (content-equivalent, no side effects).
- Fixes the corner label flickering rapidly between checking and loading states: the checking text is debounced for 0.5 s, and level entry progress now outranks background checking.

# 1.9.6

## 中文

修复旧版 1.3 DLL 共存时新版完全不加载、也不弹卸载提示的问题。

## English

Fixes the new plugin never loading — and never showing the uninstall dialog — when a legacy 1.3 DLL coexists.

# 1.9.5

## 中文

启动准备与"更多关卡"菜单并行显示，并检测未卸载的独立版「选档空白修复」补丁。

- 每个关卡集的启动转换（`DIY info checking`）与编目按集合流水线执行：一个集合转换完、编目完就立即出现在"更多关卡"菜单，菜单头部显示"加载中 x/y"，不再等全部集合准备完一次性显示。右上角角标在转换期显示文件进度、编目期显示集合进度。
- 转换与编目按集合严格串行（该集合 worker 全部退出、工作目录清理完才发起编目请求），不引入新的并发；转换批次之间的进图/预览门禁保持原逻辑。首次转换的启动总时长可能略增（失去跨集合转换并行），已转换过的启动不受影响。
- 启动时检测仍安装的独立版 ResourceReadFailureBlocker DLL（GUID `com.ch3ngyz.plugin.overcooked2.diylevel.resourcereadfailureblocker`）：本次启动仅停用集成的失败拦截功能，由已安装的独立版继续负责，快速载入等其余功能照常运行。游戏原生弹窗显示独立版 DLL 路径，提示退出游戏、卸载后重启。软依赖保证检测不受加载顺序影响；旧版 FastInit 的冲突仍为整包停用。

## English

Startup preparation now fills the "More Levels" menu progressively, and a still-installed standalone ResourceReadFailureBlocker is detected.

- Startup conversion (`DIY info checking`) and cataloguing run as a per-set pipeline: a set appears in the "More Levels" menu as soon as its own files are converted and catalogued, with a "loading x/y" header, instead of everything appearing after the last set. The corner label shows file progress during conversion and set progress during cataloguing.
- Conversion and cataloguing stay strictly serial per set (all workers exited and the work directory cleaned before that set's catalog request); no new concurrency is introduced, and entry/preview gating between conversion batches is unchanged. A first-conversion startup may take slightly longer overall (cross-set conversion parallelism is lost); startups with everything already converted are unaffected.
- A still-installed standalone ResourceReadFailureBlocker DLL (GUID `com.ch3ngyz.plugin.overcooked2.diylevel.resourcereadfailureblocker`) is detected at startup: only the integrated failure handling is disabled for the session and the standalone DLL keeps owning it, while fast init and every other feature keep running. A native dialog shows the DLL path and asks the user to exit, uninstall and restart. Detection is load-order independent via a soft dependency; the legacy FastInit conflict still deactivates the whole plugin.

# 1.9.4

## 中文

修复升级到 OC2DIYLevel 0.11.1 后，确认存档时卡在选档界面的问题。

- 适配新版关卡配置接口，初始化普通关卡和持久战所需的配置模板。
- 同一份 DLL 兼容 OC2DIYLevel 0.10.0、0.11.0 和 0.11.1。

## English

Fixes a hang on the save selection screen after upgrading to OC2DIYLevel 0.11.1.

- Adapts to the new level configuration API and initializes the templates for standard and Horde levels.
- The same DLL supports OC2DIYLevel 0.10.0, 0.11.0, and 0.11.1.

# 1.9.3

## 中文

新增旧版 DIYLevel 快速载入（启动速度优化）检测，避免更换 GUID 前后的版本同时安装补丁。

- 启动时检测旧 GUID `yang.oc2.diylevel.fastinit`，检测到后停用新版补丁。
- 复用游戏原生弹窗，显示旧版 DLL 完整路径，提示退出游戏、卸载旧版后重启。

## English

Detects legacy DIYLevel FastInit installations to prevent duplicate hooks from versions using different GUIDs.

- Checks the legacy GUID `yang.oc2.diylevel.fastinit` at startup and disables the new version's hooks when found.
- Shows the old DLL's full path in a native game dialog, asking the player to exit, uninstall that DLL and restart.

# 1.9.2

## 中文

修正大量脏盘的洗碗计数与整摞销毁流程，只需房主更新。

- 修复洗碗池超过 15 个脏盘时客户端提前停止的问题，例如放入 20 个后只洗出 4 个。房主保留真实数量，加入／洗完事件发送最多 15 的兼容数量，全部洗完才通知客户端归零。
- 整摞脏盘改用原版单对象销毁消息，客户端连同子盘子一起销毁，避开递归销毁消息固定子实体数组的限制。
- 保持原版消息格式与客户端解码兼容，队友无需更新 FastInit。其他物品的销毁和原有加载功能保持不变。

**要求与安装：** Overcooked! 2、BepInEx 5、OC2DIYLevel 0.10.0 或 0.11.0。关闭游戏后替换 `BepInEx/plugins/DIYLevelFastInit.dll`。地图专用运行库仍按地图说明安装。已安装独立 ResourceReadFailureBlocker 的，请将其 DLL 移出整个 `BepInEx/plugins/` 扫描目录。

## English

Corrects large dirty-stack washing counts and whole-stack destruction. Only the host needs the update.

- Fixes clients stopping washing early above 15 plates, such as a sink with 20 dirty plates yielding only four clean plates. The host retains the actual count, advertises at most 15 in add/cleaned events, and reports zero only when all plates are washed.
- Uses the original single-entity destruction message for whole dirty stacks, destroying child plates with their root and avoiding the recursive message's fixed descendant array.
- Preserves the original wire format and client decoding, so guests do not need to update FastInit. Other object destruction and existing loading features are unchanged.

**Requirements and installation:** Overcooked! 2, BepInEx 5 and OC2DIYLevel 0.10.0 or 0.11.0. Close the game and replace `BepInEx/plugins/DIYLevelFastInit.dll`. Keep any map-specific runtime libraries required by their authors. If the standalone ResourceReadFailureBlocker is installed, move its DLL outside the entire `BepInEx/plugins/` scan tree.
