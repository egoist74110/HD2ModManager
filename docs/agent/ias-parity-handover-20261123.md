# IAS 对齐：中断恢复记录（2026-11-23）

GPT 的 5 小时额度剩下约 34%，任务随时可能中途停下。本文写给接手的任何模型或会话：从这里能知道现在做到哪、半成品怎么判断、怎么接着做。

## 1. 总目标和原则（用户定的）

- **先 100% 复刻 IAS 1.6.4 的效果，以 IAS 为准绳；然后消耗和 IAS 相当；最后降级做开关，比 IAS 更轻。** 比 IAS 卡就算失败。见规划 `ias-whole-body-retarget-plan-20261123.md` §12、§14。
- 代码自己写，不复制 IAS 源码。用户同意在本机加载 IAS 的 DLL，用来做离线对照。
- **硬规则（用户 2026-11-23）："以后不准自己想解决办法，先看 ias 有没有解决办法"；"在没达到 ias 效果之前都不允许乱出方案"。** 出了问题，先去 IAS 源码里找它的做法照搬；IAS 没有的，报告给用户，由用户决定。白名单外、但 IAS 在用的 API 可以照用，逐个列出即可。
- 分工：GPT 额度已用完，改由 Sonnet 子代理实现，Claude 写任务说明和验收。**每一批都新开一个子代理**，不要用 SendMessage 一直续同一个，那样上下文越堆越大，额度消耗很快（用户 2026-11-23 批评过）。
- 工作树见 §2.0。

## 2. 批次进度

| 批次 | 内容 | 状态 | 产物 / 证据 |
|---|---|---|---|
| E6 | 全身独立骨、去掉离线挪顶点、解析握点（运行时 2.1.54） | 完成，未提交 | `tmp/ias-to-patch/e6/`，规划 §10–11 |
| E7 | 挂接 + 30 Hz 修正 + 插帧（2.1.55） | 完成，未提交 | `e7/`，规划 §13 |
| E9 | IAS 离线真值工具（调 1.6.4 原生 DLL） | 完成，未提交 | `e9/ias_oracle.py`、`compare.py`、`lua_replay.py`、`report.md`，规划 §15 |
| E10 | 重定向 + 衣物规则对齐（2.1.56），A 档误差千分之几 mm | 完成，Claude 已复验，未提交 | `e10/mod`、`e10/compare_a.py`，规划 §16 |
| E11 | 握枪和动作分支对齐（2.1.57），B 档误差千分之一 mm | 完成，Claude 已复验（Lua 694 过），未提交 | `e11/compare_b.py`、`e11/blender/`，规划 §17；Mod 沿用 `e10/mod` |
| E12a | 物理求解器离线逆向（Python 参考模型 `e12/work/pysim3.py`，头发链误差 <0.2 mm），**未移植到 Lua** | 部分完成 | 规划 §18；规则摘要见下文 §2.1 |
| E12b/c | 2.1.58–2.1.59：分项开销计数 `[PERF]`、找武器缓存、计数去污染、RESYNC 改为平移不重置、无枪手臂跟动画 | 已提交 `13a83a2`、`36caedd`，实机测过 2.1.58 | 规划 §19，开销清单 `ias-perf-inventory-20261123.md` |
| E12d | 2.1.60：独立骨找件和归属换成 IAS 的每帧枚举（Roster），使用 Network/GameSession/IdString64（白名单外，用户同意） | 已提交 `c97f607`，**未实机** | 规划 §20，对照表 `ias-runtime-lifecycle-map-20261123.md` |
| **E12e** | 物理会话生命周期换成 IAS 模型；尸体副本定格；队友身份补全（目标 2.1.61） | **中断**：半成品在分支 `wip/ias-e12e`（`70f3b89`），回归没跑 | 任务说明见下文 §2.2 |
| 之后 | 物理求解器移植到 Lua、物理调度对齐 IAS、消耗对标、降级开关、第一人称手 | 未开始 | |

### 2.0 分支和工作树（2026-11-23 晚）

- 开发：`D:\gitCode\HD2ModManager-ias-e12`，分支 `exp/ias-e12`（HEAD `c97f607`，2.1.60）。
- 测试：`D:\gitCode\HD2ModManager-ias-to-patch`，分支 `exp/ias-to-patch`，按快进方式合入 `exp/ias-e12`。dev 快捷方式指向这里。**开发时不要改这个工作树**，watch 会自动重编，打断用户测试。
- E12e 半成品：分支 `wip/ias-e12e`。接手时先 `git diff c97f607 wip/ias-e12e` 看做到哪，然后在它上面继续。

### 2.1 实机已知问题（截至 2.1.59 实测）

- 卡：4 人局世界约 1.5 万个 unit。每帧姿势驱动约 900 次调用、约 4 ms，物理约 550 次、1.5 ms。帧率低于 30 时，每秒 30 次的姿势修正几乎每帧都触发（IAS 默认就是 30 Hz）。
- 死后、复活后变 T 字：2.1.60 应已修好独立骨这部分，物理部分和尸体定格待 E12e。
- 突然变透明：原因待查，2.1.59 起加了"摘件原因"日志。
- 第一人称看不到手：还没做。

### 2.2 E12e 任务说明（原文要点）

只照 IAS 做，不加 IAS 里没有的机制，IAS 没有对应做法的写进报告。
1. 物理会话：每帧按 Roster 的结果，以"实例槽位 + profile id"绑定。删掉 discover、打分额度、负缓存、dropAllOnDeath、reconcile、"5 m 外尸体/人台"、兜底归属，以及物理路径上的 World.units 指针闸。按 IAS 的行为重写相关用例，删掉的逐条列出。
2. 尸体副本定格（IAS 行 5236–5990）：每 0.25 秒采集形状缓存，死后 spawn_unit 生成副本、原件隐藏，副本下落 4 秒后定格。
3. 队友身份：补全稳定互斥回退和远端 peer 的枚举。
验收：Lua 回归和 C# 全绿，运行时 2.1.61，写进规划 §21。

E6–E11 已提交为一个检查点（见 `git log`，提交信息以"E6–E11 检查点"开头）。注意：`tmp/` 不进 git，所以 E9 真值工具（`tmp/ias-to-patch/e9/`）、各批的对照脚本和产物都只存在本机，不能删。

## 3. 判断一个批次是不是半成品

1. 看 `tmp/ias-to-patch/eN/eN.log` 的末尾：
   - 有 `EXIT=0`，前面还有完整的中文报告 → 跑完了，去验收；
   - 没有 `EXIT=` 行，或者报告不完整 → 中断了，是半成品。
2. 看规划文档里有没有这一批的小节（E11 对应 §17）。没有，就说明没写完。
3. 看运行时版本：`HD2PhysBoneCompat/src/physbone_runtime.lua` 里的 `M.VERSION`，E11 完成后应该是 `2.1.57`。
4. 用 `git status` / `git diff --stat` 看工作区里改了哪些文件。注意：E6–E10 本来就都没提交，这一步只能当参考。

## 4. 中断后怎么接着做

1. **先确认工作区没坏**：
   - `python tools/physbone-harness/run_physbone_regressions.py`，Lua 回归；
   - `dotnet build -c Debug -nodeReuse:false -m:1 -p:NuGetAudit=false`，要求 0 警告；
   - `dotnet test -c Debug --no-build -nodeReuse:false`。
2. **选接手模型**：按 `~/.ai-prompt/.local/prefs.json` 的 build 角色，顺序是 `claude-sonnet-5-5` → `gpt-6.2-sol` → `gemini-4-hydrogen`。GPT 额度用完了，就用 Sonnet 子代理。
3. **原样使用同一份任务说明**，只在开头加一句：
   > "上一轮执行中断了。工作区里已经有这批的部分改动（见 git diff 和 `eN/eN.log` 末尾），先读它们、判断做到哪一步，在此基础上继续，不要推倒重来。"
4. **如果回归或构建坏了**：先修到全绿再继续；修不好，就和用户确认后再回退这一批的改动。回退前，E6–E10 的成果要先保住（比如先单独提交一个检查点）。
5. Codex 跑构建时，必须带 `-nodeReuse:false`，并且同时开孤儿 MSBuild 清理脚本（记忆 `codex-builds-leak-msbuild-nodes`）。

## 5. 节省 GPT 额度

- 验收、复跑测试、看图、写任务说明，都由 Claude 自己做，不派 GPT。
- 每批任务说明都写清楚"只做什么、不做什么"，避免它跑偏、返工。
- E12（物理）工作量最大。额度不够的话，拆成两段：先算法对齐（离线），再接到挂接调度上。也可以直接交给 Sonnet 子代理。
- 每批验收通过后，问用户要不要提交检查点，减少中断时的损失。

## 6. 关键入口

- 规划：`docs/agent/ias-whole-body-retarget-plan-20261123.md`（§12 起是新方向，§14 是验收门槛，§15 以后是各批结果）
- IAS 1.6.4 差异：`docs/agent/ias-1.6.4-diff-20261123.md`
- IAS 源：`D:\gitCode\HD2ModManager\tmp\ias-lab\upstream\ias-1.6.4\`；反编译：`D:\gitCode\HD2ModManager\tmp\ias-lab\decomp\`；Ghidra 工程：`tmp/ias-to-patch/e10/ghidra`
- 样本：`D:\gitCode\HD2ModManager\tmp\ias-to-patch\sample`
