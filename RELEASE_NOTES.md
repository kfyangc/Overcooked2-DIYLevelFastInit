# 1.9.4.2

## 中文

检测独立版「选档空白修复」（ResourceReadFailureBlocker）DLL，弹窗提示卸载，仅停用已集成的同名功能。

- 启动时检测独立版 GUID `com.ch3ngyz.plugin.overcooked2.diylevel.resourcereadfailureblocker`；检测到后本次启动不创建集成的失败拦截（`ResourceFailureBlocker`），失败拦截由已安装的独立版继续负责，快速载入等其余功能照常运行。
- 同时复用旧版检测的原生弹窗：显示独立版 DLL 完整路径，提示退出游戏、卸载后重启；弹窗期间其余功能不受影响。
- 通过软依赖保证独立版插件先于 FastInit 加载，检测不受加载顺序影响；旧版 FastInit 的冲突仍为整包停用，优先检测。
- 原有的启动后日志冲突警告移除（检测已提前到创建集成功能之前）。

Release 构建通过；装有独立版 DLL 的实机弹窗与"仅停用集成功能"行为待验证。

## English

Detects the standalone ResourceReadFailureBlocker DLL, prompts for removal, and disables only the integrated counterpart.

- At startup the standalone GUID `com.ch3ngyz.plugin.overcooked2.diylevel.resourcereadfailureblocker` is detected; the integrated failure handling (`ResourceFailureBlocker`) is then not created for the session. The installed standalone blocker keeps owning failure handling, while fast init and every other feature keep running.
- The legacy-detection native dialog is reused: it shows the standalone DLL path and asks the user to exit, uninstall and restart; nothing else is affected while the dialog is pending.
- A soft BepInEx dependency makes the standalone plugin load before FastInit, so detection is load-order independent. The legacy FastInit conflict still deactivates the whole plugin and is checked first.
- The previous post-startup log-only conflict warning is removed (detection now happens before the integrated feature is created).

The Release build passes; the in-game dialog and the feature-only disable with the standalone DLL installed still need verification.

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
