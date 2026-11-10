## 中文

自动刷新关卡目录，扩展 LZ4HC 转换，并整合资源加载失败提示。

- 后台约每 5 秒检查关卡目录，发现变化后自动刷新并转换新增或变化的资源包；只有新增、删除或重命名时显示右上角刷新进度。游戏中、切图或选档期间延后处理。
- 将 `info_*`、`s_*`等兼容资源包统一转为 LZ4HC；旧版普通 LZ4 包也可转换，已有 HC 包直接跳过。
- 支持多文件及文件内分块并行，按估算内存预算调度。`Loading.ConversionMemoryBudgetMiB` 默认 800 MiB，可配置；该预算用于转换任务，不是游戏总内存的硬限制。
- 转换校验成功后替换原文件，删除原件备份与恢复记录；全部任务完成后清理仅剩空闲空锁文件的 `.diylevel-fastinit` 目录。**新转换完成后不保留可供恢复的原件。**
- 融合 ResourceReadFailureBlocker 源码，兼容 DIYLevel 0.10 和 0.11。真正导致配置构建或资源准备失败时弹窗提示关卡、资源和错误原因，保留原始异常日志。
- 修复同一关卡再次失败不再提示、错误类型显示不准确，以及把运行期辅助初始化异常直接判为关卡加载失败的问题。

**运行要求：** Overcooked! 2、BepInEx 5，以及 OC2DIYLevel 0.10.0 或 0.11.0。地图另有专用运行库要求时，仍需按地图说明安装。

**安装方式：** 关闭游戏，替换 `BepInEx/plugins/` 中已有的 FastInit DLL。已安装独立 ResourceReadFailureBlocker 的，请将其 DLL 移出整个 `BepInEx/plugins/` 目录，包括子目录中的副本。

**首次启动：** 资源转换仍需等待，过程中会临时占用额外磁盘空间。

---

## English

Automatically refreshes the level catalog, expands LZ4HC conversion and integrates resource-load failure notices.

- Checks level directories in the background roughly every five seconds, refreshing the catalog and converting added or changed bundles when needed. Refresh progress appears only for additions, deletions or renames. Changes wait during gameplay, transitions and save selection.
- Converts compatible bundles such as `info_*` and `s_*` to LZ4HC. Ordinary LZ4 bundles from older versions can also be converted; existing HC bundles are skipped.
- Supports parallel files and block encoding within a file, scheduled against an estimated memory budget. `Loading.ConversionMemoryBudgetMiB` defaults to 800 MiB and is configurable; it controls conversion tasks, not total game memory.
- Replaces source files after validation and deletes their original backups and restoration records. Removes `.diylevel-fastinit` after all tasks finish when it contains only unused empty lock files. **New conversions do not retain restorable originals.**
- Integrates the ResourceReadFailureBlocker source, supporting DIYLevel 0.10 and 0.11. Failures that prevent configuration building or resource preparation show the level, resource and error details while preserving the original exception logs.
- Fixes missing notices on repeated failed attempts, inaccurate exception text and runtime helper exceptions being treated as fatal level-load failures.

**Requirements:** Overcooked! 2, BepInEx 5, and OC2DIYLevel 0.10.0 or 0.11.0. Maps with dedicated runtime library requirements still need them installed per the map's instructions.

**Install:** Close the game and replace the existing FastInit DLL in `BepInEx/plugins/`. If the standalone ResourceReadFailureBlocker is installed, move its DLL outside the entire `BepInEx/plugins/` directory, including copies in subdirectories.

**First startup:** Resource conversion still takes time and temporarily requires additional disk space.
