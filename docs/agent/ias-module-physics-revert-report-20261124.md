# W9 · IAS 转普通 Mod 与物理运行时回退

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`。核对了 `master` 与当前分支的 merge-base，两者均为 `332f2d2`。本报告按 W9 的最新决定验收，覆盖规划 §0.6 中关于转换输出的早期决定。

## 回退清单

| 路径 | 结果 |
| --- | --- |
| `HD2PhysBoneCompat/` | 源码与 Lua 测试恢复为 master，删除新增的七个 Lua 测试及临时双运行时副本，当前运行时版本为 `2.1.45` |
| `HD2ModCore/Infrastructure/` | `ApplyExecutor.cs`、`LuaAddonCatalog.cs`、`LuaDeploymentService.cs`、`ModLibraryImporter.cs`、`PhysBoneLuaSource.cs`、`PhysBoneLuaPackageExport.cs` 恢复为 master，删除 `JointPoseData.cs` |
| `HD2ModCore/HD2ModCore.csproj` 与 `HD2ModCore.Tests/` | Lua 内嵌资源声明与 `ApplyExecutorCleanupSafetyTests.cs` 恢复为 master，删除 `PhysBoneArmPoseTests.cs` |
| `tools/physbone-harness/` 与 `tools/ias-preview/` | 回归入口恢复为 master，删除独立骨重放、旧 Python 预览和验包脚本 |
| `HD2SourceMigration/Retarget/` 与 `VpkConversion/PhysBoneContainerEmbedder.cs` | 共用重定向和物理容器嵌入逻辑恢复为 master |
| 其他共用文件 | `Hd2MaterialTemplateTests.cs`、`SameKeyCanonicalUnitRebuilder.cs`、`SettingsService.cs` 与 `SettingsPageView.xaml` 恢复为 master |

上述路径在 `git diff master --stat` 中均无差异。W10 的冲突提示及 `PageViewModels.cs` 中复用的设置保存方法保留。IAS 页导航、CLI 入口、项目和解决方案接线保留。IAS 打包方向的文件保留了工作开始时的内容。Git 索引存放在主仓库的只读目录，使用 `git show master:<路径>` 取回源文件字节，原暂存区保留，W9 变更留在工作区。

## 保留的共用接口扩展

| 文件 | 使用方与用途 | master 调用 |
| --- | --- | --- |
| `Bc7Encoder.cs` | IAS 打包的 `VpkExtractor` 使用带 `temporaryRoot` 的内部构造函数，将纹理转换的临时文件放到工作目录 | 公共构造函数继续使用系统临时目录 |
| `RetargetedMesh.cs` | `IasNativeMesh` 将原生 UV 和顶点颜色分量传给 `UnitAuthor` | 既有顶点无原生分量，沿用现有分量生成 |
| `RetargetedMeshBuilder.cs` | `IasNativeMesh` 调用接受最终顶点位置的内部法线计算重载 | 既有入口继续从 `RetargetResult.Output` 获取位置并运行相同算法 |
| `UnitAuthor.cs` | `IasDp8Converter` 使用内部重载，按目标 LOD 选用对应 IAS 网格并保留已有原生 UV、颜色分量 | 公共入口继续沿用单一网格和原有分量生成 |

## 转换器

- 原版骨骼名和哈希直接用于标准蒙皮。专用于 IAS 的非模拟骨沿骨骼父链折叠到原版骨，合并同骨权重后取最多四项并归一化。
- 删除独立骨改名和重绑定、手臂与腿部目标、服装姿势、握把转换，以及尸体和运行时 profile 输出。输入 `.rigbin` 中的握把扩展按记录长度跳过。产物与外层输出目录中的 `arm-pose.json`、`joint-pose.json` 均为零。
- 保留有顶点权重的模拟链及其父链，使用 `jiggle_` 命名的模拟节点和原版父骨锚点。通过 master 已有的 `UnitTransformInfoAppender`、`PhysBoneSpringRig` 与 `PhysBoneCarrier` 写入 `HD2PBX01` 容器和 `HD2SRG01` v2 弹簧 rig。保留刚度、换算后的阻尼与重力，以及 master 能表达的限位近似。碰撞体、链间约束、接触项和可选组标记未迁移。
- 移除 `IasDp8Result` 的肩、腿结果字段与 CLI 对应输出。移除旧 `ias preview` 子命令。管理器与 `ias to-standard` 已通过 `ModDirectory` 获取产物，原调用满足新结果类型。

## Milfy 实样验证

- 输入样本为 `D:\gitCode\HD2ModManager\tmp\ias-to-patch\sample`，游戏数据为 `E:\SteamLibrary\steamapps\common\Helldivers 2\data`。两者只读。产物位于 [tmp/ias-w9/milfy-standard/mod](../../tmp/ias-w9/milfy-standard/mod)。
- `hd2mod ias convert` 退出码 `0`，原文为 `本体 20 个 Unit，四个选项合计 6 个 Unit。` 回读实际补丁条目得到同样计数。本体材料依赖的 9 张外部贴图、1 个外部父材质均在游戏归档找到。
- 对 26 个 Unit 回读骨骼表与蒙皮调色板。哈希全部落在原版 Avatar、对应原版护甲 Unit 或该补丁明确声明的弹簧骨集合中，旧 `ias_` 骨骼哈希和两份姿势 JSON 均为零。
- 用 master 的 Lua `load_rig` 和 `build_chains` 回读生成的三份 GPU 侧车，头盔为 234 骨 / 41 链，本体护甲为 32 骨 / 10 链，穿外套为 92 骨 / 20 链。
- `hd2mod import plan` 退出码 `0`，返回 `"ok": true`，以 `manifestStrict` 识别本体及四个选项目录，阻断诊断为零。[导入计划](../../tmp/ias-w9/milfy-standard-import-plan.json)

## 构建与测试

解决方案级 Debug 构建退出码 `1`，原文为 `生成失败。`、`0 个警告`、`0 个错误`。诊断日志显示 MSBuild 子项目任务返回失败，解决方案级测试调用也未产出汇总。改为按项目使用 `-p:BuildInParallel=false -p:UseSharedCompilation=false` 构建管理器、引用项目和六个测试项目，每次构建均退出 `0`，原文均为 `已成功生成。`、`0 个警告`、`0 个错误`。所有 .NET 命令均带要求的三个参数。

六个测试项目逐个执行完整测试，无过滤器。计数取自各项目的 `dotnet test` 汇总。

| 项目 | 失败 | 通过 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| `Decryptor.Tests` | 0 | 5 | 0 | 5 |
| `HD2ModAdaptation.Tests` | 0 | 212 | 0 | 212 |
| `HD2ModCli.Tests` | 0 | 213 | 0 | 213 |
| `HD2ModCore.Tests` | 0 | 1709 | 0 | 1709 |
| `HD2SourceMigration.Tests` | 0 | 556 | 1 | 557 |
| `HD2SourceModel.Tests` | 0 | 72 | 0 | 72 |
| 合计 | 0 | 2767 | 1 | 2768 |

`HD2SourceMigration.Tests` 完整复测时启用了 Milfy 输出夹具断言。master 的内嵌模板测试要求临时目录上溯时找不到磁盘 SDK 模板。仓库内的沙盒会命中模板，因此复测期间临时换名隔离了模板目录，结束后复原并比对文件哈希。该项目原有的一项三角形绕序诊断仍按测试声明跳过。

master 的 `python tools/physbone-harness/run_physbone_regressions.py` 退出码 `0`，原文为 `合计 PASS=486 FAIL=0；红=0`。两个外部夹具用例由 master 脚本标记为 `SKIP(缺夹具)`。使用本机已有的 Python lupa 2.8 所带 LuaJIT 2.1，通过任务目录下的临时命令入口运行。没有新增开发工具。首次 .NET CLI 初始化显示了 HTTPS 开发证书提示，检查证书存储仅见 2026-07-29 起生效的既有开发证书，后续命令关闭了该初始化步骤。

`git diff --check` 通过。相对 master 的整体检查仍报告既有 `docs/agent/ias-module-conv-report-20261124.md` 文件末尾空行。失败调用遗留的 MSBuild 节点已按启动时间与命令行核对后终止。临时脚本已移除。自动审批以 `blocked by policy` 拒绝了沙盒临时目录的递归清理和逐项清理，复测日志及临时缓存仍在 Git 忽略的 `tmp/ias-w9/`。Milfy 产物和导入计划也留在该目录。提交、分支和推送均由派活方处理。

## 待决

- Milfy 的游戏内显示、动作随动和物理帧率需要实机确认。本次验证覆盖了真实游戏数据转换、补丁与蒙皮回读、master Lua 读入和普通 Mod 导入预检。
- 解决方案单命令在当前环境中仍未通过，Debug 构建和全量测试结果来自逐项目运行。
