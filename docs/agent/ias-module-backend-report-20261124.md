# W8 IAS 后端接通（2026-11-24）

工作树 `D:\gitCode\HD2ModManager-ias-module`，分支 `feat/ias-module`。依据总规划 §0、§3–§6、页面设计 §3–§6、P0 原型及 W1、W4、W5、W6、W7 交接。管理器通过 IAS 官方流程部署护甲和角色数据。外观和 IAS 物理由用户在游戏内 F4 面板操作。

## 交付状态

| 项目 | 状态 | 结果 |
|---|---|---|
| x64 部署 worker | done | 反射绑定 IAS 1.6.4 原版 DLL，保护运行目录。原版 `status` 回读成功，错误 DLL 在调用任何业务方法前被拒绝。 |
| 部署、角色、快照 | done | 角色变更各全量生成一次。托管 Mod 保留 ID、库内目录及已有顺序。模拟 worker 和配置部署失败均验证了快照回滚。 |
| IAS 页与 CLI | done | UI 与 CLI 使用同一份适配器和 Core 服务。Release 接真实实现，Debug 环境开关可启用假实现。拖放状态文案与设计稿同步。 |
| 普通护甲与 VPK 入 IAS | partial | ZIP 或目录归一化后调用 W4/W7 公开 API。AQ SDK 支持固定提交下载和本地目录。W7 配套包已经单独通过官方验证，W8 安装链在官方 base 提交后才会走到该步骤。 |
| IAS 转普通 Mod | partial | UI 与 CLI 均从保存的角色源调用现有 DP-8 转换器并走导入预检。Milfy 单独转换产物通过预检。仅两份宿主的集成夹具缺少转换输入。 |
| 原版沙盒端到端 | blocked | 已运行全部 CLI 步骤并逐步记录清单及 SHA-256。原版 DLL 在 base `Apply` 回写事务记录的 `File.Replace` 被当前沙盒拒绝。 |
| 交付门 | done | Debug 解决方案 0 警告、0 错误，全量 .NET 测试 2,778 通过、0 失败、1 跳过，Lua 924 通过。 |

## 文件

- 新增 `HD2IasWorker/{HD2IasWorker.csproj,Program.cs,IasWorkerArtifacts.targets}`，并接入 `WpfApp1.sln`、`HD2ModCli/HD2ModCli.csproj` 和 `HD2ModManager/HD2ModManager.csproj`。
- Core 新增 `HD2ModCore/Ias/` 下的 `IasWorkerProtocol.cs`、`IasProcessWorker.cs`、`IasDeploymentService.cs`、`IasModTransaction.cs`、`IasFileTree.cs`、`IasCharacterPackage.cs`、`IasPackageDetector.cs`、`IasUpdateHttp.cs`、`IasAqSdk.cs`、`IasSlotAuthority.cs`。扩展 `IasStatusService.cs` 以同时给页面返回组件状态和签名清单结果。旧只读 CLI 状态入口维持原接口。
- UI 新增 `HD2ModManager/Services/Ias/IasModuleService.cs`，移除占位的 `UnconnectedIasModuleService.cs`。修改 `IIasModuleService.cs`、`FakeIasModuleService.cs`、`IasPageViewModel.cs` 和 `ShellViewModel.cs`。设计稿 §4 与 §6 同步。
- CLI 新增 `HD2ModCli/CliIasModuleCommands.cs`，修改 `CliApplication.cs`、`CliArguments.cs`、`CliIasConversion.cs`。旧版 `CliIasSlotAuthority.cs` 的权威槽位读取移到 Core 共享给两端。
- 测试新增 `HD2ModCore.Tests/IasDeploymentTests.cs`，更新 `IasPageViewModelTests.cs`。将 W4 的临时目录测试修复补入 `HD2SourceMigration.Tests/Hd2MaterialTemplateTests.cs`。测试从盘符根下的未创建路径执行模板定位，没有写盘符根。内嵌模板断言保持等价。
- 本轮续作未写 W7 转换器目录和用户列出的物理、Lua 文件。工作树里的 `LuaDeploymentService.cs` 仍可见中断前的临时 `preserveRuntimeRegistryFiles` 改动及 W1 改动，W8 调用方已改用现有构造参数，适用于派活方后续统一退回。

## Worker 协议与依赖

每个进程从 stdin 读取一个 UTF-8 JSON 对象，stdout 输出一个 `{ "Success": bool, "Value": string|null, "Error": string|null }`。成功退出 0，失败退出 1。进度、官方日志与异常堆栈走 stderr，调用方留档到本次操作的 `backup/<操作号>/worker-<命令>.log`。输入字段为 `Command`、`GameRoot`、`Components`、`Library`、`Backup`、`Export`、`Icon`、`Version`，按命令加 `Argument`、`Enabled` 和 `Temp`。调用进程使用 15 分钟超时，取消时结束进程树并等待退出，再恢复快照。

| 命令 | 官方调用与响应 `Value` |
|---|---|
| `prepare-base` | `PrepareSystem → Apply`，返回官方事务路径。 |
| `import` | `ImportToLibrary → SetLibraryEnabled(true)`，返回官方角色 ID。 |
| `remove` / `set-enabled` | `RemoveFromLibrary` / `SetLibraryEnabled`，成功返回 null。 |
| `regenerate` | `Create(mods:true) → Generate → Apply → ExportData`，返回导出的 ZIP 路径。 |
| `status` | `RuntimeDirectory` 和 `LibraryStatus`，返回含运行路径与 `HD2ModLibrary1` 记录的 JSON 字符串。 |
| `restore` | 校验事务的游戏根、运行根、备份目录和 `runtime_only`，然后调用官方 `Restore`。项目快照负责永久删除的恢复。 |

启动先校验 `app/HD2综合护甲系统.dll` 的 SHA-256 为 `5BC1D974A89FA7887933DD83DA9DDE019132E818C0502DC7EF3069FE853C504D`，再精确核对调用签名及读取字段的类型。Apply 前核对计划的运行根和 `RuntimeOnly`，准备对象用完调用 `Discard`。验收实际测得原版 `status` 退出 0，同位置的测试内容代替 DLL 时退出 1，错误为「IAS 版本不受支持：官方客户端 SHA-256 未通过校验。」。该状态检查也用 `DOTNET_ROLL_FORWARD=LatestMajor` 在 .NET 10 上执行通过。

- `WinRT.Runtime.dll` 由 `net8.0-windows10.0.19041.0` 的 SDK 引用包 `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57` 生成到输出，对齐官方 `.deps.json`，文件版本为 2.2.0.48161。CsWinRT 上游源码为 MIT，Windows SDK 引用包另带 Microsoft Windows SDK 许可。 [CsWinRT 许可证](https://github.com/microsoft/CsWinRT/blob/master/LICENSE) [Windows SDK 许可](https://aka.ms/WinSDKLicenseURL)
- `Microsoft.WindowsAppRuntime.dll` 由 NuGet `PackageDownload` 解析 `Microsoft.WindowsAppSDK.Foundation 1.8.251104000` 的 x64 原生资产。官方清单声明 Windows App SDK `1.8.250907003`、Foundation `1.8.250906002`。这里使用同一 1.8 系列的 servicing 版本，实际加载并调用原版 DLL 已验证。包内是 Microsoft Windows App SDK 软件许可，输出随附其 `license.txt`。构建通过 NuGet 引用解析文件，生产依赖没有本机路径或 P0 复制件。
- Debug 输出的 `ias-worker/` 和 Release 的 `bin/ias-worker/` 包含 apphost、worker DLL、两份 .NET 配置、Windows SDK DLL、两份初始化依赖及许可。管理器和 CLI 的 win-x64 框架依赖型发布均成功，管理器发布输出已逐文件核对。已有完整包布局的 `bin/dotnet` 存在时，启动器通过 `DOTNET_ROOT_X64` 传给已搬到 `ias/worker` 的 apphost。

## 状态、目录与事务

| 位置 | 内容 |
|---|---|
| `ias/components/<版本>/` | W5 验签、整包及部件校验后的激活版本。三个核心运行 ZIP 解到同版本 `expanded/`。 |
| `ias/worker/`、`ias/Runtime/<gameKey>/` | 分发目录中的 worker 被复制到前者。官方 API 用其父目录求 gameKey 运行目录。 |
| `ias/library/`、`ias/library/sources/<角色 ID>/` | 官方角色库及 W8 的角色到管理器节点的映射。保留标准化的 `main/`、`companion/` 和传入的原始 ZIP 字节。 |
| `ias/backup/`、`ias/export/`、`ias/temp/` | 按操作号保存官方事务、导出物和日志。worker 的 `TEMP/TMP` 指向该操作的暂存子目录。 |
| `ias/snapshots/previous/` | 安装/卸载时含组件目录，角色变更时含运行目录与角色库。运行快照覆盖整个 `Runtime`，包括当前 gameKey。按需要另备份受影响的管理器 Mod 目录及库、配置、选项激活文件。 |

事务依次取 IAS 针对管理器根的独占锁、现有 Mod 库写租约，以及部署时的 GameData 租约。先复制运行状态，再调用官方 worker 更新全部角色并导出。复用管理器导入预检和导入服务在 IAS 暂存库构建 Mod，随后用配置变更服务更新当前配置并调用现有配置部署。正常结束后清除快照。异常或取消时从备份恢复原始目录和配置文件，旧部署可识别时重放其配置。回滚成功的快照与标记保留至下次操作，诊断日志留在 `backup`。强杀造成的未完成快照会阻止下一次写入。

托管 data 包用 `managed-mod.json` 保存唯一节点 ID。重导入沿用该 ID、存储目录和原配置顺序。角色主包装进普通库。库目前没有持久化的用户自定义标签字段，W8 用现有 Mod 元数据存储承载 `[IAS 系统]`、`[IAS 角色]` 标识，系统条目显示名为「IAS 系统」。角色卸载时撤下其所拥有的节点及配置引用。用户另建的跨 Mod 选项关系会阻止删除，保留该关系供用户处理。

页面先判断 W5 组件和游戏版本，再判断当前配置的 MDL3 与托管 ID。MDL3 识别来自现有部署的 boot 归档及资源键，并核对 Mod 名或 Lua 载荷里的 MDL3、HD2 Data Hub 标记。只有普通 Lua addon 标记的补丁不会被当作加载器。更新来源沿用程序更新偏好和镜像策略，询问模式默认直连。AQ SDK 首次转换从官方 GitHub 固定提交 `86b7252` 获取，或用 `HD2_AQSDK_DIR` 传本地副本。分类只读后缀、目录与 ZIP 中央目录。每个真实样本 20 次本机调用的最高耗时为 Milfy 目录 6.17 ms、W7 主包 3.96 ms、W7 配套包 0.15 ms。

IAS 的配置部署为常规部署组合提供暂存的护甲规则目录，读取现有规则作为输入，避免往实际 `%LOCALAPPDATA%` 写规则。中断前的诊断尝试暴露了默认部署组合会在空配置仍尝试写 `kit-instance.rules` 的行为，沙盒拒绝该次写入。后续 W8 调用已隔离。当前配置已有映射/隐藏规则时仍需派活方验证真实游戏行为与后续主线回退兼容性。

## 沙盒端到端

最终构建复跑使用两个独立根，`tmp/ias-w8/e2e-verified/manager` 与 `tmp/ias-w8/e2e-verified/game`。默认空 Mod 配置有一个活动项。游戏目录起点仅复制以下两份宿主，值与 ArmorBase `base.json` 一致。

| 文件 | SHA-256 |
|---|---|
| `bin/helldivers2.exe` | `b434f6f326b7759978265f5863ef72e3c5edd4aa5315cc1ef4b640d204eae0bb` |
| `data/game/game.dll` | `08b756f535cdb9bdd1b4953fba53c3dfacd25730a155ea6593e5de63211acf5f` |

CLI 各步均带 `--manager-root`、`--game-root`、`--json`。离线参数读取给定的 `HD2IAS-1.6.4-offline-update.zip`，使用其 `feed.signed.json` 对内层 `release.zip` 和部件校验。内层 SHA-256 见下方日志。

| 步骤 | 退出 | 清单文件数 | 结果 |
|---|---:|---:|---|
| 初始 | — | 5 | 两份宿主、库、配置和设置。 |
| `install --offline-package …` | 2 | 18 | 清单验签、部件校验、版本闸、`PrepareSystem` 已通过。原版 `Apply` 将事务记录写到 `prepared` 后，更新记录时被拒绝。已回滚，运行树和组件激活指针均撤除。 |
| `add <Milfy sample>` | 2 | 18 | 「请先安装 IAS。」 |
| `add <W7 布莱默顿主包> <配套包>` | 2 | 18 | 「请先安装 IAS。」两文件作为一个直接 IAS 安装请求进入服务。 |
| `list` | 0 | 18 | `[]`。 |
| `to-standard sdkgroup.deca3070d87752dc9f291677f680b2c7` | 2 | 18 | 「找不到要转换的 IAS 角色。」 |
| `remove sdkgroup.deca3070d87752dc9f291677f680b2c7` | 2 | 18 | 「请先安装 IAS。」 |
| `list` | 0 | 18 | `[]`。真实双角色保留验收未达。 |
| `uninstall` | 0 | 17 | 「IAS 已卸载。」只覆盖回滚后的空态。 |

`evidence/00-initial.files.json` 至 `08-uninstall.files.json` 逐步记录路径、字节和 SHA-256，`results.json` 记录命令和退出码，另有原始 stdout/stderr。回读确认两个宿主、`library.json`、`profiles.json`、`settings.json` 的 SHA-256 在 `install` 回滚后及 `uninstall` 后均等于起点。追加文件为 worker、官方事务及诊断。 [步骤清单](../../tmp/ias-w8/e2e-verified/evidence/results.json)

```text
release.zip SHA-256: 541DC4341945B048651D16551E6EEEBC052E70CD63162758C6B52C07DCD25192
System.UnauthorizedAccessException: Access to the path is denied.
   at System.IO.FileSystem.ReplaceFile(String sourceFullPath, String destFullPath, String destBackupFullPath, Boolean ignoreMetadataErrors)
   at HD2RuntimeManager.Util.PublishPrepared(String temp, String path, Action replace, Action`1 wait)
   at HD2RuntimeManager.ExternalRuntimeDeployment.ApplyCore(ExternalRuntimePlan plan, String backupRoot, Action`1 log)
```

补充对照使用旧 `hd2mod ias convert` 读取完整游戏数据，输出写进工作树 `tmp/ias-w8/milfy-standard-control`。原文为「本体 20 个 Unit，四个选项合计 6 个 Unit。」随后 `hd2mod import plan` 退出 0，计划含主体、可选条目及默认项。P0 留存的原版 data ZIP 也通过独立导入计划，包摘要 `023A0BABE563A98D6A95C6AA76DDE4B2AF620F743783B7EF0158899BA3753D38`。这两条对照证明输入可预检，集成角色链仍需在解除沙盒限制后复跑。

## 构建与测试

本机源为已存在的 NuGet 包，只用于还原。所有 dotnet 命令附 `-nodeReuse:false -m:8 -p:NuGetAudit=false`，另附串行 MSBuild、关闭共享编译参数以保持沙盒可运行。Debug 从解决方案生产构建后调用 `dotnet test WpfApp1.sln -c Debug --no-build`，六个测试程序集无过滤运行。未启动任何图形客户端。

```text
已成功生成。
    0 个警告
    0 个错误

已通过! - 失败:     0，通过:  1702，已跳过:     0，总计:  1702，持续时间: 5 s - HD2ModCore.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 125 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 6 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 13 ms - Decryptor.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 35 ms - HD2SourceModel.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   574，已跳过:     1，总计:   575，持续时间: 4 s - HD2SourceMigration.Tests.dll (net8.0)

合计 PASS=924 FAIL=0；红=0
```

.NET 跳过的是既有 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。Lua 命令为 `python tools/physbone-harness/run_physbone_regressions.py`，其 `conform-stub-test.lua` 因夹具缺失跳过。W7 的 Python 项目身份回归独立跑了 3 个用例，结果 `OK`。双角色假 worker 测试覆盖添加两个角色、移除第一名后保留第二名、以及托管节点身份始终相同。回滚另覆盖 worker 生成失败和管理器配置部署失败，逐文件与初始哈希对比。全量日志在 `tmp/ias-w8/build-delivery.log`、`test-delivery.log`、`lua-final.log` 和发布日志。保留沙盒、清单和复跑脚本 `tmp/ias-w8/e2e.py` 作验收证据。

`git diff --check` 通过。工作树既有暂存改动保留，其中 W4 报告的末尾空行触发 `git diff --cached --check`。Serena 重新生成了工作树根的 `.serena/` 元数据，自动执行审查以 `blocked by policy` 拒绝删除，目录留在工作树。未提交、切分支、推送或安装工具。

## 待决

1. 在沙盒外用原版 IAS 复跑完整双角色序列，核对 Milfy 与 W7 布莱默顿同时存在，以及移除 Milfy 后布莱默顿仍在。正式 `to-standard` 需要完整只读 GameData，包括 `generated_customization_armor_sets.dl_bin` 和原版游戏资源。只有两份宿主的夹具足以跑 IAS 版本闸，无法独立提供转换输入。现有 DP-8 路由已验证 Milfy，其他角色兼容性待实测。
2. 实机确认 MDL3 联合加载器识别、已有官方 data 包共存提示、真实混合配置部署与 WPF 交互。离线下载链已经验证，GitHub 直连、镜像和按需 AQ SDK 的实际下载仍待联机复核。
3. 在完整发布包验证私有 .NET 运行时启动。当前核对了 UI/CLI 的框架依赖型发布目录和两种已装 .NET 主版本上的 worker 装载。后续支持新版本 IAS 时需重新记录官方 DLL 摘要、签名及依赖。
4. 对进程强杀留下的未完成快照，还需要可自动重放的恢复入口。当前保留 `previous` 阻止后续操作覆盖恢复材料。W8 续作未实现物理/Lua 的主线回退。

## 沙盒外复测修正

- **manifest 型主包预检。** Milfy 和 W7 布莱默顿主包的导入计划把空主体标记为 `PlaceholderHost`，补丁落在选项。旧预检只接受 `Host`，因此拒绝了这类包。保留派活方在 `IasModTransaction.cs` 中接受 `Host` 或 `PlaceholderHost` 的修改。`ManifestRoleWithPlaceholderHostPassesPreflightAndImportsItsOption` 使用同结构的 ZIP，确认计划含 `PlaceholderHost` 和 `Option`，随后经 IAS 角色服务导入，核对主体进入当前配置及选项补丁落库。
- **角色重新生成的清理顺序。** `e2e-outside-2` 中本体安装成功，Milfy 添加在导出之后调用 `NativeRuntimeInputs.Discard`，随后抛出 `System.IO.IOException: 无法锁定清理目标。`，内层 Win32 错误码为 3。worker 现按 P0 原型执行 `Create → Generate → Apply → Discard → ExportData`。已取得准备对象时，`Generate` 或 `Apply` 抛错仍会进入 `finally` 清理，且不会导出。测试也验证 `Discard` 抛错会向上传递。本体准备仍在清理后返回事务路径。 [外测日志](../../tmp/ias-w8/e2e-outside-2/manager/ias/backup/dbb0f3123c914bcd84438647b6f2c337/worker-regenerate.log)

使用同一份 worker 调用流程源码记录调用顺序。修复前定向测试为 **4 通过、2 失败**，两处失败均记录了 `ExportData` 在 `Discard` 前。修复后定向测试 **6 通过、0 失败**。Debug 解决方案构建退出 0，原文为「已成功生成。」「0 个警告」「0 个错误」。全量 .NET 测试退出 0，**2,784 通过、0 失败、1 跳过**。所有 dotnet 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。日志为 `tmp/ias-w8/outside-retest-{red,build,targeted,full}.log`。

Lua 回归退出 0，原文为 `合计 PASS=924 FAIL=0；红=0`，日志为 `tmp/ias-w8/outside-retest-lua.log`。`git diff --check` 通过。真实角色链仍由派活方用原版 IAS 在沙盒外复跑。本轮按约定未在沙盒内再次执行官方部署事务。

## 路径长度修正

派活方在长路径的 `e2e-outside-3` 复测中确认，`Discard` 即使先于导出执行，仍会在 `OwnedFileHandle` 抛出 Win32 错误码 3。官方暂存目录由 `TMP\HD2IAS\<32 位标识>` 与角色文件路径组成。原来的 `<管理器根>\ias\temp\<32 位标识>\worker` 使某些文件的普通 Windows 路径超过限制。同一套流程换到短根 `D:\gitCode\x` 后，派活方确认本体安装、两角色同时存在、移除 Milfy 后保留布莱默顿及卸载均成功。该夹具的 `to-standard` 仍缺少 `generated_customization_armor_sets.dl_bin`。 [长路径外测日志](../../tmp/ias-w8/e2e-outside-3/manager/ias/backup/3e787502e1174f09a4fcc8f0bf26e4dc/worker-regenerate.log)

`IasProcessWorker` 现在通过 `Directory.CreateTempSubdirectory("h2i-")` 在系统选定的用户临时根下为每次调用分配独占子目录，只给子进程设置 `TMP` 和 `TEMP`。worker 自身仍从管理器的 `ias/worker` 运行。分配前先检查临时根和前缀，分配后按实际路径长度再检查一次。加上预留的 140 个字符超过 259 时，在复制和启动 worker 前返回中文错误，提示将 `TEMP/TMP` 指向更短的可写目录。移除了请求中的旧 `Temp` 字段。正常退出、失败及取消均经过 `finally` 清理，取消分支先结束并等待子进程。官方调用保持 `Discard → ExportData`。

定向测试 **9 通过、0 失败**，覆盖深浅管理器根下的 `TMP` 等长、两次同时存活的调用目录互不干扰、119/120 字符边界及成功、失败、取消后的清理。Debug 解决方案构建退出 0，原文为「已成功生成。」「0 个警告」「0 个错误」。全量 .NET 测试 **2,788 通过、0 失败、1 跳过**。全部 dotnet 命令带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。构建和测试日志位于 `tmp/ias-w8/worker-short-path-{build,targeted,full}.log`。

Lua 回归退出 0，原文为 `合计 PASS=924 FAIL=0；红=0`，日志为 `tmp/ias-w8/worker-short-path-lua.log`。`git diff --check` 通过。测试结束后沙盒临时根中无 `h2i-*` 残留。最终代码的长路径真实端到端由派活方在沙盒外复跑。

