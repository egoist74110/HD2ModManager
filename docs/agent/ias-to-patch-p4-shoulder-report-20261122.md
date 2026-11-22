# IAS → DP-8 · P4 肩关节归位（去「焊接钢管」）报告

分支 `exp/ias-to-patch`（工作树 `D:\gitCode\HD2ModManager-ias-to-patch`）。2026-11-22。

## 1. 根因（已用数据确认）

- 「钢管感」不是手臂转角问题，是**转轴位置**问题。
- 模型自己的骨架（IAS）：`l_shoulder` 在 `(-0.102, -0.027, 1.567)`，锁骨→肩只有 2.8 cm。
  潜兵 DP-8：`l_shoulder` 在 `(-0.210, -0.069, 1.4745)`，锁骨→肩 15.6 cm。差 **10.8 cm 向外、9.3 cm 向下、4.2 cm 向后，合计 14.8 cm**。
- P1 的 ArmRefit 把整条手臂网格平移到潜兵肩关节上（手部网格中心实测偏 `(-0.109, 0, -0.087)`），
  连接手臂和躯干的那段网格横向从 13 cm 被拉到 32 cm。P3 运行时只收 2.5 cm（横向 1/4、竖向 0），
  再靠 16° 内收去凑，结果手腕被拉到肩正下方 → 「| |」钢管。
- 网格权重本身和 IAS 一致（逐 x 分桶统计几乎相同），问题只在关节位置。

## 2. 修法

1. **运行时（Lua）**：新选项 `armShoulderTarget = { l = {x,y,z}, r = {x,y,z} }`（锁骨局部坐标，米）。
   给了就每帧把 `*_shoulder` 的局部位置设回模型自己的肩关节，**不再额外内收**（默认 0°/0°）；
   持枪 IK 从挪过的肩解，手腕仍锁在原位。没给时行为与 P3 逐字一致。
   代码：`HD2PhysBoneCompat/src/physbone_compat.lua`（`M.arm_shoulder_targets`、`solve_unarmed_side`、`solve_ik_side`、`apply_arm_pose`），
   `physbone_runtime.lua`（`arm_pose_options` 透传，不再在此填默认值）。
2. **转换（P1）**：`HD2SourceMigration/IasConversion/IasArmPose.cs`
   - `ComputeShoulderTargets`：模型肩关节世界位置 → 潜兵锁骨 bind 的局部坐标，写 `arm-pose.json`，CLI 打印。
   - `ComputeBoneShifts` + `BlendShift`：**取代 IAS 路径的 ArmRefit**。手臂链每根骨平移 = 潜兵关节 − 模型同名关节
     （扭转骨和模型没有的骨沿用父骨），顶点按自己的最终蒙皮权重混合平移。
   - 为什么不能继续用 ArmRefit：它按空间位置渐变搬网格，而运行时挪肩退回的量是「肩权重 × 平移」，两者对不上，
     肩顶会被顶出尖包、外套领子折到胸前（第一版实测出现过）。按权重混合后两者逐顶点抵消。

## 3. 验证

- Lua 回归：`python tools/physbone-harness/run_physbone_regressions.py` → **PASS=523 FAIL=0**（新增 12 条肩目标用例）。
- `dotnet build WpfApp1.sln -c Debug -m:1 -nr:false`：0 警告 0 错误。
- `dotnet test … --no-build`：2,689 通过，1 跳过（既有 `TriangleWindingTests…`）。
- 真实 Lua × 真实潜兵动画（lupa，`tmp/ias-to-patch/p4-shoulder/run_lua_shoulder.py`）：
  空手手部整体平移 148 mm、0° 额外旋转；持枪/换弹手腕误差 ≤ 0.0005 mm；调用 31（空手）/ 35（持枪）次/帧。
- Blender 渲染（左＝新 Mod 关 Lua、中＝开 Lua、右＝IAS）：`tmp/ias-to-patch/p4-shoulder/shift-*.png`，
  肩部特写前后对比 `shoulder-zoom2.png`。站姿、走路、持枪接近 IAS，肩顶尖包消失。
- 新转换产物：`tmp/ias-to-patch/p4-shoulder/p1-shift/`（`mod/`、`arm-pose.json`、`blender/`）。

## 4. 未完成 / 风险

- **进游戏的通路未做**：Lua 不知道玩家穿哪件护甲，开关打开会对所有护甲挪肩 14.8 cm。
  待用户决定：跟 Mod 走（部署时注入 + 按护甲 Unit 判定） vs 设置页手填。
- 关掉 Lua 时新产物是宽肩外张（比旧 P1 更宽），新产物必须配 Lua 用。
- 持枪帧胸口有一个小亮点、外套领口轻微皱褶，未细查。
- 一切结论来自 Blender，未上真机。
- 下一项：持枪/换弹等姿势下腿部扭曲（用户 2026-11-22 指出）。
