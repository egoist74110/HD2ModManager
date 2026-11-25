# W33 · 真实 Mod 库可动骨只读普查

2026-11-25 · `feat/ias-module`

**451 个库节点中，67 个有可拼链的带权非 avatar 骨，38 个未检出管理器 physbone。** 这 38 个包括 32 个普通节点与 6 个 IAS 来源节点。32 个普通节点的带权 Unit ID 在 AQ SDK 原版护甲、头盔绑定目录里均为 **0 命中**。按当前普通 Mod → IAS 的输入范围，本库 **0 个**普通节点同时具备已识别护甲或头盔 Unit、可拼链的带权自定义骨、且无管理器物理。这个结果随库快照和绑定目录版本变化。

## 分桶

| 类别 | A 有带权骨且可拼链 | B 有额外节点、无正权重 | C 无额外节点 | D 读失败 | 合计 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 普通 | 32 | 109 | 229 | 0 | 370 |
| 素体 | 0 | 12 | 0 | 0 | 12 |
| VPK 导入 | 14 | 0 | 0 | 0 | 14 |
| IAS 转来 | 21 | 14 | 20 | 0 | 55 |
| **合计** | **67** | **135** | **249** | **0** | **451** |

| A 桶类别 | 有管理器 physbone | 无管理器 physbone | 无管理器 physbone、最长链 ≥2 节 |
| --- | ---: | ---: | ---: |
| 普通 | 0 | 32 | 8 |
| 素体 | 0 | 0 | 0 |
| VPK 导入 | 14 | 0 | 0 |
| IAS 转来 | 15 | 6 | 6 |
| **合计** | **29** | **38** | **14** |

- **A 桶的普通节点**包括枪械、战备、炮舰设施和敌方对象。27 个补丁自带 `state_machine` 资源。可拼链只说明蒙皮和骨层级满足统计条件，链可能已经被原生动画驱动。用 AQ SDK `aqsdk-86b7252` 的 `resource_binding_targets.json` 做了两次匹配：先对照 BODY / HELMET 归档内全部设备 Unit 的并集，再对照每归档设备 Unit 的交集和 `armor_part_unit_ids.json` 槽位表。32 个普通 A 桶在两种口径下均为 0；15 个已有 Lua rig 的 IAS 来源节点在两种口径下均有命中。
- **C 桶没有可分析的 Unit**。249 个节点含 92 个逻辑容器，以及 157 个无 Unit 的其他节点。需要先判断这些节点是否提供网格或引用了其他节点的载荷，再确定自动加骨适用范围。6 个带「抖动」的普通护甲样本均在 B 桶，拥有 Unit。
- **实际库存**有 453 个顶层目录。排除管理器的 `.hd2modmanager` 和 `.runtime` 后，451 个目录有合法 Mod 元数据，也与 `library.json` 的 451 个目录对应。Mod 目录中有 487 份编号补丁，其中 413 份为 `.patch_0`。全树的 487 份 `.patch_0` 另含 `.runtime` 的 74 份。Mod 目录共 23 个 VPK。一次扫描读取 1,766 条 Unit 记录，字节去重后 1,695 个 Unit 载荷，D 桶 0 个。

## 五个可复用样本

每个样本链上的全部骨均在表列同一 Unit 上查到正权重。五例都有 Lua physbone，可作为转换实验的对照样本。

| 用途 | Mod 节点 | 补丁里的 Unit ID | 实际带权链 | 带权骨 / 普查链 / Lua 链 |
| --- | --- | --- | --- | ---: |
| 头发 | 阿夸 `2594256797` | `79bd6ace03c60001` | `Twintail_1_L → Twintail_2_L → Twintail_3_L` | 35 / 25 / 25 |
| 裙摆 | 奇塔罗谢（VPK 导入） | `18ff552b00260002` | `Skirt_2_9 → Skirt_4_9` | 56 / 29 / 29 |
| 胸 | VPK `2893963355` | `3f79c563306f0002` | `R_Breast`（单节） | 26 / 26 / 26 |
| 尾巴 | VPK `3010610736` | `33dc4446ba640002` | `Tail_001 → Tail_003 → Tail_005 → Tail_007 → Tail_009 → Tail_011` | 39 / 19 / 19 |
| 饰物 | 内衣选项 `mod-options-body-9a42722c7bcf5f2f2dfebcf8-c20b4dfac2f6195d2315de6d`（IAS 转来） | `bd6394328a162b66` | `jiggle_a0babc18383daf4f_Necklace → jiggle_a0babc18383daf4f_Necklace.001 → jiggle_a0babc18383daf4f_Necklace.002` | 14 / 6 / 6 |

布莱默顿有一组直接对照。VPK 节点 `2505895990` 源模型有 33 根 jigglebone，33 根带权，导入后的 HD2 补丁有 33 根带权额外骨、23 条链，已有 23 条 Lua 链。库中的 `布莱默顿-IAS-20261119-001825160-模型主包-Files` 同样有 33 根带权额外骨、23 条链，库内未见 Lua rig。IAS 原生物理另随 Companion 分发，本次扫描范围为 Mod 库和原版数据，IAS 图形客户端及 Companion 安装位置未验证。

## 名字含「抖动」的节点

以下包括原样 `(抖动)` 两项，也包括连字符、下划线标签和沿用显示名的 IAS 节点。

| 节点 | 桶 | 额外节点 | 带权额外骨 | Lua 链 |
| --- | --- | ---: | ---: | ---: |
| 尘白胧嫣原皮 O-3 `AR479939` | B | 17 | 0 | 0 |
| 尘白胧嫣原皮 CM-17 `AR173432` | B | 134 | 0 | 0 |
| 可露凯婚纱 B-08 / CE-74 / FS-38 `AR679132` | B | 48 | 0 | 0 |
| 可露凯婚纱 O-3 `AR186220` | B | 48 | 0 | 0 |
| 可露凯泳装 CE-27 / CW-9 / I-92 `AR266818` | B | 43 | 0 | 0 |
| 芙洛伦翠雀媚 O-3 `AR223834` | B | 48 | 0 | 0 |
| 可露凯婚纱 IAS 父目录 `ias-085214980ec540a5b0d5b6a05e1b961d` | C | 0 | 0 | 0 |
| 可露凯婚纱 IAS 载荷 `ias-b569c2f92aec4b059e9a906766f72b1a` | B | 79 | 0 | 0 |

7 个有载荷的节点解析出的正权重都落在原版 avatar 骨上，可见 `chest`、`spine1`、`spine2`、四肢等。额外节点样本包含 `grp_torso_undergarment_female`、`g_torso_undergarment_female_cloth`、`hips_culling` 和 LOD/网格组。它们的补丁没有 `physics`、`cloth`、`animation`、`state_machine` 等候选资产，Unit 头 `0x20` 也没有非零状态机引用。现有文件证据排除了这 8 个目录依靠随包 Lua rig 驱动额外带权骨的解释。是否有可见抖动，以及是否来自原版继承行为或原发布环境的额外依赖，均待确认。骨名里出现 `cloth` 只说明命名。

## 扫描口径

- **统计单位与种类。** 一目录一行，保留逻辑容器和各选项。名称取 `.hd2modmanager-mod.json`，`library.json` 补充节点类型、宿主及来源关系。类别优先顺序为 IAS 转来、VPK 导入、素体、普通。IAS 类含带 IAS 或「综合护甲系统」标记的主包、模型资源、系统数据包，以及沿来源 GUID 或宿主链关联的选项。VPK 类由 VPK 文件、`vpk2patch.json` 或 `l4d2_vpk_import` 来源识别。其余 `custom-model.json` 素体登记或明确的素体名称归入素体。VPK 导入也可能有素体登记，路径另见 `customModelFiles`。裸的 `mod-options-body-*` 命名只表示选项目录，类别跟随来源证据。
- **补丁和骨。** 递归枚举 Mod 内形如 `16 位十六进制.patch_N` 的补丁，含 `Overwrite`、`sectional` 和选项载荷。用 `PatchTocScanner`、`PatchEntryPayloadReader`、`UnitMeshReader` 读取。相同 FileId 且 Unit TOC、GPU、适用的 Composite TOC/GPU 相同才去重。不同变体保留。按 `CanonicalAvatarRigReader` 从 `18235e0c9ec0e636` 读 107 个原版 avatar 哈希，按 `PatchExtractor.RegisterUnitBones` 的标准取 `TransformInfo.NameHashes` 差集。这个差集也纳入原版装备专属骨、裁剪节点和网格组。骨名表优先使用管理器 `StoragePaths.BoneHashesPath` 对应的 `data/resources/bonehash.txt`。真实管理器目录无该文件，本次使用仓库 `HD2ModManager/Resources/AssetMetadata/bonehash.txt` 的 4,913 个哈希，用 Mod 内 bones 资产、已读 rig、VPK 摆动骨名补充。未反查项显示为 `custom_0xXXXXXXXX`；哈希依然参与权重和链判断。A 桶逐 Mod 累加的 2,173 个带权额外骨计数中，627 个未反查到名字。
- **顶点权重。** 按 mesh section 的实际索引引用和材料调色板，解码 Type 6/7。只计有限且大于 0 的权重，沿 LOD 的 `Remap.FakeIndices → RealIndices → TransformInfo.NameHashes` 查到骨。只有调色板条目、机械刚性挂点、未引用的顶点缓冲区数据均不计。每个不同 Unit 载荷内相同 stream/顶点位置只计一次，不同 LOD、不同 Unit 或不同有效变体可各自贡献顶点。该值表达载荷内受影响的顶点记录数。
- **链。** 用有权重骨的直接父子边，遇到分叉就让当前节点收尾、子节点各起一链；跨 Unit 的父关系冲突处也断链。单个带权骨计一条单节链。与 `build_domain_chains` 共享父子森林思路，普查只统计直接带权骨，分叉规则按任务文字执行。生产脚本还可保留通向带权后代的无权重 jiggle 祖先，在分叉时沿第一个子节点延长主链，并追加末端骨。普查链节数是现有骨节数；单节链仍需端点及绑定检查。
- **管理器物理。** 有链的 `HD2SRG01` v2 才算有配置。逐文件检查 `.gpu_resources` 尾部 `HD2PBFT1 → HD2PBX01 → hd2_spring_rig.bin`，另查 Mod 目录的 `physbone-*.bin` 旁车。按 rig 字节去重，再按 `physbone_compat.lua` 的父索引及分叉规则数链。29 个命中节点都有 GPU 嵌入，14 个 VPK 节点同时保留旁车。29 个节点的 rig 名称转哈希后覆盖各自全部带权额外骨。库元数据用于来源和逻辑结构；`.runtime/physbone-addon` 是库级 Lua 部署缓存。扫描范围内也未发现独立的 `.hd2phys` 文件。以上是磁盘配置证据，实际启用和运行状态未测。
- **VPK。** 对每个含 MDL 的 VPK 用 `SourceRetargetPipeline.LoadSource` 的默认模型选择读取 MDL/VVD/VTX，调用 `SourceModelInspector.JiggleFacts`，再与装配几何的正权重骨索引求交。共 14 个模型 VPK，`609/609` 根 jigglebone 带权。其余 9 个 VPK 无 MDL，状态记为「无 MDL」，骨数字段为 `null`。一个包内附带其他模型时，结果只对应明细中的所选 model stem。

### 用途关键词

忽略大小写，优先看带权骨名；没有带权骨时看额外节点名。命中可以多选。只剩哈希或语义不明时保留「其他」。

| 用途 | 关键词 |
| --- | --- |
| 头发 | `hair`、`bang(s)`、`fringe`、`pigtail`、`ponytail`、`twintail`、`braid`、头发、刘海、马尾 |
| 裙摆 | `skirt`、`dress`、`cape`、`coattail`、`hemline`、裙、披风 |
| 胸 | `breast`、`bust`、`boob`、`oppai`、独立 `pec`、胸 |
| 尾巴 | 独立开头或分隔后的 `tail`、尾巴，排除 `hair/pig/pony/twin/coat` 加 `tail` 的组合 |
| 饰物 | `ribbon`、`bow`、`earring`、`pendant`、`charm`、`tassel`、`accessor`、`ornament`、`necklace`、链饰、挂件、缎带、蝴蝶结、耳环 |

## 待决

- 普通护甲的自动物理链需要新增真实正权重样本。本库的普通 A 桶可用于验证资产筛选和动画骨排除，VPK 与 IAS 转来样本可用于验证链拆分和物理迁移。是否支持修改原版权重或自动加骨属于下一轮行为决策。
- 「抖动」标签的原发布机制需要作者资料、配套依赖或单独授权的运行对照。仅凭库存文件无法归因。
- 类型、状态机资源、骨名关键字只提供离线证据。真正可运行的 IAS 链还需要核对具体目标槽位、骨架拓扑、锚点、末端和参数。

## 交付与验证

- `tmp/w33/survey.csv`、`tmp/w33/survey.json` 各 451 行记录。CSV 使用带 BOM 的 UTF-8，复合字段保存为 JSON。读取失败时明确保留目录、文件和异常文本；本轮 D 为 0。
- 最终只读扫描 **19.71 秒**。一次性 C# 扫描器在 `tmp/w33` 内 Debug 构建，复用工作树现有 Debug DLL。构建原文 **0 个警告，0 个错误**。用 Python 回读两份明细，核对目录唯一、行数、桶和骨计数、补丁与 VPK 总数，并用五例的 Unit ID 检查样本链上的骨共同带权。解决方案构建、全量测试、Lua 回归均未运行，本轮无生产代码变更或提交。未安装工具。
- Git 状态与起始基线对比，新增本报告及忽略的 `tmp/w33` 文件。起始已有的生产代码、测试和文档变更保留。真实库、游戏目录和 `%LOCALAPPDATA%` 均只读或未访问，未启动游戏、管理器或 IAS 客户端。
- 一次性工具源码位于 `tmp/w33/Program.cs`、`Survey.csproj`、`assemble.py`。命令执行策略拦截了删除命令，扫描中间数据和 Debug 构建产物也保留在 `tmp/w33`。
