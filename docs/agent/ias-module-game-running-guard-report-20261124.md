# W16 IAS 运行中防护与快照恢复

## 实施计划

1. 复用管理器的游戏进程探测，统一约束 IAS 写入口、页面命令和 CLI 恢复命令。
2. 在变更前记录快照目标，按大小和 SHA-256 恢复有差异的文件。逐项收集失败并保留可重试的快照。
3. 在 IAS 写入前和页面进入时恢复残留快照。兼容旧快照中可确定目标的内容。
4. 用隔离目录覆盖进程拦截、锁定文件、恢复重试，运行 Debug 构建、全量测试和 Lua 回归。

## 实现

- IAS 安装、更新、卸载、角色增删和开关、画面增强、组件下载，以及 IAS 页的部署相关操作复用现有 `GameProcessProbe.IsGameRunning()`。事务拍快照前检查，调用写入型 worker 和发布前再检查。拦截统一返回「请先完全退出游戏再操作 IAS（检测到 Helldivers 2 正在运行）。」IAS 页进入时检查，在页内每 3 秒更新，离页停止。该页的「转为普通 Mod」也走防护，因为它会建立 IAS 事务并写入 Mod 库。 [事务入口](../../HD2ModCore/Ias/IasDeploymentService.cs) · [页面](../../HD2ModManager/ViewModels/IasPageViewModel.cs) · [下载入口](../../HD2ModCli/CliIasFeedCommands.cs)
- 回滚逐文件比较大小和 SHA-256。内容相同的文件直接跳过，包括仍可读的锁定 DLL。只删除多余文件，缺失或有差异的文件先复制到同目录的临时文件，再改名到目标。单个文件出错时继续处理其余文件，最后汇总失败路径。 [差异恢复](../../HD2ModCore/Ias/IasFileTree.cs)
- 新事务先写恢复清单，再公开快照入口。每次修改目标前，清单已记录原路径和目标是否原先存在。发布过 Mod 选项时，恢复会把磁盘状态同步回管理器的内存。已开始的配置部署记录原配置，恢复文件后沿用现有部署或停用入口。成功写入完成标记，再从 `snapshots/previous` 移走快照；失败则保留快照，并在管理器根的 `ias/logs/recovery-*.log` 写入诊断。 [快照清单](../../HD2ModCore/Ias/IasSnapshot.cs) · [事务发布](../../HD2ModCore/Ias/IasModTransaction.cs)
- IAS 页进入时和下次写操作前会尝试恢复。页面进入时游戏还在运行，会等页内检测到退出后再尝试。手动入口为 `hd2mod ias recover --manager-root <目录> --game-root <目录>`，支持 `--json`，失败退出码为 `2`。旧版快照中已有的 IAS `Runtime`、`library`、`components` 副本可恢复。 [CLI](../../HD2ModCli/CliIasModuleCommands.cs)

## 验证

标准并行构建在沙盒内退出 `1`，原文为 `生成失败。`、`0 个警告`、`0 个错误`。随后使用现有还原缓存并串行运行解决方案级 Debug 构建和全量测试，两项退出码均为 `0`。

```powershell
dotnet build WpfApp1.sln -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -maxcpucount:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet test WpfApp1.sln -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -maxcpucount:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
```

```text
已成功生成。
    0 个警告
    0 个错误
```

| 测试程序集 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1767 | 0 | 1767 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| HD2SourceMigration.Tests | 0 | 556 | 1 | 557 |
| 合计 | 0 | 2825 | 1 | 2826 |

- 新增六个回归用例。覆盖运行中拒绝写入且无快照、无 worker，Windows 只读共享句柄锁住同内容 DLL 时回滚成功，锁住两份同大小但异内容 DLL 时汇总两处失败并继续恢复其余文件，重新打开服务后下次写入自动恢复，旧版快照成功和留档路径，以及 IAS 页游戏运行时置灰、退出后重进触发恢复。 [事务用例](../../HD2ModCore.Tests/IasDeploymentTests.cs) · [页面用例](../../HD2ModCore.Tests/IasPageViewModelTests.cs)
- CLI 沙盒实跑 `ias recover --json` 三次，退出码依次为 `0、0、2`。第一次补回缺失文件、删除多余文件并清除快照。第二次报告没有待恢复快照。第三次保留无法确定原路径的旧版快照，返回实际存在的诊断日志路径。
- 全量首轮的材质用例从工作树内的临时目录向上找到了 SDK 模板。复测期间将该目录暂存于本工作树的沙盒，使内嵌模板用例走到指定分支。测试后恢复原位，10 份 SHA-256 一致。唯一跳过项为既有三角形绕序诊断。部署类 CLI 测试没有出现 `GameProcessRunning` 失败。
- `python tools/physbone-harness/run_physbone_regressions.py` 退出 `0`，原文 `合计 PASS=486 FAIL=0；红=0`。两个已知夹具缺失用例按脚本规则跳过。使用本机已有的 Lupa 2.8 和 LuaJIT 2.1，临时入口已删除，新增开发工具为 0。
- 工作树 `git diff --check` 通过。相对 `master`，物理、Lua、共用部署及游戏进程探测文件的检查为空，现存 `OptionActivationStore.ReloadAsync` 是 W15 的纯新增。本轮改动留在工作区，既有暂存状态保留。构建和测试写入根重定向到本工作树，未启动游戏、管理器或 IAS 图形客户端，未运行安装器或安装证书。真实游戏目录、Mod 库和事故现场未写入。
- 两次并行构建遗留的 210 个 MSBuild 子进程根据构建日志 PID、父进程链和启动时间核实后已结束，复查余数为 0。自动审批以 `blocked by policy` 拒绝递归删除本轮 `tmp/w16-verification/`，已单独删除临时入口和核验脚本，构建日志、测试缓存和沙盒数据留在该 Git 忽略目录。

## 待决

- 旧版快照缺少原游戏根与 Mod 节点原路径。残留内容若涉及这些目标，恢复已知 IAS 副本后会保留快照并给出日志。原先缺失、旧版未记录的目标不作推断。新清单若与当前游戏或 Mod 库目录不匹配，也保留快照。
- 配置部署若原有激活状态不完整、恢复所需的旧配置找不到，或共用部署入口拒绝恢复，快照保留并给出原因。真实事故现场和 WPF 实际绘制未验收，本轮证据为沙盒 CLI、Windows 文件锁定回归和 ViewModel 测试。
