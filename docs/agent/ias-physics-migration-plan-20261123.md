# IAS → 普通 Mod：把头发 / 外套物理搬过来（2026-11-23，方案）

工作树 `D:\gitCode\HD2ModManager-ias-to-patch`，分支 `exp/ias-to-patch`。前置：手臂独立骨 + 持枪 IK 已实机可用
（`ias-independent-bones-plan-20261122.md` §5.6–5.8）。

## 1. 现状：为什么头发是硬的（已核实）

- **转换时把物理骨全合并掉了。** `IasConversion/NativeRetargetInput.cs` 里 `IncludeJiggleBones = false`，
  权重统一折到最近的潜兵骨（头发 → `head`，外套飘带 → `chest`…）。转换后的 Unit 里**根本没有**头发骨。
  - 核对：IAS 物理文件共 352 条 joint 记录，含每条链的驱动根。296 根模拟骨按名字哈希和转换前的普通 Mod Unit 比对，原方案核对命中 **0**。
- **IAS 原版的物理参数**在样本 `companion/HD2IndependentRig/*.hd2phys`（格式 `HD2PHY1`，上游 HD2-PhysBoneTool）：

  | 文件 | IAS Unit → DP-8 目标 | 链 | joint 记录／模拟骨 | 碰撞体 |
  |---|---|---:|---:|---|
  | `…Head.1c0c131d96ecbb19.hd2phys` | 头盔（头发）`1c0c131d96ecbb19` → `510291bd9b94c994` | 41 | 275／234 | 3 个胶囊（`chest`、`hips`、`spine1`） |
  | `shared_…canonical.hd2phys` | 身体（胸、臀、外套飘带等）| 15 | 77／62 | 7 个 |

  每个链有 stiffness / damping / gravity / 碰撞掩码；每根骨有半径、角度限位（NONE/CONE/HINGE/SPHERICAL）、
  刚度/阻尼缩放；骨用 **palette_slot**（= IAS Unit 里的变换下标）引用。
- **管理器自己的物理系统已经能用**（`physbone-status.md`）：
  - 驱动的是"潜兵骨架里没有的新节点"（写同名骨画面不认，这条和手臂独立骨是同一个原理）；
  - 参数是 `HD2SRG01`（jigglebone 风格，每骨一条记录），封进 `HD2PBX01` 容器、附在 patch 的 `.gpu_resources` 末尾，跟着 Mod 走；
    链根挂在哪根潜兵骨上靠 `physbone-rig.bin`；
  - 曾实现纯 Lua 的球 / 胶囊身体碰撞，当前树已移除，见 `physbone-status.md` §6g.7；
  - VPK 转换走的 `Retargeter.JiggleBones`（`RetargetExtraBone`）已经会把物理骨作为"合成节点"插进 Unit、权重保留。
- **许可**：上游 HD2-PhysBoneTool 是 **GPL-3**。我们**只照格式自己写读取器**，不拷它的代码。

## 2. 做法：分四步

### A. 转换时保留物理骨
- 读 IAS 的 `.hd2phys`，按 `palette_slot` 找到 Profile 节点，把这些骨标成"物理骨"，让 IAS 转换也走
  `Retargeter.JiggleBones` 的合成节点路径（只对这些骨打开，别的照旧合并）。
- 节点名：用 IAS 原骨名会和别的 Mod 撞名（`physbone-status.md` §3.5），加前缀（如 `ias_phys_<名>`，具体按 §3.5 现有规则）。
- 验收：`hd2mod ias preview` 导出的非潜兵节点里能找到全部物理骨；Blender 绑定姿势下网格和现在**逐顶点一致**
  （只换权重目标，不改几何——`RetargetExtraBone` 注释里的老坑：AnchorIndex 要给对）。

### B. 参数换算 `HD2PHY1 → HD2SRG01`
- C# 自己写 `HD2PHY1` 读取器（按格式，带样本文件的测试），再做一张换算表：
  链 stiffness/damping/gravity × 骨的缩放 → jigglebone 的刚度/阻尼/tip_mass；CONE/HINGE 限位 → 锥 / 偏航 / 俯仰约束；
  半径 → 碰撞半径。**有损的地方逐条写进文档**（比如 SPHERICAL 限位、链间连接 chain_links、自碰撞）。
- 验收：换算结果用 lupa 跑**真的**运行时 Lua：链全部找到、静止不抖、重力下自然下垂、无 NaN。

### C. 打包
- 头发参数嵌进头盔 patch，身体参数嵌进身体 patch（`PhysBoneContainerEmbedder` / `PhysBoneCarrier`），
  链根锚点写 `physbone-rig.bin`（头发挂 `head`，外套挂 `chest`/`hips`…）。
- 验收：管理器部署后，游戏日志里 `限位来源：…（共 N 根骨）` 的 N 对得上。

### D. 碰撞
- 原定复用运行时的 9 个身体胶囊。当前树缺少该实现，等待决定是否重新引入，见 §5。
- 武器碰撞（用户 2026-11-23 提到大枪捅穿）：**本轮不做**。物理系统"看不见"枪；以后可以试在 `attach_weapon` 上挂一个近似胶囊。

## 3. 风险

1. **性能（最大的坑）**：实机验证过的模型是 54–57 根物理骨，这次是 **296 根模拟骨**（头发 234），约 5 倍。
   可能要合并 / 精简头发链（只留主链），或者降子步。先离线量每帧引擎调用数，再定。
2. 运行时挑 rig：同一角色身上两件 Unit（头盔、身体）各带一份参数，确认 `M.select_rig` 两份都会挂上、不会挂错 Unit
   （以前出过"物理挂到一个静止 Unit 上"的现象，没查完）。
3. 和手臂独立骨同时开：两套每帧写入加起来的开销。

## 4. 用户定的（2026-11-23）

- **区分"必须的动骨"和"可选的动骨"**：
  - 必须：不动就会严重穿模的（长头发主体、外套下摆 / 飘带这类会插进身体或腿里的）——始终开。
  - 可选：装饰性 / 吃性能的（胸、臀、呆毛、刘海细节、小挂件等）——**每组一个开关，用户自己决定开不开**，默认关。
  - 分组规则按链名 / 部位自动归类，归类结果写进文档，用户能看懂。
- 武器碰撞：以后再做。
- 实现交给 GPT（Codex，build 角色），我验收。

## 5. E3 实现与验收

### 样本链与开关

275 和 77 是源文件的 joint **记录数**。每条链的首条是驱动根记录，实际模拟骨分别为 **234** 和 **62**，共 **296** 根。身体文件的 `palette_slot` 先在 canonical profile 上校验骨名，再按同名节点映射到各个 consumer，避免把 canonical 槽号直接用到另一件 Unit。

| 来源 | 组 | 源链名 | 链数 |
|---|---|---|---:|
| 头盔 | 必须 · 长发 | `Back.001`、`Back_L`、`Back_L.008`、`Back_L.016`、`Back_L.023`、`Back_P_L.001`、`Back_P_R.001`、`Back_R`、`Back_R.008`、`Back_R.016`、`Back_R.023` | 11 |
| 头盔 | 必须 · 长发 | `Side_L`、`Side_L.004`、`Side_L.012`、`Side_L.023`、`Side_R`、`Side_R.004`、`Side_R.012`、`Side_R.023` | 8 |
| 头盔 | 必须 · 长发 | `Twin_L`、`Twin_L.006`、`Twin_L.011`、`Twin_L.018`、`Twin_L.023`、`Twin_R`、`Twin_R.006`、`Twin_R.011`、`Twin_R.018`、`Twin_R.023` | 10 |
| 头盔 | 可选 · 刘海与呆毛 | `Ahoge.001`、`Bang_L`、`Bang_M`、`Bang_M2`、`Bang_P.001`、`Bang_P.002`、`Bang_P_L.001`、`Bang_P_L.005`、`Bang_P_R.001`、`Bang_R` | 10 |
| 头盔 | 可选 · 头部挂件 | `ACC`、`ACC2` | 2 |
| 身体 | 必须 · 外套与飘带 | `JacketStrap_01`、`Jacket_01_01_L`、`Jacket_01_01_R`、`Jacket_02_01_L_弹簧链`、`Jacket_02_01_R_弹簧链`、`Jacket_03_01_L_弹簧链`、`Jacket_03_01_R_弹簧链`、`Jacket_04_01_L_弹簧链`、`Jacket_04_01_R_弹簧链`、`PelvisStrap_01`、`SpineStrap_01` | 11 |
| 身体 | 可选 · 胸部 | `Breast.L`、`Breast.R` | 2 |
| 身体 | 可选 · 臀部 | `Butt.L`、`Butt.R` | 2 |

分类规则按 `Bang/Ahoge`、`ACC`、`Breast`、`Butt` 前缀依次归入四个可选组，其他链归入必须组。管理器的现有 Mod 选项以载荷切换为主。四个二元组合需要叠加到同一头盔或身体归档，因此复用 **设置 → 内置 Lua → IAS 可选物理** 及现有 Lua 部署，不增加选项 patch 变体。四项都默认关闭，启用物理模式后必须组始终运行。设置按管理器保存，对所有带 IAS 分组标记的产物生效。更改会请求重新部署活动配置。仅有第三方加载器的用法需要导出带当前设置的物理包。

### 格式与有损转换

| 源数据 | E3 处理 |
|---|---|
| 骨的位置与层级 | 用 consumer Unit 的 bind，保留模拟骨之间的实际父子关系。仅作为源驱动根的节点仍走现有折叠，运行时从保留的父骨或原生锚点读姿态。源链的起点单独标记，避免把相邻链段合成一条。 |
| 刚度、阻尼 | 刚度为链值乘单骨缩放。阻尼把源固定步速度保留率 `r = max(0.001, 1 - damping × scale)` 转为连续率 `-ln(r)/step`。IAS 的运行时积分用隐式阻尼，离散响应仍依赖目标求解器和帧步长。 |
| 重力与段长 | 向下的 Z 分量以 0.01905 m 为单位换算到 `tipMass`。横向重力、向上重力与风不进入目标参数。段长取转换后绑定关节距离，极短距离下限 1 mm。目标继续使用尖端弹簧。 |
| 角约束 | NONE 无限位。CONE 传半角，参考朝向使用当前运行时的 bind 方向。HINGE 近似为对称 yaw，原铰链轴与锁止语义不完整。SPHERICAL 用两角最大值作圆锥，丢失椭圆方向与原旋转参考框架。样本模拟骨只使用 NONE 与 CONE。 |
| 碰撞及耦合 | 半径读入但没有运行时消费字段。作者胶囊、逐骨半径与掩码、段接触、自碰撞、`chain_links` 和 link collision 都没有迁移。`physbone-status.md` §6g.7 记录身体胶囊已整体移除，树上的运行时也没有该功能。需要决定是否重新引入身体胶囊和可接受的性能。 |
| 其他求解特性 | 固定步只用于换算阻尼。源参考方向、局部弯曲及 root swing 的算法差异由现有求解器近似。 |

### 产物与证据

- `tmp/ias-to-patch/e3/mod` 为重新转换的普通 Mod。头盔、身体本体及穿外套的选项分别在自己的 patch 侧车尾部写入容器。相应的 `physics/<archive>/physbone-rig.bin` 保留同一份 HD2SRG01 与链根锚点。头盔只保留蒙皮和物理所需的原生祖先，从而满足 Unit 的 256 节点限制。
- `ias preview` 与 e2 按选项、Unit 和 mesh 逐项比对。26 个 LOD0 mesh，161,572 个顶点位置，最大偏差 **0 m**，三角索引相同。另重导出 e2 与 e3 的所有可视 LOD，104 个 mesh 共 646,288 个顶点，逐位置序列的 SHA-256 全部相同。
- 用真实的 `physbone_runtime.lua` 与 `physbone_compat.lua`，让 Lua 从仿部署目录读取 E3 原始 GPU 侧车。测试用一名 Slim 角色及头盔、身体、髋、外套四个 Unit，60 FPS，记录 60 帧静态窗口及 200 帧横移窗口。只开必须组时，关闭的骨无引擎节点查询和姿态写入。全开覆盖 56 条源链，`Butt.L` 同时出现在两个 Unit，因此多一个链实例及两根骨实例。

| Lua 离线回放 | 必须组 | 全开 |
|---|---:|---:|
| 挂载链实例／写入骨实例 | 40／245 | 57／298 |
| 静态窗口最大骨位置漂移 | 0.690 mm | 1.714 mm |
| 相对 bind 最大下垂 | 71.365 mm | 71.365 mm |
| 静态每帧调用数，均值／峰值 | 42.52／406 | 48.28／496 |
| 横移每帧调用数，均值／峰值 | 441.10／806 | 515.22／629 |

两档模拟状态与写入四元数均为有限值。Blender 图使用真实 Lua 烘焙的手臂 IK，加上必需物理链在固定锚点回放中测得的稳定旋转。游戏内动态观感、胶囊穿模效果及管理器界面点击仍需实机验收。

独立产物路径为 `tmp/codex-build`。Debug 构建 0 警告、0 错误，.NET 全量测试 2,715 通过、1 个既有用例跳过。两份实际 `.hd2phys` 也在测试中读取。Lua 全量回归 554 个断言通过，2 个既有外部夹具用例跳过。
