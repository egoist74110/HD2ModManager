# W36 IAS worker 运行库按需下载

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`，起始和交付时的 HEAD 均为 `f66816f`。改动留在工作区。

## 实施结果

- **done · 发布瘦身。** `HD2IasWorker/IasWorkerArtifacts.targets` 保留开发构建的完整 worker 输出。在 Publish 阶段过滤 worker 目录的三个 DLL 和 LICENSE，同时将它们从 SDK 发布清单中移除。后者也覆盖项目引用带入的 LICENSE 和单文件捆绑中的原生 DLL。发布脚本要求 worker 的四个启动文件存在，并扫描整个暂存包，发现任一个按需文件就中止打包。
- **done · 下载和校验。** `HD2ModCore/Ias/IasWorkerRuntime.cs` 校验 `ias\worker` 内现有 DLL 的 SHA-256，只获取含缺失或损坏 DLL 的包。每个包先流式校验 SHA-512，然后按固定 ZIP 路径提取并校验 DLL 的 SHA-256。全部缺失项通过校验后才把文件逐个原子移入目标目录。失败和取消都会清理暂存。开发布局沿用 worker 文件复制。
- **done · 安装和运行。** `IasDeploymentService` 在安装或更新 IAS 时用现有进度通道上报「下载 IAS 运行库」，在激活组件和调用 worker 之前准备依赖。`IasProcessWorker` 复制程序文件后、启动进程前再次检查，已安装 IAS 的用户升级管理器后也能自动补齐。管理器与 CLI 使用同一路径及现有 IAS HTTP 客户端，按固定源自动回退，无新增弹窗。下载失败走 IAS 现有错误处理，操作可以重试。
- **done · 卸载。** 原卸载流程只清理本体组件、缓存、`ias\Runtime` 和指定的角色数据。现在同一 IAS 部署锁内也删除 `ias\worker`。选择保留角色时，`ias\library` 及已有角色数据仍可用于重装。应用内更新只替换 `bin`、`third_party`、`hd2mod-docs` 和 `data\resources`，`ias\worker` 可跨应用更新复用。

改动文件包括发布的 `IasWorkerArtifacts.targets`、`tools/PublishRelease.ps1`，Core 的 `IasWorkerRuntime.cs`、`IasUpdateHttp.cs`、`IasComponentStore.cs`、`IasProcessWorker.cs`、`IasDeploymentService.cs`，管理器的 `IasModuleService.cs`，以及 `IasWorkerRuntimeTests.cs`、`IasDeploymentTests.cs`。

## 下载源、版本和校验常量

固定尝试顺序为 `https://api.nuget.org/v3-flatcontainer/`、`https://repo.huaweicloud.com/repository/nuget/v3-flatcontainer/`、`https://mirrors.cloud.tencent.com/nuget/v3-flatcontainer/`。每个源追加 `{id}/{ver}/{id}.{ver}.nupkg`，沿用 IAS HTTP 传输的 15 秒连接超时，单源尝试上限为 2 分钟，单包接收上限为 32 MiB。安装和 worker 启动都传递现有取消令牌。

版本取自 `HD2IasWorker/HD2IasWorker.csproj`。包的 SHA-512 使用本机 `%USERPROFILE%\.nuget\packages` 下对应的 `.nupkg.sha512`（Base64）。DLL 的 SHA-256 来自实际包内条目。用本机缓存的原始包验证了这两层哈希。

| 包 ID | 版本 | 包 SHA-512（Base64） |
| --- | --- | --- |
| `microsoft.windows.sdk.net.ref` | `10.0.19041.57` | `TI9FqukRZSFfSZ6fLh0cjO0X0OIAn5vZKGU/iNzINt3IZeK7o9Y1Dmtp+xwEv696XO1cHMB7aUqTjql3/KY9xg==` |
| `microsoft.windowsappsdk.foundation` | `1.8.251104000` | `a0LrWAS5g12rpk1hMuYB1NrZ0upc5cSTa/DhQ8P5C26DOpzmBL6Zd9+JHbTjinSp8JpulNv6NNNPX7lQ5f0iDQ==` |

| 包内路径 | DLL SHA-256 |
| --- | --- |
| `lib/net8.0/Microsoft.Windows.SDK.NET.dll` | `0EC371D93798852E36461C8ADDDBEADCE0F963A04752F0B64E54FE19C1C834A7` |
| `lib/net8.0/WinRT.Runtime.dll` | `BCF3A14E8712A90837FC5C0D8C8A24696AF2BD7F74E9767597EC81EDEDFB23DB` |
| `runtimes-framework/win-x64/native/Microsoft.WindowsAppRuntime.dll` | `89FEC903532A888AE4EEF250E2D7D0CF0D02574799957F70844C99F28330717A` |

包 ID、版本、镜像地址、包内路径、两层哈希和超时上限集中在 `IasWorkerRuntime.cs`。本次采用源码固定值。升级 worker 的 NuGet 依赖时须同步更新该表。

## 验证

| 场景 | 结果 |
| --- | --- |
| 本地三库哈希均正确 | HTTP 请求 0 次。 |
| 文件缺失或损坏 | 两个包从首源获取并替换损坏文件。再次检查时 HTTP 请求仍为 2 次。 |
| 仅一个包的 DLL 缺失 | 只获取对应包。 |
| 首源 HTTP 失败或返回错误包哈希 | 依次尝试官方源、华为云和腾讯云。腾讯云桩返回正确包后成功。 |
| 所有源校验失败 | 分别覆盖包 SHA-512 和包内 DLL SHA-256 失败。即使第一包已通过，也不会发布任何暂存 DLL。 |
| 下载取消 | 不尝试后续镜像，暂存被删除。 |
| 开发布局 | HTTP 请求 0 次，Debug worker 输出仍包含三个 DLL 和 LICENSE。 |
| 卸载并保留角色 | `ias\worker` 被删除，角色文件和角色条目保留。 |
| 发布断言自检 | 在 Windows PowerShell 5.1.26100.9818 和 PowerShell 7.6.10 解析实际脚本并调用 `Assert-UpdatablePackage`。干净布局通过。把四个禁带文件逐个注入 `bin\ias-worker`，以及把 LICENSE 注入 `bin`，五个用例均被拒绝。 |
| 发布版 worker 实跑 | 从最终 complete ZIP 取出四个 worker 启动文件。在 `tmp\w36` 用注入的 HTTP 处理器读取本机缓存的两个官方原始 NuGet 包。首次从零补齐三库，`check-interface success=True`。再次运行仍返回成功，两次共收到 2 个 HTTP 请求，落地 DLL 的 SHA-256 与上表一致。使用的是工作树内已有 IAS 1.6.4 DLL 的副本。 |

### Debug 构建和测试原文

`Build succeeded.`，`0 Warning(s)`，`0 Error(s)`。并行解决方案探测在当前沙盒退出 1 且报告零错误。为保持指定的 `-m:8` 等实参，复用工作树内已有的串行解决方案目标跑完全部 15 个项目。

| 项目 | 输出原文 |
| --- | --- |
| HD2SourceModel | `Passed!  - Failed:     0, Passed:    72, Skipped:     0, Total:    72` |
| Decryptor | `Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5` |
| HD2ModAdaptation | `Passed!  - Failed:     0, Passed:   212, Skipped:     0, Total:   212` |
| HD2SourceMigration | `Passed!  - Failed:     0, Passed:   595, Skipped:     1, Total:   596` |
| HD2ModCli | `Passed!  - Failed:     0, Passed:   213, Skipped:     0, Total:   213` |
| HD2ModCore | `Passed!  - Failed:     0, Passed:  1858, Skipped:     0, Total:  1858` |

六套共通过 2,955 项，失败 0，跳过 1 项既有三角形绕序诊断。Core 比基线增加 9 项。材质套件首跑时，工作树中的 SDK 材质会被临时目录的祖先搜索命中，导致内嵌回归断言失败。第二次将十个 SDK 材质文件暂存到沙盒，整套通过，随后还原并逐项核对 SHA-256 一致。

- Debug 使用 `dotnet build WpfApp1.sln -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -p:DirectorySolutionTargetsPath=<工作树>\tmp\w34\serial.targets -t:W34SerialBuild -p:BuildInParallel=false -p:UseSharedCompilation=false`。
- 六个项目分别执行 `dotnet test <测试项目>.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false`。
- Lua 回归原命令发现 PATH 缺少 Lua 5.1，复用工作树已有 Lupa LuaJIT 2.1 适配器运行同一 harness，原文为 `合计 PASS=486 FAIL=0；红=0`。两项既有外部夹具按 harness 规则跳过。无新增开发工具。

### 发布包大小

旧包使用工作树已有 `artifacts\publish` 下的同版本 ZIP。新包由 `.\publish.ps1 -OutputDirectory tmp\w36\after` 生成。命令的子进程沿用规定的 dotnet 参数，使用 NuGet 静态图还原和串行项目构建以适配沙盒。

| ZIP（版本 1.0.0） | 旧包字节 | 新包字节 | 减少字节 |
| --- | ---: | ---: | ---: |
| `HD2ModManager-1.0.0-win-x64-complete.zip` | 98,811,406 | 89,691,910 | 9,119,496 |
| `HD2ModManager-1.0.0-win-x64-requires-dotnet.zip` | 22,798,618 | 13,679,497 | 9,119,121 |

两个新 ZIP 的 `bin\ias-worker\` 清单相同。两个包均通过发布断言，ZIP 的 SHA-256 与本次生成的 `SHA256SUMS` 一致。

| 文件 | 解压后字节 |
| --- | ---: |
| `HD2IasWorker.deps.json` | 1,088 |
| `HD2IasWorker.dll` | 38,912 |
| `HD2IasWorker.exe` | 151,552 |
| `HD2IasWorker.runtimeconfig.json` | 426 |

`.\publish-compat.ps1 -OutputDirectory tmp\w36\compat` 首次在管理器 Publish 子命令退出 1，使用相同参数重跑成功。compat-complete 为 89,692,033 字节，compat-requires-dotnet 为 13,679,610 字节，两者的 worker 清单和校验结果与标准版规则一致。

## 沙盒和工作区

- 四份验证 ZIP 和校验文件分别位于 `tmp\w36\after` 和 `tmp\w36\compat`。
- 按父进程、启动时段和命令行确认并终止本轮失败探测遗留的 230 个 MSBuild 节点。六个无法确认归属的节点留在原位。worker 已退出，工作区原有的 `.serena/` 未跟踪目录保留。
- 测试辅助脚本已删除。自动命令审批拦截了递归清理 `tmp\w36` 内临时文件的操作，返回 `blocked by policy`。编译日志、暂存目录和运行探针产物仍位于忽略的 `tmp\w36` 内。

## 待决

- **blocked · 真实 NuGet 下载实跑。** 沙盒对 nuget.org 的 HTTPS 请求在本机代理连接处失败，返回 `curl: (7) Failed to connect`。官方源和镜像的真实网络可达性留待联网环境验证。发布版 worker 已使用本机 NuGet 原包进行同路径实跑。

## 派活方验收修正（2026-11-25）

- **镜像地址修正**：原写的华为/腾讯 `…/v3-flatcontainer/` 实测都是 404。华为改为其 `v3/index.json` 里的 PackageBaseAddress `https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote/{id}/{ver}/{id}.{ver}.nupkg`；腾讯只有 v2 源，改为 `https://mirrors.cloud.tencent.com/nuget/{id}/{ver}`。
- **超时**：原来每个源固定 2 分钟总时限，慢速但在下载的连接会被误杀；改为「30 秒没收到数据才放弃」（连接另有 15 秒超时）。
- **源顺序跟设置走**：更新设置为「优先使用镜像」时，先华为、腾讯，最后官方（`IasUpdateHttp.PrefersMirror`）。
- **实测**（本机，日本网络）：官方与华为两个包都下全、SHA-512 与条目 SHA-256 全对；腾讯小包对、大包这次传输中断（会被 SHA-512 拒收并报失败，属预期的兜底行为）。
- 构建 0/0；Core 测试 1860 通过（新增源顺序测试），全量其余不变。
