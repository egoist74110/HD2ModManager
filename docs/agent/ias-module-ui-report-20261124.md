# W6 · IAS 页面实现报告

工作树为 `D:\gitCode\HD2ModManager-ias-ui`，分支 `feat/ias-module-ui`，起点 `3eed986`。依据为《IAS 页面设计（2026-11-24）》和上位规划 §0、§1。改动留在工作区。

## 设计稿对照

| 条目 | 状态 | 实现与核对 |
|---|---|---|
| §1 顶栏和导航 | done | 顺序为「护甲映射」「IAS」「设置」。IAS 使用指定盾牌路径和提示「HD2 综合护甲系统」。补齐页面类型、模式、模板、页面创建、激活状态、历史回放和工作区标题。 |
| §2 页面与样式 | done | 照设置页使用平滑滚动、卡片间距及标题和按钮资源。两张卡片显示指定标题、说明和动作，角色为空时列表占高为零。 |
| §3 状态 | done | 后端状态按表映射颜色、标题、说明和唯一主按钮，涵盖官方源不可用的已装与未装两种情况。进行中的操作使用蓝色转圈与进度，取消按钮由 `CanCancel` 控制。 |
| §3 更新与卸载 | done | 更新说明默认收起且正文可滚动，最大高度 220。安装和更新显示下载来源提示。已装状态提供卸载入口，确认窗口的复选框默认未勾选。官方页面使用后端 URL，并提供官方 Releases 默认值。 |
| §4 就绪与角色行 | done | 未就绪时卡片为 0.5 透明度，保留只读列表。角色图标为 48，空图由 `ImagePlaceholderConverter` 提供占位，按来源显示指定胶囊标签。 |
| §4 拖放 | done | 角色卡片内任意位置可作为投放点。虚线为 `4 3`，圆角 8，内边距 16，高度随角色数取 72 或 180。拖入使用强调色和 8% 背景，四类提示和拒绝效果按稿实现。IAS 页面根元素在预览事件中标记 `Handled`，包括卡片外或不就绪的拒绝场景。`MainWindow_Drop` 保持原样。 |
| §4 从库选择和角色操作 | done | 选择窗口提供 40 像素缩略图、名称、类型、搜索和单选。转换及卸载角色先确认。转换结果能在库快照解析到 Mod 时显示「去看看」，用现有单页导航打开详情，否则仅显示结果。 |
| §5 串行、刷新、取消 | done | 页面操作和刷新串行。初次进入、再次进入和操作结束刷新状态及角色。离页取消刷新，活动安装继续，离页期间结束的操作在返回时刷新。忙时页面内动作禁用，进度轮到可取消阶段时开放取消。 |
| §5 消息和警告 | done | 成功结果显示绿色并在 6 秒后消失，失败结果使用后端错误文本并保留到下一次操作。两者都发消息中心通知。警告数附于结果文字，展开后可看列表。 |
| §6 接口及服务 | done | 接口、枚举和记录与稿中代码块一致。Debug 且 `HD2MM_IAS_FAKE=1` 时使用进程内演示，正式构建显示「IAS 功能尚未接通」。演示安装约 3 秒且可取消，角色增删、转换和 IAS 卸载均在内存完成。 |
| §7 范围 | done | IAS 接入集中在新增页面和服务目录，现有生产文件仅改导航接线。真实 IAS 下载、部署、转换、外观预选及 IAS 设置均不属于 W6。 |
| W6 单测 | done | 新增 22 个用例，覆盖状态表和忙碌态、全部拖放提示、失败文本与通知、卸载复选框的选中／不选／取消、离页取消刷新及安装继续。单测通过注入接口替换弹窗。 |
| W6 交付门 | partial | 全部 14 个项目逐项通过 Debug 构建，均为 0 警告、0 错误。全量测试实际有 1 条材质模板用例受工作树内临时目录影响而失败，见「待决」。按要求由派活方做 WPF 界面检查。 |

### 稿内差异与实现选择

- §4 的交互段称 `ClassifyAsync`，§6 定义的是同步 `Classify`。实现保留 §6 原接口，在拖动期间缓存文件列表与分类，离开页面拖放目标或投放时清理缓存。混合投放将完整路径列表交给 `AddAsync`，由服务跳过并返回警告。
- §3 表格中的 `Busy` 由前端根据进度呈现，§6 的后端枚举原样保留。服务首次报告进度前短暂显示「正在处理…」。下载大小用 MB 格式化，字段缺失时显示「未知大小」。
- 弹窗沿用 `AppDialog` 的父窗口居中、表面资源、边距和按钮顺序。搜索过滤名称和类型。普通动作复用次按钮，底部链接复用安静按钮，状态使用已有 `StatusSuccess`、`StatusWarning`、`StatusDanger` 和 `StatusInfo`，灰色按稿用 `#8B949E`。
- 模拟转换只生成成功结果文字，库中无可打开的实际 Mod 时按稿隐藏「去看看」。正常适配器返回 `CreatedModId` 后，页面按现有库快照查名称并开放详情跳转。模拟安装被取消后刷新页面，取消本身不发失败通知。
- 模拟状态由首次进入 IAS 页时读取的 `HD2MM_IAS_FAKE_STATUS` 决定。页面实例随 Shell 缓存，因此切页保留同一进程内的演示数据。
- 现有测试工程通过源码链接复用管理器的纯逻辑。Windows 下为实际测到 WPF 程序集中的 ViewModel，`HD2ModCore.Tests` 改用 `net10.0-windows` 并引用管理器，原来两个源码链接改由程序集提供。非 Windows 保留原 `net8.0` 及原有测试。

## 文件清单

### 新增

| 文件 | 内容 |
|---|---|
| `HD2ModManager/Services/Ias/IIasModuleService.cs` | 设计稿 §6 接口和类型。 |
| `HD2ModManager/Services/Ias/FakeIasModuleService.cs` | Debug 内存状态、分类、进度与操作结果。 |
| `HD2ModManager/Services/Ias/UnconnectedIasModuleService.cs` | 占位状态、默认 Releases 地址和 Debug 服务选择。 |
| `HD2ModManager/Services/Ias/IIasPageDialogs.cs` | 可替换的确认及库选择接口。 |
| `HD2ModManager/ViewModels/IasPageViewModel.cs` | 页面映射、操作和刷新生命周期、结果与通知。 |
| `HD2ModManager/Views/IasPageView.xaml` 和 `IasPageView.xaml.cs` | 两张卡片、状态和进度视图及 IAS 页作用域内的拖放。 |
| `HD2ModManager/Views/IasPageDialog.xaml` 和 `IasPageDialog.xaml.cs` | IAS 确认与单选窗口及弹窗接口实现。 |
| `HD2ModCore.Tests/IasPageViewModelTests.cs` | ViewModel 测试及服务和弹窗替身。 |
| `docs/agent/ias-module-ui-report-20261124.md` | 本报告。 |

### 修改

| 文件 | 各处修改原因 |
|---|---|
| `HD2ModManager/Enums/WorkspaceMode.cs` | 追加 `Ias` 供导航模式识别。 |
| `HD2ModManager/Enums/WorkspacePageType.cs` | 追加 `Ias` 供工作区页面类型与历史恢复。 |
| `HD2ModManager/Controls/Nav/NavItemDescriptor.cs` | 追加图标枚举，加入可选提示文字，默认仍读取标签，标签变化时同步通知默认提示。 |
| `HD2ModManager/Controls/Nav/NavBarHost.xaml` | 主导航模板绑定描述符的提示文字。 |
| `HD2ModManager/Themes/Controls.xaml` | 加入 `NavIcon.Ias` 盾牌几何资源。 |
| `HD2ModManager/MainWindow.xaml` | 增加 IAS ViewModel 到 View 的 DataTemplate。 |
| `HD2ModManager/ViewModels/ShellViewModel.Nav.cs` | 插入 IAS 按钮和提示，订阅 IAS 模式属性变化，更新 IAS 和设置的激活索引。 |
| `HD2ModManager/ViewModels/ShellViewModel.cs` | 导入 IAS 服务命名空间，新增页面缓存、入口命令和激活属性。补齐正反路由、页面工厂、槽位标题和模式通知。槽位更新时通知 IAS 页进入或离开，按常驻页规则缓存并于 Shell 销毁时释放。在库快照查找转换产物名称，用既有单页入口打开详情。 |
| `HD2ModCore.Tests/HD2ModCore.Tests.csproj` | Windows 目标直接引用管理器程序集，原先链接的公开类型由程序集提供。其他系统沿用现有目标和链接，仅排除依赖 WPF 的 IAS 测试文件。 |

## 验证记录

本轮未安装开发工具。验证使用的 `tmp/w6-ias-verification` 和工具临时生成的项目索引已清理。保留正常构建产物以便派活方检查。

直接执行 `dotnet build WpfApp1.sln -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false`，退出码 1，输出如下。诊断重试显示解决方案的 MSBuild 任务在并发调度子项目时结束，未记录更具体的错误。关闭还原并发后，14 个项目均完成还原，解决方案的 Build 任务仍以 `BuildInParallel=True` 调度并失败。

```text
生成失败。
    0 个警告
    0 个错误
```

随后按解决方案中的项目清单逐一执行 `dotnet build <项目> -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false -p:UseSharedCompilation=false`。14 个项目均退出 0，每个项目的汇总原文如下。

```text
已成功生成。
    0 个警告
    0 个错误
```

直接执行 `dotnet test WpfApp1.sln -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false`，退出码 1，未产生测试结果。随后逐一对解决方案中的六个测试项目执行 `dotnet test <项目> -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false -p:TestTfmsInParallel=false`，统计原文如下。

```text
已通过! - 失败:     0，通过:  1677，已跳过:     0，总计:  1677，持续时间: 5 s - HD2ModCore.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 126 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 6 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 15 ms - Decryptor.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 37 ms - HD2SourceModel.Tests.dll (net8.0)
失败!  - 失败:     1，通过:   565，已跳过:     1，总计:   567，持续时间: 7 s - HD2SourceMigration.Tests.dll (net8.0)
```

IAS 专项追加 `--filter 'FullyQualifiedName~IasPageViewModelTests'`，退出码 0。

```text
已通过! - 失败:     0，通过:    22，已跳过:     0，总计:    22，持续时间: 48 ms - HD2ModCore.Tests.dll (net10.0)
```

`python tools/physbone-harness/run_physbone_regressions.py` 退出码 0。已知夹具用例 `conform-stub-test.lua` 显示 `SKIP(缺夹具)`。

```text
合计 PASS=925 FAIL=0；红=0
```

额外核对了 §6 代码块与接口文件一致，以及 `git diff --check` 通过。本轮未启动管理器、游戏或 IAS 客户端，视觉、原生拖放路由和对话框交互待派活方检查。

## 用假实现启动

以下 PowerShell 命令供派活方执行。从工作树根目录运行，命令会复制 Debug 构建到一个新的 `tmp` 子目录，并写入独立的游戏数据和 Mod 库路径。

```powershell
$demo = Join-Path (Get-Location) ("tmp/ias-ui-demo-" + [Guid]::NewGuid().ToString("N"))
Copy-Item -LiteralPath 'HD2ModManager/bin/Debug/net10.0-windows' -Destination $demo -Recurse
@{
    GameDataFolder = (Join-Path $demo 'game/data')
    ModLibraryFolder = (Join-Path $demo 'mods')
    BuiltInLuaEnabled = $false
    AutoCheckGameDataIndex = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $demo 'settings.json') -Encoding utf8
$env:HD2MM_IAS_FAKE = '1'
$env:HD2MM_IAS_FAKE_STATUS = 'NotInstalled'
& (Join-Path $demo 'HD2ModManager.exe')
```

点顶栏「IAS」。可安装并观察取消，再用 `.zip`、名称含 `ias` 的 `.zip`、`.vpk` 或目录测试拖放，搭配 `.txt` 查看混合或全部拒绝的提示。启动前也可以把 `HD2MM_IAS_FAKE_STATUS` 改为 `Installed`、`UpdateAvailable`、`GameMismatch`、`MissingMdl3`、`NotInProfile`、`FeedUnavailable`、`Error` 或 `Checking`。状态开关在新进程中生效。

## 待决

- 全量测试共 2746 项，通过 2744、失败 1、跳过 1。失败为 `HD2SourceMigration.Tests.Hd2MaterialTemplateTests.EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists`。用例用 `Path.GetTempPath()` 创建起点，并假定祖先目录中没有 SDK 模板。工作树内的沙盒会向上找到已跟踪的 `tools/ref/HD2SDK-CommunityEdition/materials`，所以断言得到路径而非 `embedded`。尝试用临时盘符保留工作树内的物理路径时，`subst.exe` 返回 `Access denied`。需要派活方在祖先无 SDK 模板的授权临时目录复跑，或决定是否另修该用例的环境假设。
- §4 的拖放高亮、窗口布局和动画需要派活方按假实现命令做 WPF 界面确认。真实适配器仍由 IAS 后端任务接入。
