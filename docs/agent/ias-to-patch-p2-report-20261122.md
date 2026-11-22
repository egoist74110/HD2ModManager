# IAS → DP-8 普通 Mod · P2 手臂姿态修正离线实验报告

分支 `exp/ias-to-patch`。本轮除完成 P2 纯离线 Blender 4.3.2 实验外，亦根据用户复看反馈在 `HD2SourceMigration/IasConversion/IasNativeMesh.cs` 修复了**手臂 `*_shoulder_twist` 枢轴错位**（将样本处于肘端的 `*_shoulder_twist` 按 50/50 平滑折叠到 `*_shoulder`/`*_elbow`，彻底消除站立/行走时的肘部断层折角；不改动 `HD2ModCore` 与现有 VPK 路径），并新增**原版 IAS 模型三方同框对比**。

## 0. 肘关节 bind-pose 畸变与 twist 枢轴错位修复（`NativeRetargetInput.cs` / `Retargeter.Pose.cs` / `IasNativeMesh.cs`）

- **根因 1（bind-pose `ArmScratch` + L4D2 基底旋转）**：`NativeRetargetInput.cs` 此前未显式关闭 `ArmScratch`（默认 `true`）、`AxisDeltas`（默认 `true`）、`AxisRoundtrip`（默认 `true`），导致 `BuildArmScratchPose` 对本就是 HD2 骨架朝向的 IAS 样本强行拉长大臂（`0.3064 m → 0.443 m`，`+45%`），随后 `ApplyArmRefit` 在肘部过渡带产生 `0.24x / 2.82x` 剪切畸变；且 `Retargeter.Pose.cs` 在 `ArmScratch == false` 时还会默认注入 L4D2 T-pose `_profile.InitialBasisRotations`。
  - 修复：在 `NativeRetargetInput.cs` 显式设置 `ArmScratch = false`、`AxisDeltas = false`、`AxisRoundtrip = false`、`ArmFit = refitArms ? 1.0 : 0.0`；在 `Retargeter.Pose.cs` 的 `BuildPose` 仅对 `_nativeSourceNames is null`（VPK 路径）注入 `_profile.InitialBasisRotations`。
  - 实测 bind-pose 边长比值（P1 vs 原版 IAS）：`LeftArm`（22,986 边）`p05 = 0.934x / p50 = 1.011x / p95 = 1.436x`（彻底消除 `0.24x` 压扁）；`Torso_Armor`（101,970 边）`p05 = 0.969x / p50 = 1.000x / p95 = 1.578x`。
- **根因 2（twist 枢轴错位）**：样本 `LeftArm`/`RightArm` 将 `l_shoulder_twist`/`r_shoulder_twist` 置于肘端（`t = 1.0`），而 HD2 原生骨架将其置于大臂中段（`t = 0.42`，距肘约 18 cm），且站立动画存在 `18.2°`/`37.1°` 相对转角，导致 67 个肘帽顶点绕 18 cm 外的错误枢轴旋转而折断。
  - 修复：在 `IasNativeMesh.BuildArmFoldContext` 中对比样本与目标 HD2 骨架各段 twist 骨（`*_shoulder_twist`、`*_hand_twist`）的归一化轴向投影与绝对间距（阈值 `|Δt| > 0.25` 或 `|Δd| > 0.05 m`），对错位 twist 骨自动平滑折叠到起止主骨（肘端 `*_shoulder_twist` 按 `0.5 / 0.5` 分配至 `*_shoulder` 与 `*_elbow`），消除断层折角。
- **构建与测试**：`dotnet build -c Debug -m:1 -nr:false` **0 警告 0 错误**；`dotnet test -c Debug --no-build -m:1 -nr:false` **2,683 通过，0 失败，1 跳过**（含新增回归单测 `NativeRetargetDoesNotApplyArmScratchStretchOrL4d2BasisToElbowEdges` 与 `MisalignedTwistBoneFoldsProportionallyAlongSegmentToEliminateWrongPivot`）。

## 1. 动画位置轨道核实（§1.3 / P2.3）

检查游戏归档 `18235e0c9ec0e636`（Avatar 状态机 `4d1c334d294dfa97`）：

- **站立 `c2c16b9f0c49b54a`**（0.167 s，Layer 0 状态 0）：位置轨道 0 条，但 `initial_bone_states` 为全部 90 根骨骼存储了初始 `position`，其中 `l_clavicle = (0.1212, -0.0723, 0.0391)`、`l_shoulder = (0.1553, 0, 0)`、`l_elbow = (0.3027, 0, 0)`、`l_hand = (0.2872, 0, 0)`。
- **持枪 `2f741aaf34065276`**（0.167 s，Layer 2）：含 24 条位置轨道，覆盖 `l_elbow`、`l_hand`、`l_hand_twist`、`r_elbow`、`r_hand`、`r_hand_twist`，且 `initial_bone_states` 同样给出全部手臂骨初始局部位置。
- **结论**：引擎动画系统每帧都会覆盖手臂骨局部位置。离线只改补丁绑定矩阵无法保留肩宽收窄；若 P3 需收窄肩宽，必须在运行时每帧调用 `Unit.set_local_position`（或 `set_local_pose`）改写 `*_shoulder` 局部位置。

## 2. 求解器设计与量化指标（`p2-metrics.json`）

脚本 `tmp/ias-to-patch/p2-out/p2_experiment.py` 按 `hd2pbx_api.txt` 的 `local_pose` / `local_position` / `local_rotation` / `world_pose` 1:1 实现：

### 2.1 空手站立扫描（`c2c16b9f0c49b54a`，`forearm_comp = 5°`）

| 内收角 θ | 肩宽收窄 | 左/右肘内收量 | 左/右手 X 坐标 | 穿外套超限边数 | 最大边拉伸 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| `10°` | `0.0 cm` | `4.95 / 4.95 cm` | `-24.52 / +31.69 cm` | 142 | 21.120x |
| `10°` | `2.5 cm` | `7.36 / 7.22 cm` | `-22.11 / +29.42 cm` | 132 | 18.413x |
| `14°` | `0.0 cm` | `7.00 / 7.00 cm` | `-20.69 / +27.90 cm` | 159 | 22.021x |
| `14°` | `2.5 cm` | `9.40 / 9.27 cm` | `-18.28 / +25.63 cm` | 148 | 19.229x |
| **`16°`（推荐纯旋转）** | `0.0 cm` | `8.03 / 8.03 cm` | `-18.77 / +26.00 cm` | 172 | 22.463x |
| **`16°`（推荐+收肩）** | `2.5 cm` | `10.44 / 10.30 cm` | `-16.36 / +23.73 cm` | 155 | 19.632x |
| `20°` | `0.0 cm` | `10.10 / 10.11 cm` | `-14.93 / +22.21 cm` | 202 | 23.330x |
| `20°` | `2.5 cm` | `12.51 / 12.39 cm` | `-12.52 / +19.94 cm` | 169 | 20.425x |

### 2.2 持枪解析两骨 IK（`2f741aaf34065276`，`inward_pull = 0.09 m`）

- 左手位置误差：**`0.0005 mm`**，右手位置误差：**`0.0003 mm`**（远低于 `< 1 mm` 验收线）；
- 左/右手朝向误差：**`0.0000° / 0.0000°`**；
- 左/右肘内收位移：**`8.21 cm / 6.32 cm`**；
- 穿外套超限边数：原版 `287` 条 vs IK `316` 条。

### 2.3 状态平滑过渡（`alpha ∈ [0, 1]`）

在 `alpha = 0.0, 0.25, 0.50, 0.75, 1.0` 下，左肘从 `(-0.1131, 0.2021, 1.0714)` 平滑单调过渡至 `p2-metrics.json` `blend_curve` 终态，左手从 `(0.1456, 0.2836, 1.1660)` 平滑过渡至持枪原位，无跳变。

### 2.4 预期 P3 每玩家每帧引擎 API 调用次数（基于 `hd2pbx_api.txt`）

- **空手 · 纯旋转内收**：每侧 `2 × local_rotation + 2 × set_local_rotation` + 共享 `1 × world_pose(chest)` = **9 次/帧**；
- **空手 · 旋转 + 收窄肩宽**：每侧增加 `1 × local_position + 1 × set_local_position` = **13 次/帧**；
- **持枪 · 两骨 IK**：每侧 `4 × world_pose(clavicle, shoulder, elbow, hand) + 3 × set_local_rotation(shoulder, elbow, hand)` + 状态查询 `1 × animation_get_variable` = **15 次/帧**（命中目标 `15~20` 次/帧，远低于硬上限 `≤ 40`）。

## 3. Blender 4.3.2 渲染对比图

### 3.1 三方同框对比（左：修复后普通 Mod 原版动画 | 中：P2 轻量修正 | 右：原版 IAS 模型）

原版 IAS 模型由 `tmp/ias-to-patch/p2-out/ias-reference.json` 导出（保留样本 `TARGET` 自有 `rest_local` 骨长，套同一潜兵动画的源骨相对旋转，`inherit_parent_pose` 自定义骨保持 `rest_local`）。

| 动作 | P2 `l_shoulder / l_elbow / l_hand` (m) | 原版 IAS `l_shoulder / l_elbow / l_hand` (m) | 差异说明 |
| --- | --- | --- | --- |
| 站立 `c2c16b9f0c49b54a` | `[-0.163, 0.038, 1.474]` / `[-0.201, 0.014, 1.175]` / `[-0.164, 0.143, 0.921]` | `[-0.108, -0.011, 1.569]` / `[-0.222, -0.058, 1.289]` / `[-0.254, 0.070, 1.049]` | P2 肘 X（`-0.201`）已与 IAS（`-0.222`）同量级收拢；肩高差源自 DP-8 原生骨架身高/肩位与样本自有 `rest_local` 差异 |
| 持枪 `2f741aaf34065276` | `[-0.147, 0.194, 1.372]` / `[-0.139, 0.181, 1.070]` / `[0.104, 0.314, 1.144]` | `[-0.046, 0.131, 1.561]` / `[-0.087, 0.165, 1.259]` / `[0.171, 0.239, 1.316]` | P2 双手精确锁在 DP-8 持枪动画手位（误差 `< 0.001 mm`），肘部内收贴身 |

三方对比图（位于 `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\`，均已通过 `read_file` 亲自查看）：

- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-ias-standing-coat-on.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-ias-standing-coat-off.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-ias-standing-side.png`（确认左/中肘关节折角已完全消失）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-ias-rifle.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-ias-locomotion.png`（确认行走肘关节平滑）

### 3.2 P2 参数对比图

全部产物位于 `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\`：

- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-standing-coat-on.png`（穿外套站立：原版 vs 纯旋转 16° vs 旋转 16° + 收肩 2.5 cm）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-standing-coat-off.png`（脱外套站立：原版 vs 纯旋转 16° vs 旋转 16° + 收肩 2.5 cm）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-standing-side.png`（站立侧视图：原版 vs 修正后）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-rifle.png`（持枪 `2f741aaf34065276`：原版 vs 两骨 IK 肘内收）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-locomotion.png`（行走 `9994e9a6b1b4714e`：原版 vs 修正后）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\compare-reload.png`（上身持械动作 `69bec243a5c74461`：原版 vs IK 修正后）
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\ias-dp8-arm-pose.blend`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p2-out\blender\p2-metrics.json`

## 4. 复跑与工作树状态

```powershell
Set-Location 'D:\gitCode\HD2ModManager-ias-to-patch'
& 'E:/Games/blender-4.3.2/blender.exe' -b --factory-startup -t 8 --python-exit-code 1 --python 'tmp/ias-to-patch/p2-out/p2_experiment.py'
& 'E:/Games/blender-4.3.2/blender.exe' -b --factory-startup -t 8 --python-exit-code 1 --python 'tmp/ias-to-patch/p2-out/p2_ias_compare.py'
git diff --check
git status --short
```

未改动 `HD2ModCore` 与现有 VPK 路径，未提交 commit；实验脚本、中间 JSON 与渲染图均在 Git 忽略的 `tmp/ias-to-patch/p2-out/`。
