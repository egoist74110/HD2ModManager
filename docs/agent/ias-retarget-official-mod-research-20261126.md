# W51 · 第三方 IAS Mod 换护甲调研

**有条件能换。** 第三方样本 Milfy 发光外套从 SR-64 煤渣砖迁到 BP-20 思想矫正官和 AD-49 阿波罗神，两组都通过 IAS 1.6.4 官方导入、占用和原生 Core 回读，作者可见模型、材质和物理解算保持一致。目标原版槽位须覆盖作者模型及其差分，游戏内画面与换装仍待实测。

2026-11-26 本机快照。下文 SDK 指工作树内 AQ SDK aqsdk-86b7252 的 hd2_system，Generator 指指定 IAS 安装下 1.6.4 的 expanded/Generator/runtime，Assets 指同安装的 expanded/ArmorAssets/metadata，Client 指 [IAS 客户端反编译结果](../../tmp/w51/decompiled/HD2RuntimeManager)。客户端 SHA-256 为 5bc1d974a89fa7887933dd83da9dde019132e818c0502dc7ef3069fe853c504d。

## 绑定清单

| 位置 | 内容 | 换护甲时怎么改 | 证据 |
| --- | --- | --- | --- |
| 官方装备与原版来源表 | equipment.json 的 key/archive，bindings.json 的装备原版 Unit 闭包，stock-assembly.json 的 shape/cell/variant、原版节点数及 TOC SHA，parts.json 的语义槽，source_catalog.json 的 Unit 到原版 Archive 映射 | 查询目标并交叉验证。一个 Archive 可有多个外观键，按 SDK 的同域、闭包交集规则判定，官方基础表继续由原组件提供 | Assets/equipment.json:46,165,745,1792，Generator/companion/declared_assembly.py:61、stock_catalog.py:41，SDK/resource_isolation.py:70 |
| owned_*.isolation.ini | domain、archive、可多行的 equipment、unit=原版,发布、rig_profile=发布 | 更新目标 Archive、装备键及 Unit 左侧。作者发布模型和 profile 留在原发布键上。隐藏 point 右侧使用目标专属 ID。按项目与新 Archive 重算 owned_ 文件名和 isolation_id | SDK/resource_isolation.py:57–95，Generator/companion/declared_assembly.py:19–32，[路由回读](../../tmp/w51/readback/BP-20/verify.json) |
| hd2free_*.difference.ini、hd2_*.difference.ini | FREE v6 有 archive/equipment/domain、控件与选项 ID、free_route=部件,选项,原版,默认发布,所选发布。GROUP 有装备外观键和基于默认／所选发布 ID 的差分 | FREE 重算 manifest 文件名和 ID，换装备键、Archive、每个选项的原版 Unit。保留选项标签、顺序与发布 ID。GROUP 更新 body_appearance_key 等相应作用域并验证每个选项；本样本覆盖 FREE v6 | SDK/free_runtime_manifest.py:13–67、runtime_manifest.py:136,219–277，Client/OwnedStore.cs:930–951,1193，Generator/companion/selection.py:27 |
| 普通项目.Archive.rigbin | 文件名携带 Archive。HD2IRG1 内有源骨、按发布 Unit 定位的 profiles、flags、palette、抓握及 Rest 数据 | 改绑该域时更新文件名。发布 ID 保持时沿用 Rig 正文。若发布 ID 变更，更新所有消费者和 Rig CRC | SDK/rig_format.py:10–17,201–212,268–276,324，Client/OwnedStore.cs:599–625,891–919 |
| 普通项目.Archive.部件.Unit.hd2phys | 文件名绑定项目、Archive、发布 Unit。HD2PHY1 头有 unit_id 和 skeleton fingerprint，正文是链、关节、碰撞体、骨哈希、palette 索引、约束等 | 改绑域时重命名；发布 Unit 保留时沿用头和正文。共享 canonical Unit 按下一行处理 | HD2SourceMigration/IasPackaging/Scripts/ias_physics/format_v1.py:106,160–166,712–729,746–800，Generator/companion/library_catalog.py:26 |
| shared_*.hd2shared 及 Rig、Phys、Pose、guard | 头内项目、Archive（偏移 96）、canonical Unit（104）、revision（112）、CRC；v2/v3 消费记录含发布 Unit、骨映射、基变换、原版 Unit 与选项摘要；引用含文件名、长度、SHA | 按项目和目标 Archive 重算文件 stem 和 canonical ID；换消费者的原版 Unit，保持发布键、选项摘要、基变换、骨映射。新 canonical ID 同步到 canonical rigbin、hd2phys、hd2pose；重算 Rig CRC、Pose profile digest、共享文件引用、CRC、revision，重命名版本 guard | SDK/shared_physics.py:22–29,37–86,199,227–266、clothing_pose.py:142–187，Client/OwnedStore.cs:1013，[共享消费者回读](../../tmp/w51/readback/BP-20/verify.json) |
| .hd2pose、.hd2preview、.hd2morph | Pose 的 profile digest 含 Unit ID，非共享 Pose 路径和 guard 绑定项目、Archive 和 Unit。Preview 的头与每条几何记录都有 Archive，另有引用 Rig SHA、顶点和索引见证。SDK Morph 的文件名及内文含项目、Archive、来源摘要 | Pose 保留规则与 Rest，更新所需身份及摘要。Preview 更新头、记录、Rig 引用及 CRC，保留见证。Morph 需另作版本验证，原型拒绝，本机客户端允许扩展名未包含 Morph | SDK/clothing_pose.py:142,187–203、preview_transport.py:45–102、morph_format.py:169–179,303,333、runtime_group.py:44,56–60，Client/OwnedStore.cs:37–43,970–1010 |
| 加密项目.Archive.hd2author | 密文含项目、Archive 和发布时的逐文件 SHA，官方读取后比对。署名未经身份认证 | 旧声明随字节一致的成员沿用。变更成员需由获授权的发布者用官方 seal helper 生成新声明，或用派生物来源及署名记录说明。本原型在原件保存身体密文，派生包省略失效的身体声明，沿用头盔声明，主包标明长啸云天 | SDK/author_credit.py:15–54，Client/AuthorCredit.cs:35–76、OwnedStore.cs:909–933，[官方回读](../../tmp/w51/readback/BP-20/official.log) |
| 配套 ZIP mod_group.json | id/name、members[].project/archive、文件路径与 SHA，图标独立携带 | 每个独立成员重新回读、封组并重算 SHA。完整组包括头、身两域。read_member 面向单成员，write_group 合组后由客户端验证整组 | SDK/runtime_group.py:22,56–78,81–131，Client/OwnedStore.cs:814–843 |
| 主包 manifest.json 和装备 patch 三件套 | 导入清单 Guid、名称及 Include；Archive 是 patch 文件名前缀，TOC 按 type/id 定位，记录 TOC/GPU/stream 载荷偏移 | 使用派生 Guid 和目标三件套。样本身体 TOC 仅有发布 Unit，保留 18 个作者 Unit 的原 ID 和三段载荷。删除旧隐藏条目，以目标原版 donor 做隐藏条目。重写类型计数、条目序号及偏移，GPU 对齐到 64 字节 | Client/Archive.cs:136–187，Generator/companion/archive_payloads.py:11–33,46–66，[TOC 回读](../../tmp/w51/readback/BP-20/official.log) |
| Unit 引用、共享材质和全局 patch | Unit 的材质表、骨、Composite 指针，材质的纹理表，特定 Mod 的 Package 依赖表。可见 alias 的 SDK 初次生成可含 Archive；项目材质与纹理私有 ID 可跨身／头共享。样本 9ba626afa44a3aa3.patch_2 只有材质及贴图 | 移位模式保留作者 Unit 键及闭包，全局 patch 三件套按字节沿用。目标 point 使用目标原版引用。原版 ID 覆盖、Composite 或显式 Package 依赖须按类型处理实际引用；共存型若重编号，还须重绑 Unit、材质和 GPU 身份 | SDK/independent_packaging.py:115、resource_isolation.py:48,98–119,156–190,250–299，Generator/core/tools/stock_material_closure.py:15–46、stock_composite_closure.py:10，[资源图回读](../../tmp/w51/readback/BP-20/native/geometry.json) |
| 生成的目录、组装、F4 | 启用库与 CAS 生成 owner、profiles、外观、选项和骨映射。游戏侧按当前外观关联 owner，预设有旧 scope 风险 | 使用现有 IAS 更新作业重新生成。原型用官方解析器组装新 owner 声明。预设迁移需要明确的旧→新装备和控件映射 | Generator/companion/library_catalog.py:88,145–189、metadata_generation.py:16–81、appearance_routes.py:11–27,53–86、equipment_binding.lua:97–125 |
| 官方库、管理器角色登记和 W32 | mod_library.json 有官方 id、source_id、enabled、版本及配套 CAS。roles.json 以官方 id 关联主包 Mod 节点。W32 的集合来自单包 id/source_id、规范 Rig 文件名和 CAS 共享头 | 走官方导入重新登记、由管理器关联两件套，重读全组 Archive 做冲突检查。保留原头盔时全组仍占用它 | Client/OwnedStore.cs:317–347,372–432,599–625,802–843,1690，HD2IasWorker/Program.cs:124–141,213–225,245–260，HD2ModCore/Ias/IasDeploymentService.cs:257–293,398–479 |

## 官方现成能力

- 在 IAS 1.6.4 客户端反编译的登记、资源、启用和生成路径中，未找到给已发布两件套更换 Archive 的入口。导入验证并捕获配套，生成从启用库组装运行元数据。Archive 提供回读，NativeModResources 按资源键定位已部署载荷。证据为 Client/OwnedStore.cs:372,599,702,891,1690、NativeRuntimeInputs.cs:324,386,419、Archive.cs:57,107、NativeModResources.cs:98–128。
- SDK 支持作者在指定 Archive 上重新生成独立保存计划，入口需要场景对象及活动原版 Unit。已发布包重绑可复用绑定、封组、清理规则及写包格式，仍需自行转换路由及身份。证据为 SDK 根 __init__.py:3673,3820,4467,4691,4968、SDK/independent_packaging.py:124–161、armor_cleanup.py:22,88。
- Generator 的 rebind 处理运行中的装备关联及复制生命周期，按现成 owner/Archive/外观读取选择。visual_assets.compile_assets 为渲染生成影子资源和依赖闭包。本原型复用官方 selector、组装声明、Archive codec 与 Core。证据为 Generator/companion/copy_driver.lua:164–190、appearance_routes.py:53–86、runtime_generation.py:123–143、visual_assets.py:20–23,72–112。

## Unit 对应和拒绝规则

1. 固定域和每个外观的归属。身体和头盔分别取官方 bindings、parts 和 stock recipes，按语义槽或 recipe cell、shape 及 recipe variant 对齐原版 Unit，再核验每个差分路由、原版 TOC 与节点数。同一原版 Unit 可能同时参加两种体型，先按位置匹配再按 Unit 去重，不能按哈希排序或只看 Unit 自带的 Stocky/Slim 定制字符串。证据为 Generator/companion/assembly_catalog.py:57–91、declared_assembly.py:61–92。
2. 目标缺槽时只允许省略能证明纯隐藏、且无差分／共享／预览消费的旧槽。AD-49 少两个体型的 cell 23，源 11d1ce4a5cf0b125 和 feb7708e2fb358ed 均符合 SDK 隐形 point 的 ID 公式。每个对应装备的闭包比 stock recipe 多一个样本未接管资源。目标 donor 的布局可变化，例如 BP-20 cell 3 的原版骨节点从 16 变为 82，因此用目标 donor 重做 point。见 [目标普查](../../tmp/w51/target-survey.json)、[point 对比](../../tmp/w51/survey.log)、[AD-49 映射](../../tmp/w51/readback/AD-49/verify.json)。
3. 目标多槽时，在明确为目标原版装饰配件后可生成隐藏 point。新增可见体型可借鉴 PatchExtractor 的按槽补齐及使用 donor 定制元数据，但现成 fallback 取第一个可见变体，直接套用会改变作者选择或遗漏配套。IAS 还需同步发布 alias、Rig、物理消费者、衣物姿态、预览及每个差分选项。反向转换的路由合并已有不同发布 Unit 撞同一目标就拒绝的做法。证据为 HD2SourceMigration/IasPackaging/PatchExtractor.cs:163–218,331、HD2SourceMigration/IasConversion/IasStandardConverter.cs:331–342。
4. 必须拒绝缺可见槽、两个不同输出被合成一条、拆分后无完整合法副本、额外槽语义不明、闭包或 build 不匹配、未知配套版本或外部资源归属悬空等情形。用真实空闲候选跑三例，RS-40 缺可见槽、RS-6 体型合并／拆分、A-9 新增槽，全部停在规划阶段。见 [负例日志](../../tmp/w51/readback/rejected-targets.json)。
5. 头盔原则可行，按同域主 Head、LOD/变体与配件闭包匹配，任何可见 Head 缺失都应拒绝。目标有额外的原版装饰可考虑生成目标 point。两组均保留原头盔域，只测了头盔物理随同身体改绑的回读。证据为 SDK/armor_cleanup.py:41–68、independent_packaging.py:105–115、Generator/companion/declared_assembly.py:73–85。

## 物理与材质

- 两组身体的 format_v1.parse 深比较仅 unit_id 改为新 canonical ID。链、关节、碰撞、约束、palette 与骨身份相同。Canonical Rig 改 ID 与 CRC，consumer rigbin 整体字节相同。衣物姿态的 profile digest 包含 canonical ID，第一次只改 ID 时 Core 返回 “clothing Rig identity/Rest mismatch or duplicate program”，补齐 digest 后通过。头盔 hd2phys 和配套逐文件相同。证据为 SDK/clothing_pose.py:142–154、tmp/w51/retarget.py:79–136 与 [物理回读](../../tmp/w51/readback/AD-49/verify.json)。
- 作者可见 Unit 的 TOC/GPU/stream 逐资源相同，材质键、贴图链接、骨索引和权重随原件保留。全局 13 材质和 9 贴图、头盔侧 2 材质都按原字节随包携带。隐藏甲片重新生成会使用目标原版材质和骨；官方 stock 查找器定位了两组主包外直接资源键 21 / 17 个。实际同名资源覆盖顺序和游戏渲染仍需现场确认。证据为 Client/NativeStockResources.cs:125–148、[BP-20 外部引用](../../tmp/w51/readback/BP-20/native/external-stock.log)、[AD-49 外部引用](../../tmp/w51/readback/AD-49/native/external-stock.log)。

## 身份

建议产品层保持同一角色的来源和旧／新装备映射，部署层为每个已绑定产物使用独立、确定性的身份，同一角色同一时刻只激活一个产物。

- 官方单包首选 sdk.项目.Archive，组包首选 sdkgroup.组 ID，登记再按 source identity、名称／署名、图标以及排序后的文件路径与 SHA 计算导入指纹。复用组 ID 但指纹变化时，旧条目仍存在则可分配 lib.散列身份。仅沿用组 ID 不会覆盖旧登记。包版本取 ZIP SHA 的前 16 位。原型 BP-20 登记为 sdkgroup.cdda2dfe601255f390e166699f41f579，AD-49 为 sdkgroup.155485dff4895c76aad0047570209e38。两组与原包的官方导入指纹不同。证据为 Client/OwnedStore.cs:303–367,399–414,802–867 和两组 official.log。
- 管理器把官方 id 写进 IasRole，ModIds/OwnedModIds 关联主包导入的节点。按现有拖放，派生包是新安装角色。W32 读官方库的启用状态与全组 Archive，roles.json 提供角色显示名。两组与原包、或两组彼此，都会因保留 SR-64 头盔而冲突。即使未来头和身一起搬，复用可见 alias 的版本也应互斥启用，Generator 还有整组发布 ID 校验。证据为 HD2ModCore/Ias/IasDeploymentService.cs:275–306,415–469、Generator/companion/library_catalog.py:148–160，[沙盒占用](../../tmp/w51/readback/AD-49/occupancy-after.json)。

## 原型与回读

样本从用户 IAS 库 sources/sdkgroup.deca3070d87752dc9f291677f680b2c7/ 复制已展开主包和配套。原始归档登记名为 milfy发光外套等3个文件.rar，作者长啸云天。身体原为 SR-64 煤渣砖 b186eae3f2b61509 / 5096bce9，头盔为 SR-64 煤渣砖头盔 84fd7aab7cf6f62d / 735a0c0b。身体 15 条共享物理链，头盔 41 条链。W32 worker 从沙盒库读取 4 个启用角色、8 个 Archive。两件目标身体均空闲。见 [占用快照](../../tmp/w51/occupancy.json)。

| 回读项 | BP-20 思想矫正官 | AD-49 阿波罗神 |
| --- | --- | --- |
| 目标身体 Archive／装备键 | e4e41c5f9c3848f0 / 40c6635c | 74bc9cf98d65b1bf / 0a18c237 |
| 身体 patch | 32→32，保留作者 18 个，重建隐藏 14 个 | 32→30，保留作者 18 个，重建隐藏 12 个，省略目标缺失的 2 个隐藏槽 |
| SDK 成员及 SHA | 2 个成员 read_member，13 个组内文件 SHA | 同左 |
| 官方读包和冲突 | 3 个 patch 经 Archive.Inspect/ResourceKeys，得 BP-20 身体及原头盔。原样本仍启用时报告 84fd7aab7cf6f62d，停用后全组通过冲突检查 | 3 个 patch 经官方 reader，得 AD-49 身体及原头盔。停用原样本后全组通过冲突检查 |
| Generator 与原生 Core | 全启用集合 8 个外观登记。两体型 × 默认、脱外套、脱鞋共 6 组、720 帧，与原包同输入的资源顺序、骨矩阵和计数完全一致，拒绝帧 0 | 8 个外观登记，6 组、720 帧、拒绝帧 0，与原包同输入回读一致 |
| 证据 | [格式与 SHA](../../tmp/w51/readback/BP-20/verify.json)、[官方日志](../../tmp/w51/readback/BP-20/official.log)、[原生帧](../../tmp/w51/readback/BP-20/native/results.json) | [格式与 SHA](../../tmp/w51/readback/AD-49/verify.json)、[官方日志](../../tmp/w51/readback/AD-49/official.log)、[原生帧](../../tmp/w51/readback/AD-49/native/results.json) |

复用已安装的 Python 3.12、ilspycmd、.NET 和工作树已有 DLL。依次运行 python -B tmp/w51/survey.py、构建 tmp/w51/Probe/Probe.csproj、python -B tmp/w51/retarget.py、python -B tmp/w51/retarget.py --reject-tests、python -B tmp/w51/native_readback.py。Probe Debug 构建输出为 0 个警告、0 个错误。生产代码与 Lua 保持本轮基线，生产解决方案构建、全量测试和 Lua 回归未运行。

已有索引上的官方 Windows File.Replace 在本轮沙盒返回拒绝访问。验证改用空索引实际登记，再复制 CAS，对内存里的合并状态调用官方冲突检查，最后将沙盒合并索引交给 W32 和 Generator 回读。真实管理器部署事务与游戏启动留待实测。

最终校验回读四个 ZIP，核对报告链接、原库索引、roles.json、客户端和 27 个样本文件的哈希。结果见 [最终校验日志](../../tmp/w51/final-verification.log)。工具策略拒绝递归清理，重复探针输出和反编译附带文件保留在 tmp/w51，交付 ZIP 位于 test 子目录。

## 测试包与实测

- [BP-20 主包](../../tmp/w51/test/Milfy-SR-64-to-BP-20/IAS_MainMod.zip) · [BP-20 配套](../../tmp/w51/test/Milfy-SR-64-to-BP-20/IAS_Companion.hd2irpack.zip) · [说明](../../tmp/w51/test/Milfy-SR-64-to-BP-20/说明.txt)
- [AD-49 主包](../../tmp/w51/test/Milfy-SR-64-to-AD-49/IAS_MainMod.zip) · [AD-49 配套](../../tmp/w51/test/Milfy-SR-64-to-AD-49/IAS_Companion.hd2irpack.zip) · [说明](../../tmp/w51/test/Milfy-SR-64-to-AD-49/说明.txt)

1. 退出游戏，留好原安装包以便恢复。在管理器 IAS 页卸载「Milfy发光外套 SR-64」。仅停用在官方占用回读层面也能消除冲突，首轮实测建议卸载以一并退出原主包节点。
2. 先测 BP-20。在 IAS 页同时拖入该组 IAS_MainMod.zip 和 IAS_Companion.hd2irpack.zip。安装完成后进游戏，穿 BP-20 身体和 SR-64 头盔，F4 选择 Milfy 发光外套身体外观及原头盔外观。
3. 两种体型都检查。看头身模型是否完整、有没有原版甲片穿出，确认外套发光和贴图与原样本一致。分别测试穿／脱外套、穿／脱鞋，再同时切两项。转头、跑动、停步和切外观时看头发、身体摆动以及是否卡住。
4. 卸载 BP-20 版后再测 AD-49 版。穿 AD-49 身体和 SR-64 头盔，重复上一步，并检查目标少 cell 23 时两个体型仍无残留甲片。记录游戏 build、体型、F4 选项和具体动作。

## 接入管理器

建议将两件套分析、可逆换绑和回读放在 HD2SourceMigration/IasPackaging 的独立模块。首版限定隔离 Unit、清理点网格、FREE v6、普通 Rig、shared v2/v3 及版本匹配的 Preview/Pose。先计算适配性和具体缺槽原因，再重建目标 point、输出格式和哈希。复用 IasPackageRoutes、IasAssets.BuildIasPointUnit、现有 patch writer 和官方 worker，保留作者模型载荷。

在 IasModuleService 与 IAS 页增加入口，复用 IasEquipmentCatalog 的 picker 和 IasVpkAllocation 的占用、批次保留与候选排序思路。先过滤不兼容候选。自动模式挑适配且全组空闲的目标。手动模式显示不可换原因与占用者。返原护甲时列出需停用的角色及其冲突 Archive，由用户确认后把竞争角色停用和两件套替换合入一次操作。证据为 HD2ModManager/Services/Ias/IasModuleService.cs:512–554、IasEquipmentCatalog.cs:97–138。

在锁外准备并验证派生包，提交前持 IAS 租约重查占用和源指纹。由 IasDeploymentService 与 IasModTransaction 同步官方库启用状态、主包节点、角色来源和一次 regenerate，异常回滚，原始 ZIP 可恢复。现在已有两处占用门和回滚入口。证据为 HD2ModCore/Ias/IasDeploymentService.cs:247–293,398–479、IasModTransaction.cs:211。

粗估首版 5–8 工程日。约 2–3 天做格式及候选验证，2–3 天做事务和 UI，1–2 天做样本回归和游戏实测。多版本共存、GROUP/旧式覆盖和复制新增可见体型另估。

## 待决

- 两组的游戏内显示、差分同时切换、头发及身体物理、真实换装生命周期待验证。离线帧使用合成输入。
- 补齐独立头盔迁移、GROUP、原版 ID 覆盖、显式 Package/Composite、不同扩展格式的案例。确定哪些新槽可自动隐藏、哪些新增体型允许复制作者方案。
- 确定正式产品的派生署名、逻辑角色与产物关联及 F4 预设迁移策略。普通 Mod 同名资源沿用现有主库冲突规则，改通用装载规则需另作决策。
