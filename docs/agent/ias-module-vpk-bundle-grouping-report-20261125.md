# W29 IAS 整合包分组与材质解析

## 实施与验收计划

1. IAS 将同次拖入的 VPK 一并交给普通导入规划器。每组转换一次，安装前复核该组模型和所有材质来源。单组失败记录文件名和原因，随后继续处理。
2. IAS 和 CLI 使用同一个多 VPK 材质解析入口。按普通导入默认层序合成，记录实际读取的材质包、未读取的材质包及跳过理由。
3. 在 `tmp/ias-w29/` 用两个原始压缩包验证规划、打包与逐 Unit 读回，和普通转换对照。再跑 Debug 构建、全量 .NET 测试、Lua 回归与物理/Lua 工作区检查。

## IAS 拖入行为

- 同次拖入的独立 VPK、文件夹及压缩包展开后进入同一次 `VpkImportPlanner.Plan`。按 `Groups` 转换，模型和组内材质分别传入打包服务，每组出一对 ZIP。重复模型自动跳过，武器、道具及未归属材质沿用普通规划结果，在消息中心详情逐包显示。两个主体同名时，显示名加源目录名便于分辨。
- 仅有材质时提示「拖入的 VPK 只有材质包，没有角色模型。请将模型 VPK 一并拖入。」其他没有可转换主体的情况提示查看详情。无主体时直接返回，跳过 AQ SDK 与底子预检。
- 每组沿用 IAS 原有发布事务，发布前复核模型及所有材质的原始拖入项。单组失败记录源 VPK 路径和原因，其他组继续转换。有角色写入时任务成功。全部失败时首条错误进入任务摘要，其余错误仍可在详情中查看。取消继续沿用任务取消通道。
- `ias pack l4d2` 接受重复的 `--material-vpk <文件>`。模型 VPK 专管几何，各材质层与模型自带资源一起参与 VMT 和 VTF 查询，`HD2_L4D2_DIR` 或 `--l4d2-root` 对应的 `pak01_dir.vpk` 仍作为最后回退来源。普通导入规划器原样复用。`SourceMaterialSource.Open` 新增可选读取回调，供 IAS 区分实际读到的包和没有命中的包。原入口、索引与同名查找规则保持原语义。

## 材质与组件选项决策

普通管理器先按主体平铺源 VPK。同文件名按规划顺序复制，后复制的覆盖前一份。`VpkRebuildService.FindSourceVpks` 按文件名升序重构建，`VpkPayloadCollector.Collect` 按报告顺序收集材质 patch。无显式用户选择时，`MaterialOptionSettings.Read` 和 `OverwriteMaterializer` 默认全启用。材质可多选、可排序，后部署的层覆盖同键资源。普通管理器没有把源目录名中的「二选一」解释为互斥。

| 路径 | 证据与结论 |
| --- | --- |
| 生成可切换 IAS 选项 | 官方 Generator 的 `assembly_catalog.py`、`appearance_routes.py` 支持 `GROUP` 和 `FREE`。Eku 实样的外套与表情选择有预制的 Unit 选项可供切换，W28 已回读。当前 `package_model.py` 为每个域生成一组已绑定 Unit，材质资源隔离后统一写入 `patch_2`，主包 manifest 只有一个 `Files` 选项。Generator 按选出的 Unit 继续取材质及贴图，当前转换器没有可供它选择、叠加或重排的材质层。将同一 Unit 上的衣物、贴图层和隐藏开关直接映射为 GROUP/FREE 控件，无法保持普通导入的同键覆盖与独立多选语义。 |
| 按默认顺序合成 | 本次采用此路径。参与合成的 VPK 按普通导入的文件名顺序排列，再倒序交给现有的先命中优先索引。同文件名的材质来源保留列表最后一份。详情列出层序、命中的材质包、未命中的材质包，以及平铺时被同名覆盖的文件。 |
| `$no_draw` 组件开关 | IAS 的合法选项需要作者提供备选 Unit 和相应的绑定数据。普通路径用 section 掩码隐藏模型里的几何。当前 IAS 打包器只为默认几何生成绑定，无法为同一 Unit 上的多组重叠 section 开关生成等价选项。本次按规划器结果跳过，每个包都附原因，CLI 显式传入该类包时也从材质索引排除。 |

## 两份实样

用 7-Zip 读取 Downloads 的原始压缩包并解包到 `tmp/ias-w29/`。普通导入对照把每组来源平铺至沙盒，按文件名顺序调用 `vpk2patch` 并用 `--treat-as-list` 标记材质包。对照使用同一游戏数据、骨名表和 texconv，以及普通初次导入的 `--components merge --matcap metal`。普通转换的管理器根及资产索引是沙盒副本。

| 样本 | 规划与合成结果 | IAS 回读 |
| --- | --- | --- |
| 婚纱睡衣 | 10 个 VPK，1 个主体，2 个材质候选，7 个重复。油光层供给可见几何所用的材质与底色，neko 层未命中。 | 11 个可见 Unit，208 个可见 section，13 个源材质及底色路径与普通输出一致，16 份私有贴图资源，依赖闭包 101 节点，缺失 0。 [规划](../../tmp/ias-w29/wedding-plan.json) · [回读](../../tmp/ias-w29/audit-wedding-1.log) · [IAS 产物](../../tmp/ias-w29/ias-wedding-1/) · [普通转换报告](../../tmp/ias-w29/ordinary-wedding-1.json) |
| 八人包动画版 | 44 个 VPK 共同规划出 2 个主体，每组 4 个材质候选。全批 14 个重复模型及 24 个组件开关。 | 11 个可见 Unit，588 个可见 section，93 个源材质及底色路径与普通输出一致，22 份私有贴图资源，依赖闭包 187 节点，缺失 0。 [规划](../../tmp/ias-w29/eight-plan.json) · [回读](../../tmp/ias-w29/audit-eight-1.log) · [IAS 产物](../../tmp/ias-w29/ias-eight-1/) · [普通转换报告](../../tmp/ias-w29/ordinary-eight-1.json) |
| 八人包无动画版 | 两版均实际读取基础材质与油光层。两份同名 neko VPK 的 SHA-256 一致，平铺保留「角色无动画」目录内的一份，随后同名资源由后层覆盖，最终未读取这份 neko。 | 11 个可见 Unit，588 个可见 section，94 个源材质及底色路径与普通输出一致，22 份私有贴图资源，依赖闭包 188 节点，缺失 0。 [回读](../../tmp/ias-w29/audit-eight-2.log) · [IAS 产物](../../tmp/ias-w29/ias-eight-2/) · [普通转换报告](../../tmp/ias-w29/ordinary-eight-2.json) |

三次 IAS 打包和三次普通转换均退出 `0`。回读解码两个路径的实际补丁。IAS 逐 Unit 和 mesh 检查可见 section 的材质 ID 及源材质名，普通路径用实际 Unit、section 来源表和平铺后的材质层对照。三份 IAS 产物的可见 section 都绑定包内材质，引用能闭合到随包 BC7 贴图或游戏归档中的所需资源。官方 Generator 的视觉编译分别复制 13、93、94 份材质。婚纱样本有 1 个材质在源 VMT 里声明的法线 VTF 缺失，沿用转换器的平面法线降级。该缺失已在打包报告标明，生成包的材质依赖仍完整。

复现 IAS CLI 时，对每个 `modelGroups` 对象单独运行一条命令，模型归档与底子从参数传入，按规划结果为各个材质来源重复加参数。

```text
hd2mod ias pack l4d2 <模型 VPK> --game-data <data> --output <新目录> --ias <Generator> --aqsdk <SDK> --bone-hash <bonehash.txt> --body-archive <护甲归档> --helmet-archive <头盔归档> --material-vpk <材质 VPK A> --material-vpk <材质 VPK B> --json
```

## 写死项清单

| 项 | 来源 |
| --- | --- |
| Mod 参数、文件名、材质名、模型身份、装备归档 | 生产代码新增写死 0 项。分组来自规划器，材质来自各组归档，装备来自原有选择器或 CLI 参数，UUID 沿用打包服务生成规则。 |
| 固定规则与提示 | 新增 CLI 参数名、消息文案及普通默认叠加顺序。文件名去重与同名覆盖均适用于任意输入。 |
| 实样配置 | 两个压缩包名、DP-8 底子归档、游戏目录及工具位置只进入沙盒验证。回读脚本中的资源类型、补丁偏移和 authored 槽 ID 是格式常量。 |
| 工具 | 沿用本机 .NET SDK、7-Zip、Python 3.12、lupa 2.8、已有 AQ SDK 快照 `86b7252`、texconv 和官方 IAS 1.6.4 文件。未安装开发工具。 |

## 构建、全量测试与范围检查

解决方案级 Debug 构建在本机沙盒退出 `1`，原文为 `生成失败。`、`0 个警告`、`0 个错误`。解决方案级测试调用未输出测试汇总。两条路径均留下了 MSBuild 子节点。根据进程的父进程、启动时间及 `/nodemode:1 /nodeReuse:false` 参数核实任务归属后，终止了本任务留下的节点。改用 `-p:BuildInParallel=false -p:UseSharedCompilation=false` 逐项目构建，管理器、CLI、依赖和六个测试项目均覆盖到，原文为 `已成功生成。`、`0 个警告`、`0 个错误`。所有 .NET 命令都带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。

六个测试项目逐个执行完整测试。新测试用合成 VPK 覆盖单独拖入材质、跨拖入项分组及去重、组件开关排除、首组失败后续组成功、所有主体均失败。专项测试原文为 `已通过! - 失败:     0，通过:     4，已跳过:     0，总计:     4`。

| 测试项目 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1798 | 0 | 1798 |
| HD2SourceMigration.Tests | 0 | 595 | 1 | 596 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| 合计 | 0 | 2895 | 1 | 2896 |

`HD2SourceMigration.Tests` 的内嵌材质模板用例要求从临时目录向上找不到磁盘模板。首轮沙盒临时目录的祖先命中仓库模板，随后以临时隔离模板的方式重跑了整个项目。原目录已恢复，10 份模板哈希逐份一致。唯一跳过项是测试自身声明跳过的三角形绕序诊断。 [隔离复测](../../tmp/ias-w29/test-HD2SourceMigration-isolated.log)

`python tools/physbone-harness/run_physbone_regressions.py` 退出 `0`，原文为 `合计 PASS=486 FAIL=0；红=0`，两项外部夹具按回归脚本标记为缺夹具。对 `master`（`332f2d2`）检查 `HD2PhysBoneCompat/`、`tools/physbone-harness/`、`tools/ias-preview/`、共用 Lua 部署和内嵌文件、物理容器写入文件，差异为零。共用 `SourceRetargetPipeline.cs` 有 W29 开始前已存在的读取与解析计时扩展。 [Lua 回归日志](../../tmp/ias-w29/lua.log)

`git diff --check` 通过。工作区仍是 `feat/ias-module`，既有改动保留。验证只读访问原始压缩包和游戏数据，未启动游戏、管理器或 IAS 图形客户端。未提交、切分支或推送。产物、回读程序和日志留在沙盒。自动审批以 `blocked by policy` 拒绝了递归清理任务沙盒缓存和扁平源副本的命令，这些目录仍在 Git 忽略的 `tmp/ias-w29/`。

## 待决

- 沙盒中解决方案级单命令仍没有可用汇总。上表覆盖同一解决方案的全部六个测试项目。
- IAS 角色暂按普通默认材质层序生成一次，组件开关逐包提示跳过。若后续要支持运行时切换，需要为转换器设计能表达叠加顺序和多开关重叠区域的 Unit 变体生成方案。
