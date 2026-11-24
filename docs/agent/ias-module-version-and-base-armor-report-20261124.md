# W23 · IAS 版本兼容、底子护甲与转换审计

官方 IAS 1.6.4 的 DLL 通过接口探测，使用该版本真实 Generator 打包了 Bremerton 和 Eku。模拟新版 `9.8.7` 接口齐全时通过，保留同名错签名重载但删除正确的 `Generate` 时，worker 返回包含版本号和预期签名的中文错误。

F4 可以把角色外观应用到其他 IAS 可识别的已穿戴装备。底子归档影响角色包自己的目标配方、体型分支、格子和隐藏件。VPK 打包确认框增加护甲和头盔下拉，CLI 增加 `--body-archive`、`--helmet-archive`。候选从 binding catalog 和当前游戏归档取交集。候选唯一时自动选中，存在多个候选时要求选择。W23-B 的旧默认值与 W23-C 的零护甲特定常量要求冲突，兼容默认值的来源留在「待决」。

Debug 最终构建退出 `0`，原文 `已成功生成。`、`0 个警告`、`0 个错误`。最新解决方案全量测试退出 `1`。通过 **2858**，失败 **5**，跳过 **1**，总计 **2864**。5 个失败由当时已运行的 Helldivers 2 进程触发共用部署防护。Lua 原文为 `合计 PASS=486 FAIL=0；红=0`。 [构建](../../tmp/ias-w23/build-final.log) · [全量测试](../../tmp/ias-w23/test-final.log) · [Lua 回归](../../tmp/ias-w23/lua.log)

## A · 版本兼容

### 官方 DLL

- 取消归档内客户端 DLL 的单一 SHA-256 门槛。发行端仍验签，下载和提取仍依据签名清单校验大小与内容 SHA-256。worker 加载后先核对反射接口，再执行相应命令。方法核对公开作用域、静态或实例、参数顺序和类型、返回类型以及是否为开放泛型，构造函数核对两个参数的精确类型。
- 检查生成与清理、运行目录导出与恢复、角色库、ReShade、DLSS 使用的类型、构造函数、方法和字段。补查 `ModLibraryState.entries` 与 `ModLibraryEntry.id/enabled` 的公开实例字段和类型，因为 Core 直接从 worker 状态 JSON 读取它们。旧检查里未调用的 `ExternalRuntimeDeployment.Prepare` 已移出必需接口。
- 已安装 `1.6.4` 在 IAS 页显示「已验证」，其余已安装版本显示「未经验证的新版本」。这两个标签记录验证状态。实际运行许可来自接口检查。
- 从指定离线包的内层 `release.zip` 提取客户端及 Generator。真实 DLL 的 SHA-256 为 `5bc1d974a89fa7887933dd83da9dde019132e818c0502dc7ef3069fe853c504d`，`check-interface` 和真实 `status` 均退出 `0`。新版夹具的完整形态退出 `0`，正确重载缺失的形态退出 `1`，报 `此 IAS 版本 9.8.7 的接口有变化：缺少 方法 static Void NativeRuntimeInputs.Generate(NativeRuntimePreparation, String, CancellationToken, Action`1)，请等待管理器更新。` [worker](../../HD2IasWorker/Program.cs) · [状态标注](../../HD2ModManager/ViewModels/IasPageViewModel.cs) · [真实 DLL 与 Generator 日志](../../tmp/ias-w23/official.log) · [新版夹具](../../tmp/ias-w23/fixtures.log)

### Generator、AQ SDK 和发行协议

| 检查位置 | W23 的处理 | 依据 |
| --- | --- | --- |
| `IasDependencyLocator` | 取消 `library_catalog.py` 指纹。逐个诊断缺少的 Python、归档脚本及角色目录脚本 | 版本优先读 `generator.json` 的 `version`，其次读邻近安装包 `bundle.json/versions/armor`。两者均无版本字段时显示「未知」，仍按接口检查 |
| `ias_package.py` | 实际导入并验证归档 `encode/decode`、目录 `materialize` 的调用签名，以及三段载荷往返。启用 IAS 物理时还核对生成端的 `physical_nodes` | 真实 1.6.4 探测退出 `0`。新版兼容夹具退出 `0`，缺 `physical_nodes` 的新版夹具退出 `2`，报出具体成员与版本 |
| `package_model.py`、`IasAssets` | 本地 AQ SDK 按实际导入、函数参数、所用字符串常量和表结构检查。部位表由 SDK 自己校验，C# 还校验绑定目录的归档键、域字段、装备集合、Unit ID 和重复归档 | `build_package_plan`、`build_save_jobs`、`binding_for_plan` 等依赖具体数据契约。兼容的本地 SDK 可通过 `--aqsdk` 或 `HD2_AQSDK_DIR` 指定 |
| `IasAqSdk` | 自动下载继续使用官方 `86b7252` 作为可复现快照，本地自选 SDK 没有提交号门槛。下载出错时提示可使用兼容的本地 SDK 根目录 | GitHub 项目当前接线未提供签名的能力/表版本 feed。自动追踪浮动分支会改变用户下次取得的代码和表。兼容性校验仍由打包器执行 |
| `IasFeedClient` | 保留 `HD2ManagerFeed1`、签名公钥、官方源范围、序号和数值版本字段 | 属于发行协议。版本取 feed 的 `latest_version`、匹配的 release 行和安装指针 |
| `IasComponentStore`、`IasPaths.RuntimeFiles` | 保留 `app/` 与 ArmorBase、ArmorAssets、Generator、bundle 的发行布局。签名清单缺件时报版本和确切路径。画面组件仍依其独立归档判断 | 这些文件名是当前运行模块的部件入口。`app/` 下的实际文件列表来自签名清单 |
| `IasGameCompatibility` | 保留 `HD2ArmorBase1` 的 host 字段和两个游戏文件路径。所需 SHA-256 按当次 ArmorBase 读取 | 官方 `ExternalRuntimeDeployment.ReadPackage` 检查 host 的两个键，`NativeRuntimeInputs.PrepareSystem` 对比 Base 与 Assets 的 host/version |

SDK 的兼容性检查验证接口和输入表。函数行为和未来 Generator 对物理目录的等价替代实现仍需用实包验收。 [依赖定位](../../HD2SourceMigration/IasPackaging/IasDependencyLocator.cs) · [打包探测](../../HD2SourceMigration/IasPackaging/Scripts/ias_package.py) · [AQ SDK 下载](../../HD2ModCore/Ias/IasAqSdk.cs) · [组件提取](../../HD2ModCore/Ias/IasComponentStore.cs) · [游戏兼容](../../HD2ModCore/Ias/IasGameCompatibility.cs)

## B · 底子护甲会影响什么

1. **F4 的外观列表与当前穿戴分开。** 官方 WinUI 的 `UiText.cs` 把 F4 定义为原生护甲面板的默认键。Generator 的 `panel_state.lua:59–70,109–123,180–187,322–323` 按域列出 Mod 外观，选择后发送独立的 `equipment` 与 `appearance`。 `appearance_controller.lua:38–46,69–78` 验证当前装备仍对应 live snapshot，再独立找目标外观。
2. **底子确定目标格子，当前装备提供 live 体型。** `assembly.lua:440–521` 从当前装备和观测的原版 Unit 得到 Slim、Stocky 或头盔 Any。 `assembly.lua:525–584` 检查目标合约与选择身份，按 live 体型查目标 recipe，再分别列出要隐藏的当前原件、目标可见 Unit 和 point 占位。目标缺少对应 recipe、profile 或可见网格时可以失败。源码证据支持跨装备显示，同时要求当前装备能被运行时识别和绑定。
3. **选底子会改变可打包的对象。** `declared_assembly.py:64–68` 对该归档声明的装备取 Unit 交集并拒绝越界的 isolation。 `equipment_binding.lua` 用实际资源、节点、体型和格子关联模型。C# 增加目录与游戏归档交集的选择，按目标实际存在的 Unit 落入部位。胸部优先用 `Torso`，缺失时尝试 SDK 的 `Torso_Armor` 格子。已有源分区找不到兼容目标格子时报告归档和部位。
4. **支持范围按 AQ SDK 绑定表决定。** 本次使用的 `HD2ResourceBindingCatalog1` 有 **127 个身体归档、161 个头盔归档**，分别关联 128 和 161 条装备键。全部有非空的可绑定 Unit 交集。B-01 Tactical Variation 1、SR-64 Cinderblock、DP-53 Savior of the Free 与 A-9 Helljumper 等条目来自这份数据，完整归档表见附录。管理器显示当前游戏可解析归档，名称来自游戏注册数据，缺名称时显示归档 ID。数据包含当前游戏目录中由 `bundles.nxa` 提供的归档。

证据来自指定 ILSpy 目录下的 `HD2RuntimeManager.WinUI/UiText.cs`、`HD2RuntimeManager/OwnedStore.cs`、`NativeRuntimeInputs.cs`，以及真实 1.6.4 Generator 的 `runtime/companion`。本轮遵守进程限制，F4 显示结论来自官方代码和离线产物，游戏中的实时装配未实测。 [打包入口](../../HD2SourceMigration/IasPackaging/IasPackagingService.cs) · [部位提取](../../HD2SourceMigration/IasPackaging/VpkExtractor.cs) · [确认下拉](../../HD2ModManager/Views/IasPageDialog.xaml) · [CLI](../../HD2ModCli/CliIasPackaging.cs)

## C · 全分支转换固定值清单

核对范围包括 `git diff master --name-only` 的转换、素体和 sectional 接线，以及工作树里 Git 差异列表无法列出的 W22 新源码，包括 `IasStandardConverter`、`IasPackageRoutes`、`IasStandardDrop`、`IasPackageDetector` 和 Python 项目身份脚本。测试和 Debug 演示的样本名仅在下表注明其作用。

| 位置 | 写死内容或原有假设 | 处理 |
| --- | --- | --- |
| worker `SupportedHash` | 只能加载一个 IAS DLL 指纹 | 已通用。签名发行包内的客户端按反射 ABI 检查，已验证版本标签单独记录 |
| `IasDependencyLocator.LibraryCatalog164` | 只接受一个 Generator 脚本指纹 | 已通用。读版本元数据并检查实际接口 |
| `IasAqSdk` | 自动下载绑定 `86b7252` | 已通用。本地运行兼容按能力。自动下载的可复现快照属于依赖来源，适用于所有角色 |
| `IasFeedClient`、`IasPaths.RuntimeFiles`、`IasGameCompatibility` | feed、外置组件与 host 路径和 schema | 有意保留。官方发行/运行格式，签名、版本与游戏预期哈希来自输入 |
| `IasPackagingService` VPK 参数 | 身体 `58e4bd4b2278d15c`、头盔 `a856edff49cfdd95` | 已通用。按当前 SDK 和游戏归档取候选，用确认框或 CLI 指定 |
| `VpkExtractor` 材质暂存 | `9ba626afa44a3aa3.patch_0` | 已通用。使用实际身体归档的独立暂存补丁名，路径由编写器返回 |
| `package_model.py` 主包材质 | `9ba626afa44a3aa3.patch_2` | 已通用。共用资源使用已产出域的实际归档，优先身体，只有头盔时用头盔；补丁层号属于输出格式 |
| `VpkExtractor` 部位候选 | 固定身体格子数组，胸部只走 `Torso` | 已通用到 SDK 的实际 Unit 交集和当前几何分区，胸部可用 `Torso_Armor`。保留部位名称的游戏/SDK 语义及映射 |
| `VpkExtractor` 材质索引 | 同 basename 时取第一行 | 已通用。IAS 层按现有编写器返回的源材质下标匹配。共用 VPK 材质层本身的 basename 查找见「待决」 |
| `VpkExtractor` 的 `modelKey` | `ias-model-cli-materials` | 已通用。材质编写器的诊断键取源 VPK 名，发布身份为每包 UUID |
| `VpkExtractor` 的源变体 | `SourceModelIndex == 0` 的默认 bodygroup | 有意保留。Source MDL 的默认可见变体。提取任意数量的默认分区；SDK 对这条路径输出基础组。导出求生可切换选装见「待决」 |
| `PatchExtractor.SelectBestArchive` | 命中相同时选字符串排序第一项 | 已通用。要求覆盖该域的可绑定来源。多套都完整时利用原补丁归档，仍无法唯一确定时明确提示拆分 |
| `PatchExtractor.ProcessDomainAsync` | 写死部位列表 | 已通用。迭代部位表，按目标拥有的 Unit 求交集。 `Head` 表示 SDK 头盔域 |
| `PatchExtractor` 缺体型复制 | 用同槽的实际原 Mod 网格补缺 | 有意保留。相同 SDK 槽的体型展开需要一个 donor 时按稳定 Unit 顺序取候选，并读取对应原件的定制数据。输入无任何目标格子的情况报错 |
| `PatchExtractor` 的补丁层 | `<16 位归档>.patch_<数字>`、较大序号覆盖 | 有意保留。游戏补丁命名和叠层格式。跨独立变体的版面冲突见「待决」 |
| `IasAssets`、`VpkExtractor`、`PatchExtractor` | Unit、材质、package 类型 ID，TOC、GPU、stream，隐藏 point Unit 和源绑定交集 | 有意保留。游戏格式与 IAS 占位协议；具体 ID/资源、可见性与格子均按输入读取 |
| `IasAssets` 的两份数据表名 | `armor_part_unit_ids.json`、`resource_binding_targets.json` | 有意保留。SDK 数据表接口文件。表中的归档、装备、Unit、格子通过运行时读取 |
| `IasPackagingService`、`ias_package.py`、`package_model.py` | `authored_manifest.json`、`authoring_project.json`、`jiggle_facts.json`、`stock_body/stock_helmet`、`raw_materials`、`warnings.json`、`ias_package.py`、`package_model.py`、`bonehash.txt` | 有意保留。转换器与脚本间的文件协议及工具发行资源。模型名、归档、体型、骨骼和路径随输入变化 |
| `IasPackagingService`、`package_model.py`、`IasPackageDetector` | `IAS_MainMod.zip`、`IAS_Companion.hd2irpack.zip`、`Files/`、`manifest.json`、`mod_group.json`、`HD2IndependentRig/` | 有意保留。成对输出和识别协议。专属资源使用项目 UUID 与实际归档；目录里可以放任意名称的角色 |
| `package_model.py` 包描述 | 写死 IAS 1.6.4 | 已通用。去掉描述中的版本锁定 |
| `package_model.py`、`project_identity.py` | `owned_*.isolation.ini`、`*.difference.ini`、`<project>.<archive>.*`、`HD2ArmorPoint1`、`HD2Point`、显示名称上限、基础组标记 | 有意保留。Generator 根据项目、归档和源 Unit 验证标识，相关名称是格式字段与派生公式 |
| `IasPackageRoutes` | v6 `free_parts`、v1 isolation，按域合并，默认选项优先 | 有意保留。读取格式版本，按清单行展开任意数量组与选项。未知 schema、域或同域多套件有明确报错 |
| `IasStandardConverter` | 单一旧样本路径、归档与材质文件名、固定选项名称和个数 | 已通用，沿用 W22。读取发布路由、所属归档、部位和默认配置。独立资源补丁从内容发现，按层号和来源合并 |
| `IasStandardConverter` | 本体 `.patch_0`，同目标归档的资源 `.patch_2`，本体与可选组目录 | 有意保留。普通 Mod 存储格式与资源层分配，归档 ID 按目标及输入选择 |
| `IasNativeMesh`、`IasRigReader`、`IasArmBindAlignment`、`CustomProportionRetarget` | LOD0、未绑定的低模阴影代理、四权重、骨骼容量、左右肢体和指骨与二进制字段 | 有意保留。游戏 ABI 或源与目标骨架语义。顶点、骨名哈希、计数及姿态矩阵从当前模型和 Avatar 读取 |
| `IasPhysicsReader`、`IasPhysicsMigration`、`Scripts/ias_physics` | 独立/共享物理格式的 magic、版本、record 容量、能力和 FNV 参数 | 有意保留。官方输入与管理器现有运行容器的格式常量。本轮不改这些文件 |
| `IasSlotAuthority.ReadNamesAsync` | `9ba626afa44a3aa3` 和英语装备名 fallback | 有意保留。共用游戏数据的语言资源归档，只为装备显示名。IAS 选择或输出的 Unit 目标来自游戏装备件表与发布数据 |
| `IasStandardDrop`、`IasDropWorkspace`、`IasCharacterPackage` | `main`/旧主包名约定、标记、单个唯一散装主包与配套包、嵌套层数限制 | 有意保留。协议入口、歧义防护和未标识包的配对边界。单配套包内可带多个域、选项及清单。多对自动配对见「待决」 |
| `IasPackageDetector` | ZIP/RAR/7z 文件头、`.vpk`、`.hd2irpack.zip`、旧 `_IAS_Companion` 命名提示和 sidecar 后缀 | 有意保留。格式和旧发行包兼容提示，深入识别仍检查压缩包内容 |
| `SectionalSourceManifest`、`IasSectionalSource`、`SectionalSourceAssembler`、`Legacy2SectionalConverter`、`SectionalCompiler*`、`LowPollution*`、`SectionalSplitter` | `ias-per-archive-v1`、`IasSourceVersion=1`、源自带父材质与骨骼、归档内补丁编号、manifest 与开启选项 | 有意保留。普通 Mod/sectional 协议，来源、父材质闭包、骨骼、选项 ID 和补丁计划按当前数据计算 |
| `ImportQueueService`、`ObjectTreeImporter`、`ExternalPackageAdoptionPlanner`、`ModCloneService`、`OverwriteMaterializer`、`ManagedOverwriteDirectory`、`ModNodePayloadLayout`、`OptionActivationStore`、`ModCompileService`、`ProfileApplyService`、`CoreServices` | 导入、存储、构建与生效接线中的协议目录和角色标记 | 有意保留。分支接线复用内容检测、存储格式与选项状态。未发现把特定角色、装备或选项个数写入生产路径的参数 |
| `CliIasConversion`、`IasModuleService`、`IasStandardDropImport` | 入口动词、目标参数、同批计数、一个 VPK 时复用外层命名 | 有意保留。用户入口协议，实际源和目标由当前输入或用户选择 |
| `FakeIasModuleService`、测试数据与文档示例 | `Milfy SR-64`、`1.6.4`、Eku、Bremerton 和具体样本归档 | 演示、测试输入与报告证据，不参与生产转换 |

旧的 `IasDp8Routes`、`IasDp8Converter`、`CliIasSlotAuthority` 中对 SR-64、DP-8、单一源头盔、固定的 `coat/shoes` 命名、隐藏项文案和固定选项数量的替换详见 [W22 报告](ias-module-generic-converter-report-20261124.md)。W23 核对当前入口的解析与输出回读，确认未重新接入这些旧参数。

## 实样与回归

| 输入和操作 | 结果 | 证据 |
| --- | --- | --- |
| Eku IAS → 普通 Mod | 本体 14 个 Unit，选项 23 个。 `manifestStrict`，22 个节点，阻断 0 | [转换日志](../../tmp/ias-w23/Eku-convert.log) · [预检](../../tmp/ias-w23/Eku-plan.json) |
| Milfy IAS → 普通 Mod | 本体 24 个 Unit，选项 6 个。 `manifestStrict`，7 个节点，阻断 0 | [转换日志](../../tmp/ias-w23/Milfy-convert.log) · [预检](../../tmp/ias-w23/Milfy-plan.json) |
| Eku 的普通 Mod 本体 → IAS | 按输入识别 DP-53 身体，产生 22 个身体 Unit。材质层随身体归档，含 5 个材质、13 张贴图；此输入只包含本体，无独立选项目录的转换 | [打包日志](../../tmp/ias-w23/Eku-repack.log) |
| Bremerton VPK → IAS，用原来那组底子，启用 IAS 物理 | 主包 22 个身体 Unit、1 个头盔 Unit。材质层随身体归档，含 5 个材质、10 张贴图。再转普通 Mod 得 23 个本体 Unit，通过 `manifestStrict` 预检与逐 Unit 材质闭包回读 | [打包](../../tmp/ias-w23/Bremerton-pack.log) · [再转换](../../tmp/ias-w23/Bremerton-roundtrip.log) · [预检](../../tmp/ias-w23/Bremerton-plan.json) |
| Bremerton 同 VPK → IAS，换 SR-64 身体及 B-01 Tactical Variation 1 头盔，关闭 IAS 物理 | 主包 26 个身体 Unit、1 个头盔 Unit。5 个材质、10 张贴图写到新选定的身体归档。两个 ZIP 均通过 CRC，官方 Generator 解码三段载荷 | [打包](../../tmp/ias-w23/Bremerton-alt.log) · [逐补丁计数和 SHA-256](../../tmp/ias-w23/package-evidence.log) |

真实官方角色库接口在独立沙盒中写入并读回了 Bremerton 与 Eku 的记录，每份 `status` 都读回 1 条记录、`enabled=true`。worker 的组合 `import` 命令接着调用 `SetLibraryEnabled`，在官方 `SaveLibrary` 的二次 `File.Replace` 处被沙盒拒绝，退出 `1`。因此真实 DLL 加载、运行库读回、Generator 与打包已验证，组合导入成功退出、部署与游戏内应用仍待确认。 [官方接口与产物证据](../../tmp/ias-w23/package-evidence.log)

| 最新全量测试项目 | 通过 | 失败 | 跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 1777 | 0 | 0 | 1777 |
| HD2ModAdaptation.Tests | 212 | 0 | 0 | 212 |
| HD2ModCli.Tests | 208 | 5 | 0 | 213 |
| Decryptor.Tests | 5 | 0 | 0 | 5 |
| HD2SourceModel.Tests | 72 | 0 | 0 | 72 |
| HD2SourceMigration.Tests | 584 | 0 | 1 | 585 |
| 合计 | 2858 | 5 | 1 | 2864 |

5 项失败均为 `DeploymentCliTests`，计划或应用记录显示 `GameProcessRunning`，检测到 PID 88072。唯一跳过是仓库已有的三角形绕序诊断。全量命令保留用户要求的参数，并追加 `-m:1` 和 `-p:BuildInParallel=false` 在沙盒中串行执行。首次全量运行另遇到现有内嵌材质测试对开发模板目录的依赖。复跑时暂改工作树内该目录名，测试结束复原，10 份文件 SHA-256 校验一致。Lua 使用现有 Lupa LuaJIT 运行原仓库 harness，无新增工具，两个原有外部夹具跳过。首次 .NET SDK 首次运行输出包含开发证书的通用欢迎文案，后续调用显式禁用开发证书生成。只读检查当前用户个人证书库只找到 2026-07-29 起效的既有 ASP.NET Core 开发证书。 [模板复原](../../tmp/ias-w23/template-restore.log)

```powershell
dotnet build WpfApp1.sln -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet test WpfApp1.sln -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -m:1 -p:BuildInParallel=false --logger 'console;verbosity=normal'
```

## 待决

- **旧默认值来源。** W23-B 要求保留旧默认，W23-C 硬性禁止在转换中保留护甲特定值。当前按目录显式选择。可由派活方指定外部默认配置或数据来源，旧值为 B-01 Tactical Variation 1 `58e4bd4b2278d15c` 与 A-9 Helljumper `a856edff49cfdd95`。
- **运行环境。** 游戏进程退出后仍需一轮全绿解决方案测试。当前沙盒下官方二次 `File.Replace` 被拒绝。图形下拉、F4 和部署最终效果受本轮进程约束，采用了编译、源码与离线验证。
- **主线共用行为。** 同 stem 材质在上游 VPK 材质生成器已可能合并，改动会影响非 IAS 转换。多套件同域、同一拖入含多对未标识的包、求生 bodygroup 作为 IAS 可切选项、普通 Mod 外观变体或物理链的自动迁移，都需要新增归属或输入协议。现阶段按已有格式或清晰错误处理。
- **与 master 的差异口径。** 共用 `HD2PhysBoneCompat/`、`tools/physbone-harness/`、`HD2SourceMigration/Retarget/`、`ApplyExecutor.cs`、`LuaDeploymentService.cs`、`PhysBoneContainerEmbedder.cs` 与 master 的实际文件差异为零。入场已存在的 IAS 专用物理文件与 IAS 部署代码仍与 master 不同，部分尚未跟踪。本轮改动 worker 的接口预检、组件描述和打包接线。扩大「零差异」到全部既有 IAS 文件需要派活方确定回退范围。

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`。未提交、切分支或推送。任务未启动游戏、管理器或官方图形界面，也未运行安装器、请求管理员权限或写入实际游戏目录和 Mod 库。沙盒输出、构建与验证日志位于 `tmp/ias-w23`，已清理临时程序、解包副本和测试目录，用户已有的素材目录已复原。

## 附录 · 绑定目录支持的底子归档

附表数据来自本次实际使用的 AQ SDK `hd2_system/resource_binding_targets.json`，名称补自同一 SDK 的 `hashlists/archivehashes.json`。未收录名称的行显示装备键。列表表示绑定目录覆盖范围，具体打包还要求当前游戏有该归档并能容纳源模型的部位。

<details>
<summary>身体（127 个归档）</summary>

| 归档 | SDK 名称 | 装备键 |
| --- | --- | --- |
| `f32d3723bfe55d2f` | A-35 Recon | `d879973a` |
| `1d6dc4216e7ce52d` | A-9 Helljumper | `16e1b9d3` |
| `25fa0f1d426ad6bd` | AC-1 Dutiful | `ac4235b3` |
| `460f2faee22f5850` | AC-2 Obedient | `55fd699b` |
| `89395c2f8ce3d8a0` | AD-11 Livewire | `f8fadb6c` |
| `1dafd22cfccc02b8` | AD-26 Bleeding Edge | `01c2a674` |
| `74bc9cf98d65b1bf` | AD-49 Apollonian | `0a18c237` |
| `cbfe5ddf73d50785` | AF-02 Haz-Master | `7d96d0c6` |
| `86a9bdfc23c01517` | AF-50 Noxious Ranger | `5cc09255` |
| `b2d7f35cb2ab24ce` | AF-52 Lockdown | `f6303500` |
| `62c6fbc5cd90448d` | AF-91 Field Chemist | `6b619174, 100a5dcd` |
| `681a512c5a5c0de7` | AF-91 Field Chemist | `135c4274` |
| `fda9b1496320364e` | AF-91 Field Chemist | `732693a1` |
| `58e4bd4b2278d15c` | B-01 Tactical (Variation 1) | `61b31723` |
| `562a45e9bc984eb9` | B-01 Tactical (Variation 2) | `4f7fb2bd` |
| `6cd3d55c05d4eac1` | B-01 Tactical (Variation 3) | `6351a9aa` |
| `519cc1ec2eb56e1d` | B-01 Tactical (Variation 4) | `6d30f386` |
| `dfa7c28c3b490a8c` | B-08 Light Gunner | `1d20d6ee` |
| `1120577ed1e095f8` | B-22 Model Citizen | `cb7e9736` |
| `1866f762c49358ea` | B-24 Enforcer | `ac0adecd` |
| `33cecc4e485ea3c4` | B-24 Enforcer | `803276ce` |
| `788df19150915809` | B-24 Enforcer | `8b9ed77d` |
| `824aa91040dadba8` | B-27 Fortified Commando | `d2c578e1` |
| `e4e41c5f9c3848f0` | BP-20 Correct Officer | `40c6635c` |
| `1bb362e77179996f` | BP-32 Jackboot | `09b08ec4` |
| `5fb542484cb2c2a5` | BP-77 Grand Juror | `f4598295` |
| `ffcad1f7ff9888d7` | CE-07 Demolition Specialist | `5a17d6d6` |
| `726edbdd8808874b` | CE-101 Guerilla Gorilla | `dee800a8` |
| `a88115eeba44ec4e` | CE-27 Ground Breaker | `7edf6505` |
| `4ceb3d4e31097c68` | CE-35 Trench Engineer | `fb4b1254` |
| `f1175472a3f1469a` | CE-64 Grenadier | `674671a0` |
| `8079db396fbfd31c` | CE-67 Titan | `f34064ac` |
| `8ade54353acbe39d` | CE-74 Breaker | `26bf31fe` |
| `88ccc33c4cca4d20` | CE-81 Juggernaut | `97cf0a8a` |
| `2d25ef67b7d87ea3` | CM-09 Bonesnapper | `1c6c05fa` |
| `784acdd4158a9a42` | CM-10 Clinician | `7a07031f` |
| `b0db7f4f0a11debd` | CM-14 Physician | `38aa207d` |
| `d7e0e8a6c8ab0e2c` | CM-17 Butcher | `95903adf` |
| `41203170aae394c0` | CM-21 Trench Paramedic | `d52bb413` |
| `e7449208a97fa22e` | CPG-48 Sapper | `5f075dc5` |
| `81bf25bbdd4624c1` | CPH-26 Commandant | `242702a5` |
| `07a2056e7a5f313f` | CPR-80 Bulwark | `9c0bbe91` |
| `1d9e8acfc3ee3ace` | CW-22 Kodiak | `69bc2d4a` |
| `7b19ad7f21d347b4` | CW-36 Winter Warrior | `0b5cd386` |
| `d451ab9c17f64b19` | CW-4 Arctic Ranger | `b92e1781` |
| `78c4f1839dea282d` | CW-9 White Wolf | `b3deb5d9` |
| `3d8cf2088ed56091` | DP-00 Tactical | `3f1bec1a` |
| `bb8d9bee0012e672` | DP-11 Champion of the People | `e3aaa79d` |
| `1beb230363aaf427` | DP-40 Hero of the Federation | `b513fd54` |
| `5e0dba19a567fb99` | DP-53 Savior of the Free | `fd456de0` |
| `d2fde496226be96f` | DP-8 Mountain-Scaled | `a9a71fe7` |
| `aaebfbedc571fd7a` | DS-10 Big Game Hunter | `c60c706d` |
| `b60eaad684926425` | DS-191 Scorpion | `0ade6719` |
| `541fbc971b1aef62` | DS-42 Federation's Blade | `4a545f06` |
| `2b6d645c35bce4ea` | EX-00 Prototype X | `d7b2fd5d` |
| `5e86a6b2862f92cf` | EX-03 Prototype 3 | `24e3d6ba` |
| `f1973d347198bd6f` | EX-16 Prototype 16 | `aa32b384` |
| `31d06451a09679f1` | FS-05 Marksman | `076d9aad` |
| `8670598c1f4462dc` | FS-05 Marksman | `ecabadbf` |
| `ccac0a57aca3ae92` | FS-05 Marksman | `58dbdacd` |
| `24abba68248f7adf` | FS-11 Executioner | `77947add` |
| `9102938b4b2aef9d` | FS-23 Battle Master | `26d572a4` |
| `558b259b94d2d1c2` | FS-34 Exterminator | `8bd85dd2` |
| `267fcb7e9e312e33` | FS-37 Ravager | `1f9bfa78` |
| `813da450b8029cb2` | FS-38 Eradicator | `20a85cc1` |
| `1308bf1fbd277eb2` | FS-55 Devastator | `d3461392` |
| `68ac37dae3b424c1` | FS-61 Dreadnought | `537e8e45` |
| `2351919ff531f494` | GS-11 Democracy's Deputy | `407ab98a` |
| `14fe2734dcb8d1ed` | GS-17 Frontier Marshall | `16eeb860` |
| `00cf0ef7df912a6e` | GS-66 Lawmaker | `b482b460` |
| `b6f3029666a39fe8` | I-09 Heatseeker | `2d1dd830` |
| `57cebd7e5f985d45` | I-102 Draconaught | `5662c173` |
| `1683ee92e6aec3eb` | I-44 Salamander | `223a3b4b` |
| `8710c8ba1e22b42c` | I-92 Fire Fighter | `8c83c789` |
| `d0de7721e4a47990` | IE-12 Righteous | `9fa96490` |
| `4dad43d7cc148a49` | IE-3 Martyr | `b028b970` |
| `0a15a5b5d8695e47` | IE-57 Hell-Bent | `e1d53693` |
| `476488a5861d15e0` | KDM-500 Outrider | `ae5978b1` |
| `d937c4ae5e9f3221` | KDM-73 Dragoon | `1b8329ab` |
| `19d6f65fa6f101c6` | KDM-763 Pacesetter | `1e28f118` |
| `5fec3757445c4e16` | O-2 Heavy Operator | `2e706db8` |
| `f9ced2d898787705` | O-3 Free Spirit | `f7e6f127` |
| `50038ca035821a74` | O-44 Bonded Pilot | `bc5cf836` |
| `aac562a77d283684` | PH-202 Twigsnapper | `de631a28` |
| `d8cd425483086455` | PH-56 Jaguar | `23d0e15c` |
| `40faf112f20318ea` | PH-9 Predator | `72b472a7` |
| `39fe1f0308f4514a` | RE-1861 Parade Commander | `c71dbba4` |
| `d923906f8b815987` | RE-2310 Honorary Guard | `30e54b57` |
| `f50c7fd427839c47` | RE-824 Bearer of the Standard | `fde8f657` |
| `1e2ba3712fa1b030` | RS-100 Sanctioner | `4dd749c6` |
| `6d07f1a7176ae5d0` | RS-20 Constrictor | `ec4638af` |
| `27d61c15acfeaa0e` | RS-40 Beast of Prey | `dbc21a99` |
| `da1948db72db5343` | RS-6 Fiend Destroyer | `98e86082` |
| `18e9f450ff227f68` | RS-67 Null Cipher | `ca092176` |
| `85d392768bf6a259` | RS-89 Shadow Paragon | `aed67d10` |
| `5def9553433fceda` | SA-04 Combat Technician | `c9d2ad39` |
| `1de000bcfe445daf` | SA-12 Servo Assisted | `f2b7d3f5` |
| `aade1a73a16b5fb5` | SA-25 Steel Trooper | `dae4a744` |
| `d1421b2988398f0a` | SA-32 Dynamo | `3c3ebaf0` |
| `d895f447d332c331` | SA-7 Headfirst | `c7a5782f` |
| `14060e113e41f4ab` | SA-8 Ram | `b69df10d` |
| `63a534dd4757fe92` | SC-15 Drone Master | `f57d61ce` |
| `e54eaa26d836a2bf` | SC-30 Trailblazer Scout | `1d60fc90` |
| `0bb13de574d4acce` | SC-34 Infiltrator | `5bb4bbb0` |
| `c72998936b1d88a2` | SC-37 Legionnaire | `9bc153c0` |
| `d5f85cb5efb44cd2` | SC-37 Legionnaire | `c2a2375f` |
| `dd3c12b413651958` | SC-37 Legionnaire | `14209e3a` |
| `1a204afc2362648b` | SR-18 Roadblock | `d63ffdd3` |
| `082f22454170efd6` | SR-24 Street Scout | `05e56ee5` |
| `b186eae3f2b61509` | SR-64 Cinderblock | `5096bce9` |
| `3953b30dc9ae1114` | SS-15 “焦点” | `a2441063` |
| `3ab4af712c5ec0df` | SS-33 “拟态元帅” | `983c1ac3` |
| `97b5edb328db59b5` | SS-500 “苍白检察官” | `0b2045c7` |
| `bbbb8499544b83ef` | TG-122 Demo-Trooper | `a45385cd` |
| `1434ec7cf0edf1bc` | TG-8 Sharpshooter | `5d0d8002` |
| `5e9a360985f0a6e0` | TR-117 Alpha Commander | `9f73133e` |
| `4574fd34bddba5c4` | TR-40 Gold Eagle | `1c089a28` |
| `08241ba732c97a9d` | TR-62 Knight | `7346edc6` |
| `7dc940635e3dd573` | TR-7 Ambassador of the Brand | `8a957897` |
| `c04df62c527e2761` | TR-9 Cavalier of Democracy | `940716d6` |
| `19b5a68e61d7a9d7` | UF-16 Inspector | `b661572a` |
| `871290e9d5703851` | UF-50 Bloodhound | `b3550d20` |
| `0fecb336be1d287b` | UF-84 Doubt Killer | `a574d33d` |
| `91033cfaaad4ad18` | UF-84 Doubt Killer | `2521d401` |
| `0c206dbe38b89339` | — | `3412f5e4` |
| `5717aeb23774a749` | — | `8c6ae37c` |
| `5ad0c7bf483d382a` | — | `f8b8cf9b` |

</details>

<details>
<summary>头盔（161 个归档）</summary>

| 归档 | SDK 名称 | 装备键 |
| --- | --- | --- |
| `1a2fc86abd27bf5b` | A-35 Recon | `b407147e` |
| `a856edff49cfdd95` | A-9 Helljumper | `9f3f8a17` |
| `424fed21023b620d` | AC-1 Dutiful | `ae8f7b4c` |
| `080f2f101b1fb6f6` | AC-2 Obedient | `289884f4` |
| `be54826ac0d5f4db` | AD-11 Livewire | `24e765ba` |
| `7cfc63f636fa5132` | AD-26 Bleeding Edge | `dcce533f` |
| `c5e272869f1386b6` | AD-49 Apollonian | `f8c6437f` |
| `be1fbdaa4b77e86b` | AF-02 Haz-Master | `2f748b84` |
| `a0a51b7b380c6d1a` | AF-50 Noxious Ranger | `de575ea9` |
| `884fde6481f699e3` | AF-52 Lockdown | `9ad6358e` |
| `21a24072a79aeba8` | AF-91 Field Chemist | `df5f7ccb` |
| `75a21fc49f80fb9c` | AF-91 Field Chemist | `8c6b78e3` |
| `7ae5a2f32d6f24f1` | AF-91 Field Chemist | `0fb65a0e` |
| `fd109bfeeed36726` | B-01 Tactical (Variation 1) | `261c4a52` |
| `3b143cc283b7f707` | B-01 Tactical (Variation 2) | `45d80a38` |
| `f4880623d32bafa9` | B-01 Tactical (Variation 3) | `b4027b70` |
| `8313c9a556b8ee85` | B-01 Tactical (Variation 4) | `df8e4ada` |
| `7805dbf6f83b09ba` | B-08 Light Gunner | `48226e8f` |
| `d1a39b5edac646aa` | B-22 Model Citizen | `ec49526f` |
| `bb58aea9beba9e0e` | B-24 Enforcer | `41211c7f` |
| `49076aae3fe24987` | B-27 Fortified Commando | `d11da85b` |
| `44c466a6cf09718b` | BP-20 Correct Officer | `3b5af7c7` |
| `2a7c626239a5d6b4` | BP-32 Jackboot | `7fa97f66` |
| `3e345b62ed9e3f46` | BP-77 Grand Juror | `1805a245` |
| `fdd3d3e677e5a3f4` | CE-07 Demolition Specialist | `65c07df2` |
| `c0b7d29585edde5a` | CE-101 Guerilla Gorilla | `89b1b9e3` |
| `38a82f5be66c5b36` | CE-27 Ground Breaker | `ac5d49f9` |
| `b72f72e3863c1611` | CE-35 Trench Engineer | `affb955b` |
| `92c0b2ea0ab6f69e` | CE-64 Grenadier | `4e0785ae` |
| `ed5d869cda68286e` | CE-67 Titan | `97d7668f` |
| `80f8e300f67850e9` | CE-74 Breaker | `ee2e8ce0` |
| `8230afe78d703e38` | CE-81 Juggernaut | `fcde237a` |
| `5828025300024f4f` | CM-09 Bonesnapper | `c25abb74` |
| `fcb56daa66137cb6` | CM-10 Clinician | `b18b6c66` |
| `1bf281b613081b05` | CM-14 Physician | `203f720c` |
| `22bb11d902251d11` | CM-17 Butcher | `a67010e7` |
| `fadecbde1e20ad47` | CM-21 Trench Paramedic | `bd1e5e84` |
| `d4fb7d61ad409841` | CPG-48 Sapper | `fdb93622` |
| `a67993f7af5594d8` | CPH-26 Commandant | `37442029` |
| `51a7562176a8ac19` | CPR-80 Bulwark | `a5574ac2` |
| `e9f7da425997f38b` | CW-22 Kodiak | `91d773c8` |
| `9cfd4019e0902fa8` | CW-36 Winter Warrior | `cd3d20dc` |
| `5f6dbf7fbbccccdd` | CW-4 Arctic Ranger | `63aa42c5` |
| `70335c90463d4368` | CW-9 White Wolf | `7c59469b` |
| `eb3914f15e4151ff` | DP-00 Tactical | `584ab37b` |
| `a45cfd81d5ea873b` | DP-11 Champion of the People | `ef6d2ad3` |
| `9d538582c1cd96b8` | DP-40 Hero of the Federation | `5c3087d2` |
| `0121fd0e8370a1dc` | DP-53 Savior of the Free | `085838c1` |
| `59d238094a164c88` | DP-8 Mountain-Scaled | `b19d6c0a` |
| `4b92ff9a0256159f` | DS-10 Big Game Hunter | `52791e64` |
| `dbfd326577ba126f` | DS-191 Scorpion | `f2dcc254` |
| `9852e5e0b70a7441` | DS-42 Federation's Blade | `ee2e3efa` |
| `bf02ab023a9248f3` | EX-00 Prototype X | `d1a64ac3` |
| `18e40c54da8c54bf` | EX-03 Prototype 3 | `8abccc54` |
| `3a169e8ac4b9c62e` | EX-16 Prototype 16 | `212df664` |
| `453c6def93940335` | FS-05 Marksman | `529efe65` |
| `ff2a9db5dfe875d3` | FS-11 Executioner | `5642fa1a` |
| `98da89d0d5132867` | FS-23 Battle Master / TR-62 Knight | `1e9444f9` |
| `40cecc6f5af56654` | FS-34 Exterminator | `38b05b83` |
| `67baff6f1c9a2775` | FS-37 Ravager | `7934ed8b` |
| `0290d8011fce62ef` | FS-38 Eradicator | `da64ee0d` |
| `13f9269d08e52cf2` | FS-55 Devastator | `056848e9` |
| `c720eb21745daa53` | FS-61 Dreadnought | `2e15716b` |
| `dbe6c57c7e3b8747` | GS-11 Democracy's Deputy | `58d5cd85` |
| `213bbf4edad25223` | GS-17 Frontier Marshall | `f646f083` |
| `a6065ffb410d5e1c` | GS-66 Lawmaker | `24fdb2c0` |
| `98d52b64727d362b` | I-09 Heatseeker | `b1d021ff` |
| `e9f6ef51d693fdd5` | I-102 Draconaught | `73893816` |
| `71aa8c9bf9a0aef0` | I-44 Salamander | `d0ce5bc7` |
| `9e9d2e828d1146d0` | I-92 Fire Fighter | `fd428984` |
| `a2f4121316ccdffc` | IE-12 Righteous | `8af14536` |
| `fea0e08cff101f8e` | IE-3 Martyr | `542c752e` |
| `37882724fe5a136e` | IE-57 Hell-Bent | `930dc02f` |
| `e2a2e33407398d9e` | IX-Voidwalker | `6478678e` |
| `1cd676e91f90d21d` | KDM-500 Outrider | `d2a83ca7` |
| `88be7bc44053c400` | KDM-73 Dragoon | `f5cb7633` |
| `f1ab0e94450ff1f6` | KDM-763 Pacesetter | `71e406e4` |
| `5e40404d846b2b00` | O-2 Heavy Operator | `e1ca6837` |
| `a2999407862595df` | O-3 Free Spirit | `8d06ea5a` |
| `3cf26a29a0012282` | O-44 Bonded Pilot | `41dd184d` |
| `0c7ecd8156d49fe9` | PH-202 Twigsnapper | `57a7f31d` |
| `5c82005edd85558c` | PH-56 Jaguar | `f06ad8d7` |
| `f0daae91a2027fd7` | PH-9 Predator | `1b2fde48` |
| `2fb07d7a9b9252aa` | RE-1861 Parade Commander | `677bee02` |
| `b4d15b145b3f7677` | RE-2310 Honorary Guard | `6809ad22` |
| `94b8a07a98780017` | RE-824 Bearer of the Standard | `a8bf0ccb` |
| `b977f9d5411386d0` | RS-100 Sanctioner | `47bcec20` |
| `2c5f7890ce9b75b6` | RS-20 Constrictor | `e4e2b61b` |
| `afecffb25e09988d` | RS-40 Beast of Prey | `ed2350ca` |
| `42d4df4c690e6491` | RS-6 Fiend Destroyer | `bd177f77` |
| `b0db976d98b02a95` | RS-67 Null Cipher | `58716e11` |
| `804e40c3115e9650` | RS-89 Shadow Paragon | `d05754cd` |
| `7f49d834a7764207` | SA-04 Combat Technician | `4e8f9f4f` |
| `3196ede0c925ba83` | SA-12 Servo-Assisted | `8cd3973b` |
| `fd1028aaaa651c2f` | SA-25 Steel Trooper | `a3a7e93d` |
| `e4e4639f4365ca4e` | SA-32 Dynamo | `f3bcf18e` |
| `719e9e75f790d580` | SA-7 Headfirst | `9e987d5e` |
| `a72abfe9616771df` | SA-8 Ram | `790c1b61` |
| `19888141475bf282` | SC-15 Drone Master | `8166c1cc` |
| `e08b7a38aa847b58` | SC-30 Trailblazer Scout | `5aaf5d66` |
| `984f49b3c0542aa3` | SC-34 Infiltrator | `da8bbeaa` |
| `0a03573534303d12` | SC-37 Legionnaire | `5facabb9` |
| `c7b0eaff041030c2` | SR-18 Roadblock | `dc7304b9` |
| `b68f33f2cdd058da` | SR-24 Street Scout | `93ad3833` |
| `84fd7aab7cf6f62d` | SR-64 Cinderblock | `735a0c0b` |
| `5eb938fd7f03bcd8` | SS-15 “焦点” | `8f8192c1` |
| `2600aebfd2e3ebc0` | SS-33 “拟态元帅” | `a0350465` |
| `f6e9a778084c023a` | SS-500 “苍白检察官” | `7462879a` |
| `8517a93d6e19a56e` | TG-122 Demo-Trooper | `c1611ac9` |
| `bf06d78a228c3904` | TG-8 Sharpshooter | `0b66fc55` |
| `7c3496a468bf0028` | TR-117 Alpha Commander | `2f51d8dc` |
| `c19663a7f34f88fc` | TR-40 Gold Eagle | `f9d912a7` |
| `a0ce7d80e81fc62e` | TR-7 Ambassador of the Brand | `35e8934f` |
| `7cec1f264535f3c8` | TR-9 Cavalier of Democracy | `eeb4c2de` |
| `fb025c06b0408e5f` | UF-16 Inspector | `d270fccc` |
| `15dcd137ebe874ed` | UF-50 Bloodhound | `0e3b10bf` |
| `292b09dac80ce1de` | UF-84 Doubt Killer | `05720b46` |
| `88b7d737b6e62cc0` | UF-84 Doubt Killer | `12832e96` |
| `0845a15bbb31dcc6` | — | `782087b2` |
| `0d7bc50b425c1daa` | — | `44d93620` |
| `0e220545432c3676` | — | `4452e375` |
| `1523cd5b1924c862` | — | `52045a9b` |
| `155c93418f2c6a72` | — | `126f9cb6` |
| `16bf8d02fa2801b8` | — | `ede9d6b7` |
| `1a9e30964b6b9812` | — | `fe3437e9` |
| `202af54b4fe5dd85` | — | `8ce73dae` |
| `27d27e14b4e66f50` | — | `ee7019ed` |
| `304313b88483ef47` | — | `b6a2f46a` |
| `32a6a7761c70c19b` | — | `fdbf4741` |
| `3cd03a1159968eb5` | — | `e207c84a` |
| `3ee85c0f44ad92cc` | — | `10707d29` |
| `3fc1a4041b22131e` | — | `f6ae9439` |
| `4214b099ad2458bd` | — | `9b5bc520` |
| `52b63b45c7a6dbac` | — | `0c0af708` |
| `5dc26abb2113cda5` | — | `4db09ca4` |
| `5fdb046d58a7c28b` | — | `2d61f0f9` |
| `687636554fbf6a46` | — | `95ac1cfd` |
| `770f67630b1b3292` | — | `68aa2d85` |
| `782350ebeb6de6d8` | — | `7451baa3` |
| `8008eb7a0eccbcc1` | — | `36456057` |
| `8676515e7b30ed3d` | — | `e417ae7b` |
| `8978807640a47498` | — | `4f6ccd6c` |
| `89f78c9443bffe9c` | — | `5c1a87d2` |
| `9a37b0e0df207c60` | — | `843df174` |
| `9fcde56b5a3667f2` | — | `37c61642` |
| `ac3d44f70c5fa23b` | — | `7fa29b61` |
| `adabc9debb6e2bfc` | — | `a2f4f328` |
| `aee084ead49d7542` | — | `db7db6aa` |
| `bb23cba9b7524c91` | — | `e406ced0` |
| `bbaf861ceb582ef6` | — | `2180eb8d` |
| `c0dc0a7c8fc55214` | — | `7b6406da` |
| `c926637abbe0eaf1` | — | `a57e78ba` |
| `cceca27fd7bc742e` | — | `8507bb35` |
| `cd08c722235e75cc` | — | `b638f3c4` |
| `e0491eb4fb357d2a` | — | `40a7d694` |
| `e4a1da534bac818e` | — | `0f41a67e` |
| `e5fe990d7d56cd2e` | — | `69b2ec39` |
| `e9809fc724be99c6` | — | `d915a671` |
| `ee8dcc850d7a793b` | — | `020a57a8` |
| `f85a77eace8afa7a` | — | `0542e6f1` |
| `fe98ed69982d30b3` | — | `3313a0ec` |

</details>
