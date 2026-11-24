# W10 · IAS 启用时的冲突提示

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`。改动留在工作区。

## 页面行为

- IAS 已安装且托管 data 包在当前配置里时，IAS 页顶部常驻显示「IAS 已启用：请不要同时使用带物理的普通 Mod 和护甲映射，它们会和 IAS 冲突。」启用判断独立于官方源可用性、游戏版本检查和 MDL3 检查。
- 管理器物理模式启用时，警告条显示「关掉管理器物理」按钮。检测到启用的护甲映射或有效的隐藏规则时，追加「当前有护甲映射规则在用，建议在护甲映射页停用。」护甲映射和隐藏规则均只读。
- 安装、更新或加入当前配置成功后，页面刷新 IAS 状态和物理设置。若 IAS 已启用且物理仍开着，会弹出确认框。通过警告条按钮也会打开同一个确认框。操作完成时若已离开 IAS 页，返回后按刷新到的状态决定是否弹出。
- 确认框标题是「关掉管理器物理？」，正文是「IAS 和管理器自带的物理会冲突。关掉后，管理器物理 Mod 不再生效；以后可以在设置里重新打开。」，主按钮是「关掉物理」，次按钮是「先不关」。
- 确认后，再次核对 IAS 在当前配置里启用，调用设置页共用的保存及部署请求路径，并刷新已打开的设置页绑定。取消后物理设置维持原值。

## 改动文件

| 文件 | 改动 |
| --- | --- |
| `HD2ModManager/Services/Ias/IIasModuleService.cs` | 增加启用、物理和映射状态，以及关闭物理的操作。 |
| `HD2ModManager/Services/Ias/IasModuleService.cs` | 用 IAS 安装记录和当前配置查启用状态。用护甲映射工作区文档、托管部署开关及已生成的隐藏规则文件读取冲突状态。 |
| `HD2ModManager/Services/Ias/FakeIasModuleService.cs` | 假服务支持物理和映射状态。Debug 下使用 `HD2MM_IAS_FAKE=1` 及 `HD2MM_IAS_FAKE_STATUS=Installed+Physics+Mapping` 可呈现带两项附加提示的状态，也可以去掉任一后缀。用 `NotInstalled+Physics`、`UpdateAvailable+Physics` 或 `NotInProfile+Physics` 可覆盖操作成功后弹框的入口。 |
| `HD2ModManager/Services/Ias/IIasPageDialogs.cs`、`HD2ModManager/Views/IasPageDialog.xaml`、`HD2ModManager/Views/IasPageDialog.xaml.cs` | 沿用 IAS 对话框并支持自定义次按钮。 |
| `HD2ModManager/ViewModels/IasPageViewModel.cs`、`HD2ModManager/Views/IasPageView.xaml` | 加入常驻警告条，将关闭物理的确认接到操作完成后的状态刷新和警告条按钮。 |
| `HD2ModManager/ViewModels/PageViewModels.cs`、`HD2ModManager/ViewModels/ShellViewModel.cs` | 提取设置页原有的物理保存和部署请求为共享方法。设置页沿用原属性、部署条件及绑定通知。 |
| `HD2ModCore.Tests/IasPageViewModelTests.cs` | 增加 17 个用例，覆盖警告条组合、成功后才提示、确认或取消、以及离页完成后回到 IAS 页的行为。 |

## 验证

所有 .NET 命令带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。工作树沙盒用于临时目录和 CLI 缓存，还原使用本机已有 NuGet 缓存。使用环境已有 .NET、Python 和 Lua 测试依赖。

- 解决方案级 `dotnet build WpfApp1.sln -c Debug` 退出 1，原文是「生成失败。」「0 个警告」「0 个错误」。诊断日志显示失败发生在 MSBuild 调用项目引用的 `GetTargetFrameworks` 时，没有给出具体错误。解决方案级 `dotnet test ... --no-build` 未输出测试结果，已中止。两项分别用逐项目构建、逐项目测试完成验证。失败命令残留的 560 个 MSBuild 子进程按父进程和命令行核实后已结束。
- 管理器及六个测试项目逐项目 Debug 构建通过，覆盖解决方案全部项目。各构建的汇总原文均为「已成功生成。」「0 个警告」「0 个错误」。
- IAS 页面定向测试原文为「已通过! - 失败:     0，通过:    43，已跳过:     0，总计:    43」。以下为全量测试汇总，逐项目运行且无过滤。

```text
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 15 ms - Decryptor.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 146 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 6 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:  1729，已跳过:     0，总计:  1729，持续时间: 6 s - HD2ModCore.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   574，已跳过:     1，总计:   575，持续时间: 5 s - HD2SourceMigration.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 34 ms - HD2SourceModel.Tests.dll (net8.0)
```

合计 2,805 通过、0 失败、1 跳过。跳过项是已有的 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。

`python tools/physbone-harness/run_physbone_regressions.py` 退出 0，原文为「合计 PASS=924 FAIL=0；红=0」。`conform-stub-test.lua` 按 harness 现有规则标记为 `SKIP(缺夹具)`。

XAML 已由 Debug 构建编译，并核对警告条、两项可见性绑定和关闭按钮命令。按任务限制，未启动管理器、游戏或 IAS 图形客户端，因此未做界面目视验收和真实游戏部署。

## 待决

- 无需产品决策。解决方案级 MSBuild 命令的异常原因仍未定位，逐项目构建与全量测试结果如上。
- 工具策略拒绝清理本次验证沙盒。`tmp/ias-w10/` 保留测试缓存及构建、测试日志，进程诊断脚本已移除。该目录由 Git 忽略。
