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
