# IAS 对齐：中断恢复记录（2026-11-23）

本文写给接手的任何模型或会话：从这里能知道现在做到哪、半成品怎么判断、怎么接着做。

## 0. 最新状态（2026-11-23 23:05，Claude 额度用完，交给 GPT 接手）——先读这一节

### 0.1 代码在哪
- **集成分支 `wip/ias-e12e` = `f9bb2ac`，运行时 2.1.66**。工作树是 `D:\gitCode\HD2ModManager-e12e-cmotion`（分支 `wip/ias-e12e-cmotion`，和集成分支同一个提交）。接着开发就在这里做。
- **测试工作树** `D:\gitCode\HD2ModManager-ias-to-patch`（`exp/ias-to-patch`）现在停在 `09ae7cf`（2.1.65）。用户的 dev 快捷方式指向它。**只有用户同意后**才能把它快进到集成分支：`git -C D:\gitCode\HD2ModManager-ias-to-patch merge --ff-only wip/ias-e12e`。开发时不要在里面改文件或构建，它的 watch 会自动重编。
- 可以删的旧工作树：`D:\gitCode\HD2ModManager-e12e-roster`、`D:\gitCode\HD2ModManager-e12e-live`（都已合并完）。删之前要问用户。
- 测试用 Mod 已经用新转换器转好：`D:\gitCode\HD2ModManager-e12e-cmotion\tmp\ias-to-patch\e12f-out\IAS-DP8-runtime2.1.65.zip`。joint-pose 里带 `profileHashes`。用户已经导入管理器了。
- **IAS 的做法统一查** `docs/agent/ias-reference-roster-physics-corpse-identity-20261123.md`，内容包括护甲名单、发现开销、归属、物理绑定、身份、尸体定格和原生部分。每批的结果写在规划 `ias-whole-body-retarget-plan-20261123.md` 的 §21–§25。
- 回归命令：`$env:PYTHONIOENCODING='utf-8'; python tools/physbone-harness/run_physbone_regressions.py`，当前 916 项全过。C# 用 `dotnet build -c Debug -nodeReuse:false -m:1 -p:NuGetAudit=false`（要求 0 警告）和 `dotnet test -c Debug --no-build -nodeReuse:false`，跑完执行 `dotnet build-server shutdown`。

### 0.2 实机结果（2.1.65，2026-11-23 22:30，用户实测）
- 先遇到一次"全身摆大字"：原因是管理器设置里「物理模式」是「关闭」，运行时没有部署。用户改成「外置加载器兼容」后正常。**物理模式关着时，IAS 转换的 Mod 一定会摆大字。**
- **打起来只有 20 多帧，卡成 PPT，用户很不满意**（"这是人类能玩的？"）。日志 `%LOCALAPPDATA%\hd2physbone.log` 的 PERF 行（4 人、任务中、约 30 fps）：
  - F9 约 2880 次/帧、7 ms，F4p 约 1100 次、5.5 ms，F6 约 1170 次、3.2 ms，F3 约 430 次。
- 落地后摆大字约 20 秒才恢复正常。
- 两者在 2.1.66 的处理见 0.3。**2.1.66 还没有上过实机。**

### 0.3 2.1.66 做了什么（P1 批，规划 §25）
- **F9 暴涨的根因是尸体定格的活体采样。** 角色为 1/2 时，每 0.25 s 对每个主人的每件护甲逐节点调用 `world_pose + to_elements`：4 人 × 14 件 × 约 180 节点 × 2 ≈ 2 万次，全挤在一帧。
  - 现在每帧只采一个主人的一整组，峰值从 20279 降到 5087，但**平均仍约 2700 次/帧（约 5 ms）**。
  - IAS 同样每 0.25 s 采一次，但它读的是 retarget 已算好的显式世界姿态（R:4705），几乎不花钱。
- **摆大字的根因：独立骨原来只给身份判定为 role 1 的本人做**，要等身份解出来；队友默认根本不做。
  - IAS 不等身份，每帧对每个绑定的 avatar 都做 retarget（R:6860–7010）。现在改成不等身份，队友默认开。
  - 队友路径保留旧的限频：20 Hz，6 m 外每 4 帧一次。这和 IAS 的每帧不一致，**用户还没表态**。
- PERF 行拆了子项：F9d 发现、F9i 身份、F9c 尸体、F9s 物理同步。F3、F4p、F9 也会打印 API 明细。

### 0.4 下一步（按顺序；每一步先查 IAS 怎么做，照做）
1. **尸体采样改用 retarget 的结果**（照 IAS R:4705 explicit）：IndependentLink 本来就在算这些骨的姿势，让它把节点世界姿态交给 CorpseFreeze，不要再去问引擎。纯物理件（没有独立骨的）怎么采，先查 IAS；查不到就报告用户。
2. **写骨照 IAS 省调用**：没变的骨不写（IAS R:2387 用阈值判断 `unchanged`，R:2648 起只写变化的节点）；每个 Unit 每帧只调一次 `World.update_unit`（R:4566）。F4p（IndependentLink.present）和 F6（物理写骨）都要做。
3. 测试必须用**实机场景**的数量来量（4 人、每人 14 件、约 180 节点、1.9 万个 unit、30 fps，见 `perf-combat-test.lua` 里的"P1 任务场景"），和 IAS 同场景的预算比。**写骨这部分以前从来没和 IAS 比过，之前说"开销不超过 IAS"只覆盖了发现和身份**，这次要补上。
4. 做完问用户能不能快进测试工作树；让用户测之前，提醒他物理模式必须开。想单独看尸体定格的开销，可以在 `%LOCALAPPDATA%\hd2pbx_tune.txt` 里加 `corpseFreeze = 0` 对比。这个文件是用户自己的：Claude 新建在 %LOCALAPPDATA% 的文件游戏看不到，所以要让用户手动加。那里的 `identLog = 1` 会多写日志，测帧数时建议改成 0。

### 0.5 待用户决定的事
- 队友 retarget 用 20 Hz 限频，还是照 IAS 每帧都做。
- 尸体副本不删（照 IAS，上限 256）：等对齐完成后再做"能不能删"的实验。
- 设置页要不要加尸体定格开关（IAS 面板没有这个开关，默认开）。
- 不是 IAS 做法、而是我们自己补的地方：死者撕裂估计（IAS 用原生快照），采样分帧（IAS 用的是免费数据，不需要分帧）。

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
| E12a | 物理求解器离线逆向（Python 参考模型 `e12/work/pysim3.py`，头发链误差 <0.2 mm），**未移植到 Lua** | 部分完成 | 规划 §18 |
| E12b/c | 2.1.58–2.1.59：分项开销计数 `[PERF]`、找武器缓存、计数去污染、RESYNC 改为平移不重置、无枪手臂跟动画 | 已提交 `13a83a2`、`36caedd`，实机测过 2.1.58 | 规划 §19，开销清单 `ias-perf-inventory-20261123.md` |
| E12d | 2.1.60：独立骨找件和归属换成 IAS 的每帧枚举（Roster），使用 Network/GameSession/IdString64（白名单外，用户同意） | 已提交 `c97f607`，**未实机** | 规划 §20，对照表 `ias-runtime-lifecycle-map-20261123.md` |
| **E12e** | 物理会话生命周期改 IAS 模型（运行时 2.1.63） | B1/B2/B2b/B3/B4/B5 完成（B4：持枪找武器、删看门狗、物理归属闸；B5：删残留判活与 item.disabled），未实机验证 | 规划 §21、§22、§23；IAS 做法备忘 `ias-reference-roster-physics-corpse-identity-20261123.md` |
| **E12f** | 尸体定格全 IAS 化（运行时 2.1.65） | C1 CorpseMotion / C2 CorpseFreeze / C3 判活 / D1 整合测试+默认开 / D2 成本对账（§24.5）完成，Lua 回归 906 项全绿；未实机验证 | 规划 §24；用户待决：IAS 式不销毁副本的删除实验 |
| E12（其余） | 物理算法对齐（C 档，求解器移植到 Lua） | 未开始 | 任务说明待写 |
| 之后 | 实机测试，家族过滤/authored/租约/marker/第一人称门控/对端抑制，第一人称手，消耗对标 IAS，降级开关 | 未开始 | |

### 2.0 分支和工作树（2026-11-23 深夜）

- 集成分支：`wip/ias-e12e`（E12e + E12f，2.1.65），工作树 `D:\gitCode\HD2ModManager-e12e-cmotion`（分支 `wip/ias-e12e-cmotion`，与 `wip/ias-e12e` 同一提交）。
- 测试：`D:\gitCode\HD2ModManager-ias-to-patch`，分支 `exp/ias-to-patch`，按快进方式跟进集成分支。dev 快捷方式指向这里。**开发时不要改这个工作树**，watch 会自动重编，打断用户测试。
- 旧开发树 `D:\gitCode\HD2ModManager-ias-e12`（`exp/ias-e12`，2.1.60）已不再是开发主线。

### 2.1 实机已知问题（截至 2.1.59 实测）

- 卡：4 人局世界约 1.5 万个 unit。每帧姿势驱动约 900 次调用、约 4 ms，物理约 550 次、1.5 ms。帧率低于 30 时，每秒 30 次的姿势修正几乎每帧都触发（IAS 默认就是 30 Hz）。
- 死后、复活后变 T 字：2.1.60 修了独立骨部分，2.1.65 加了尸体定格，待实机确认。
- 突然变透明：原因待查，2.1.59 起加了"摘件原因"日志。
- 第一人称看不到手：还没做。

E6–E11 已提交为一个检查点（见 `git log`，提交信息以"E6–E11 检查点"开头）。注意：`tmp/` 不进 git，所以 E9 真值工具（`tmp/ias-to-patch/e9/`）、各批的对照脚本和产物都只存在本机，不能删。

### 2.1.0 E12f 已知问题 / 待决
- 尸体副本默认开；开关只有调参文件 `corpseFreeze = 0/1` 与安装选项，没有设置页（IAS 1.6.4 面板同样没有；落盘在 `optimization.json` 的 `freeze`，默认 true）。
- 副本不销毁（IAS 常态，上限 256/会话），长局会累积；删除是以后的实验。
- 原生 `freeze_corpse_physics` 以"停原件 Lua 物理会话"代替；`corpse_freeze_context`、profile gender/authored 标志没有。
- 开着时每帧多查网络（`sharedLife`），D2 已把它算进 IAS 同场景预算（IAS 也照付，R:5097–5119），perf-combat 开着测。
- `runtime-harness-test.lua` 的 2 条尸体用例仍为 pending（该 harness 没有网络身份造不出 role 3），已由 `corpse-integration-test.lua` 覆盖。
- 实机未验证。

### 2.1 E12e-B1：Roster 枚举全部转换件（分支 `wip/ias-e12e-roster`）

- 转换器 `IasDp8Converter` 新写 `profileHashes`（`[{hash, option}]`，含有独立骨的件和有物理的件），`JointPoseData` 转成 Lua 选项 `profileHashes`。`independentPieces` 语义不变。
- 运行时 `roster_hashes` 枚举这份名单（IAS 的单一 profile 表）；物理按"发现的件 → 哈希 → 容器（`physics_rig`，每个哈希只匹配一次）"绑定。兼容垫片：旧数据没有 `profileHashes` 时退回 `independentPieces` 的键（不是 IAS 机制）。
- 发现对齐：没有名单就不扫（连 60 帧全量也不做）；归属取 `{1,0}` 全部探针、每个 avatar 取最小距离、`num_scene_graph_items > 1`；signature 用实例槽位（`Roster.instanceSlot`，IAS 行 925–945）；600 帧没扫的世界记录清掉（行 1651–1656）；只扫"有 avatar 且单位数 > 0"的世界。
- `physics_rig` 的部分命中条件恢复为 2.1.60 的 `full or allowPartial or score >= 1`（按件切骨的产物只带容器的一部分骨）。
- 仍缺的 IAS 做法：家族过滤、authored 归属证明、租约、marker、第一人称门控、scene_sources / 第一人称图的 active 世界判据。
- 旧用例已在 B3 改写完毕（规划 §21）：T7/T8/T11/T20/T21/T24/T26 改成 IAS 行为，perf-combat 门槛换成 IAS 同场景预算；依赖尸体定格的两条为 pending。

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
