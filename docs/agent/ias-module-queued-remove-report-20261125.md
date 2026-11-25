# W35 IAS 页角色卸载队列

## 改动与验收

- **done · 队列入口。** 审核并保留派活方在 `HD2ModManager/ViewModels/IasPageViewModel.cs` 的改动。角色卸载使用与转换共用的入队可用条件，IAS 正忙时仍可点击。用户确认后，应用级 `IasOperationQueue` 为该卸载创建一条消息中心任务。
- **done · 去重与取消。** 卸载和转为普通 Mod 共用 `role:<id>` 键。相同角色的任务正在执行或等待时，卸载入口沿用队列提示并跳过确认与入队。在 IAS 页或消息中心取消一条等待中的卸载任务，只移除该项。
- **done · 按 ID 执行与刷新。** 确认入队时捕获角色 ID。队列轮到卸载项后直接将该 ID 交给服务。前一操作的刷新可以替换页面上的旧角色行，队列项仍然有效。队列清空触发现有角色列表刷新。
- **done · 游戏状态。** 页面检测到游戏运行时禁用角色卸载。游戏在等待期间启动时，队列在派发前按既有规则将卸载标记为失败，服务调用次数为零。
- **done · 回归范围。** 本轮新增修改集中在 `HD2ModCore.Tests/IasPageViewModelTests.cs`，保留工作树中的 W32、W34 和拖入免确认改动。IAS 本体卸载、角色开关和转为普通 Mod 继续走各自现有入口。

## 测试场景

`ServiceStub.RemoveCharacterAsync` 按 ID 记录调用，可注入挂起的卸载结果。卸载成功后，下次读取角色列表会剔除已卸载的 ID。

| 场景 | 断言 |
| --- | --- |
| 独占更新运行中确认卸载 | 按钮可点并显示卸载确认。更新期间任务等待，更新刷新替换旧角色行后按原 ID 执行，队列结束后列表反映卸载结果。 |
| 连续卸载不同角色 | 两次确认各创建一条消息中心任务。第一项执行时第二项等待，服务按提交顺序收到两个 ID。 |
| 取消卸载确认 | 无队列项、无消息中心任务、无服务调用，角色行仍在。 |
| 同角色重复卸载 | 等待和执行阶段各拦截一次重复请求，两次均发布已有队列提示。只有一次卸载确认、一次入队和一次服务调用。 |
| 单独取消等待项，两个测试数据 | 分别使用 IAS 页取消按钮绑定的命令与消息中心任务取消，当前添加任务继续运行，卸载服务调用次数为零。 |
| 排队期间游戏启动 | 队列执行前标记卸载任务失败，角色卸载服务调用次数为零。 |

另更新原独占操作测试的忙碌断言，在原混合队列测试中验证同角色的转换已排队时卸载会被拦截。原有游戏运行时不可卸载、当前配置禁用 IAS 时仍可卸载的断言继续通过。

## 构建与回归

- **done · 调试构建。** 默认并行解决方案命令退出 `1`，原文为 `生成失败。`、`0 个警告`、`0 个错误`。用已有 NuGet 还原结果及工作树 `tmp/w34/serial.targets` 依次构建解决方案全部 15 个项目，退出 `0`，原文为 `已成功生成。`、`0 个警告`、`0 个错误`。
- **done · 定向测试。** `已通过! - 失败:     0，通过:    84，已跳过:     0，总计:    84`。初跑暴露同文案通知会被通知中心聚合，断言已改为按发布事件计数。
- **done · 全量测试。** 解决方案六个测试项目逐个运行，全部退出 `0`。合计通过 2944、失败 0、跳过 1、总计 2945，较所给基线增加 7 个通过项。已跳过的是既有的 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。
- **done · Lua 回归。** 原命令找不到 PATH 上的 Lua 5.1 解释器。沿用工作区已有的 Lupa LuaJIT 2.1 适配器执行原 harness，退出 `0`，原文为 `合计 PASS=486 FAIL=0；红=0`。两项外部夹具按 harness 既有规则跳过。

| 测试项目 | 输出原文 |
| --- | --- |
| `HD2ModCore.Tests` | `失败:     0，通过:  1847，已跳过:     0，总计:  1847` |
| `HD2ModAdaptation.Tests` | `失败:     0，通过:   212，已跳过:     0，总计:   212` |
| `HD2ModCli.Tests` | `失败:     0，通过:   213，已跳过:     0，总计:   213` |
| `Decryptor.Tests` | `失败:     0，通过:     5，已跳过:     0，总计:     5` |
| `HD2SourceModel.Tests` | `失败:     0，通过:    72，已跳过:     0，总计:    72` |
| `HD2SourceMigration.Tests` | `失败:     0，通过:   595，已跳过:     1，总计:   596` |

    dotnet build WpfApp1.sln -c Debug --no-restore -nodeReuse:false -m:8 -p:NuGetAudit=false -p:DirectorySolutionTargetsPath=tmp/w34/serial.targets -t:W34SerialBuild -p:BuildInParallel=false -p:UseSharedCompilation=false
    dotnet test HD2ModCore.Tests/HD2ModCore.Tests.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false --filter FullyQualifiedName~IasPageViewModelTests
    dotnet test <测试项目>/<测试项目>.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false
    python tools/physbone-harness/run_physbone_regressions.py

串行构建实参使用 `DirectorySolutionTargetsPath` 的绝对路径。材质内嵌回归在测试期间临时隔离工作树内的 SDK 材质文件，测试结束后全部恢复并核对 SHA-256。现有 WPF 测试和命令测试完成验证，按任务限制未启动图形客户端进行手测。无新增开发工具。

前两次失败的并行探测继承了默认 `TEMP`，系统临时目录中可见相应时段的 MSBuild 临时文件。按写入范围限制，保留这些工作树外文件。随后将运行时的临时目录和应用数据环境变量指向工作树内沙盒。失败的并行调度遗留的 350 个 MSBuild 子节点已按父 PID 和节点参数确认归属并终止。

- **partial · 临时文件清理。** 自动命令策略拦截了删除本轮工作树内沙盒 `tmp/w35-queued-remove` 的命令。沙盒留有构建及回归产生的日志、临时文件和本地验证辅助脚本。SDK 材质暂存目录已确认清空。

## 待决

- 角色开关仍由 `CanToggleCharacter → CanWrite → CanAct` 控制，IAS 忙时会变灰。是否也允许排队，留待单独决定。
