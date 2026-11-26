# W44 · Lunalice_Zoey 接缝诊断与 Blender 验证

## 结果

修复了 VPK→IAS 的一处确切接缝缺陷。部分 Unit 沿用原版外壳的 `10:10:10:2` 权重流，编码时第四槽被置零，前三槽也比相邻 Unit 的半精度权重粗。皮肤上的 294 对同位置副本因此获得不同的权重。改成 `vec4_half` 后，294 对全部一致，其中 118 对位于腰胯到大腿范围。新包已在工作树内生成。

Blender 复现了原包卧倒松开的细缝，以及裙摆和背带局部拉伸。修复消除了上述 294 对副本在四组姿态中的额外位移差。模拟中没有复现站立时大量白色三角片离体。卧倒和摆动时仍可见局部锯齿、相交和拉伸，原任务的整体验收仍需实机证据。

## 输入与复现

- 在 Downloads、D 盘和 E 盘搜寻 `*.vpk`，系统保护目录有拒绝访问记录。在只读库 `E:\SteamLibrary\steamapps\common\HD2ModManager\mods\Lunalice_Zoey` 找到模型及 A1、B1、C2、D1、E1、F1 六个配套材质 VPK。主 MDL 为 `models/survivors/survivor_teenangst.mdl`，校验值 953860819，127 根骨、57 根 jiggle 骨、92 个材质。实际材质包括 `Shinano_body`、`Dress_White01_Skirt`、`Lace_White01_Skirt`、`Lower_White01_Lace_Garter` 和 `Lower_White01_Lace_Underwear`。
- 用上述七个 VPK、原组 UUID、AC-2 归档 `460f2faee22f5850` 和 BP-20 归档 `44c466a6cf09718b` 先跑了一次修复前 CLI。主包内 10 个 ZIP 条目的 CRC 和大小与用户原包逐项相同。配套包内 13 个条目有 10 个 CRC 和大小相同。测量和渲染的修复前基线直接来自用户的 `main/Files` 和 `companion/HD2IndependentRig`。
- 回读用仓库 C# 的 `PatchUnitMeshReader`。蒙皮槽经 Type 6 → 材质 remap → LOD 的 `RealIndices` → TransformInfo，再按骨名哈希比较。物理骨用头部和 shared canonical BODY 的 `hd2phys` 关节名匹配，分 Unit 调色板槽号独立解码。输出侧位置容差 20 μm，源侧 0.0007 源单位。把所有有引用的副本计入，并单看法线点积 ≥ 0.9 的近似连续表面。
- 检查了 17 个 Unit 的 LOD 与分段。选 Slim 身体的一套可见 Unit 和头盔汇总单人 LOD0，同一装备的 Stocky 副本另计入逐段表。LOD0–3 使用同一源 LOD 的拓扑。极小的隐藏占位另计，不纳入可见统计。 [逐 Unit、LOD、材质统计](../../tmp/w44/unit-lod-sections.tsv) · [整体统计](../../tmp/w44/unit-audit.log)

## 对照与归因

| 指标 | 源 VPK LOD0 | 原 IAS | 新 IAS |
|---|---:|---:|---:|
| 单角色可见三角面 | 330,805 | 330,805 | 330,805 |
| 同位置候选对 | 83,722 | 87,461 | 87,461 |
| 有效骨名或权重差 > 1e-6 | 158（0.189%） | 324（0.370%） | 30（0.034%） |
| 法线近似一致的候选对中有权重差 | 140 / 39,089 | 319 / 42,173 | 28 / 42,173 |
| 候选对单骨最大权重差 | 0.216916 | 0.216797 | 0.216797 |
| 同位置而仅一侧受链骨驱动 | 0 | 0 | 0 |
| 全部可见 Unit 与 LOD 的顶点引用中权重和误差 > 1e-6 | — | 46,873 / 1,538,532 | 0 / 1,538,532 |
| 全部可见顶点引用的最大权重和误差 | — | 0.00977516 | ≤ 4e-8（JSON 十进制回读误差） |
| 全部可见组中超出分段顶点范围、无法解析的正权重骨槽 | — | 0、0 | 0、0 |

源与出货的点对分母受分区、UV 顶点复制和重定向位置影响。用 `Shinano_body` 的有向 UV 三角面唯一签名追踪 15,835 个顶点时，294 对不一致中 274 对来自同一源顶点 ID，其余 20 对的源权重相同。源左、右大腿占最大权重的各 537 个可对应顶点，出货主骨分别为 `l_thigh`、`r_thigh`，均正确。例子是髋 Unit 顶点 42 与左腿 Unit 顶点 3，绑定点约为 `(-0.170378, 0.108455, 1.116014)`，原包的 `hips/l_thigh` 分别为 0.800293/0.199707 和 0.800587/0.199413。 [皮肤源顶点追踪](../../tmp/w44/skin.log)

| 用户关心的机制 | 检查结果 |
|---|---|
| 分段、LOD 漏面 | 92 个材质的汇总面数与源侧一致。皮肤 27,378、裙布 18,751、裙蕾丝 4,047、内裤蕾丝 4,704，以及吊袜带、袜子、后蝴蝶结等重点材质，有向 UV 三角面签名在整数 UV 归位和统一半精度口径下逐项对应。回读验证各可见 LOD 的三角面索引都落在分段声明范围内。 |
| 接缝副本被拉开 | 皮肤的权重差可追溯到出货编码。原包跨 Unit 的权重不一致在同一位置发生，源顶点在对应点上权重一致。剩余 30 对由脸↔皮肤 24、长袜↔丝袜 4、手套 2 组成，其中脸部最大差可追溯到源脸部刚性头骨与皮肤的头/颈过渡。 |
| 背面剔除与法线 | 重点材质的有向 UV 签名符合预期绕序翻转。原包髋部 2,324 个皮肤面与其平均顶点法线点积均非负，裙布、裙蕾丝、后背金属分别有 4、1、98 个反向点积样本。全体可见 Unit/LOD 有 18,948 / 2,113,672 个反向点积，含装饰材质与重复 LOD。点积是局部法线一致性检查。裙边和背带源 VMT 还用了 `$nocull`，实机单面可见性需单独复核。 |
| 皮肤透明裁剪 | 回读皮肤材质 `dd878b7e9dfb6d94` 的 Parent 为 `advanced`（`ef9b3fc2fc1ba61e`），裙布同模板。裙蕾丝 `4b880ba2dc00a6b8`、内裤蕾丝 `4333f6a2b9c31897` 才用 `alphaclip`。Blender 不用透明纹理仍能显示原包皮肤的局部细缝。 |
| 物理骨邻静态骨 | 裙布 502、裙蕾丝 16、裙金属 364、后背蝴蝶结主体 128、两类后背蕾丝 238 和 60 个三角形的顶点中混有链驱动和非链驱动。各材质的计数在源模型与 IAS 一致。髋及双腿的皮肤、吊袜带和内裤蕾丝此类面为 0。可见 Unit 的逆绑定矩阵误差 ≤ 2.75e-7。 |

统计中的物理骨按链成员识别，包含链首。交界面混合蒙皮在源 VPK 已存在。当前证据支持保留作者设置的跨骨过渡。新包对链文件的检查见下文。 [源面签名与 ZIP 对照](../../tmp/w44/topology.log) · [原包细分统计](../../tmp/w44/stats-before.log) · [新包细分统计](../../tmp/w44/stats-after.log)

## 修复位置

1. `VpkExtractor.BuildDomainUnitsAsync` 把不同分区交给各自的原版外壳。权重由共用的 `RetargetedMeshBuilder.CollectInfluences` 合并、取四槽并量化到 1/2048。此处同一源点构造出的权重一致。 [分区与出货](../../HD2SourceMigration/IasPackaging/VpkExtractor.cs#L496) · [四槽决策](../../HD2SourceMigration/VpkConversion/RetargetedMeshBuilder.cs#L149)
2. 通用 `CanonicalStreamContractCompiler.BuildContract` 在可以表示四个分量时沿用外壳格式。其中 `CanonicalMeshPreparation.PackTenBit` 只写三个 10 bit 分量，第四槽为 0。部位使用不同外壳，就在写 Unit 时引入了可重复的差异。Python 包装后的原包对此仍能回读重现。 [流选择](../../HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/CanonicalStreamContractCompiler.cs#L58) · [有损编码](../../HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/CanonicalMeshPreparation.cs#L326)
3. 在 IAS 的 VPK 入口调用已有的半精度流适配，针对视觉流原本为 `vec4_1010102` 的权重组件使用四槽半精度。已用半精度的流、标量外壳的标准回退、UV 格式和剔除流均走原流程。合成测试覆盖两版 ABI、五取四后接缝两侧槽号不同但权重一致，以及静态与物理顶点的预期位移。 [补丁入口](../../HD2SourceMigration/IasPackaging/VpkExtractor.cs#L551) · [流适配](../../HD2SourceMigration/IasConversion/IasNativeMesh.cs#L11) · [合成测试](../../HD2SourceMigration.Tests/IasVpkSkinWeightTests.cs)

普通 VPK→Patch 也调用 `UnitAuthor`，有同类有损外壳时会触发同一风险。本次生产修复限定在 VPK→IAS。普通路径和共同编码器的调整列在待决中。

## Blender 4.3.2 验证

用 `-b --factory-startup` 配置工作树内的目录。试用已装 AQ 插件和仓库参考导入脚本时，插件读取 Unit 后在后台取 `io_scene_helldivers2_AQ` 偏好项报 `KeyError`，退出码 27。正式图使用 C# 回读的骨架、分段网格和权重在 Blender 内重建。按实际分段使用纯色，使用回读顶点法线，同一组测量用同一骨架动作、镜头和近重合点对。站立为轻微躯干/腿部偏转，卧倒包含全身倾倒、两腿外展、屈膝和固定种子的轻摆，另有上身前倾 30° 和各链骨偏转 15–30°。变换以单位内调色板和绑定矩阵作线性蒙皮。此模拟未执行游戏内的 IAS 更新循环或着色器。

| 姿态 | 294 对已修皮肤接缝的最大额外开口，原 → 新 | 最长单边增长，原 → 新 | 增长 > 25 mm 的边，原 → 新 |
|---|---:|---:|---:|
| 站立 | 1.634 → 0 mm | 12.754 → 6.101 mm | 0 → 0 |
| 卧倒 | 1.400 → 0 mm | 47.283 → 47.283 mm | 22 → 22 |
| 前倾 30° | 1.559 → 0 mm | 66.541 → 66.541 mm | 238 → 238 |
| 物理链偏转 | 1.638 → 0 mm | 71.543 → 71.543 mm | 138 → 139 |

卧倒图中，原皮肤单独显影有细窄暗线，新图中对应已修接缝贴合。带衣物图仍能看到局部锯齿和层间相交，裙摆也仍有拉伸。全部基线候选对的卧倒最大额外开口约 3.184 → 3.181 mm，仍有 18 对超过 2 mm。单模型的色块和暗线可用于比较蒙皮，最终白片、裁剪和遮挡效果以实机为准。

卧倒与摆动时最长增长边均在 `Dress_White01_Skirt`，端点只挂 `source_extra_115` 至 `source_extra_118` 两条裙链。前倾时最长增长边同样在裙布，一端 100% 挂 `hips`，另一端在 `spine2/chest/hips` 之间混合。这两个样本把纯链偏转和纯身体屈曲分别定位到了边与骨。 [Blender 回读日志](../../tmp/w44/blender-after.log)

- [站立正面裙摆对照](../../tmp/w44/blender/compare/stand-front-skirt.png) · [站立背面](../../tmp/w44/blender/compare/stand-back-skirt.png)
- [卧倒臀/腰带特写](../../tmp/w44/blender/compare/prone-hips-waist.png) · [卧倒臀部皮肤](../../tmp/w44/blender/compare/prone-hips-waist-skin.png)
- [卧倒大腿内侧、丝袜上沿与内裤](../../tmp/w44/blender/compare/prone-inner-thigh.png) · [同镜头皮肤显影](../../tmp/w44/blender/compare/prone-inner-thigh-skin.png) · [卧倒侧面裙摆](../../tmp/w44/blender/compare/prone-side-skirt.png)
- [前倾 30°](../../tmp/w44/blender/compare/lean30-side-waist.png) · [物理摆动正面](../../tmp/w44/blender/compare/swing-front-skirt.png) · [背部飘带](../../tmp/w44/blender/compare/swing-back-ribbon.png)
- [修复前数值](../../tmp/w44/blender/before/metrics.json) · [修复后数值](../../tmp/w44/blender/after/metrics.json)

## 新包与用户实测

用 IAS Generator 1.6.4、已装 AQSDK `aqsdk-86b7252` 和随附 `texconv.exe` 重出包。两个 ZIP 放在 `tmp/w44/out/`。与现有角色使用相同 UUID。退出游戏后在 IAS 页卸载旧角色，将下面两个 ZIP 一起拖入，按原搭配选择 AC-2「服从指挥」与 BP-20「思想矫正官」。进船后依次看站立正背面和卧倒分腿时的臀腰与大腿内侧，再对照是否仍出现离体白片。

- [IAS_MainMod.zip](../../tmp/w44/out/IAS_MainMod.zip)
- [IAS_Companion.hd2irpack.zip](../../tmp/w44/out/IAS_Companion.hd2irpack.zip)

与修复前的重新打包结果相比，主包中仅身体补丁和其 GPU 侧车变动。头盔补丁、材质补丁、配套包内的 `.hd2phys`、骨架 `.rigbin` 和 consumers 条目通过 CRC 与长度对照。头盔 UV 流布局仍与原包一致。配套包的 author、preview 和组清单随重新导出更新。 [ZIP 条目校验](../../tmp/w44/topology.log)

两个 ZIP 均通过完整性检查。SHA-256 主包为 `45bf0dcb5b0372f068bfdf34c6bb2a401fc11c3a8a786b08d9ab66945dd77461`，配套包为 `7e813c7bf762efc10691d6a6a8858f9c2d612469f0526afc68fa31dcbbe6b7ae`。

## 构建与回归

Debug solution 使用工作树现有的 `tmp/w34/serial.targets` 顺序构建，保留规定的 `-nodeReuse:false -m:8 -p:NuGetAudit=false`，输出「已成功生成。0 个警告，0 个错误」。默认 solution 路径多节点没有完成构建，所以采用已有的顺序目标。测试对 solution 的六个测试项目逐个执行 `dotnet test -c Debug --no-build --no-restore`，同样保留规定参数。 [构建日志](../../tmp/w44/build.log)

| 项目 | 原文数字 |
|---|---|
| Decryptor.Tests | 失败: 0，通过: 5，已跳过: 0，总计: 5 |
| HD2ModAdaptation.Tests | 失败: 0，通过: 212，已跳过: 0，总计: 212 |
| HD2ModCli.Tests | 失败: 5，通过: 208，已跳过: 0，总计: 213 |
| HD2ModCore.Tests | 失败: 4，通过: 1865，已跳过: 0，总计: 1869 |
| HD2SourceModel.Tests | 失败: 0，通过: 72，已跳过: 0，总计: 72 |
| HD2SourceMigration.Tests | 失败: 1，通过: 626，已跳过: 1，总计: 628 |
| 合计 | 失败: 10，通过: 2988，已跳过: 1，总计: 2999 |
| 新增夹具定向测试 | 失败: 0，通过: 2，已跳过: 0，总计: 2 |

机器上的既有游戏进程触发了部署保护。CLI 的 5 个部署用例和 Core 的 4 个 IAS 用例与 W43 游戏运行时日志中的失败项一致。迁移项目的 `Hd2MaterialTemplateTests.EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists` 期望 `embedded`，工作树内 TEMP 使它找到已有 SDK，跳过的是原有绕序统计用例。 [CLI](../../tmp/w44/test-HD2ModCli.Tests.log) · [Core](../../tmp/w44/test-HD2ModCore.Tests.log) · [迁移](../../tmp/w44/test-HD2SourceMigration.Tests.log) · [定向](../../tmp/w44/test-skin.log)

Lua 的原命令因 PATH 缺 Lua 5.1 可执行文件无法直接启动。沿用工作树已有的 Lupa/LuaJIT 包装器运行同一个 harness，并把进程日志目录指向工作树。最终原文为「合计 PASS=486 FAIL=0；红=0」，两个缺外部夹具用例跳过。新增开发工具安装数 0。 [Lua 日志](../../tmp/w44/lua-regressions.log)

交付文件保留在本工作树。自动审批策略拒绝了对任务自建的临时目录执行批量清理，返回 `blocked by policy`。因此 `tmp/w44` 还保留 C# 回读项目、JSON/NPZ、中间包、Blender 插件副本和已隔离的测试目录。任务启动的 Blender 已退出。

## 待决

1. 实机确认站立白色碎片及卧倒背部尖刺。若仍出现，需同一帧的 IAS 运行时骨调色板和 Unit 信息，与本报告的静态调色板、绑定矩阵、链骨名称对拍，进一步查运行时 consumer 映射或更新顺序。
2. 裙布、蕾丝、飘带上的物理骨/静态骨交界来自源分配。决定是否单独标定链根旋转、限位与近邻伸长门槛，需要实机姿态证据。全局抹平这些权重会改动既有物理表现。
3. 普通 VPK→Patch 的共用写入逻辑同样依赖外壳格式。是否把有损权重格式的防护下沉到共享流编译器，需确认普通 Mod 的体积、ABI 和引擎兼容性。
4. 若仅从特定方向观察到洞，继续对照源 `$nocull` 和游戏中的剔除与透明采样。当前离线图没有模拟这些着色器行为。

## 派活方验收（2026-11-25）

- 修复点（`IasNativeMesh.cs`、`VpkExtractor.cs` 共 10 行：有损 `vec4_1010102` 权重流在 VPK→IAS 入口改用四槽半精度）合理、改动小。全量测试 10 个失败里 9 个是测试时游戏正在运行触发的保护，需在游戏关闭后复跑确认。
- 离线可复现的缝隙只有 1.4 mm 级别，Blender 前后对照肉眼差别很小；用户实机看到的大量白色碎片、成排镂空和横向撕扯的裙摆，**离线没有复现**，大概率在 IAS 运行时（consumer 映射/物理链/剔除与透明采样）。下一步：用户用 `tmp/w44/out/` 新包实测；仍坏则需要运行时证据。
