# W32 · IAS 装备 Archive 占用冲突

## 根因

Airi 婚纱睡衣完成约 25 秒转换后，官方 `NativeRuntimeInputs.Create` 才发现启用的角色「2606520120」已占用 DP-8 头盔 Archive `59d238094a164c88`。源异常来自 `ModConflictReport.RequireClear()`，现有事务成功回滚。原流程把全局设置的同一对底子交给每个 VPK 角色，撞库内角色时第一个就失败，同批多个角色也会互撞。普通 Mod、素体 Mod、IAS 主包与配套包，以及启用已停用角色原先同样依赖 regenerate 才发现冲突。

## A · 官方判定口径

复用官方方法。用本机已有的 `ilspycmd` 检查 IAS 1.6.4 客户端 DLL（SHA-256 `5bc1d974a89fa7887933dd83da9dde019132e818c0502dc7ef3069fe853c504d`）。`NativeRuntimeInputs.Create` 读取 `OwnedStore.LibraryStatus()`，对启用条目调用 `OwnedStore.InspectEnabledConflicts(state)`，其中 `BundleArchives(bundle)` 给出每个配套的 Archive 集合，再由 `ModConflictReport.RequireClear()` 拒绝重复归属。直接调用 `InspectEnabledConflicts` 只会报告重复项，所以查询完整占用表时，worker 反射复用了它使用的同一 `BundleArchives` 方法。

- `archive-occupancy` 用官方 `LibraryStatus` 读登记和启用状态。档案来自官方配套身份、`.rigbin` 文件和 CAS 中的 `.hd2shared` 归属数据。启用角色前可额外读一个指定停用条目的 Archive，该条目只作为候选。托管 data Mod 和 Lua boot Archive 在这份官方角色登记之外，旧 Foundation 由官方 `LibraryStatus` 过滤。
- `companion-archives` 用官方 `ImportSdkZip` 读取已准备的候选配套，并交给同一 `BundleArchives`。官方解析器的 CAS 写入落在 worker 的一次性临时库。两个动词都绕过 Runtime 目录计算和生成部署，缺少反射接口时沿用 worker 的轻量版本错误。
- 预检和转换使用的装备名来自当前游戏护甲索引。角色名优先用管理器登记，外部登记用官方配套名。设备索引缺少对应名称时显示 Archive。官方共享文件版本冲突、源配套损坏和其他错误继续走既有官方错误路径。

## B–F · 入口行为

| 入口 | 预检与结果 |
| --- | --- |
| 拖入求生 VPK | 每个已分组角色在打包前读取当前启用占用。护甲、头盔分别保留可用的首选，首选被占时从 IAS 页候选目录顺序取第一个空闲项。已成功添加的角色为本批预留 Archive，分配函数接收调用方提供的预留字典。任一槽无空闲则该角色在打包前失败。 |
| 从库添加求生 VPK | 和拖入共用分组与分配，提示使用库中的 Mod 名称。 |
| 拖入普通 Mod、从库添加普通或素体 Mod | 在复制 GPU/stream 侧车和打包前扫描源 patch TOC。Archive 由转换器同一套绑定目录、Unit 匹配和补丁覆盖顺序决定。素体仅扫描根补丁。 |
| 拖入 IAS 主包与配套包 | 已解出的配套在锁外准备阶段通过官方 worker 查 Archive。占用查询使用短读段的 IAS 租约，冲突在部署事务与官方导入之前返回。普通 Mod 和 VPK 的实际转换产物进入同一检查。 |
| 开启已停用角色 | 持有部署锁时读取角色配套 Archive，排除角色自身后对比启用占用，冲突在 `set-enabled` 与 regenerate 前返回。停用沿用既有流程。 |
| 锁内发布 | 导入配套后立刻从真实登记复查 Archive，冲突走既有回滚，且在写角色来源、导入主包和 regenerate 前退出。 |
| 官方异常兜底 | 若 regenerate 仍报官方 Archive 冲突，从异常中定位 Archive，再查此时的启用占用和游戏索引。相同提示口径保留原始异常为内层异常。 |

同批部分添加成功时，继续使用原有结果详情展示后续角色的失败。所有角色均失败时，第一条失败显示为主消息。换底子提示写入既有结果详情。

### 提示文案

- 换底子：`{首选装备名}已被『{占用角色}』占用，『{新角色}』改装到 {候选装备名} 上。`
- 底子耗尽：`可用底子都被已启用角色占用，请先停用其中一个。占用者：『{角色一}』、『{角色二}』。`
- 预检与官方兜底：`与已启用角色『{占用角色}』占用同一装备（{装备名} / {Archive}），请先停用其中一个。`
- 无装备名时的示例：`与已启用角色『2606520120』占用同一装备（59d238094a164c88），请先停用其中一个。` 如果官方异常发生后的登记查询也失败，提示改为 `启用的角色占用同一装备（{Archive}），请先停用其中一个。`，并附官方原始错误与诊断路径。

## 验证

真实库对照测试只读复制 `mod_library.json` 和 CAS 到工作树 `tmp/`，把副本索引重新绑定到临时游戏目录。在内存中逐个复制真实角色条目，调用官方 `InspectEnabledConflicts` 让它报告同一 Bundle 的两个归属者。四组均验证 `RequireClear()` 抛错，且报告中的 Archive 集合与新占用动词、从真实配套副本读出的候选集合一致。原四角色一起检查时官方报告为空，原库索引字节一致。

复跑 `IasOfficialArchiveOccupancyTests.RealLibraryCopyMatchesOfficialConflictReport` 时，`HD2_IAS_W32_LIBRARY` 与 `HD2_IAS_W32_COMPONENTS` 分别指向 Release 的 IAS 库和 1.6.4 组件目录，`HD2_IAS_W32_WORKER` 指向 Debug 的 worker 可执行文件，`HD2_IAS_W32_SANDBOX` 指向工作树内的临时目录。最后一次专项复核原文为 `测试总数: 4`、`通过数: 4`。

| 角色 | 官方报告和两种 worker 查询共同确认的 Archive |
| --- | --- |
| 2606520120 | `59d238094a164c88`、`d2fde496226be96f` |
| Eku原皮 | `0121fd0e8370a1dc`、`5e0dba19a567fb99` |
| VRC-milfy-CE-07 | `fdd3d3e677e5a3f4`、`ffcad1f7ff9888d7` |
| Milfy发光外套 SR-64 | `84fd7aab7cf6f62d`、`b186eae3f2b61509` |

Fake 库覆盖 DP-8 头盔冲突时仅换头盔、同批两角色分配不同底子、耗尽时不调用打包、普通 Mod 以 Unit 归属预检且不调用打包、停用角色不占用、开启冲突角色时不调用官方开关、源复核期间新增占用时锁内拦截并回滚、官方错误转译装备与角色名。另验证成对包在导入 worker 调用前拒绝占用。

默认解决方案并行调度在沙盒退出 `1`，原文 `生成失败。`、`0 个警告`、`0 个错误`。进程命令行和父 PID 确认归属后已清理留下的 630 个 MSBuild 子节点。用本地临时目标把解决方案生成的 `Build` 项目遍历改为串行，`dotnet build WpfApp1.sln -c Debug` 执行该目标退出 `0`，原文 `已成功生成。`、`0 个警告`、`0 个错误`。六个测试项目分开 Debug 构建和全量测试。所有 .NET 命令带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。

| 测试项目 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1841 | 0 | 1841 |
| HD2SourceMigration.Tests | 0 | 595 | 1 | 596 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| 合计 | 0 | 2938 | 1 | 2939 |

SourceMigration 的内嵌材质模板测试要求临时目录的祖先链不含磁盘模板。仓库内的临时目录首轮命中了开发模板，隔离这 10 份模板后全项目复跑通过，模板已恢复且逐份 SHA-256 一致。唯一跳过是套件既有的三角形绕序诊断。

Lua 回归由原仓库 `run_physbone_regressions.py` 编排，沿用已安装的 Lupa LuaJIT 2.1 执行；原文 `合计 PASS=486 FAIL=0；红=0`，两项外部夹具按脚本规则跳过。物理和 Lua 共用实现与 `master` 核对零差异。没有安装开发工具、启动游戏、管理器或 IAS 图形客户端，也没有提交、切分支或推送。

临时验证脚本、解决方案串行目标、真实库副本和复核目录均已清理。初期测试缓存 `tmp/w32-dotnet-temp/` 含只读条目，强制递归清理及逐项删除请求均被自动审批以 `blocked by policy` 拒绝，剩余缓存留在 Git 忽略目录。工作开始前已有的 `.serena/` 保留。

## 写死项与待决

- 生产代码中的数据写死项为零。候选和顺序来自游戏护甲目录与 AQ SDK 绑定目录，装备名来自游戏数据。常量仅包括 worker 协议动词、官方反射成员、官方 Archive 冲突消息前缀与 16 位十六进制格式。样本角色名及其八个 Archive 只出现在测试和报告中。
- 是否在 IAS 页下拉标出「已占用」，待界面设计确认。
- 是否增加手动「换底子」入口，待产品确认。W31 批量安装可复用带预留字典的分配函数。
- 游戏内实装留待派活方验证，本轮覆盖沙盒和官方 DLL 只读对照。
