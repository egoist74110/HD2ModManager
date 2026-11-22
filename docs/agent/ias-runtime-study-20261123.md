# IAS 运行时是怎么做的（学思路用，2026-11-23）

来源：`D:\gitCode\HD2ModManager\tmp\ias-lab\upstream\ias-1.6.3`（IAS 1.6.3）。没有许可证：**只看思路，不抄代码**。
用户意见：本地装它的 DLL 和 Python 做开发对照可以，但不给用户用；动画仍由我们自己的运行时驱动（见记忆 `no-ias-runtime-route`）。

## 1. 三层结构

| 层 | 位置 | 做什么 | 能不能读 |
|---|---|---|---|
| Lua 外壳 | `Generator/runtime/lua/lua/runtime.lua`（545 KB，7833 行）+ `Generator/runtime/companion/*.lua` | 找 avatar 和护甲；把 avatar 骨骼姿势读成浮点数组；调原生求解；写回；**每帧回读校验，对不上就回滚** | 能读 |
| 原生求解 | `ArmorAssets/runtime/hd2ias_native_bridge_pluginw64_release.dll`（约 2 MB）。Lua 用 `ffi` 调导出函数（如 `C.weapon_root_offset`）或 `E.HD2IASNative` | 按模型骨长做重定向换算、物理、握枪偏移 | 闭源，只能看行为 |
| Python 伴随 | `Generator/runtime/companion/*.py`（自写约 51 个）+ 内置 Python 3.12 | 生成器、打包、设置面板、帧数据传输（WebSocket/管道） | 能读，但不含求解数学 |

所以"直接运行它的 Lua"拿不到核心计算，数学在 DLL 里。Lua 里能学的是**策略**：什么时候改、改哪些、怎么防崩。

## 2. 手和枪：按动作分类处理（对应我们的"换弹诡异"）

`runtime.lua` 约 3349–3480 行（`__hd2ir_tp_contact_action`、`__hd2ir_tp_singlehand_state`）：

- **判断当前动作**：
  - 每帧取 `Unit.animation_get_state(avatar)`，返回 31 层状态元组：第 4 层是武器层，第 5 层是上半身，第 12 层是主动作。
  - 换弹时再取 `Unit.animation_layer_info(avatar, 11)` 的片段时长，和目录里记录的时长比对。
  - 状态目录是从游戏 bundle 提取的，开机时用 CRC 校验来源；游戏更新后对不上，就**只关掉这部分功能**，基础动作不受影响。
- **三种模式**：
  - 0 = 不处理：菜单姿势（上半身层 76/77）、未分类动作、拿不到状态时。
  - 1 = `native-weapon-posture`：持枪或举枪，手钉到枪的 `IK_left`/`IK_right`。
  - 2 = `verified-native-reload` / `native-hand-motion`：换弹和其它主动作时，**跟着原生手部轨迹走，绝不把手钉在移动的武器挂点上**。
- **长武器**（`__hd2ir_holding_contact_frame`）：挂点离手很远（比如锤柄挂点在 65–80 cm 外）时，按握把半径 r 混合。距离 ≤ 3r 用挂点，≥ 4r 用原生手，中间做平滑过渡。
- **单手武器实验**（`WHOLE_WEAPON_TRIAL`）：**反过来，把枪挪到模型手里**，而不是把手拉到枪上。
  - 渲染前把武器根节点平移 ≤ 18 cm，下次 update 前还原，这样弹道不受影响。
  - 只用于手枪/单手武器，并且标明是实验。
- **防崩**：调 `animation_get_state` 之前先调 `has_animation_state_machine(unit)`。尸体或刚生成的 unit 没有状态机，直接调会原生崩溃，pcall 接不住。
- 以上 API **全部在白名单里**；IAS 另外用到的 `ffi`、`io.open`、`WebSocket` 我们不能用。

## 3. 其它可借的思路

- **残缺手臂件**（`__hd2ir_native_contact_action`）：躯干件可能只带锁骨/肩/肩扭转骨，没有肘和手。IAS 把它当"部分手臂"，只有在身体锚点对得上时才接受，不会单独为它推导第一人称。
  - 对应我们 E4 时 Torso 件只改了 2 根骨的情况。
- **每帧回读 + 回滚**：写完马上读回，误差超过 2 mm / 0.5° 就回滚并隔离这件，连续 3 帧正常才恢复（第一人称切换时是 12 帧）。
  - 对应我们"手臂弹出"：没被驱动的件会停在绑定姿势。IAS 的做法是一旦不可信就整件退回原版，不留半成品。
- **只写必要的骨**：`native_follow` 让映射骨直接继承 avatar 的原生运动，只在差异处做数值修正。和我们"抄 avatar 旋转 + 少量 IK"是同一思路。

## 3.5 反编译原生 DLL 的结论（Ghidra 12.2，2026-11-23）

工具：`D:\dev\tools`（Ghidra 12.2 + JDK 25；在本机跑必须把 APPDATA/LOCALAPPDATA/TEMP 指到 `D:\dev\tools\g*`，否则 OSGi 报 "data file must be inside the data dir"）。
反编译结果和三份报告在 `D:\gitCode\HD2ModManager\tmp\ias-lab\decomp\`（`report-retarget.md`、`report-grip.md`、`report-physics.md`）。

- **重定向**：
  - 不复制局部旋转，用"世界增量"：`D = 头像当前世界 × inv(头像静止世界)`，模型世界旋转 = `D.rot × 模型静止世界旋转`。
  - 位置 = 父骨世界 × 模型自己的静止偏移，骨长不变。
  - **平时不做 IK，手不保证落在头像手上。**
  - 扭转骨、手指都没有专门算法。
- **握枪**：
  - 只在持枪姿态生效。
  - 目标手腕 = 枪握点 × inv(作者握点)。
  - 模型臂够不着时才把**枪**沿手→肩方向拉回，最多 18 cm；够得着就不动。
  - 手臂约束：用模型臂长，最多拉伸 5%，有角度限幅，子树刚体整体搬动。
- **物理**：
  - 位置法：粒子带速度，保持段长，加角度约束，再重建旋转。
  - 碰撞只做"节点对静态胶囊"，节点之间不互碰。
  - 用链组连接让外套各片同步。
  - 坏的链单独退回动画；时间跳变直接重置，不追赶。
  - **没有休眠和距离 LOD**。
  - 精确积分公式所在的 `FUN_18009c590` 等还没导出。

## 4. 落到我们这边（建议顺序）

1. **手臂弹出**：重扫时不要丢掉仍存活的已追踪件；某件这一帧没写成功，就把它整件恢复到 avatar 原姿势（或隐藏），不要停在绑定姿势。
2. **换弹**：
   - 先加状态日志：`has_animation_state_machine` → `animation_get_state` 取元组，加上 layer 11 信息，状态变化时写一行。
   - 用户实机做站立、举枪、换弹、冲刺、扑倒各一次，整理出持枪状态表。
   - IK 只在模式 1 打开；其它状态（包括认不出来的）关掉 IK，只抄旋转。
3. **长枪**：参考按握把半径混合的做法，避免手被拉到很远的挂点。
4. **可选的开发对照**：本地装 IAS 看同一动作的表现（要先关掉我们的映射和运行时，两者共存必崩），只用来对比观感，不进产品。
