# W14 IAS 可选画面增强（2026-11-24）

IAS 页第三张卡提供 ReShade 滤镜、DLSS 三选一和首次风险确认。Debug 构建 0 警告、0 错误，全量 .NET 测试 2,817 通过、0 失败、1 跳过，Lua 回归 486 通过、0 失败。原版 worker 的状态读取和冲突说明已验证到 CLI。完整开关流程仍待在沙盒外用原版 DLL 复跑。

## 实施计划

1. 扩充同一份验签发布包的部件保留范围。安装、更新与同版本修复补齐三个画面部件。
2. 在 worker 校验官方签名及部署范围。实际状态以官方运行目录组件回执为准。ReShade 关闭时由官方计划一次移除 ReShade 与 DLSS。
3. 把切换、生成 data 包和替换托管 Mod 放进现有 IAS 独占事务。重复请求在事务内按当前状态判定。失败、取消和超时沿用快照回滚，新增 ShaderToggler 单文件快照。强杀沿用已有未完成快照保护。
4. 接入 IAS 第三张卡与 CLI，在管理器根的 IAS 目录保存首次确认。验证操作顺序、异常回滚、冲突原文和界面禁用条件，生成独立沙盒复跑脚本。

## 实现

- **组件库。** 把 `ExternalRuntime/ReShade.zip`、`DLSSFrameGeneration.zip`、`DLSSNeuralRendering.zip` 加入同一份官方 `release.zip` 的保留范围。校验发布包整体及各部件的 SHA-256、大小、路径后，分别展开至版本目录中的 `expanded/<组件名>`。同版本修复会补齐旧版本安装漏存的 ZIP，优先使用本地已缓存发行包，也可传入官方离线包。在线修复缓存缺失时由现有发行包下载入口补回整包。开关操作读取本地部件。 [组件库](../../HD2ModCore/Ias/IasComponentStore.cs) · [安装和修复](../../HD2ModCore/Ias/IasDeploymentService.cs)
- **官方调用。** worker 增加 `reshade-install`、`reshade-remove`、`dlss-install <fg|nr>`、`dlss-remove` 和只读的 `graphics-status`。继续固定 IAS 1.6.4 客户端 DLL 的 SHA-256，逐一核对方法参数、返回类型、静态性和读取字段类型。官方 `Prepare` 已绑定，其计划包含直接写入游戏 data 补丁的路径。实际安装按 1.6.4 官方界面调用 `PrepareRuntime → Apply`，DLSS 使用 `PrepareDlss → Apply`。两个卸载预览生成永久清理计划，由官方 `ApplyPurge` 执行。worker 校验计划的游戏根、运行根、组件名及写入范围。游戏根侧只许可官方 ReShade 安装写入 `bin/ShaderToggler.ini`，`dxgi.dll` 落在 IAS 运行目录的 `reshade/`。 [worker](../../HD2IasWorker/Program.cs)
- **事务和状态。** 以官方运行目录组件记录为准。DLSS 开启需要 ReShade，ReShade 关闭时一并移除 DLSS 并提示。重复设置按现状返回。实际切换沿用 IAS 锁、Mod 库租约及运行快照，重走 `Create → Generate → Apply → Discard → ExportData`，原位替换托管 Mod 并保持配置顺序。临界区内回读确认组件状态。另修复了空角色库重新开启 IAS 时漏加系统 Mod 的既有问题，确保该场景下可继续启用画面功能。 [事务](../../HD2ModCore/Ias/IasDeploymentService.cs)
- **异常和资源。** ReShade 安装前备份既有 `ShaderToggler.ini`，并记录原先缺失的状态。worker、生成、导入、配置部署等步骤失败或取消时，既有快照恢复运行目录和已触及的管理器文件，披风 INI 恢复旧内容或撤掉本次新文件。永久清理后的失败同样恢复 ReShade 和 DLSS。worker 超时沿用进程树终止和等待，读状态目录在 `finally` 清理。未完成快照沿用已有拦截机制。官方冲突独立标记，CLI 和页面收到原始中文，详细堆栈仍留在操作日志。 [快照](../../HD2ModCore/Ias/IasModTransaction.cs) · [进程边界](../../HD2ModCore/Ias/IasProcessWorker.cs)
- **界面和 CLI。** 卡片按设计第 11 节提供 ReShade 开关、Home 键说明、DLSS 的关／插帧／神经渲染单选和对应的显卡及游戏设置说明。安装、总开关、前置和忙碌状态控制可用性。缺少画面部件时提供修复入口。首次确认原子写入管理器根下 `ias/graphics-risk.json`，取消后可在下次开启时再确认。风险确认记录独立于安装结果。CLI 支持 `hd2mod ias reshade on|off` 和 `hd2mod ias dlss off|fg|nr`，显式运行 CLI 不会记作页面风险确认。 [页面](../../HD2ModManager/Views/IasPageView.xaml) · [ViewModel](../../HD2ModManager/ViewModels/IasPageViewModel.cs) · [CLI](../../HD2ModCli/CliIasModuleCommands.cs)

## 验证

Debug 构建及全量测试均退出 0。各次 dotnet 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`，另加 `-maxcpucount:1` 适配沙盒，构建使用 `--no-restore` 和现有缓存，测试使用 `--no-build`。SDK 材质目录在测试期间临时移到本工作树内，让内嵌材质用例走到指定分支。已恢复原位，10 份 SHA-256 匹配。 [构建日志](../../tmp/ias-w14/build-final.log) · [全量测试日志](../../tmp/ias-w14/test-final.log) · [材质回读](../../tmp/ias-w14/material-restore.log)

```text
已成功生成。
    0 个警告
    0 个错误
```

| 测试程序集 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1759 | 0 | 1759 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| HD2SourceMigration.Tests | 0 | 556 | 1 | 557 |
| 合计 | 0 | 2817 | 1 | 2818 |

- 唯一 .NET 跳过项为已有三角形绕序诊断。新增测试覆盖旧部件库补齐、拒绝未列入签名清单的画面 ZIP、IAS 和 ReShade 前置、两种 DLSS 切换、级联卸载、失败回滚、冲突原文及 ViewModel 的禁用、首次取消和记住确认。首轮全量测试用例复现了无角色时总开关重开失败，修正后通过。
- `python tools/physbone-harness/run_physbone_regressions.py` 退出 0，原文 `合计 PASS=486 FAIL=0；红=0`。两项按既有缺夹具规则跳过。复用本机已有 Lupa 2.8 的 LuaJIT 2.1，临时入口已删除，新增开发工具为 0。 [Lua 日志](../../tmp/ias-w14/lua.log)
- 使用原版 1.6.4 DLL 核对新反射签名与 `graphics-status`，退出 0，结果为 ReShade 关闭、DLSS 关闭。在 ReShade 未打开时直接运行 `dlss-install fg`，退出 1，返回「请先打开 ReShade 滤镜。」用独立测试管理器根执行 CLI `ias reshade on`，退出 2，`message` 与官方说明逐字一致，模拟游戏 bin 中冲突 DLL 的 SHA-256 一致。 [原版状态](../../tmp/ias-w14/official-graphics-status.log) · [CLI 冲突输出](../../tmp/ias-w14/cli-conflict.json)
- 核对了物理、Lua 和共用配置部署的相关文件相对 `master` 差异为空。W14 的代码改动限于 IAS 专用文件与测试，`HD2SourceMigration/` 中的其他任务改动保留。未提交、切分支或推送。游戏、管理器、IAS 图形客户端、安装器均未启动。真实游戏和 Mod 库目录无写入。
- 隔离 `DOTNET_CLI_HOME` 后 SDK 第一次运行显示了开发证书提示。读取当前用户证书库，仅发现 2026-07-29 的既有 ASP.NET Core 开发证书。后续命令关闭了 SDK 自动证书生成。没有运行证书安装或信任命令。

## 待决

1. **原版开关端到端。** 在本机运行 `python tmp/ias-w14/e2e.py`，首步通过发行包校验、部件解压及版本激活，到官方 `prepare-base` 的 `ExternalRuntimeDeployment.ApplyCore → FileSystem.ReplaceFile` 收到 `System.UnauthorizedAccessException: Access to the path is denied.`，CLI 退出 2。该错误与 W8、W12 的沙盒限制一致。复跑脚本使用 W8 的宿主夹具和离线包，在工作树内新建沙盒，断言冲突原文、ReShade 开 → DLSS 插帧 → DLSS 关 → ReShade 关、级联卸载，以及组件回执、托管 Mod ID 和宿主哈希。后续实际切换步骤待派活方在沙盒外复跑。 [脚本](../../tmp/ias-w14/e2e.py) · [失败记录](../../tmp/ias-w14/e2e.log)
2. **WPF 与游戏内表现。** XAML 构建和 ViewModel 测试通过。第三张卡的实际视觉、点击反馈以及游戏内滤镜、插帧和神经渲染效果待在允许启动相应程序的环境验收。
3. **沙盒清理。** 自动审批以 `blocked by policy` 拒绝递归删除本轮 `tmp/ias-w14/` 下核对过的临时子目录。该 Git 忽略目录仍保留运行副本、测试缓存、日志和复跑脚本。
