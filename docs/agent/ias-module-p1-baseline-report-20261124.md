# IAS 模块 P1 基线清理与主线影响审计

审计范围为 `feat/ias-module` 的 `3eed986d85b4e5a59f754d7336c6d524bdea7468` 对比 `master` 的 `332f2d29f46d2edf2d5f2482c51be032d838af40`。核对了总规划 §0、§2、§2.1 和 §7 的 P1。本次只交付 W1，反向转换器移植和 IAS 页属于其他工作。

## 清理与隔离

| 状态 | 文件 | 结果 |
| --- | --- | --- |
| done | `HD2ModManager/Views/SettingsPageView.xaml`、`ViewModels/PageViewModels.cs`、`Services/SettingsService.cs` | 移除 IAS 可选物理四个绑定、手臂姿态设置、两项持久化和启动同步。旧值随设置模型失效。 |
| done | `HD2ModCore/Infrastructure/LuaDeploymentService.cs`、`LuaAddonCatalog.cs`、`PhysBoneLuaSource.cs`、`PhysBoneLuaPackageExport.cs`、`JointPoseData.cs`、`HD2ModCore/HD2ModCore.csproj` | 删除开关和参数。物理模式启用时只接受包含 `ias_` 独立骨的旧包，或包含独立骨数组和 profile 表的纯物理转换包。专用数据仍按启用的 manifest 选项筛选。纯普通配置、独立手动导出继续用主线 Lua 与原安装参数，转换件选 2.1.67。 |
| done | `HD2PhysBoneCompat/src/physbone_{compat,runtime}.lua` | 在载体评分和引擎节点查询前过滤所有非零 IAS 分组，行为与原掩码 0 一致。长发、外套和飘带保留，刘海呆毛、头部挂件、胸和臀固定关闭。独立骨由非空 `independentBones` 数据启动。 |
| done | `HD2PhysBoneCompat/master/physbone_{compat,runtime}.lua` | 新增两份主线 2.1.45 原始 Lua 资源，文件内容与上列 master 提交的 `src/` 对应 blob 逐字节相等。入口、加载顺序和求解均由原始资源提供。 |
| done | `HD2SourceMigration/Retarget/Retargeter{,.ArmRefit,.Pose,.Vertices}.cs`、`HD2ModAdaptation/PatchReconstruction/UnitMesh/SameKeyCanonicalUnitRebuilder.cs` | 回到 master 内容。IAS 现版本走骨名映射和自己的网格编译入口。 |
| done | `HD2SourceMigration/IasConversion/IasDp8Converter.cs` | 更新 `joint-pose.json` 的部署说明。 |
| done | `HD2ModCore.Tests/PhysBoneArmPoseTests.cs`、`HD2PhysBoneCompat/test/arm-pose-test.lua`、`runtime-harness-test.lua` | 改验普通安装行和主线 Lua 哈希、转换件数据触发独立骨、扁平选项还原和物理链过滤。Lua T27 同时验证遗留全开参数也无法开启可选组，普通名的骨即使带高位标志仍保留。 |
| partial | `HD2ModCore/Infrastructure/ApplyExecutor.cs`、同一配置的两类物理 Mod | 详见待决。 |

### 手臂姿态 Lua 保留证据

- `physbone_runtime.lua:2534` 的 `arm_pose_enabled` 为调度入口，`drive` 在 6674、6778、6900 调 `apply_arm_pose_frame`。
- `apply_arm_pose_frame` 位于 5380，5412 的 `hasIndependent` 分支调用 `independent_frame` 并提前返回。5381 还检查 `Compat.apply_arm_pose` 是否存在，旧单体路径在 5470 调用它。
- 保留 `physbone_compat.lua:4443` 的 `M.apply_arm_pose`、直接调用和调参兼容。管理器部署只传转换件数据，通常的普通包不会启用此路径。Lua 独立骨用例已移除一处 `armPose=true` 并继续核对旋转、平移、未列骨、avatar 写入和调用数。

## C# 主线影响逐处审计

位置以 `git diff --unified=0 master...HEAD` 的原 HEAD 起始行计。多个位置列在一行时，每个位置适用同一条件或在该行分别说明。

| 文件 | 原 HEAD hunk 位置 | 判定 | 隔离条件、改法和证据 |
| --- | --- | --- | --- |
| `Retargeter.cs` | 38、83、98、110、157、291、628 | 已恢复 | 原字段、重载、单位缩放、权重来源、骨头位置覆盖和 `head_tip` 路径是旧 IAS 实验。私有构造器唯一调用者传两个 null。四个重定向文件的最终字节恢复到 master。 |
| `Retargeter.ArmRefit.cs` | 79、110 | 已恢复 | 原扭转残差要求 native 名单，普通三权重循环原本等价。随死分支一起恢复 master。 |
| `Retargeter.Pose.cs` | 15、267、409、472、539 | 已恢复 | `InitialBasisRotations` 原先仅在非 ArmScratch 且 native 名单为 null 时走；普通构造器始终满足后一个条件。恢复主线条件和三权重循环。 |
| `Retargeter.Vertices.cs` | 182 | 已恢复 | 恢复主线三权重最大值与平局选择。 |
| `Retargeter.JiggleBones.cs` | 79、83 | 安全 | `IasPhysicsMigration` 独有非空 `selected` 调用；普通调用保留 `bone.Jiggle is not null`、原排序和后续算法。 |
| `IasConversion/NativeRetargetInput.cs` | 新增 1–15 | 安全 | 早期 `2c0f793` 仅在 IAS 私有 `RunNative` 构造的 `RetargetOptions` 将 `ArmScratch/AxisDeltas/AxisRoundtrip` 置 false；`ed6b0e4` 已改成按同名骨直接映射。当前只有 `IasNativeMesh` 调用，三项开关默认值和普通 `Run` 未变。 |
| `VpkConversion/PhysBoneContainerEmbedder.cs` | 44 | 安全 | `EmbedNewPatchAsync` 只有 `IasDp8Converter` 调用，普通 VPK 仍走原嵌入方法。 |
| `VpkConversion/RetargetedMesh.cs` | 2、22 | 安全 | 内部 `NativeSurfaceComponents` 只由 `IasNativeMesh` 赋值，普通顶点为 null。 |
| `VpkConversion/RetargetedMeshBuilder.cs` | 89、206、209、223 | 等价 | 普通调用仍逐点取同一 `result.Output`，只将参数收窄成该列表供 IAS 复用。 |
| `VpkConversion/UnitAuthor.cs` | 56、120、130、480 | 安全 | 普通公开入口传 `nativeLods=null`，用相同 request 生成网格和包围盒；只有转换件带原生组件值，普通组件生成仍走原 switch。 |
| `SameKeyCanonicalUnitRebuilder.cs` | 48、121 | 已恢复 | HEAD 的包装器 `HiddenUnitGenerator.Generate` 直接委托现有 `TryMinify`。共享原调用恢复，IAS 隐藏单元仍经 `CanonicalHiddenUnitBuilder`。 |
| `ApplyExecutor.cs` | 202、843、854、857 | 待决 | 停用向删除函数传库源，部署替换也复用函数。所有只读 patch 都会先清只读，停用可能再设库源只读。现有删除入参没有足以判定 IAS 的来源信息。 |
| `ModLibraryImporter.cs` | 1052、1492 | 安全 | 辅助函数只从已有 manifest 枚举启用 option 的 Include，唯一调用处是 IAS 数据读取；末尾换行无行为变化。 |
| `JointPoseData.cs` | 新增 1–331 | 安全 | 解析器接受独立骨、路由、姿态与 profile 表，管理器调用前校验转换件格式；普通旧肩腿数据会被跳过。 |
| `LuaDeploymentService.cs` | 82、196、504、515、570 | 已隔离、待决 | 删除静态开关，保留两种物理入口，按启用数据选运行时。纯普通配置回到原安装行；混装见待决。 |
| `LuaAddonCatalog.cs`、`PhysBoneLuaSource.cs`、`PhysBoneLuaPackageExport.cs` | Catalog 29、36；Source 37、71；Export 27 | 已隔离 | 无转换数据的 boot、external、手动导出选择原始 Lua。仅验证通过的转换件数据从自动部署两入口注入。 |
| `SettingsService.cs`、`PageViewModels.cs`、`SettingsPageView.xaml` | 64、442、483；1894；114 | 已恢复 | 新增 UI 与绑定、序列化和同步全部移除。 |
| `CliApplication.cs` | 79、3346 | 安全 | 仅三个位置参数且首两个匹配 `ias convert` 或 `ias preview` 才分派；注册 `ias` 命令名后，已有有效命令仍走原分支。非法参数建议列表多一个 `ias` 候选。 |
| `HD2SourceMigration.csproj` | 34 | 安全 | 对 `HD2ModCli.Tests` 开放内部访问，仅供测试。 |

## Lua 主线影响逐处审计

2.1.45 和 2.1.67 的同一位置可能同时含专用与共用改动。以下以原 HEAD 的 hunk 起始位置分组，零上下文 diff 的 166 个 hunk 都落入表中的范围。结论“版本隔离”指纯普通配置使用 master 原始资源。IAS 配置的非 profile 件仍有待决。

| 文件 | 原 HEAD hunk 位置 | 判定 | 隔离条件、改法和证据 |
| --- | --- | --- | --- |
| `physbone_compat.lua` | 444、456、496、522、1861、2128、2156、2205–2235、2349、2364、2409 | IAS 条件与版本隔离 | 链首/分组由 `ias_phys_` 与标志位识别。姿态来源、弹簧参数、世界锚定只在 `isIas` 分支改变；同 hunk 的缓存锚点与缺失位置条件也用于普通链，靠版本隔离。T27 验证必选链和普通链继续挂载。 |
| `physbone_compat.lua` | 1095、1123、1134、1237、1241 | IAS 条件与等价 | 新积分和重力仅在 `gains.ias` 分支，锥角局部缓存保持同一表达式。 |
| `physbone_compat.lua` | 1436、1442、1450、1643、1659、1661、1731、1779、1781、1951、1954、1966、2004、2008、2017、2034、2099、2114、2497、2506 | 版本隔离 | 异常数值、观察判活、rig 评分、attach 缓存从资源/节点数变成分 rig、传送阈值等可以影响共用路径；普通配置保留主线原资源。 |
| `physbone_compat.lua` | 2518、2563、2593、2683、2708、2757、2764、2768、2780、2787、2804、2857、2915、2921 | 版本隔离与 IAS 条件 | 长帧重置、角度链异常处理和末节护栏、写入失败返回值会影响普通链。2.1.67 对原 P1 测试出现 `disabled=nil`。合批刷新只在 `session.iasCommit` 有效。 |
| `physbone_compat.lua` | 3113、3132、3157、3223、3238、3243、3251、3278、3291、3311、3316、3344、3355、3371、3373、3411、3421、3432、3438、3469、3476 | 版本隔离与 IAS 条件 | 尖端健康检查、传送重置/平移、失败时写入计数属于共用变化。投影、补动画覆盖、姿态插值依赖 `iasCommit`、`iasPose` 或独立骨采样状态。 |
| `physbone_compat.lua` | 3485 | IAS 条件 | 新增手臂目标、IK、姿态与握点算子；独立骨和实验 Lua 调参入口才调用。 |
| `physbone_runtime.lua` | 103、127、158、189、201、265、368、372、387、391、429、435、438、464、478、509、525、538、541、646、655、661、694、702、708、724、726 | 版本隔离与 IAS 条件 | 版本、日志、性能计数、安全探针、载体扫描从单归档扩到多归档，旧近邻发现和补挂逻辑被撤。专用分组只处理 IAS 命名，普通配置走原加载器和调度。 |
| `physbone_runtime.lua` | 1170、1227、1229、1275、1278、1282、1286、1431、1556、1798、2316、2322 | 版本隔离 | 相机缓存、重新扫描热键、快照 IO、裙摆判活均在公共驱动路径。 |
| `physbone_runtime.lua` | 2383–6347（含 6340、6344、6347） | 版本隔离与 IAS 条件 | 大 hunk 替换共享前置流程并新增诊断、姿态和尸体模块。Roster 的 profile 枚举、网络身份、3 m 归属会改普通件发现；独立骨和衣物以转换件 ID/骨表驱动，尸体实现分布在 5574–6355，2.1.67 内默认开启。普通配置保留 master 驱动。 |
| `physbone_runtime.lua` | 6367、6370、6384、6423、6533、6540、6587、6596、6598 | 版本隔离、混装待决 | `physics_rig` 按每资源哈希缓存、可接受部分命中；`physics_sync` 以本帧 roster 分配会话并走过滤，取代 master 的挂载。外部普通件不进 IAS roster。 |
| `physbone_runtime.lua` | 6665、6667、6670、6684、6702、6732、6739、6746、6773、6780、6793、6795、6832、6846、6878、6884、6894、6907、6922、6929、6955、6962、7009、7014 | 版本隔离与 IAS 条件 | 普通件去掉健康窗口、旧驱动失效回退和 reconcile、改距离和时间推进；只在姿态门打开时调用手臂/独立骨入口，只在 `iasPose` 会话中运行合批。原主线多人、挂载和 P1 差分测试实际检测到行为差异。 |
| `physbone_runtime.lua` | 7034、7082、7113、7121、7125、7128、7141、7145、7149、7177、7194、7196、7202、7218、7247 | 版本隔离与 IAS 条件 | 安装、卸载、全局钩子和默认性能计数改变公共入口；武器回滚由已有试验状态触发。W1 移除掩码安装项，普通配置使用原始入口。 |

生产路径之外，`mp_harness.lua`、`runtime-harness-test.lua` 转成 profile 枚举用例，`p1-regression-test.lua` 的护栏断言从永久停链改成重置，`skirt-guard-test.lua` 与 `snapshot-test.lua` 的断言适配了新版 API 使用和版本号。`lifecycle-test.lua` 增补恢复用例，`run_physbone_regressions.py` 增补 IAS 用例、lupa 回退和日志隔离。这些已改过的测试不能证明 2.1.45 等价，因此另外跑了未改写的 master 测试脚本。

## 待决

1. **混装时普通 physbone 发现。** 2.1.67 的 `roster_discover`（2669）只枚举 IAS profile，主线普通 Mod 与 IAS 转换件在同一配置时，目前仍只部署一份运行时。纯普通配置已按主线隔离。混装要确定如何保持普通发现与 IAS 的性能边界，以及两个运行时怎样共享 render/update 和同一件的所有权。多个启用的 IAS 转换包也仍只选第一份可识别数据。建议先定混装发现方案，再补双来源测试，避免将两套调度直接并挂。
2. **只读硬链接删除。** HEAD 的补丁对普通 patch 生效，失败时清只读成功但后续删除失败还可能让源保持可写。当前保留已有实现和测试，等待决定将修复独立送审、还是为部署状态增加可判定来源的字段后做 IAS 限定。
3. **缺失或损坏了转换元数据的旧包。** 运行时选择依赖可识别的 `joint-pose.json`；只有 patch/sidecar、无可识别元数据的孤包不能保证使用 2.1.67。自带完整元数据的实测版本包走 IAS 路径。未启动游戏、管理器或 IAS 图形客户端，游戏引擎行为仍需后续交测。

## 构建与测试

### Debug 构建

解决方案命令为 `dotnet build WpfApp1.sln -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false`。诊断输出定位到 MSBuild 子项目任务返回失败。NuGet 联网尝试另报 `NU1301`，端点 `127.0.0.1:9` 拒绝连接。改用本机已有包缓存作为只读还原回退源后完成还原，解决方案构建仍退出 1，输出为：

```text
生成失败。
    0 个警告
    0 个错误

已用时间 00:00:04.96
```

随后逐项目编译，管理器含 CLI 依赖的最后一次 Debug 构建退出 0。保留要求的三个 MSBuild 参数，附加 `--no-restore -p:BuildInParallel=false -p:UseSharedCompilation=false`。输出原文：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:15.86
```

### .NET 全量测试

解决方案级测试命令含 `--no-build` 的重试在汇总前退出 1。逐个测试项目执行 `dotnet test <项目> -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false` 后得到所有六个项目的结果，未设置测试过滤器。还原使用本机缓存回退，`TMP`、`TEMP`、`LOCALAPPDATA`、`DOTNET_CLI_HOME` 指向本工作树的隔离目录。

```text
已通过! - 失败:     0，通过:  1653，已跳过:     0，总计:  1653，持续时间: 6 s - HD2ModCore.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 8 s - HD2ModCli.Tests.dll (net10.0)
失败!  - 失败:     1，通过:   565，已跳过:     1，总计:   567，持续时间: 6 s - HD2SourceMigration.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 195 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 34 ms - HD2SourceModel.Tests.dll (net8.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 14 ms - Decryptor.Tests.dll (net8.0)
```

合计 2,720 通过、1 失败、1 跳过。唯一失败为 `Hd2MaterialTemplateTests.EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists`。测试从 `Path.GetTempPath()` 建目录，模板定位逐级向上找到本工作树的 SDK 模板，未命中预期内嵌分支。失败原文摘录如下。测试与模板定位器均经 `git diff master --exit-code` 核实完全相同。尝试为同一工作树内的临时目录建立独立祖先视图时，Windows `subst` 返回 `Access denied`，未建立映射。

```text
Assert.Equal() Failure: Strings differ
Expected: "embedded"
Actual:   "D:\\gitCode\\HD2ModManager-ias-module\\tools"···
```

### Lua

`python tools/physbone-harness/run_physbone_regressions.py` 退出 0，使用环境已有的 Python lupa 2.8。输出原文摘录：

```text
arm-pose-test.lua              PASS         PASS=185  FAIL=0   exit=0
conform-stub-test.lua          SKIP(缺夹具)    PASS=0    FAIL=0   exit=1
mp_harness.lua                 PASS         PASS=47   FAIL=0   exit=0
perf-combat-test.lua           PASS         PASS=38   FAIL=0   exit=0
runtime-harness-test.lua       PASS         PASS=83   FAIL=0   exit=0

合计 PASS=924 FAIL=0；红=0
```

另用 Lua 5.1 执行 master 原始用例，固定测试脚本，仅替换运行时代码。原 P1 的 `disabled=nil` 和原挂载/多人测试的无会话由交叉运行直接复现，不能由新版 harness 已通过推定两版行为等价。

```text
runtime-harness-test.lua master: exit=0 PASS=120 FAIL=0
runtime-harness-test.lua w1: exit=1 PASS=9 FAIL=9
mp_harness.lua master: exit=0 PASS=37 FAIL=0
mp_harness.lua w1: exit=1 PASS=0 FAIL=3
p1-regression-test.lua master: exit=0 PASS=18 FAIL=0
p1-regression-test.lua w1: exit=1 PASS=17 FAIL=1
schedule-test.lua master: exit=0 PASS=54 FAIL=0
schedule-test.lua w1: exit=0 PASS=54 FAIL=0
```

### 范围与交付状态

设置页已与 master 逐字节核对。版本选择、启动参数和源文件哈希由 Core 测试覆盖，非 IAS 原始 Lua 单独跑主线回归。VPK 重定向的四个恢复文件、`RetargetOptions.cs` 和同键重建文件已通过 `git diff master --exit-code`。验证未安装工具，未启动图形程序，未访问真实部署目录进行写入，未提交、切分支或推送。独立工作树中的 SDK 模板和其他任务文档均保留。

自动执行审查以 `blocked by policy` 拒绝了本任务临时目录的删除命令。构建和回归日志仍在 Git 忽略的 `tmp/ias-w1-checks/`。已移除本次 Serena 工具生成的配置文件。

交付门为 **partial**。Lua 通过，管理器 Debug 项目为 0 警告、0 错误。解决方案级 Debug 和全量测试全绿的门槛仍未达成。
