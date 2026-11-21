# HD2ModManager IAS 集成可行性研究

2026-11-18 · 实验基准 IAS 1.6.3

**离线结果支持把 IAS 转换纳入 HD2ModManager 的候选功能。** VPK 和已有 HD2 Patch 各有一个样本通过官方 Generator 的元数据生成、Unit 检查与场景目录编译。建议先验证由官方 IAS 运行这些转换产物的路径。IAS-Plus 纯 Lua 原型已通过打包后的 Lua 5.1 回归，仍需要单独进行实机验证。

## 接手时卡在哪里

`tmp/ias-lab/ias-plus-proto/` 已有 Addon、Rig Carrier 和 T1–T5 测试脚本。当时缺少验证报告。我用现有脚本复现了 T5 的 `tf5` 摆动为 `0°`。模拟角色缺少 PhysBone 用来识别玩家的 `support_mg`，测试在默认发现与稳定期结束前就读数，模拟引擎也缺少读取基准姿态的 `Matrix4x4.to_elements`。

修正模拟环境后，双手握点误差暴露出克隆根节点的问题。原型把骨骼调色板偏移用作根节点索引，在样本中得到 `-2`，克隆根在玩家移动 3.6 米后仍留在原位。将测试入口切到实际 Addon ZIP 还发现了一个打包错误，Lua 模块末尾的 `return` 后拼接了安装语句，Lua 5.1 拒绝编译。现已修复根节点选择，补齐 Addon 声明并重打包。

## 已验证的范围

| 项目 | 结果 | 证据与范围 |
| --- | --- | --- |
| 布莱默顿 VPK → 官方 IAS | RigOnly 和 WithIasPhys 两种 Companion 各通过 11 个 profile、2 个 assembly、11 个 rig Unit、12 个 point Unit 的离线检查。 | [RigOnly](../../tmp/ias-lab/samples/2505895990_bremerton/dist/generator_validation_report.json) · [WithIasPhys](../../tmp/ias-lab/samples/2505895990_bremerton/dist/generator_validation_report_iasphys.json) |
| AR479939 胧嫣现有 Patch → 官方 IAS | RigOnly Companion 通过 11 个 profile、2 个 assembly、11 个 rig Unit、6 个 point Unit 的离线检查。 | [胧嫣 Generator 报告](../../tmp/ias-lab/samples/ar479939_longyan/dist/generator_validation_report.json) |
| 官方 IAS 与 PhysBone 探针 | Lua 5.1 模拟场景 5 项通过，覆盖低节点克隆、长距离移动、回调顺序、延迟生成和换装。上游回归 120 项通过。 | [PhysBone 探针报告](../../tmp/ias-lab/physbone-probe/dist/probe_verification_report.json) |
| HD2ModManager 沙盒部署 | 5 个用例通过。适用用例的补丁和 `.gpu_resources` SHA-256 一致，编号连续，停用后沙盒剩余补丁为 0。BML 0.13.4 用例使用合成启动补丁。 | [部署报告](../../tmp/ias-lab/compat-check/compat_report.json) |
| IAS-Plus 独立运行原型 | Lua 5.1 从实际 Addon ZIP 加载代码并扫描真实 Rig Carrier 数据，在模拟场景生成两个克隆。移动后的根节点误差低于 0.1 mm，两处可达握点误差均低于 1 mm，臂长保持约 24.16 cm，`tf5` 帧末偏离绑定姿态约 46.69°，清理后恢复原件可见性。 | [IAS-Plus 验证报告](../../tmp/ias-lab/ias-plus-proto/dist/ias_plus_verification_report.json) · [构建报告](../../tmp/ias-lab/ias-plus-proto/dist/ias_plus_build_report.json) |

官方 Generator 的验收使用 `metadata_only`，证明样本产物能进入官方生成链。来源模型范围只有当前两个样本。模拟场景的握点精度和摆动角度是回归数据，游戏内观感和稳定性仍待测。

## 实机反馈与 IAS-Plus 复测

用户确认官方 IAS 布莱默顿能正常显示、切换和活动，加入 PhysBone 探针后的摆动与换装也通过。用户报告 IAS-Plus 原独立配置在看到飞船时闪退。19:08 的转储记录了 `helldivers2.exe` 的 `0xC0000005` 访问冲突，部署记录显示使用了 MDL3、MainMod、IAS-Plus Addon 和 Rig Carrier。

新增严格模拟后，旧 Addon 确实会以索引 `0` 写一个含两个网格的 Unit。官方 IAS 的同类调用使用 `1..网格数` 及 `default` 上下文。IAS-Plus 0.1.1 已遵循这个调用契约，实际 Addon ZIP 的 Lua 5.1 回归通过。19:14 的复测确认部署了 0.1.1，转储仍在 `helldivers2.exe + 0x3BE8C9` 读取地址 `0x4` 时失败，两次签名一致。

进一步读取部署包和 MainMod 后，旧 Rig Carrier 被证实遗漏了 Unit 引用的 Bones 资源。Rig Carrier r2 扫描所有 Unit，注册并携带 14 个本地原始 Bones payload。旧包被新增的离线断言拒绝，r2 的 14 个资源均通过注册与字节对照，实际 ZIP 的 Lua 5.1 回归仍通过。

用户确认只启用 MDL3、MainMod 与 Rig Carrier r2 可以进游戏。加回 Addon 0.1.1 后再次于看到飞船时闪退，19:24 的部署记录与转储确认了包版本及相同的 `0x3BE8C9` 签名。诊断版逐次记录引擎调用及原渲染回调的进入和返回，日志位于 `%LOCALAPPDATA%\hd2iasplus_diagnostic.log`。 [诊断 Addon](../../tmp/ias-lab/ias-plus-proto/dist/IASPlus_Prototype_Addon_0.1.1_Diagnostic.zip)

19:32 的实机日志在 `BEGIN World.spawn_unit position=table rotation=userdata` 截止。`Vector3` 在实机是可调用的命名空间。旧代码对这个调用形态作了错误判断，随后把普通 Lua 表传成了位置参数。0.1.2 始终调用引擎的 `Vector3(...)` 构造位置。严格模拟上，旧 ZIP 重现了 `World.spawn_unit received a table position`，新 ZIP 成功生成两个克隆，原有回归全部通过。高频诊断日志影响本轮帧率，性能验收使用普通包。

0.1.2 实机能进舰桥并生成副本。用户截图显示副本倒悬且停在原地，玩家的原版部件有缺失。离线模拟保留生成位置作为根节点的父变换后，旧 ZIP 把 1.25 米的世界位置重复写成了局部位置。0.1.3 将目标换算到根节点父坐标，查找范围仅限当前世界里唯一符合条件的玩家 Avatar，并在根节点及髋部姿态回读通过后才显示副本、隐藏原件。模拟覆盖了非零起点、非玩家动画 Unit，以及姿态写入被拒绝时移除副本并按原状态恢复网格。实机待复测。首次姿态失败写入 `%LOCALAPPDATA%\hd2iasplus_pose.log`。

0.1.3 实机仍有严重网格变形、移动时闪现以及 B-01 原件混显。19:46 的日志记录 `Clone hips did not follow`。样本 HD2IRG1 第一个源节点为 `boss`，`hips` 在第 49 个；0.1.3 错用了第一个节点的姿态。实际 torso 和 helmet Unit 各有 180 个节点，profile 会跨过未映射的中间父节点，原型直接写 profile 局部姿态也会用错骨骼空间。0.1.4 按名字绑定髋部，通过实际场景图把目标姿态换算到每根骨的真实父坐标，再全部回读后统一显示副本与隐藏已匹配原件。移动中回读失败时恢复原件，并在本局停用替换，避免反复生成闪现。

离线夹具将上述两份 180 节点图的节点哈希及父索引与打包后的 Unit 逐项校验，覆盖静止时的真实绑定姿态、移动跟随、持枪、引擎重置原件显隐后的恢复，以及姿态失败后不再生成副本。0.1.3 在同一夹具上复现髋部偏移，0.1.4 通过这些用例。

0.1.4 实机中，用户换到 B-01 仍显示原版护甲。20:04 的日志在第 1996 帧记录 `Clone torso orientation diverges from the avatar`。替换已匹配到装备，但躯干朝向校验失败，保护逻辑销毁副本并在同一游戏进程内停用替换。离线将玩家躯干骨骼的旋转基准翻转、保持骨点直立，可复现同一错误。0.1.5 从玩家实际髋部、脊柱、头部和双肩骨点求解躯干姿态，保留模型自身骨段长度与朝向校验。

0.1.5 实机仍显示原甲。20:20 的日志在第 1559 帧记录 `Clone pose readback failed at node 5`，该帧已经执行姿态写入与 `World.update_unit`，随后回读校验失败。对照 SHA-256，部署中的 MainMod、Rig Carrier r2 与 Addon 0.1.5 共 15 个文件均与实验室对应 ZIP 一致。日志没有记录 Unit 资源 ID、节点索引基准、期望值和读回值，因此无法区分坐标误差与旋转误差。保护逻辑依然会恢复原甲并停用本进程内的替换。

离线夹具现在从 MainMod ZIP 读取全部 11 个 Unit 的图、局部变换和世界绑定数据，交叉检查打包记录和 Carrier 路由。模拟覆盖 10 个不同的身体资源换装及同一头部的保留、非零位置生成、转身、移动、持枪、网格显隐与失败回退。独立注入第 5 节点回读误差会触发同一错误文字并恢复原甲，说明回退行为可离线验证。该注入无法还原实机产生误差的原因，网格与引擎实际回读也尚未验证。

## 实机验收起点

1. **先验证官方 IAS。** 在独立测试配置中，用布莱默顿 RigOnly Companion 生成 IAS data 包，并搭配同一份 MainMod。使用一个加载器，先关闭两套物理。确认装备映射、F4 换装、跑动、站姿比例和双手握枪都能工作。MDL3 与 BML 的游戏内兼容性分别记录。
2. **再验证物理和性能。** 同一模型按 A/B/C/D/E 采集。对比 B/C、B/D、C/E 时固定画质、场景、加载器及 ReShade 状态，记录帧时间与 1% Low。当前汇总有 **0 条实机采样**。 [性能手册](../../tmp/ias-lab/benchmark/BENCHMARK_RUNBOOK.md) · [采样汇总](../../tmp/ias-lab/benchmark/results/SUMMARY.md)
3. **暂停 IAS-Plus 实机试包。** 0.1.5 的离线报告只作为模拟回归结果。实机读回根因若继续调查，需要一次性采集触发帧的 Unit 标识、节点编号、父节点、写入值与读回值，作为离线重放输入。官方 IAS 配置已通过用户实测，可作为集成可行性判断的依据。 [离线报告](../../tmp/ias-lab/ias-plus-proto/dist/ias_plus_verification_report.json)

本次续跑的代码改动在 Git 忽略的 `tmp/ias-lab/` 内。未部署到实际游戏，未切换管理器配置。仓库 Debug 构建为 0 警告、0 错误，测试 2,575 项通过、1 项跳过。

## 离线复跑命令

在仓库根目录运行。Generator 使用实验目录内 IAS 1.6.3 自带的 Python。

```powershell
python -B tmp/ias-lab/ias-plus-proto/build_ias_plus.py
python -B tmp/ias-lab/ias-plus-proto/test_ias_plus_lua51.py
python -B tmp/ias-lab/ias-plus-proto/test_ias_plus_lua51.py --fault write
python -B tmp/ias-lab/ias-plus-proto/build_ias_plus.py --diagnostic
python -B tmp/ias-lab/ias-plus-proto/test_ias_plus_lua51.py --diagnostic
python -B tmp/ias-lab/physbone-probe/test_probe_lua51.py
& 'tmp/ias-lab/upstream/ias-1.6.3/Generator/python/python.exe' -B tmp/ias-lab/converter/validate_with_ias_generator.py --dist-dir tmp/ias-lab/samples/2505895990_bremerton/dist
& 'tmp/ias-lab/upstream/ias-1.6.3/Generator/python/python.exe' -B tmp/ias-lab/converter/validate_with_ias_generator.py --dist-dir tmp/ias-lab/samples/ar479939_longyan/dist --stem AR479939_Longyan_IAS
dotnet build -c Debug
dotnet run --project tmp/ias-lab/compat-check/DeployCompatLab/DeployCompatLab.csproj -c Debug
dotnet test -c Debug --no-build
```
