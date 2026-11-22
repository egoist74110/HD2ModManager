# IAS → DP-8 普通 Mod：进度交接（2026-11-23）

工作树 `D:\gitCode\HD2ModManager-ias-to-patch`，分支 `exp/ias-to-patch`。用户的 dev 快捷方式指向这个工作树。
样本：Milfy 发光外套（替换 SR-64），`D:\gitCode\HD2ModManager\tmp\ias-to-patch\sample`。

## 1. 已完成（都实机确认过）

| 提交 | 内容 | 实机 |
|---|---|---|
| `540924d` | **双臂独立骨**：两条手臂肩以下（含手指）共 40 根骨改名 `ias_*`，绑定放模型关节；Lua 每帧抄 avatar 的 local 旋转 | 手臂、手指正常（鸡爪已修） |
| `e3d3a55` | **持枪 IK**（`independentIk` 默认开，模型手腕落到 avatar 手上）；**头发/外套物理迁移**（读 IAS `HD2PHY1` → 管理器 `HD2SRG01`，随 patch 打包）；**必须/可选分组**（设置页「IAS 可选物理」4 个开关，默认关）；**IAS 专用求解器修正**（胸部 170° 翻转、头发站立乱飘） | 持枪正常；物理"好很多了" |

细节：
- 手臂方案与结果：`docs/agent/ias-independent-bones-plan-20261122.md`（§5.5–5.8）。
- 物理方案、分组表、换算有损点、修复记录：`docs/agent/ias-physics-migration-plan-20261123.md`。
- 关键事实：护甲里和 avatar 同名的骨，画面只认角色动画，运行时写不动；要改形状只能改名成独立骨（或新节点）。
- 离线预览：`hd2mod ias preview` + Blender 后台渲染（`tmp/ias-to-patch/e2/e2_render.py`、`e3` 下的 lupa 脚本跑真运行时 Lua），站立手差 144.5 mm 与实机 145.6 mm 对得上，可以先离线看效果。

## 2. 进行中：性能优化（GPT 已做完，Claude 已验收，**等实机测帧数，未提交**）

> 2026-11-23 更新：GPT 跑完。运行时 2.1.48，产物 `tmp/ias-to-patch/e4/mod`。离线：手臂调用 554→153/帧，全开横移总调用约 977→569/帧；几何逐顶点一致、物理摆幅不变、持枪误差 0.0001 mm。
> Claude 复跑：Lua 559 过、C# 构建 0 警告、测试 2715 过 1 跳过；新增 API 只有白名单内的 `Quaternion.from_elements`。
> 细节见物理方案 §6、独立骨方案 §8。
> **E4 实机进飞船加载即崩**（01:29，dump `c0000005` 读 0x30，helldivers2.exe+3e5674，Lua 还没写任何骨）。
> 疑因：E4 一侧手臂只改名一部分（Torso 改 2 根、其余 18 根仍叫 avatar 原名挂在改名骨下），E3 从没有这种结构。
> 修：`IasIndependentBones.SelectWeighted` 选中骨向下闭合整棵子树 ⇒ 一侧要么整条改名要么整条原名。重转到 `tmp/ias-to-patch/e4b/mod`（改名骨合计 E3 520 → e4b 240；几何与 E3 一致）。
> 之后 GPT 额度紧，用户要求不再派 GPT，由 Claude 自己做。
> **真正根因（对照实验确认）**：E3 数据 + 2.1.48 也崩，不装 IAS 不崩 ⇒ 是运行时。2.1.48 的 `independent_scan` 先对场景每个 unit 调 `Unit.world_position(unit, 1)`，
> 没有第 1 节点的小物件让引擎原生崩（pcall 接不住）。2.1.49 改为先 `has_node` 筛肩骨、命中才读位置。上面"部分改名"的猜测不是原因，但整侧闭合保留（运行时按肩骨筛依赖它）。
> 待测：`tmp/ias-to-patch/e4b/mod` + 运行时 2.1.49。
> **01:44 实机（e4b + 2.1.49）：能进游戏，不崩。** 日志副本 `tmp/ias-to-patch/e4b/hd2physbone-20261123-reload.log`。帧数未量。用户新报两个问题（**下次从这里开始**）：
> 1. **换弹姿势诡异、完全不兼容。** 待查：IK 把模型手腕硬拉到 avatar 手上，换弹时手去弹匣或腰间，模型臂长不同，肘的极向可能翻转；也可能是 E4 前就有（E3 没专门测过换弹）。先用 Blender 和 lupa 套换弹动画离线复现，再定改 IK（换弹时降权或关掉、极向稳定）还是别的方案。
> 2. **偶尔手臂从身体里弹出来。** 日志线索：01:46:14 那次重扫只找到 2 件（`d438…`、`21bc…`），两条手臂件 `bc93…`/`3d7f…` 漏了，那段时间它们没被驱动，独立骨停在绑定姿势，看起来就像手臂伸出来。
>    怀疑重扫时的距离判断（`world_position(unit,1)` ≤ 1.0 m）在某些动作下不满足，或者重扫时把已追踪的件丢了。另外 `l_shoulder差` 有时从约 148 mm 跳到约 300 mm，也值得查。
>    方向：重扫时保留仍存活的已追踪件、放宽或去掉距离判断（改成按"挂在 avatar 上"判断）。下一步：用户导入 E4、重开 dev 快捷方式，全开测帧数，重点看袖口和躯干衔接；没问题就提交。

- 实机：可选组全开时 **100 fps → 约 75 fps**（日志 `tmp/ias-to-patch/e4/hd2physbone-20261123-0058-allon.log`）。
  - 手臂：`手臂调用=554/帧`——7 件护甲每件都写 40 根骨 + IK，腿/头等不含手臂的件是白算。
  - 物理：每帧 400–620 次调用；同一条链在多件护甲上重复模拟（`Butt.L`、`SpineStrap_01` 等）。
- 任务说明：`tmp/ias-to-patch/e4/perf-brief.md`（手臂只驱动有权重的件/骨；物理链去重；其它量化后有效的手段；观感不能变）。
- 2026-11-23 派给 Codex `gpt-6.2-sol`（medium，build 角色），日志 `tmp/ias-to-patch/e4/codex-perf.log`，产物预期在 `tmp/ias-to-patch/e4/`。

### 如果 GPT 额度耗尽 / 中途停了

1. 看 `tmp/ias-to-patch/e4/codex-perf.log` 末尾有没有最终报告；没有就是半成品。
2. `git -C D:\gitCode\HD2ModManager-ias-to-patch diff --stat e3d3a55` 看它改了什么（全部未提交）。
3. 二选一：
   - 接着做：按 `~/.ai-prompt/.local/prefs.json` 的 build 白名单换下一个模型（顺序 `claude-sonnet-5-5` → `gpt-6.2-sol` → `gemini-4-hydrogen`），同一份 brief，加一句"接着工作区里已有的未提交改动继续"。
   - 放弃：`git -C … checkout -- .` 加删掉新增文件，回到 `e3d3a55`（先和用户确认）。
4. 任何结果都要：Lua 回归 `python tools/physbone-harness/run_physbone_regressions.py` 全过、C# 构建 0 警告/测试全绿（游戏开着时 5 个部署用例会被挡），再让用户实机量帧数。

## 3. 待办（用户已确认放后面）

1. **轻量身体碰撞**（还有一点穿模）：只给必须组，只用 IAS 的 3 个胶囊（chest/hips/spine1），带开关。注意 2026-11-03 全量碰撞层因性能被删过（`git show 55fbff6`）。等性能优化后实机再定。
2. **武器碰撞**：大枪穿模，物理看不见枪；以后试在 `attach_weapon` 挂近似胶囊。
3. **外套/鞋子选项**：整组禁用会露出 DP-8 原版（没有文件替换 Torso_Armor `e3c2ff7999bd66ad`/`d438f72d512e082e`）。改成只能二选一，或本体放"脱外套"版。
4. **腿**：仍是旧的离线平移，必要时改独立骨。
5. 接入管理器正式流程（目前靠 CLI `hd2mod ias convert` 产出 Mod 再手动导入）。

## 4. 用户的工作方式（这次确认的）

- 回答用大白话中文。
- 实现交给 GPT（Codex），Claude 写任务说明、验收（看图、复跑测试、核白名单 API、看实机日志）。
- 实机测试：用户自己导入 Mod、开 dev 快捷方式部署；Lua 改了要重开快捷方式；游戏日志 `%LOCALAPPDATA%\hd2physbone.log` Claude 能直接读。
- 提交前：关管理器（允许 `Stop-Process -Name HD2ModManager -Force`）跑全量构建+测试。不提交 `.serena/`。
