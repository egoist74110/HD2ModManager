# IAS → DP-8 普通 Mod · P3 运行时手臂姿态修正报告

分支 `exp/ias-to-patch`（独立工作树 `D:\gitCode\HD2ModManager-ias-to-patch`）。

## 1. Lua 求解器与运行时安全集成

- **`HD2PhysBoneCompat/src/physbone_compat.lua`**：
  - 按 `C:\Users\q1243\Downloads\hd2pbx_api.txt` 严格白名单（`Unit.has_node`、`Unit.node`、`Unit.num_scene_graph_items`、`Unit.world_pose`、`Unit.local_pose`、`Unit.local_rotation`、`Matrix4x4.to_elements`、`Quaternion.from_elements` / `axis_angle` / `to_elements`、`Matrix4x4.from_quaternion_position`、`Unit.set_local_rotation` / `set_local_pose`、`World.update_unit`、`Unit.animation_has_variable` / `animation_find_variable` / `animation_get_variable`）1:1 移植 P2 手臂求解器：
    - `M.resolve_arm_bones`：先检 `Unit.has_node(unit, name) == true` 再取 `Unit.node`，校验 `id >= 1 and (nodeCount == nil or id <= nodeCount)`；探测动画变量前若存在 `Unit.animation_has_variable` 亦先判 `true`。
    - `M.probe_fast_write` / `M.verify_fast_write` 安全门：`apply_arm_pose` 首次拿到有效 `unit` 与 `l_elbow` 时按需执行一次自检与回读校验；仅当 `M._fastWrite == true and M._fastWriteVerified == true` 且不改位置时才走 `Quaternion.from_elements` + `Unit.set_local_rotation`（2 次调用/骨），否则一律用已读 `poses` 算出的局部平移退回 `Matrix4x4.from_quaternion_position` + `Unit.set_local_pose`，绝不裸调 `set_local_rotation`。
    - `M.solve_unarmed_side`：无武器内收（默认 `adductionDeg = 16`、`forearmCompDeg = 5`、`shoulderNarrow = 0.025 m`），收肩沿 `clavicle → shoulder` 向量等比缩放，骨链方向无关。
    - `M.solve_ik_side`：持枪 2-bone 解析 IK（默认 `inwardPull = 0.09 m`），锁定 `*_hand` 世界位置与旋转。
    - `M.detect_arm_target_alpha`：优先探测动画变量，退回角色局部几何判据（手腕相对肩高 + 双手位于胸前判定）。
    - `M.apply_arm_pose`：按 `dt` 平滑过渡 `alpha ∈ [0, 1]`，写回局部姿态并在 `written > 0` 时刷新 `World.update_unit`。
- **`HD2PhysBoneCompat/src/physbone_runtime.lua`**：
  - 支持安装参数 `options.armPose`（默认 `false`）及热调键 `armPose`、`armAdductionDeg`、`armForearmCompDeg`、`armShoulderNarrow`、`armIkPull`、`armMode`。
  - **默认关闭零开销**：`armPose = false` 时 0 额外引擎调用，心跳格式与旧版完全一致。
  - **零物理护甲兼容与多人安全**：即使 `runtime.sessions == nil`（无物理链的普通 Mod），当 `armPose = true` 时仍通过 `resolve_local_avatar_for_arm` 发现本地 `avatar_helldiver`、每帧驱动手臂修正并在心跳输出 `[physbone] 手臂修正心跳 ... 手臂调用=...( .../帧)`；若已有 `runtime.sessions` 与 `runtime.avatars`（多人联机）则直接复用 `runtime.avatars[1].unit` 而不覆盖 `runtime.avatars`；`refresh_live_units` / `Unit.alive == false` 时自动清理 `runtime.avatar` 与 `runtime.armState`。

## 2. 管理器默认关闭开关（低耦合）

- `HD2ModCore/Infrastructure/PhysBoneLuaSource.cs`：`ComposeStandalonePackage(..., bool armPoseEnabled = false)`；`false` 时输出与原版逐字节一致，`true` 时注入 `armPose = true`。
- `HD2ModCore/Infrastructure/LuaAddonCatalog.cs` & `LuaDeploymentService.cs`：新增 `ArmPoseEnabled`（默认 `false`），透传至 `ExternalLuaEntry` 与 `ExclusiveLuaEntry`。
- `HD2ModManager/Services/SettingsService.cs`、`ViewModels/PageViewModels.cs`、`Views/SettingsPageView.xaml`：新增默认关闭的「手臂姿态修正（实验）」开关，启动时由 `ApplyToLuaDeployment()` 同步并在更改时持久化。

## 3. 验证结果

- **Lua 离线全量回归（`python tools/physbone-harness/run_physbone_regressions.py`，无 `luajit` 时自动回退 Python `lupa` 独立子进程）**：
  - **22 个可执行套件全部 PASS（`PASS=511, FAIL=0, 红=0`）**，2 个缺外部夹具套件 `SKIP(缺夹具)`（`conform-stub-test.lua`、`lift-probe-test.lua`）。
  - 其中 `HD2PhysBoneCompat/test/arm-pose-test.lua`（`25 PASS / 0 FAIL`）覆盖：无武器内收与左右肩对称收窄（`<= 34` 调用/帧）、持枪 2-bone IK（手腕位置误差 `0.00000 m`、旋转误差 `0.00000 rad`、`<= 34` 调用/帧）、`verify_fast_write` 失败安全退回 `set_local_pose`（第 2 帧起零 `set_local_rotation`）、`has_node=false` 与越界下标拒绝、`alpha` 平滑过渡、`armPose=false` 零调用、零物理护甲发现/驱动/心跳、`num_scene_graph_items` 重建、`Unit.alive=false` 清理、多人 `runtime.avatars` 不被覆盖。
- **.NET 构建与全量测试**：
  - `dotnet build WpfApp1.sln -c Debug -m:1 -nr:false`：**0 警告，0 错误**。
  - `dotnet test WpfApp1.sln -c Debug --no-build -m:1 -nr:false`：**2,686 通过，0 失败，1 跳过**（`HD2ModCore.Tests` 1,636 通过含新增 `PhysBoneArmPoseTests`、`HD2ModAdaptation.Tests` 212、`HD2ModCli.Tests` 213、`Decryptor.Tests` 5、`HD2SourceModel.Tests` 72、`HD2SourceMigration.Tests` 548 通过 / 1 跳过 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`）。

## 4. 真实 Lua + 潜兵动画端到端视觉闭环（`tmp/ias-to-patch/p3-out/`）

使用 `tmp/ias-to-patch/p3-out/p3_export_anim_poses.py` 从 P1 模型与 5 个真实潜兵动画导出骨架位姿，经 `tmp/ias-to-patch/p3-out/p3_run_lua.py`（Python `lupa` 直接加载真实 `physbone_compat.lua` + `physbone_runtime.lua`，`armMode = "auto"` 且不提供动画变量以真实测试几何判据回退）运行 15 帧 `render()` 闭环，再由 `tmp/ias-to-patch/p3-out/p3_render_3way.py` 在 Blender 4.3.2（`BLENDER_EEVEE_NEXT` + 真实贴图与 Alpha）渲染左（普通 Mod 原版动画）/ 中（**P3 真实 Lua**）/ 右（原版 IAS 模型）三方对比图。

### 4.1 真实 Lua 5 动作实测统计（`tmp/ias-to-patch/p3-out/lua_poses.json`）

| 动作 | Clip ID | 几何判据 `targetAlpha` | 写入骨数 | 引擎调用/帧 | 左手腕误差 | 右手腕误差 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 站立 `stand` | `c2c16b9f0c49b54a` | `0.0`（空手） | 4 | 31 | —（内收位移 `155.5 mm`） | —（内收位移 `154.9 mm`） |
| 持枪 `rifle` | `2f741aaf34065276` | `1.0`（持枪 IK） | 6 | 31 | `0.0004 mm / 0.0000°` | `0.0002 mm / 0.0000°` |
| 行走 `walk` | `9994e9a6b1b4714e` | `0.0`（空手） | 4 | 31 | —（内收位移 `131.4 mm`） | —（内收位移 `127.1 mm`） |
| 奔跑 `run` | `afb70a7c048986b0` | `0.0`（空手） | 4 | 31 | —（内收位移 `146.4 mm`） | —（内收位移 `152.2 mm`） |
| 换弹 `reload` | `69bec243a5c74461` | `1.0`（持枪 IK） | 6 | 31 | `0.0004 mm / 0.0000°` | `0.0005 mm / 0.0000°` |

### 4.2 渲染效果图路径（均已通过 `read_file` 亲自查看确认）

- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-standing-coat-on.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-standing-coat-off.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-standing-side.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-rifle.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-locomotion.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-run.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p3-out\blender\p3-lua-reload.png`
