# 1.9.4.1

## 中文

启动加载与"更多关卡"菜单展示并行化：不再等全部关卡集准备完才显示。

- 每个关卡集的启动转换（`DIY info checking`）改为按集合执行，该集合转换完、编目完就立即出现在"更多关卡"菜单；菜单头部显示"加载中 x/y"，右上角角标在转换期显示文件进度、编目期显示集合进度。
- 转换与编目按集合严格串行（该集合 worker 全部退出、工作目录清理完才发起编目请求），不引入新的并发；`InfoFilesPreparing` 仅在当前集合批次运行期间为真，批次间隙进图/预览按原逻辑放行。
- 总转换工作量不变；失去跨集合的尾部并行，首次转换的启动总时长可能略增。热刷新转换与进度显示保持原行为。

Release 构建通过；实机冷启动逐集显示、热启动回归与联机等待仍待验证。

## English

Pipelines startup preparation with "More Levels" display: sets no longer wait for all sets to finish.

- Startup conversion (`DIY info checking`) now runs per level set; a set appears in the "More Levels" menu as soon as its own conversion and catalog entry complete. The menu header shows "loading x/y"; the corner label shows file progress during conversion and set progress during cataloguing.
- Conversion and cataloguing stay strictly serial per set (all workers exited and the work directory cleaned before that set's catalog request); no new concurrency is introduced. `InfoFilesPreparing` is true only while a set's batch runs; entry and previews pass between batches exactly as before.
- Total conversion work is unchanged; cross-set tail parallelism is lost, so a first-conversion startup may take slightly longer overall. Hot-refresh conversion and its progress display keep their existing behavior.

The Release build passes; in-game cold-start incremental display, warm-start regression and multiplayer waits still need verification.

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
