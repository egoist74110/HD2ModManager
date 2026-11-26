# W53 · IAS Mod 身体护甲换绑交付报告

工作树 `D:\gitCode\HD2ModManager-ias-install`，分支 `feat/ias-install-ux`，基线 `17fa424`。实现留在工作区。技术依据是 W51 的样本与官方回读，界面依据是 W52 确认稿场景①。 [实施计划](ias-retarget-w53-plan-20261126.md) · [W51 调研](ias-retarget-official-mod-research-20261126.md) · [W52 场景①](../../tmp/w52/mockup.html)

## 按需求交付

| 项 | 状态 | 实现与验证 |
| --- | --- | --- |
| 1. 换绑引擎 | partial | IAS 1.6.4 身体按体型、cell 和 variant 对齐，检查隔离、差分、节点数、原版 TOC SHA、骨及材质闭包。用目标 donor 重建隐藏点，调用 `PatchArchiveWriter` 写 patch，逐字节回读作者 Unit 的 TOC/GPU/stream。配套重算身份、CRC、revision、Pose 摘要、Preview 绑定与组 SHA。SDK、官方 Archive reader 和官方配套 Archive 回读通过后才可登记。两组真实产品包覆盖 shared v3，shared v2 和普通身体 Rig／Pose 实包回归待补。 |
| 2. 候选与占用 | done | 复用护甲目录的轻甲、中甲、重甲和名称顺序。索引缺失时使用已安装 IAS 身体目录。背景任务分析适配，结合官方启用登记显示空闲、占用者和拒绝原因。 |
| 3. 自动换绑 | done | 新拖入 IAS Mod 有纯身体冲突时，按目录顺序选第一件空闲且验证通过的身体，安装并启用。结果条和消息中心包含原护甲、占用者和目标。头盔冲突或没有可用候选时，原包停用登记，黄色结果条保留「换成这个」。 |
| 4. 手动换绑 | done | 已登记 IAS Mod 有灰底搜索面板，包括三组、占用者和「原护甲」标记。选择后直接进入队列。提交前复查占用和源指纹，停用目标护甲的占用者，替换两件套并 regenerate 一次。已注入生成失败并验证官方登记、角色表、模组库和运行文件回滚。 |
| 5. 身份与登记 | done | 原包标识、角色名称、主节点 ID、设置和来源关联同一条角色记录。官方身份随所选产物变化，替换时移除旧身份。换回时读取保存的原两件套。重拖同内容跳过，重拖新内容按当前目标准备，失败回原护甲。原护甲有冲突时停用登记。刷新按角色拥有的库节点过滤派生产物。 |
| 6. 署名 | done | 派生主包标「由管理器换护甲」，说明含原作者，角色行展示署名。失效的身体声明留在原件，头盔声明随头盔逐字节携带。Milfy 主包和官方配套回读均标识长啸云天。 |
| 7. 影响范围 | done | 普通 Mod 和求生 VPK 转 IAS 沿用原转换与占用路径，管理器外的 CLI 编译沿用既有入口。 |

## 模块与改动文件

- `IasBodyRetargetService.AnalyzeAsync` 与 `RetargetAsync` 接收 W52 已展开的 `main/` 和 `companion/` 目录。C# 处理原版和闭包校验、点网格、patch 与主包。配套脚本使用 AQ SDK 和 `format_v1`。现有 MSBuild 内容 glob 将脚本复制到 `data/resources/ias`，实际解释器是 IAS Generator 随组件分发的 `python/python.exe`。本机 Python 用于 Lua harness。使用已有工具，无新增安装。 [C# 引擎](../../HD2SourceMigration/IasPackaging/BodyRetarget/IasBodyRetargetService.cs) · [配套脚本](../../HD2SourceMigration/IasPackaging/Scripts/ias_retarget.py) · [分发声明](../../HD2SourceMigration/HD2SourceMigration.csproj)
- `IasDeploymentRetarget` 负责候选、原件、锁外准备和官方回读。提交复用 `CommitAddAsync` 与 `IasModTransaction`。新增 worker 回读命令和隔离读标记，换绑回读使用一次性目录。 [换绑编排](../../HD2ModCore/Ias/IasDeploymentRetarget.cs) · [数据模型](../../HD2ModCore/Ias/IasBodyRetarget.cs) · [事务接入](../../HD2ModCore/Ias/IasDeploymentService.cs) · [进程隔离](../../HD2ModCore/Ias/IasProcessWorker.cs) · [协议](../../HD2ModCore/Ias/IasWorkerProtocol.cs) · [官方 worker](../../HD2IasWorker/Program.cs)
- 管理器接入原版身体目录和现有 IAS 队列。 [候选服务](../../HD2ModManager/Services/Ias/IasBodyRetargeter.cs) · [模块服务](../../HD2ModManager/Services/Ias/IasModuleService.cs) · [界面契约](../../HD2ModManager/Services/Ias/IIasModuleService.cs) · [面板模型](../../HD2ModManager/ViewModels/IasArmorPanelViewModel.cs) · [页面模型](../../HD2ModManager/ViewModels/IasPageViewModel.cs) · [页面布局](../../HD2ModManager/Views/IasPageView.xaml)
- 服务回归使用模拟官方 worker。截图由 WPF 离屏宿主和模拟数据生成。产品对照探针使用从 W51 只读复制的夹具。内嵌材质测试以盘根下不存在的路径为查找起点，避开工作树的 SDK 模板。 [事务测试](../../HD2ModCore.Tests/IasDeploymentTests.cs) · [面板测试](../../HD2ModCore.Tests/IasPageViewModelTests.cs) · [离屏宿主](../../HD2ModCore.Tests/IasLibraryPickerRenderTests.cs) · [内嵌材质测试](../../HD2SourceMigration.Tests/Hd2MaterialTemplateTests.cs) · [产品探针](../../tmp/w53/probe/Program.cs)

## 构建与测试原文

指定命令 `dotnet build WpfApp1.sln -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false` 退出码 1。诊断跟踪到 Restore 的 `_FilterRestoreGraphProjectInputItems` 内层 MSBuild 调用失败，日志无子节点具体错误。关闭还原并行后还原完成，八节点 Build 出现相同无诊断失败。追加 `-m:1` 后通过同一解决方案默认 Build 入口构建 15 个项目。两个仅输出消息的子项目能独立复现八节点失败、单节点通过。 [指定命令日志](../../tmp/w53/build.log) · [诊断日志](../../tmp/w53/build-final-diagnostic.log) · [单节点日志](../../tmp/w53/build-single-node-final.log) · [最小复现](../../tmp/w53/msbuild-trace/parent.proj) · [多节点原文](../../tmp/w53/msbuild-trace/parallel.log) · [单节点原文](../../tmp/w53/msbuild-trace/serial.log)

~~~text
生成失败。
    0 个警告
    0 个错误
Default Build exit: 1

已成功生成。
    0 个警告
    0 个错误
~~~

逐工程执行 `dotnet test <工程>/<工程>.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false`。 [Core](../../tmp/w53/HD2ModCore.Tests.log) · [CLI](../../tmp/w53/HD2ModCli.Tests.log) · [Adaptation](../../tmp/w53/HD2ModAdaptation.Tests.log) · [Decryptor](../../tmp/w53/Decryptor.Tests.log) · [SourceMigration](../../tmp/w53/HD2SourceMigration.Tests.log) · [SourceModel](../../tmp/w53/HD2SourceModel.Tests.log)

~~~text
已通过! - 失败:     0，通过:  1888，已跳过:     0，总计:  1888，持续时间: 1 m 17 s - HD2ModCore.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 9 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 161 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 13 ms - Decryptor.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   597，已跳过:     1，总计:   598，持续时间: 6 s - HD2SourceMigration.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 39 ms - HD2SourceModel.Tests.dll (net8.0)
~~~

`python tools/physbone-harness/run_physbone_regressions.py` 在 PATH 中找不到独立 Lua 解释器。沿用 W52 的 Lupa LuaJIT 桥接运行原 harness，其中两条按原规则缺外部夹具。 [原命令日志](../../tmp/w53/lua-direct.log) · [Lua 回归日志](../../tmp/w53/lua.log)

~~~text
合计 PASS=486 FAIL=0；红=0
~~~

## 离屏截图

- [自动换至 BP-20 的绿色结果条、原护甲与署名](../../tmp/w53/screens/automatic-retarget.png)
- [三组手动面板与原护甲标签](../../tmp/w53/screens/retarget-panel.png)
- [头盔被占用的停用安装与「换成这个」](../../tmp/w53/screens/helmet-conflict.png)
- [换回 SR-64](../../tmp/w53/screens/returned-to-original.png)

## 产品包与 W51 对照

- BP-20 产物。 [主包](../../tmp/w53/test/Milfy-SR-64-to-BP-20/IAS_MainMod.zip) · [配套](../../tmp/w53/test/Milfy-SR-64-to-BP-20/IAS_Companion.hd2irpack.zip) · [官方登记](../../tmp/w53/official/BP-20-registration.log)
- AD-49 产物。 [主包](../../tmp/w53/test/Milfy-SR-64-to-AD-49/IAS_MainMod.zip) · [配套](../../tmp/w53/test/Milfy-SR-64-to-AD-49/IAS_Companion.hd2irpack.zip) · [官方登记](../../tmp/w53/official/AD-49-registration.log)

| 核对项 | BP-20 | AD-49 |
| --- | --- | --- |
| 官方 Archive 集合 | 身体 `e4e41c5f9c3848f0`、头盔 `84fd7aab7cf6f62d` | 身体 `74bc9cf98d65b1bf`、头盔 `84fd7aab7cf6f62d` |
| 身体 patch | 18 作者 Unit、14 个重建点 | 18 作者 Unit、12 个重建点 |
| 与 W51 的身体载荷 | 32 个资源的 TOC/GPU/stream 逐段相同 | 30 个资源的 TOC/GPU/stream 逐段相同，少 cell 23 的两个清理点 |
| 头盔、全局补丁与配套图标 | 对应文件 SHA-256 相同 | 对应文件 SHA-256 相同 |
| 物理和配套 | 对原件的 `format_v1` 结构比较仅 `unit_id` 改变。Rig、Phys、Pose、Preview、共享文件和 isolation 与对应 W51 产物逐文件 SHA 相同 | 同 BP-20 |
| 官方产物身份 | `sdkgroup.87f3badf98bd5012bd28b9d1dabd37e2` | `sdkgroup.2e6b9918fbe35d6284b6d75937a9da19` |

产品保留原主包的 `本体` 目录与选项 Include，W51 原型使用 `Files`。产品使用独立的确定性 GUID、名称、署名说明、固定 ZIP 时间和原组导出时间。FREE INI 去除空行，键值和行序与 W51 一致。patch writer 重排条目并可去重 GPU 载荷，按资源键核对三段载荷。官方登记指纹与原件不同，重读指纹一致。两版和原件同时启用均会触发原 SR-64 头盔冲突。 [产品对照原文](../../tmp/w53/product-readback.log) · [官方 worker 回读](../../tmp/w53/official/readback-passed.log)

两次 BP-20 生成的 ZIP SHA-256 相同。

~~~text
main      68AA02D3E5F5397A61F1475E8810CA6F85DAA5A8E6DD1180294CC216F310192E
companion B2105767EAD3B9786ABC7F462A7015AE4125C904CCBB3730C6021D8988D08A27
~~~

RS-40 因缺作者可见部件或差分槽被拒绝，RS-6 因体型合并／拆分被拒绝，A-9 因多出语义未经确认的原版槽被拒绝。GROUP、Morph、显式 Package/Composite、原版 Unit ID 覆盖和无法核验的格式或闭包均走拒绝路径。

## 与场景①的差异

- 头盔冲突采用任务条款中的黄色停用安装，「换成这个」触发 W52 交换。
- 场景稿手动提交会开启角色。实现按第 5 条保留目标角色的启用状态，手动选择占用项会停用面板列出的占用者。
- 面板按管理器目录和 WPF 主题显示。原护甲与当前护甲相同时从目标列表省略当前项。结果条入口和行内入口共用面板。
- 多个外观闭包无法唯一匹配的 Archive、多个身体组成员或多个身体 patch 首版会收到拒绝原因。

## 待决与工作区留存

- 指定八节点构建仍失败。单节点默认 Build 通过，八节点失败的底层原因待独立复现。
- 真实游戏里的两种体型、外套／鞋子组合、头身物理、换装和 F4 旧预设需现场实测。真实用户库上既有索引的官方替换及失败恢复由模拟事务覆盖，官方 DLL 实际登记使用空沙盒索引。
- 本次真实 Milfy 夹具为 shared v3。shared v2、普通身体 Rig／Pose 和其他合法的独立两件套需要各自实包回归。未验证的新增槽保守拒绝。
- 保留 `tmp/w53` 中的产品包、截图、探针、复制夹具、日志和沙盒。早期离屏回归按原测试默认路径刷新了 `tmp/w30/screens` 和 `tmp/w52/screens`，后续统一重定向到 `tmp/w53`。自动审批策略拒绝用于清理的 `Remove-Item`，Serena 初始化生成的三份本地配置留在 `.serena/`。
