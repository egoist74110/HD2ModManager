# IAS → DP-8 · P5 腿关节归位（去「小腿中段弯」）报告

分支 `exp/ias-to-patch`（工作树 `D:\gitCode\HD2ModManager-ias-to-patch`）。2026-11-22。只做了转换和 Blender 验证，**Lua 未改**。

## 1. 根因

- 腿的网格基本在模型原位，但弯曲用的是潜兵 DP-8 的关节。

  | 关节 | 模型（IAS） | DP-8 | 差 |
  |---|---|---|---|
  | l_thigh | (-0.082, 0.004, 1.065) | (-0.087, -0.019, 0.942) | 低 12 cm |
  | l_knee | (-0.082, 0.005, 0.624) | (-0.145, -0.035, 0.516) | 低 11 cm、偏外 6 cm |
  | l_foot | (-0.082, -0.038, 0.127) | (-0.191, -0.118, 0.104) | 偏外 11 cm、偏后 8 cm |
  | l_ball | (-0.082, 0.100, 0.062) | (-0.194, 0.011, 0.008) | 相对脚踝差约 3 cm |

- 结果：膝盖在小腿中段弯，小腿外弓，看起来「扭」。

## 2. 修法（与肩膀同一套）

1. **转换**：`HD2SourceMigration/IasConversion/IasArmPose.cs`
   - `ComputeLegShifts`：`thigh`、`knee`、`foot`、`ball` 用自己的平移（潜兵关节 − 模型关节）。`toe`、扭转骨、模型没有的骨沿用父骨的平移。
   - 顶点按最终蒙皮权重混合平移，和手臂合成一张表传给 `IasNativeMesh.Convert`（参数改名 `boneShifts`）。
   - `ComputeLegTargets`：给出这 8 根骨在潜兵父骨 bind 朝向下的局部位置。父骨也在归位表里时，相对的是父骨归位后的位置。
   - 写进 `arm-pose.json` 的 `legJointTarget`，CLI 打印。
2. **运行时（待做，等确认）**：每帧按 thigh → knee → foot → ball 依次 `Unit.set_local_position(unit, node, target)`，旋转不动。
   - 每帧 +8 次调用：空手 31 → 39，持枪 35 → 43，超过 40 的目标。
   - 去掉 `ball`（`toe` 改成跟 `foot`）可以省 2 次，变成 37 / 41，代价是脚掌弯折点偏约 3 cm。

## 3. 验证

- 单测 `LegTargetsPutEveryJointBackOnTheModelAndShiftsCancel`：
  - 把目标依次放回，每根骨都落在模型关节上。
  - 挪动量正好等于 −平移，所以每个顶点逐一抵消。
  - 父骨带 90° 旋转，确认用的是局部坐标而不是世界差值。
- `dotnet build WpfApp1.sln -c Debug`：0 警告 0 错误。
- Blender 静止姿势：用 C# 算出的目标归位后，8 个关节离模型关节最远 0.009 mm。
- Blender 渲染：`tmp/ias-to-patch/p5-legs/legs-*.png`。
  - 四列从左到右：新 Mod 关 Lua | 旧 P4（只修肩）开 Lua | 新 Mod 开 Lua（肩 + 腿）| IAS。
  - 膝部特写在 `knee-compare.png`，三列从左到右：旧 | 新 | IAS。
  - 脚本：`legs_render.py`。设 `MEASURE=1` 时只输出关节位置，不渲染。
- 关节测量（相对 IAS 模拟）：
  - 站姿、持枪、换弹：新版 thigh、knee、foot、ball 四个关节相对 IAS 的偏差完全相同（例如持枪都是 (-3.7, -5.2, -10.2) cm）。也就是说，腿的形状和 IAS 一致，只是整条腿平移了。
  - 旧版每个关节的偏差各不相同（17–24 cm），所以腿形不对。
  - 走路、跑步的偏差不一致，是因为 IAS 模拟和 Blender 帧取的时间点略有不同。快动作对时间敏感，所以这不是几何差异。

## 4. 剩下的差别与风险

- **整条腿的平移来自胯部支点**：DP-8 的 `hips` 比模型低 21 cm，骨盆转动的支点不同。
  - IAS 模拟在持枪时脚离地约 10 cm。新版的脚反而在地面上，因为脚高和静止时一致。
  - 游戏里的 IAS 是不是也离地，没有核实。
  - 要不要把 `hips` 也归位，待定：它牵涉躯干链，spine1、spine2 也差 20–30 cm。
- **关掉 Lua 时腿会严重变形**（第一列）。新产物必须配合 Lua 使用，手臂也是这样。
- 进游戏的通路仍未决定：目标值是跟着 Mod 走、按护甲判定，还是在设置页手填。
- 以上结论都来自 Blender，没有在游戏里验证。
- 新转换产物在 `tmp/ias-to-patch/p5-legs/p1-legs/`（`mod/`、`arm-pose.json`、`blender/`）。
