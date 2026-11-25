# W38 · IAS 拖入缺包提示

在 `feat/ias-module` 的 `5869262` 上完成。改前和改后都把 W34 A 组的两个 ZIP 复制到 `tmp/w38/`，通过模组库的 `ImportQueueService` 和 `ModLibraryImporter`、IAS 页的 `IasModuleService.AddAsync` 实测。IAS 使用已启用的沙盒配置，模组库使用空库。实测入口止于服务层，界面提示渠道通过调用路径核对。全程未启动图形客户端，也未部署到游戏。

## 四种单包拖入

| 页面与输入 | 改前实测 | 改后实测 |
| --- | --- | --- |
| 模组库 · 只有主包 | 队列按普通归档处理，未进入 IAS 转换。库里新增 `Bremerton W34 (IAS)` 普通 Mod 和 `Bremerton W34` 选项。 | 队列标记失败并跳过导入，没有新增 Mod。消息中心提示「缺少 IAS 配套包（.hd2irpack.zip），请和主包一起拖进来。」 |
| 模组库 · 只有配套包 | 队列按普通归档处理，导入器抛出 `No importable patch or decoration package was found.`，没有新增 Mod。原页面会把该错误放进导入失败弹窗。 | 队列标记失败并跳过导入，没有新增 Mod。消息中心提示「这是 IAS 配套包（hd2irpack），需要和同一个 Mod 的主包一起拖进来。」 |
| IAS 页 · 只有主包 | 「添加 IAS 角色失败：每次请添加一组 IAS 主包和配套包（找到主包 1 个、配套包 0 个）。」角色数为 0。 | 返回「缺少 IAS 配套包（.hd2irpack.zip），请和主包一起拖进来。」角色数为 0，显示于页内结果和消息中心。 |
| IAS 页 · 只有配套包 | 「添加 IAS 角色失败：每次请添加一组 IAS 主包和配套包（找到主包 0 个、配套包 1 个）。」角色数为 0。 | 返回「这是 IAS 配套包（hd2irpack），需要和同一个 Mod 的主包一起拖进来。」角色数为 0，显示于页内结果和消息中心。 |

## 识别依据

- 主包须有可识别的 patch 主文件，且它所在的包或拖入目录名去掉扩展名后等于 `IAS_MainMod`，或以 `_IAS_MainMod` 结尾，忽略大小写。打包服务和脚本固定产出 `IAS_MainMod.zip`，既有部署测试使用 `Sample_IAS_MainMod.zip`。W34 A 样本同时包含 `Files/2a7c626239a5d6b4.patch_0`、`Files/89395c2f8ce3d8a0.patch_0` 和 `Files/89395c2f8ce3d8a0.patch_2`，清单描述为 `Model resources for the accompanying IAS Companion.`。 [打包服务](../../HD2SourceMigration/IasPackaging/IasPackagingService.cs#L11) · [打包脚本](../../HD2SourceMigration/IasPackaging/Scripts/package_model.py#L565)
- 通用目录名 `main` 只在已有配套包时参与配对。无明确 IAS 命名的来源继续走对应页面的普通 Mod 路径。清单描述在本次只用于核实样本，识别代码使用包名和 patch 主文件。
- 配套包沿用 `mod_group.json` 文件标记。W34 A 配套 ZIP 的根目录含该标记，`HD2IndependentRig/` 下有 `.rigbin` 和 `.hd2phys`，ZIP 内无 patch 主文件。 [检测器](../../HD2ModCore/Ias/IasPackageDetector.cs#L68) · [解包和扫描](../../HD2ModCore/Ias/IasDropWorkspace.cs#L82)

## 改动与回归

- **done · 模组库**。同批配对先执行，再给剩余的半包标记失败。Shell 使用现有消息中心并跳过导入和转换。含多个普通主包候选、无法唯一配对时，配套包给出提示，普通候选继续进入原有导入流程。 [批次路由](../../HD2ModCore/Ias/IasStandardDrop.cs#L52) · [导入队列](../../HD2ModManager/Services/ImportQueueService.cs#L45) · [消息中心入口](../../HD2ModManager/ViewModels/ShellViewModel.cs#L1169)
- **done · IAS 页**。识别出缺包后跳过添加事务。单一半包直接返回页内结果，混合批次继续处理独立来源，缺包信息随结果显示。拖入沿用既有的无确认队列。 [IAS 添加流程](../../HD2ModManager/Services/Ias/IasModuleService.cs#L433) · [IAS 页拖入入口](../../HD2ModManager/ViewModels/IasPageViewModel.cs#L344)
- **done · 回归**。测试覆盖两页的两种单包、同批双 ZIP、外层 ZIP 内的一对、配套包与泛名主包的既有配对、普通 patch/VPK/ZIP、只有通用目录名的 `main`、泛名主包、无 patch 的 `IAS_MainMod.zip`、以及一批失败后下一批继续处理。IAS 配对经过测试部署服务并创建一个角色，普通 patch 转换用沙盒 SDK 验证进入原有转换分支。模组库的配对测试验证路由和转换入口。 [模组库测试](../../HD2ModCore.Tests/IasStandardDropTests.cs) · [IAS 页服务测试](../../HD2ModCore.Tests/IasDeploymentTests.cs#L15)

## 构建和测试原文

- **done · Debug**。`已成功生成。`、`0 个警告`、`0 个错误`，退出 0。并行 MSBuild 在沙盒中停滞，沿用 W34 的串行目标执行 `dotnet build WpfApp1.sln -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -p:DirectorySolutionTargetsPath=<工作树>\tmp\w34\serial.targets -t:W34SerialBuild -p:BuildInParallel=false -p:UseSharedCompilation=false`。
- **done · 全量 .NET**。六个测试项目分别执行 `dotnet test <项目>.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false`。合计通过 2966、失败 0、跳过 1，共 2967。Core 较给定基线增加 9 项。下表保留各项目输出的计数原文。

| 项目 | 结果 |
| --- | --- |
| HD2SourceModel.Tests | `失败:     0，通过:    72，已跳过:     0，总计:    72` |
| Decryptor.Tests | `失败:     0，通过:     5，已跳过:     0，总计:     5` |
| HD2ModAdaptation.Tests | `失败:     0，通过:   212，已跳过:     0，总计:   212` |
| HD2SourceMigration.Tests | `失败:     0，通过:   595，已跳过:     1，总计:   596` |
| HD2ModCli.Tests | `失败:     0，通过:   213，已跳过:     0，总计:   213` |
| HD2ModCore.Tests | `失败:     0，通过:  1869，已跳过:     0，总计:  1869` |

- SourceMigration 首跑为 `失败:     1，通过:   594，已跳过:     1，总计:   596`。内嵌模板用例从工作树中的 TEMP 向上查找时命中了 SDK 模板。临时盘符映射被系统拒绝，随后把工作树内十个 SDK 材质模板暂存到 `tmp/w38/`，复跑整套通过，再逐项还原并核对 SHA-256。跳过项为既有的 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。
- **done · Lua**。运行 `python tools/physbone-harness/run_physbone_regressions.py` 时 PATH 中没有 Lua 5.1 解释器。通过临时适配脚本调用本机已有的 Lupa LuaJIT 2.1，原 harness 输出 `合计 PASS=486 FAIL=0；红=0`，退出 0。两项外部夹具按 harness 既有规则跳过。本轮没有安装工具。
- 改前和改后实测使用的临时探针已从测试代码移除。两次并行探测的 130 个任务子进程均已退出。移除辅助脚本后复跑专项测试，输出 `失败:     0，通过:    14，已跳过:     0，总计:    14`。
- **partial · 沙盒清理**。自动命令审查拦截了递归删除 `tmp/w38/` 的操作。该目录仍有样本副本和构建、测试生成的临时文件。

## 待决

- 现有跨 ZIP 配对按候选和命名判断，同批里有多个候选时缺乏可靠的同一 Mod 身份证据。本次沿用既有配对规则。泛名或改名主包若要在单独拖入时识别，需要稳定的主包标记或更多发布样本。
- 游戏内骨架表现和 GUI 实际显示未启动客户端验证。本次验收到服务层导入、转换路由及结果文案。
