# W52 · IAS 安装与角色列表交付报告

工作树 `D:\gitCode\HD2ModManager-ias-install`，分支 `feat/ias-install-ux`，HEAD `a4de545`。改动留在工作区。测试使用工作树内沙盒，截图使用 WPF 离屏宿主和假数据。 [确认稿](../../tmp/w52/mockup.html) · [用户决定](ias-install-conflict-issues-20261126.md) · [实施计划](ias-install-ux-w52-plan-20261126.md)

## 按需求交付

| 项 | 状态 | 实现与边界 |
| --- | --- | --- |
| 1. 角色分组 | done | 按官方启用状态分为「已启用」和「未启用」，两组都显示计数和说明。未启用的冲突角色常驻黄色 Archive 占用提示和「换成这个」。装备名称来自护甲索引，索引缺项时显示 Archive 标识。 |
| 2. 打开冲突角色 | done | 开关与「换成这个」共用同一队列入口。事务锁内读取官方占用，依次停用冲突角色、启用目标、校验状态，再生成一次 data。结果条和消息中心记录目标、被停用者及共享装备。在生成或部署失败时回滚官方状态、Mod 库、配置和运行文件。沿用 W32 的官方冲突错误翻译。 |
| 3. 重拖同一角色 | done | 使用官方配套组 ID 判定身份，比对主包、配套展开后的相对文件名和 SHA-256。相同内容提前返回，结果条提示、对应行闪亮并刷新一次。不同内容在事务中替换配套和主包，沿用角色名、库节点 ID、启用状态和可对应的选项及材质设置。「刚更新」显示到离开该页。 |
| 4. 新包抢占 | done | 新 IAS 包先以停用状态登记。发现启用角色占着 Archive 时照常写入库，黄色结果条给出占用者和「换成这个」，后续显式操作再换。 |
| 5. 库内发现与登记 | partial | 识别未登记的「IAS 角色」节点，以及有主包与有效配套清单的普通导入节点。显示来源、黄色标签、登记忙碌态和「在库里查看」。可读取已有 IAS 源目录中可按主包 GUID 唯一配对的配套。配套齐全时，开关登记原库节点并启用，同时走 Archive 自动换。缺配套的旧角色保留在列表并提示重拖，重拖回收同一主包与选项 ID。已登记且 Mod 库文件缺失时保留登记和原启用状态，显示「库里已删除」。 |
| 6. 刷新 | done | 按钮在角色标题右侧，扫描中转圈并禁用刷新按钮，完成显示本地同步时间。进页、返回 IAS 页、库快照变化时自动扫。扫描排在 IAS 写事务之间，后台读取库索引、清单、目录及有限文件头。手动结果进入结果条，库变更产生的差异进入消息中心。扫描识别新增、改动、缺失和恢复，应用工作由用户触发。 |
| 7. 提示方式 | done | 冲突、重拖、登记及刷新使用结果条、行内按钮、消息中心。以上入口均可直接执行。 |

## 改动文件

- IAS 核心使用 [IasDeploymentService.cs](../../HD2ModCore/Ias/IasDeploymentService.cs)、[IasModTransaction.cs](../../HD2ModCore/Ias/IasModTransaction.cs)、[IasCharacterPackage.cs](../../HD2ModCore/Ias/IasCharacterPackage.cs)、[IasLibraryScan.cs](../../HD2ModCore/Ias/IasLibraryScan.cs)。更新在既有 IAS 租约与 Mod 库写租约内提交，快照覆盖官方登记、角色源、受影响节点及配置。相同重拖在创建快照前完成，事务暂存随成功、失败或取消清理。
- 官方读取使用 [IasProcessWorker.cs](../../HD2ModCore/Ias/IasProcessWorker.cs) 和 [Program.cs](../../HD2IasWorker/Program.cs)。刷新将 worker 与日志放在一次性目录，从官方库读取清单和 Archive 归属头。启用和安装仍使用官方完整占用校验。
- 管理器使用 [IasPageView.xaml](../../HD2ModManager/Views/IasPageView.xaml)、[IasPageViewModel.cs](../../HD2ModManager/ViewModels/IasPageViewModel.cs)、[IIasModuleService.cs](../../HD2ModManager/Services/Ias/IIasModuleService.cs)、[IasModuleService.cs](../../HD2ModManager/Services/Ias/IasModuleService.cs)、[FakeIasModuleService.cs](../../HD2ModManager/Services/Ias/FakeIasModuleService.cs)、[ShellViewModel.cs](../../HD2ModManager/ViewModels/ShellViewModel.cs)。库快照通知接到 IAS 页，库内查看使用现有导航、筛选和选中机制。
- 测试位于 [IasDeploymentTests.cs](../../HD2ModCore.Tests/IasDeploymentTests.cs)、[IasPageViewModelTests.cs](../../HD2ModCore.Tests/IasPageViewModelTests.cs)、[IasLibraryPickerRenderTests.cs](../../HD2ModCore.Tests/IasLibraryPickerRenderTests.cs)。覆盖冲突时停用后安装、交换顺序及回滚、两种启用状态下的同内容跳过和不同内容更新、更新后保留材质设置、两类库内角色的登记、旧主包及选项 ID 回收、物理文件变更和删除、分组、一次刷新与闪亮、登记忙碌态、刷新差异及离屏布局。

## 构建与测试原文

本会话直接执行解决方案的默认 Build 入口时，MSBuild 返回退出码 1，输出为「生成失败。0 个警告，0 个错误」，诊断日志显示 MSBuild 子任务失败且没有具体错误。改为从同一解决方案显式依次构建六个测试目标，依赖图覆盖解决方案全部 15 个工程。 [默认入口日志](../../tmp/w52/solution-build.log) · [显式目标构建日志](../../tmp/w52/build-targets.log)

```powershell
dotnet build WpfApp1.sln -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false -p:UseSharedCompilation=false -t:'HD2ModCore_Tests;HD2ModCli_Tests;HD2ModAdaptation_Tests;Decryptor_Tests;HD2SourceMigration_Tests;HD2SourceModel_Tests'
```

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:14.27
```

对六个工程逐个执行 `dotnet test <工程>/<工程>.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false`。以下是各工程的汇总原文。 [Core](../../tmp/w52/HD2ModCore.Tests.log) · [CLI](../../tmp/w52/HD2ModCli.Tests.log) · [Adaptation](../../tmp/w52/HD2ModAdaptation.Tests.log) · [Decryptor](../../tmp/w52/Decryptor.Tests.log) · [SourceMigration](../../tmp/w52/HD2SourceMigration.Tests.log) · [SourceModel](../../tmp/w52/HD2SourceModel.Tests.log)

```text
已通过! - 失败:     0，通过:  1882，已跳过:     0，总计:  1882，持续时间: 38 s - HD2ModCore.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 6 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 151 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 14 ms - Decryptor.Tests.dll (net8.0)
失败!  - 失败:     1，通过:   596，已跳过:     1，总计:   598，持续时间: 4 s - HD2SourceMigration.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 34 ms - HD2SourceModel.Tests.dll (net8.0)
```

SourceMigration 失败的是 `Hd2MaterialTemplateTests.EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists`。它从 `TEMP` 向上找模板，而 W52 沙盒必须放在工作树内，因此找到了仓库跟踪的 `tools/ref/HD2SDK-CommunityEdition/materials`。断言原文为 `Expected: "embedded"`、`Actual: "D:\\gitCode\\HD2ModManager-ias-install\\tool"···`。跳过的是既有三角绕序诊断用例。

Lua 原命令在 PATH 中找不到独立解释器，使用本机已有的 Lupa 2.8 LuaJIT 2.1 模块作为子进程解释器运行原 harness。正式运行将 `TEMP`、`TMP`、`LOCALAPPDATA` 和 `APPDATA` 指向工作树沙盒。两条依赖外部夹具的测试按 harness 规则跳过。 [桥接脚本](../../tmp/w52/run-lua-harness.py) · [Lua 原文日志](../../tmp/w52/lua.log)

```text
合计 PASS=486 FAIL=0；红=0
```

用只读参考树的 IAS 1.6.4 DLL、登记和 CAS 夹具在 W52 沙盒内实测 worker，人工将一个条目在沙盒中标为停用。 [对照日志](../../tmp/w52/official-archive-check.log)

```text
IAS 1.6.4：读取 4 个登记项（含 1 个停用），与官方 Archive 归属一致，登记清单 SHA-256 保持一致。
```

验证期间使用现有 .NET SDK、缓存 NuGet、现有 Python 与 Lupa。SDK 首次初始化打印开发证书提示，只读检查当前证书存储可见开发证书的签发起始时间为 2026-07-29。后续命令显式禁用 SDK 的证书生成。Lua 桥接初次尝试保留了默认日志环境，正式复验重定向了上述四个目录。

## 截图

两组列表与冲突黄字行

![已启用、未启用及冲突提示](../../tmp/w52/screens/groups-and-conflict.png)

同内容重拖的黄色结果条与角色闪亮

![同内容重拖](../../tmp/w52/screens/duplicate-drop.png)

库里有、未启用的普通导入和遗留角色

![库内 IAS 角色](../../tmp/w52/screens/library-roles.png)

刷新扫描中

![正在扫描 Mod 库](../../tmp/w52/screens/refresh-scanning.png)

刷新完成

![刷新完成与同步时间](../../tmp/w52/screens/refresh-complete.png)

## 与设计稿的差异

- 实现沿用管理器 WPF 的字体、间距和 IAS 本体卡片结果槽。真实游戏装备名以现有索引为准，预览图缺失时使用管理器占位图。
- 「刚更新」不带演示版的 `v2` 后缀。检测身份和内容的依据是官方组 ID 和文件内容，包版本在该界面没有可靠的人读值。
- 手动刷新显示扫描发现的差异，配套换版和运行资源的再生成由重拖或登记操作提交。这样刷新的路径可以只读，且不会顺带修改启用状态。
- W53 负责场景①中的空闲护甲改绑、护甲选择面板。本批实现该场景在冲突时装好并保持停用的兜底。

## 待决

- 真实旧 Mod 库有只剩主包和角色标记的节点。已可显示这类节点，原配套能在当前 IAS 源中唯一配对时可直接登记；其余需要重拖原主包和配套。要从另一份管理器取回原配套，需要先指定该旧 IAS 根或包的位置。跨 Mod 共享的选项按原库关系保留。
- 若同身份更新把一个启用中的角色改到了被占用的 Archive，或新版主包的根结构无法映射到原节点，当前由事务错误提示并回滚。若要为这两类情况定义额外的迁移行为，需要确定选项和其他启用者的处理规则。
- SourceMigration 的内嵌模板回归需要一个祖先目录不含仓库 SDK 模板的临时沙盒，或单独调整该测试的隔离方式。

## 工作区留存

保留 `tmp/w52/screens`、构建与测试原文、Lua 桥接和官方读取对照日志。自动审批以「blocked by policy」拒绝 PowerShell `Remove-Item` 清理调用，构建及读取沙盒 `tmp/w52/runtime`、首次尝试的诊断日志、全量测试顺带生成的 W30 离屏产物 `tmp/w30` 留在本工作树。
