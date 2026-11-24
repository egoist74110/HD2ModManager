# W13 普通页面拖入 IAS 包并转换入库

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`。按 IAS 页面设计第 9、12 节实现，变更留在工作区供派活方审核。 [IAS 页面设计](ias-page-design-20261124.md)

## 实现

- 普通页面拖放和「导入」按钮继续走 `ShellViewModel.ProcessImportQueueAsync`。一次拖入立即进入原队列，出队时在这次拖入的范围内识别 IAS 包。独立路径继续按原顺序交给普通导入，包括现有的 VPK 分流。 [队列](../../HD2ModManager/Services/ImportQueueService.cs) · [导入入口](../../HD2ModManager/ViewModels/ShellViewModel.cs)
- 先检查目录与压缩包条目，再复用 W11 的内容识别、ZIP/RAR/7z 解压校验和最多三层嵌套规则。包含完整一组主包与配套包的来源单独转换。分开选择时优先匹配唯一明确命名的主包，其次接受唯一候选。配对有歧义时保留原路径。 [识别与配对](../../HD2ModCore/Ias/IasStandardDrop.cs)
- 确认配对后，从设置读取 `GameDataFolder`。目录缺失时显示「转换 IAS 包需要游戏数据目录，请先在设置中选择有效的游戏数据目录。」转换复用「转为普通 Mod」的 DP-8 调用和普通导入计划器的预检。解包和转换同属 W11 工作区的短暂存根。产物名称追加「（由 IAS 转换）」，随后交给普通 `ImportService` 入库并走原有结果提示。 [转换入口](../../HD2ModManager/Services/Ias/IasStandardDropImport.cs)
- IAS 页警告条使用第 12 节定稿。确认框说明潜兵身上的护甲冲突、NPC 不受影响，以及建议关闭管理器的内置 Lua。确认动作、设置写入和警告可见性条件沿用当前逻辑。 [警告条](../../HD2ModManager/Views/IasPageView.xaml) · [确认框](../../HD2ModManager/ViewModels/IasPageViewModel.cs)
- 新增 5 个分流测试，覆盖普通 Patch、独立 VPK、VPK 压缩包、缺配套包、嵌套 IAS 与普通来源混合、拆分配对、歧义、游戏数据缺失和暂存清理。物理、Lua 和共用部署文件相对 `master` 的比较为空。W13 未修改 `HD2SourceMigration/` 或 IAS 部署代码，也未改动其他工作树。 [分流测试](../../HD2ModCore.Tests/IasStandardDropTests.cs)

## 真实样本

将 Downloads 中的 `milfy发光外套等3个文件.rar` 复制到 `tmp/ias-w13/`，复制件 SHA-256 为 `FAE3220F1BAB439AA5BAE1648D8F521CAEDD44205834B9AE76B8F046EDE6D718`。探针调用最终版导入队列、W13 转换预检和普通 `ImportService`，使用 `E:\SteamLibrary\steamapps\common\Helldivers 2\data` 读取游戏数据，写入新建的 `tmp/ias-w13/library-final/` 沙盒 Mod 库，退出码为 0。 [探针输出](../../tmp/ias-w13/probe-final.log)

```text
分流：1 个 IAS 包
预检：ManifestStrict，7 个条目，0 个阻断诊断
入库：Milfy · DP-8 Mountain-Scaled（由 IAS 转换），7 个节点，14 个补丁及侧车文件 SHA-256 一致，姿势 JSON = 0，IAS 安装目录 = 0
IAS 短暂存目录 = 0
```

7 个节点包括主体、物理落盘的 4 个选项和 2 个逻辑默认子选项。普通入库会把选项放在各自的库目录，并可重新编号补丁文件。首轮探针只比较主体目录及文件名，复跑后改为比较整个空白沙盒库中的补丁及侧车 SHA-256 多重集合，14 项全部匹配。

用本次输入的 rig 夹具和真实游戏数据运行既有 `StandardMilfyOutputUsesOnlyGameAndSpringSkinBonesWhenFixtureIsProvided`。它回读 20 个主体 Unit 和 6 个选项 Unit 的骨骼表及蒙皮调色板。骨哈希全部属于原版或补丁声明的弹簧骨，旧 `ias_` 骨哈希为 0，`arm-pose.json`、`joint-pose.json` 均为 0。定向测试原文为 `测试总数: 1`、`通过数: 1`。写入沙盒库的补丁和侧车已逐文件内容比对。 [骨骼回读结果](../../tmp/ias-w13/bone-final.log)

## 构建与测试

全部 .NET 命令使用 `-nodeReuse:false -m:8 -p:NuGetAudit=false`，另加 `-maxcpucount:1` 适配本机沙盒。Debug 解决方案构建退出码为 0，原文为 `已成功生成。`、`0 个警告`、`0 个错误`。全量 Debug 测试用 `--no-build`，无过滤，退出码为 0。 [构建日志](../../tmp/ias-w13/build-final.log) · [全量测试日志](../../tmp/ias-w13/test-final.log)

| 测试程序集 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1745 | 0 | 1745 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| HD2SourceMigration.Tests | 0 | 556 | 1 | 557 |
| 合计 | 0 | 2803 | 1 | 2804 |

唯一跳过项为既有的三角形绕序诊断。全量测试启用了 Milfy 夹具。材质内嵌回归期间将工作树内的 SDK 材质目录暂存到 W13 沙盒，结束后恢复并逐份验证 10 个 SHA-256。测试工具另输出了关于 `VSTEST_ARTIFACTS_DIRECTORY` 未设置的提示。测试进程临时目录与执行后的缓存均定位在工作树内的 W13 沙盒。

`python tools/physbone-harness/run_physbone_regressions.py` 退出码为 0，原文为 `合计 PASS=486 FAIL=0；红=0`。两项缺外部夹具的用例沿用脚本的跳过规则。使用本机已有 Lupa 2.8 的 LuaJIT 2.1，新增开发工具为 0。 [Lua 日志](../../tmp/ias-w13/lua.log)

## 清理与待决

- `git diff --check` 通过。W11 解包和 W13 转换的 `h2i-*` 暂存目录为 0。临时脚本和探针源码已删除。自动审批以 `blocked by policy` 拒绝递归删除 `tmp/ias-w13/`。该 Git 忽略目录仍有样本副本、沙盒库、探针构建产物和验证日志。
- 多个通用主包竞争同一配套包，或同一个压缩包同时承载独立 IAS 与 VPK 内容时，当前保留原导入入口。若产品要自动拆分这种来源，配对规则待派活方确定。当前转换能力沿用 DP-8，其他骨架或槽位没有实样覆盖。
- 外层列表既无配套标记，也无 ZIP、RAR 或 7z 后缀的条目时，预判保留原导入入口。全部改成其他后缀的内层归档若也需要识别，需确定普通导入是否可以为此额外解压候选来源。
- WPF 实际拖放手势、警告条视觉和游戏内显示待实机验收。本轮验证了真实文件出队、转换和普通入库，并完成 XAML 构建和 ViewModel 测试。游戏、管理器、IAS 图形客户端和安装器均未启动，未安装证书。
