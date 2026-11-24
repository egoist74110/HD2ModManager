# W15 · IAS 角色选项与管理器部署同步

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`。本次改动留在工作区，原有暂存内容保留，由派活方审核。

## 根因与复现

派活方的实测记录显示，Milfy 的「本体（必选）」已写为启用，但窗口部署的 `activation-state.json` 只有 IAS 系统包。窗口启动时创建 `OptionActivationStore`，随后复用于库服务和配置部署。原来的 IAS 导入另建 store 写入选项文件，成功后的窗口刷新只重读 Mod 库。窗口下一次部署虽然读取了新的库与活动配置，但仍从启动时的 store 取得选项状态。Milfy 主体是无补丁的 `PlaceholderHost`，模型补丁来自其选项，所以计划只剩 IAS 系统。

修复前，在沙盒里先创建长期存活的 store，再用 IAS 导入含「本体（必选）」的主包。用相同库和活动配置调用真实 `ProfileApplyService` 与 `ApplyPlanner`，由测试执行器捕获计划。新读入的 store 能规划角色补丁 `a7c4c9d700000000.patch_0`，窗口 store 的计划只有系统补丁 `9ba626afa44a3aa3.patch_0`。定向测试退出 1，原文为 `Assert.Contains() Failure: Filter not matched in collection`、`失败:     1，通过:     0，已跳过:     0，总计:     1`。 [回归用例](../../HD2ModCore.Tests/IasDeploymentTests.cs)

## 修复与写入路径

- Shell 把窗口已有的 `OptionActivationStore` 注入 IAS。IAS 事务沿用严格的磁盘读取和选项合并，发布时通过共用实例更新文件和内存，IAS 自身部署也使用该实例。成功后的库刷新仍走原入口，此时选项状态已同步。CLI 省略注入参数时，沿用独立 store。 [窗口接线](../../HD2ModManager/ViewModels/ShellViewModel.cs) [IAS 服务](../../HD2ModCore/Ias/IasDeploymentService.cs)
- 事务开始发布选项后，无论选项写盘、库写盘还是部署失败，回滚都会先恢复已备份的文件，再通过新增的 `ReloadAsync` 同步共用 store，随后按既有逻辑处理游戏部署回滚。`ReloadAsync` 只读文件。回归模拟卸载后部署失败，核对选项 JSON 的原始字节和内存中的角色、普通 Mod 选项。 [IAS 事务](../../HD2ModCore/Ias/IasModTransaction.cs) [选项 store](../../HD2ModCore/Infrastructure/OptionActivationStore.cs)
- IAS 添加、从现有 Mod 转入、移除角色、卸载角色和 IAS 页「转为普通 Mod」共用事务发布路径。回归覆盖窗口再次部署、窗口保存另一选项、转换导入、移除后再保存，以及移除失败回滚。总开关和角色开关改变配置成员或 IAS 自身状态，回归确认一轮关开前后选项 JSON 字节相同。
- W13 普通页先转换到暂存目录，再走窗口 `ImportService.ImportPathAsync`。普通导入通过 `_library.InitializeDefaultOptionActivationsAsync` 更新窗口的 store，这条路径无需重新加载选项。 [普通页导入](../../HD2ModManager/ViewModels/ShellViewModel.cs) [普通导入服务](../../HD2ModManager/Services/ImportService.cs)

## 验证

NuGet 在线还原报 `NU1301`，改用本机现有包缓存后还原成功。两次解决方案级 Debug 构建均退出 1，输出 `生成失败。`、`0 个警告`、`0 个错误`。解决方案级测试退出 1，无测试汇总。随后逐项目 Debug 构建管理器、全部引用和六个测试项目，七次构建均退出 0，每次均输出 `已成功生成。`、`0 个警告`、`0 个错误`。所有 `dotnet` 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。

六个测试项目分别用 `dotnet test <项目> -c Debug --no-build` 执行全量，退出码均为 0。表中数字来自各项目汇总。

| 项目 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| `HD2ModCore.Tests` | 0 | 1760 | 0 | 1760 |
| `HD2ModAdaptation.Tests` | 0 | 212 | 0 | 212 |
| `HD2ModCli.Tests` | 0 | 213 | 0 | 213 |
| `Decryptor.Tests` | 0 | 5 | 0 | 5 |
| `HD2SourceModel.Tests` | 0 | 72 | 0 | 72 |
| `HD2SourceMigration.Tests` | 0 | 556 | 1 | 557 |
| 合计 | 0 | 2818 | 1 | 2819 |

唯一跳过项为既有的 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。素材迁移测试使用 W13 现有 Milfy 产物和 rig 夹具，只读游戏数据。内嵌材质测试运行时，将工作树内的 10 份 SDK 材质暂存到 W15 沙盒，测试后恢复并比对全部 SHA-256。

最终复测 IAS 部署和 W13 普通页导入相关用例，原文为 `失败:     0，通过:    24，已跳过:     0，总计:    24`。

`python tools/physbone-harness/run_physbone_regressions.py` 退出 0，原文为 `合计 PASS=486 FAIL=0；红=0`。两项缺外部夹具的用例由原脚本跳过。使用本机已有的 Lupa 2.8 和 LuaJIT 2.1，无新增开发工具。

`git diff --check` 通过。对比 `master`，`HD2PhysBoneCompat/`、Lua 回归入口、`HD2ModCore/Infrastructure/` 中的物理、Lua 和部署逻辑均零差异。Infrastructure 唯一差异为新增 `OptionActivationStore.ReloadAsync`。未启动游戏、管理器或 IAS 图形客户端，未写入真实游戏和 Mod 库，未提交或切换分支。

按三个失败调用的进程树核对并终止了 350 个残留 MSBuild 子进程，复查为 0。临时脚本已移除。自动审批以 `blocked by policy` 拒绝递归清理 `tmp/ias-w15/`，测试临时文件和构建缓存仍在该 Git 忽略目录中。

## 待决

- 仍需在实际窗口和游戏内确认 Milfy 模型显示，以及 F4 状态能否从「正在应用」恢复。当前证据覆盖部署计划与选项持久化。
- 如果要求 IAS 事务与另一个进程或窗口普通选项操作同时写同一文件，需要单独决定共用选项写入的串行化边界。现有普通选项保存入口没有取得 IAS 使用的库写锁，本次修复覆盖同一窗口顺序操作导致的旧快照问题。
