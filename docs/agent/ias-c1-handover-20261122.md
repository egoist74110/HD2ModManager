# IAS → DP-8 · C1 交接（2026-11-22 晚）

分支 `exp/ias-to-patch`（工作树 `D:\gitCode\HD2ModManager-ias-to-patch`）。计划见 `unified-joint-pose-converter-plan-20261122.md`。

## 已完成

- **P5 腿部转换**（`65502b4`）：thigh/knee/foot/ball 按权重平移，输出 `legJointTarget`。Blender 验证腿形与 IAS 一致。
- **C1 运行时 + 注入**（`7ce8243`）：
  - Lua 在 `apply_arm_pose` 末尾写 8 个腿关节。实测空手 47 次/帧、持枪 51 次/帧，比原先估计的 43 高。
  - 转换产物在 Mod 根目录放 `joint-pose.json`。
  - 打开「手臂姿态修正」后，部署时会把它注入物理运行时的 install 选项。只取第一份，暂不区分护甲。
- **本次提交**：
  - 藏起 DP-8 护甲层的 10 个部件（`IasSlot.Empty`）：肩甲、腿甲、slot8、slot9、躯干附加层。IAS 原包把这些发布成 310 字节的空壳，我们之前漏掉了，导致潜兵的肩甲、腿甲露在外面。
  - 运行时加了一次性诊断日志：`姿态修正已开`、`姿态修正首次生效`、`没找到玩家角色`、`找不到腿骨`。
  - `run_physbone_regressions.py` 把 `LOCALAPPDATA` 指到临时目录。之前测试会往游戏真实日志 `%LOCALAPPDATA%\hd2physbone.log` 里写假的"已安装/生效"行，误导排查。
  - lupa 脚本加了 `log=False`。

## 实机发现：Lua 从 11-21 13:41 起没在游戏里跑过

- **症状**：每次启动只有一行"已安装"，第 1 帧的探测日志都没有。最后一次真正驱动是 11-21 13:27。
- **原因**：库里的「MDL3v57_CSL1.5.4 联合加载器」（Hermes 生成）的调度 boot 有顺序 bug。
  1. 它在 MDL3 `init` **之前**存下四个全局回调。
  2. MDL3 `init` 时由 MDL3 启动我们的 addon，我们把全局 `render` 换成自己的包装。
  3. 之后它用第 1 步存的旧值覆盖全局，我们的包装丢失。
  - 管理器的护甲映射运行时和低污染隐藏器很可能同样受影响。
- **已修**：在库里原地做等长字节修改，第 2 步改为 `init or mdl_init` 等形式（保留当前值）。
  - 共 55 字节，boot 长度不变，lupa 解析通过。
  - 库文件和游戏里的 `patch_193` 是硬链接，所以游戏里已经生效。
  - 原文件备份在 `tmp/loader-backup/joint-9ba626afa44a3aa3.patch_0.orig`。
  - Hermes 的构建脚本已不在；以后重新生成联合包，必须做同样的修改。
- **已关**：从配置「迁移」移除了单独的 Chef Shared Loader 1.5.4 和 MDL3 3.0.0-dev.57 包（含 MDL3 主模块和 Data Hub），profile revision 284。**尚未重新部署**。
- **兼容性**：
  - 单 MDL3：11-21 之前实测可用。
  - 修好后的三合一：等同于单 MDL3。
  - 单 CSL：按 CSL 源码设计应该可用（它把 addon 自己替换掉的回调当作该 addon 的钩子来调用），未实测。

## 下一步（用户上机）

1. 管理器里重新部署（配置「迁移」）。
2. 导入新转换的 Mod：`tmp/ias-to-patch/c1b/mod`，替换之前的 c1 版本。它多藏了 10 个 DP-8 部件。
3. 进游戏，穿 DP-8 Mountain-Scaled，然后：
   - 看 `%LOCALAPPDATA%\hd2physbone.log` 里有没有 `姿态修正已开`、`姿态修正首次生效` 和每帧心跳。
   - 看头发、裙摆物理是否恢复。
   - 看肩和腿与 Blender 图（`tmp/ias-to-patch/p5-legs/legs-*.png`）是否一致。
   - 记录手臂修正心跳里的每帧调用数。
4. 之后再决定护甲绑定方案（记号骨或手动开关），见计划 §4。

## 环境状态

- 桌面 dev 快捷方式现在指向本工作树，原 .lnk 备份在 `tmp/lnk-backup/`。测完要问用户是否改回。
- 本次提交时有 5 个 `HD2ModCli.Tests` 部署用例失败。原因是管理器正开着，CLI 部署会拒绝执行。已确认在未改动的 `7ce8243` 上也是同样 5 个失败，属于环境原因。关掉管理器后应重跑全量测试确认全绿。
