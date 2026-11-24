# W22 · IAS 转普通 Mod 通用化

Eku 原皮已能按自己的 DP-53 护甲与头盔导出。Milfy 默认使用 SR-64，也可指定 DP-8。三份请求的 ZIP 均通过 `hd2mod import plan`，模式为 `manifestStrict`，阻断诊断为 0。Bremerton 作为无选项的第三份实样也通过。 [实施计划](ias-module-generic-converter-plan-20261124.md) · [验证汇总](../../tmp/ias-w22/verification.log)

## 交付和验证

| ZIP | 目标护甲 / 头盔 | 本体 Unit | 选项 Unit | 预检节点 |
| --- | --- | ---: | ---: | ---: |
| [Eku-W22.zip](../../tmp/ias-w22/Eku-W22.zip) | DP-53 Savior of the Free | 14 | 23 | 22 |
| [Milfy-W22.zip](../../tmp/ias-w22/Milfy-W22.zip) | SR-64 Cinderblock | 24 | 6 | 7 |
| [Milfy-DP8-W22.zip](../../tmp/ias-w22/Milfy-DP8-W22.zip) | DP-8 Mountain-Scaled | 20 | 6 | 7 |
| [Bremerton-W22.zip](../../tmp/ias-w22/Bremerton-W22.zip) | B-01 Tactical / A-9 Helljumper | 23 | 0 | 1 |

ZIP 以 `manifest.json` 为根，逐条 CRC 已校验，SHA-256、文件数和字节数见打包日志。Eku 使用已解包的 SDK 组，Milfy 使用与 W21 相同的档案解包副本，Bremerton 使用 W7 的主包与配套包。输入和导出均在 `tmp/ias-w22` 沙盒验证。Eku 另做 body-only 和 helmet-only 两次实样转换，经游戏归档 ID 检查和导入预检，分别只写出各自域的 Unit。 [打包日志](../../tmp/ias-w22/package.log) · [Eku 预检](../../tmp/ias-w22/Eku-W22.import-plan.json) · [Milfy 预检](../../tmp/ias-w22/Milfy-W22.import-plan.json) · [DP-8 预检](../../tmp/ias-w22/Milfy-DP8-W22.import-plan.json) · [Bremerton 预检](../../tmp/ias-w22/Bremerton-W22.import-plan.json)

Eku 的 `尾巴` 默认 `有`，备选 `无`。`底衣` 默认 `打底裤＋内衣`，备选 `内衣`、`打底裤`、`脱`。`表情` 默认 `默认`，备选 `看到鸡蛋`、`拿到鸡蛋`。另有外套、白丝、鞋子三个组。默认由 `free_part` 指定，导入后仍沿用首子选项启用规则。实样测试核对了默认路径和全部列出的选项目录。

| 回读对象 | 全部 Unit | 可见 section（全部 LOD） | 直接材质 | 含父链材质 | 贴图 | 缺失 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Eku | 37 | 100 | 3 | 6 | 21 | 0 |
| Milfy 默认 | 30 | 210 | 11 | 14 | 13 | 0 |
| Milfy DP-8 | 26 | 192 | 11 | 14 | 13 | 0 |
| Bremerton | 23 | 104 | 4 | 5 | 18 | 0 |

逐 Unit 读取 TOC、LOD 和 section 材质绑定。可见 section 按资源键读取本包或游戏内材质，递归回读父链和贴图。隐藏件也读取 Unit 结构，其单三角占位剔除出可见材质计数。Milfy DP-8 另外通过 W17 现成的导入、素体装配、常规编译和低污染重编回归，两个生效层均解析 14 个材质和 13 张贴图，缺失 0。 [逐 Unit 与生效层证据](../../tmp/ias-w22/test-results/)

### Milfy DP-8 对照 W21

按同名组选项对应新旧路径，15 个配对文件里 14 个 SHA-256 相同。唯一内容差异是 `manifest.json`，因为名称取主包和目标装备名称，目录以域、部件与选项 ID 的摘要命名，选项名采用原始 `display_name`。四个选项目录改变了路径。逐条核对全部 Unit、材质与贴图的 TOC、stream、GPU 载荷，差异 0。三个物理容器、各自 footer 及完整 GPU 侧车均逐字节一致。最终构建的 DP-8 复跑也与归档前的 15 个文件逐字节一致。 [逐文件和逐条目 SHA-256](../../tmp/ias-w22/Milfy-DP8-W21-W22-hashes.json)

Milfy 默认 SR-64 有 24 个本体 Unit。比 DP-8 多出的四个空壳对应 SR-64 独有的原版格子，换 DP-8 时这些格子没有目标，因此省略。

## 入口和格式

- IAS 页的确认框可勾选「手动选择目标护甲或头盔」。其他页面正常拖入时使用 IAS 默认目标，按 Shift 拖入可手动选。找不到默认装备时，两条入口按存在的域分别请求选择，使用现有护甲注册目录的名称及同一份游戏护甲件表。无需索引也可用游戏本身的装备名称。
- CLI 的 `ias convert` 与 `ias to-standard` 接受 `--target-armor <archive|itemId>` 和 `--target-helmet <archive|itemId>`。两个域分别解析。源元数据无法唯一确认时，目标有明确选择后再用已发布路由中的原版 Unit 确认源套件，无法确认格子会报中文原因。
- 支持 `body`、`helmet` 或两者，任意份 v6 `free_parts` 差分可按域合并。isolation v1 提供本体别名，只有 isolation 也可转换。有差分而没有 isolation 时，差分自带的默认路由可提供所声明选项的基础别名。每个 `free_part` 输出一组，支持一个或任意多个选择。选项顺序用默认项优先，再按清单的排序号排列。
- 源与目标相同直接用原版 Unit ID。跨套件时按官方部位、层与体型配对。Shared 可展开至同格的 Slim/Stocky。两种来源同时竞争一个目标 Unit 且产物不同，会报中文错误。使用已有的隐藏 Unit 生成路径，缺目标的空壳可省略。可见 Unit 缺目标，或整个选项缺目标以致无法保留时，报出具体格子或选项。
- 针对 Eku 原版件里缺少 `CustomizationInfo` 的情况，新增 **IAS 调用才启用**的编写参数，让 Canonical 重建保留原件的无记录布局。原本的普通 VPK 编写入口仍按既有约定调用。W17 的 `sectionalPatchNumbering=ias-per-archive-v1`、自带父材质闭包、来源骨骼装配和 W21 手臂对齐在此路径复用。共享物理按 canonical 文件及同组 consumer profile 配对，独立物理按 Unit 查找。

已确认会拒绝的输入包括差分版本不为 6、非 `free_parts` 差分、isolation 版本不为 1、缺少域、缺少可确定的 Unit 路由、同一域声明多个相异套件、可见件缺主包 Unit 或 rigbin，以及冲突的 Unit 或材质归属。任意数量的清单支持的是同域同套件的合并。涉及披风、每域同时替换多套原版装备、无法确认源格子的包，仍缺少普通 Mod 映射依据。CLI 实跑 v5 夹具退出 1，返回 `IAS 清单 legacy.difference.ini：不支持 manifest_version=5，要求版本 6。` [旧版错误](../../tmp/ias-w22/unsupported.log)

`ias to-standard` 使用 Eku 沙盒角色，护甲参数用 item ID，头盔参数用归档 ID，返回 `"success": true` 并产生普通 Mod ID。写入在沙盒 Mod 库内完成。 [沙盒入库日志](../../tmp/ias-w22/to-standard.log)

## 固定假设排查

| 位置与假设 | 处置 | 依据 |
| --- | --- | --- |
| 旧 `IasDp8Routes.SourceBody/SourceHelmet`、`TargetBody/TargetHelmet` 和全部 `Slots` Unit ID | 已通用化 | 读取源声明和游戏件表，按当前目标计算一对一或 Shared 展开 |
| 旧 `IasDp8Routes` 的 isolation 归档字段 `.Single()`、只读取一份差分、差分只允许 SR-64 body | 已通用化 | 输入按域归并，重复字段及冲突声明给出清单名和中文错误 |
| 旧 `Torso_Armor`、`LeftLeg`、两个选项、显示名 `.Single()` 和 `coat/shoes`、`on/off` | 已通用化 | 用所有 `free_part/free_option/free_route` 和 `display_name`，选项目录基于稳定 ID |
| 旧 options 以「脱外套」当作隐藏，以及固定的空壳槽 | 已通用化 | 复用可见几何判定，识别无可渲染几何的小载荷空壳 |
| 旧 `FindSamplePatch`、SR-64 `.patch_0` 和 `9ba626afa44a3aa3.patch_2` 输入 | 已通用化 | 递归扫描归档名和补丁序号，按归档及发布 Unit 查找，独立资源补丁由内容识别 |
| 旧转换器用布尔域索引唯一 Unit 的 `.Single()`，资源 `ToDictionary` 默认假定单份 | 已通用化 | 发布键多归档歧义时报错，同归档较新序号覆盖，跨归档重复资源显式报错 |
| 旧头盔 rigbin 后缀 `84fd7aab7cf6f62d`、默认只有一份共享物理的 `SingleOrDefault` | 已通用化 | 按归档作用域与 consumer/canonical 同组文件配对，重复消费者给出错误 |
| 旧 manifest 固定 `Milfy · DP-8 Mountain-Scaled`、外套/鞋子说明、CLI 固定「四个选项」 | 已通用化 | 主包显示名加实际目标装备名，按实际选项汇报 |
| 旧 `IasSlotAuthority` 的四个 item ID 和单件 `.Single()` | 已通用化 | 用游戏库查 kit，同一原 Unit 可关联多个原版件坐标 |
| `IasSlotAuthority` 的 `9ba626afa44a3aa3` | 有意保留 | 这是游戏语言表所在的基础归档，专用于装备名。IAS 主包材质输入按补丁内容发现 |
| `IasSlotAuthority` 的写出顺序，转换器的目标 `.patch_0` 与同归档资源 `.patch_2` | 有意保留 | 排序使用通用格子和体型，以维持 Unit/物理序列。后缀用于普通 Mod 的输出布局及归档内分层 |
| `IasNativeMesh` 的 mesh、LOD、材质骨骼映射和顶点字段四处 `.Single()` | 已通用化错误处理 | 仍验证每个二进制键唯一。缺失或重复返回字段名和发布 Unit |
| `IasNativeMesh` 的未绑定 LOD4 阴影代理、LOD0 要求、最多四权重、骨骼容量与基础姿态 | 有意保留 | SDK/游戏 ABI 的兼容规则。当前样本和 W21 二进制验证走同一配方 |
| `IasRigReader.ReadSingle` 与任意 LOD、权重、链的计数读取 | 有意保留 | `ReadSingle` 是 IEEE 754 浮点字段读取，计数来自二进制格式 |
| `IasSectionalSource` 的 `manifest.json` 与 IAS 编号标记 | 有意保留 | 普通 Mod 兼容协议。父链闭包不依赖角色或目标归档 |
| `IasStandardDrop` 搭配条件 `Main.Count == 1`、`Companions.Count == 1`、`Vpks.Count == 0`，同批仅自动配唯一散装对 | 有意保留 | 限制的是拖入容器配对范围，单份配套包内可有多份清单。歧义批次沿用普通导入路径 |
| IAS 服务导入中的 VPK 个数用于复用命名，`FakeIasModuleService` 的 `Milfy SR-64` | 有意保留 | 前者是 VPK 转 IAS 的界面规则，后者是 Debug 演示资料，均不参与真实转换 |
| 多套件同域、跨包依赖与未知 clean-room 资源归档 | 留待以后 | 缺少冲突归属或路由契约，当前有歧义即报告，现有样本无需引入共用部署逻辑 |

## 构建、回归和范围

解决方案 Debug 日志原文为 `已成功生成。`、`0 个警告`、`0 个错误`。构建与测试均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`，并沿用沙盒串行 MSBuild 参数及已有还原缓存。全量测试最后一轮六个项目汇总为 **失败 0，通过 2861，已跳过 1，总计 2862**。 [构建](../../tmp/ias-w22/build-final.log) · [全量测试](../../tmp/ias-w22/test-final.log)

| 测试项目 | 失败 | 通过 | 跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1777 | 0 | 1777 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| HD2SourceMigration.Tests | 0 | 582 | 1 | 583 |

已有的三角形绕序诊断是唯一标记跳过项。小夹具覆盖同域多清单、1/2/3 选项、头盔、默认项、目标选择、缺格与旧版报错。真实验证通过环境变量启用。为测试材质内嵌回退，运行全量测试期间临时改名工作树内的 SDK 模板目录，测试后复原并核对 10 份文件的 SHA-256。

Lua 路径用仓库的 `run_physbone_regressions.py` 和已有的 Lupa LuaJIT 适配器，日志原文 `合计 PASS=486 FAIL=0；红=0`，另外两个外部夹具用例按仓库原规则跳过。系统路径无单独 Lua 5.1 可执行文件。首次适配器运行漏设沙盒 `LOCALAPPDATA`，出现运行日志缺失，随后显式设为 `tmp/ias-w22/localappdata` 完成验证。对默认目录的只读回查没有发现本轮生成的物理日志。无需安装工具。 [Lua 回归](../../tmp/ias-w22/lua-sandbox.log)

本轮生产改动限于 IAS 读取、转换、选择器接线和 Unit 编写的 IAS 专用开关。共用 Lua、物理运行和部署文件（`HD2PhysBoneCompat`、`tools/physbone-harness`、`Retarget`、`ApplyExecutor.cs`、`LuaDeploymentService.cs`、`PhysBoneContainerEmbedder.cs`）与本地 `master` 的差异为零。进场时 IAS 专用物理文件和未跟踪的 IAS 部署模块已经与 `master` 不同，本轮保留这部分基线。未提交、切分支或推送，未启动游戏、管理器和 IAS 图形客户端，未运行安装器或安装证书。

保留 ZIP、输入夹具、最终展开包、预检、测试结果及哈希证据。清理首轮转换副本、单域与入库沙盒、测试暂存及临时编辑脚本，清理后复核 ZIP 的 CRC 和 SHA-256。

## 待决

- 若「与 master 零差异」指包含工作树入场时的 IAS 专用物理、IAS 部署模块等差异，仍需派活方确认处理范围。直接回退会影响已依赖的 IAS 能力。
- 图形弹窗的真实交互与游戏显示未启动验收。CLI、件表选择和导入逻辑通过离线验证。
- 多套件同域时，应先决定普通 Mod 目标选择和不同套件共享件的冲突规则。当前转换器以每域一套目标为边界。
