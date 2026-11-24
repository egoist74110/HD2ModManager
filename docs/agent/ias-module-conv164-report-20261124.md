# W7 普通 Mod 与求生 VPK 转 IAS 1.6.4 验收

工作树为 `D:\gitCode\HD2ModManager-ias-conv`，分支为 `feat/ias-module-conv`。两条路线的样本均重新生成双包，并通过 IAS 1.6.4 角色库单独部署、与 Milfy 共存部署、删除 Milfy 后部署和事务恢复。验证使用原型的 `--sandbox-metadata` 对照模式。布莱默顿在三个部署状态下均有 11 个 profile、188 个物理节点。龙炎均有 11 个 profile、0 个物理节点，与本次源包的离线回读一致。

## 根因与样本对照

用复制的原型复跑 W4 布莱默顿。角色库导入后，`NativeRuntimeInputs.Generate` 在共存和单独的装配阶段均报 `ValueError: SDK point declaration has no project metadata`。日志为 `tmp/ias-w7/probe-w4-control.log`，暂存文件列表为 `tmp/ias-w7/run-175a5ede62ae4979bbdea0d6f682b6b2/profiles/{03-two,04-bremerton-control}-failed-rig.txt`。W4 离线验证直接把 ZIP 成员放入 CAS，身体的 `.hd2author` 因此仍可被装配阶段按文件名找到。正式客户端的 `OwnedStore` 把作者声明从运行时成员中分出，暂存 rig 中不含该文件。

| 项目 | Milfy SDK 样本 | W4 产物及 W7 处理 |
|---|---|---|
| `mod_group.json` | 含各 Archive 的成员及 SHA-256 | 两条路线已有 SDK 分组。W7 由同一分组器登记新预览文件和哈希。 |
| `<project>.<archive>.hd2author` | 身体、头盔均有，客户端单独处理 | W4 布莱默顿已有。进入正式暂存 rig 后，身体没有这个项目名文件。龙炎的 RigOnly 包无作者声明。 |
| `<project>.<archive>.hd2preview` | 身体有 `30d54711a4a94d60bb484df55f52f43b.b186eae3f2b61509.hd2preview`，正式暂存可见 | W4 布莱默顿缺失。W7 身体新增 `7ec305f67bb54763ba9c540dd63b2a59.58e4bd4b2278d15c.hd2preview`。 |
| `<project>.<archive>.rigbin` 和单 Unit `.hd2phys` | 头盔有带项目前缀的骨架和物理 | 布莱默顿头盔已有相应文件。龙炎身体和头盔均有带项目前缀的 RigOnly 骨架。 |
| `shared_*` | 身体使用 SDK 共享物理 v3，含姿态资源 | 布莱默顿沿用原有 v2 共享物理；规范名称、内部引用、骨架及编译结果均保留。 |
| `owned_*.isolation.ini` 与点网格 | owner 和 point ID 绑定同一项目与 Archive | W4 的 ID 已符合公式。W7 增加一致性校验。对照两轮 ZIP，两个主包各自的全部 `Files/` 成员 SHA-256 逐项相同。 |

IAS 1.6.4 的 `declared_assembly.py` 从暂存 rig 的 `<project>.<archive>.<suffix>` 恢复项目。使用 UTF-8 的 `project + NUL + archive` 求 12 字节 BLAKE2s，拼成 `owned_<hex>`。解析点网格时，将原 Unit ID 读为整数再转换为十进制字符串，拼成 `HD2ArmorPoint1 + NUL + project + NUL + archive + NUL + source_decimal`。BLAKE2b 输出 8 字节，个性化参数为 `HD2Point`，按小端解释成整数后格式化为 16 位十六进制。Milfy 身体的真实向量 `0c74e05811da7b90 → b45c16a6c67aad62` 和 `feb7708e2fb358ed → 0803c9bbc6babaf6`，连同身体与头盔文件名和 owner ID，已写入测试。

## 实现

- `VpkExtractor.cs` 在求生 VPK 的身体物理路径中，把回读 Unit 的骨矩阵、父索引、Mesh/Stream 表和骨调色板写入转换暂存。正式 ZIP 只收录 SDK 编译出的预览配套。
- `package_model.py` 用最终隔离 Unit 的 GPU 字节、物理 rig profile 和共享消费者 `.rigbin` 调用 AQSDK `preview_transport.compile_preview`。SDK 负责生成几何见证、消费者骨架 SHA-256 与文件校验信息。无法生成有效预览时打包报错，服务会清理暂存。
- `project_identity.py` 和 `package_model.py` 校验预览或骨架文件名、SDK isolation owner 文件名及主包点网格 ID 使用同一项目和 Archive。`IasDependencyLocator.cs` 将预览编译器纳入 AQSDK 依赖检查。
- `HD2SourceMigration.Tests/test_ias_project_identity.py` 包含三项可脱离样本目录运行的 Milfy 向量测试。新增 Python 测试使用单独的 `unittest` 命令执行。继承自工作区的 `HD2SourceMigration.Tests/Hd2MaterialTemplateTests.cs` 差异未在 W7 修改。

## 正式角色库流程

将现有原型、官方发行输入和既有对照 DLL 复制到 `tmp/ias-w7/`。原型在子进程内调用 `OwnedStore.ImportToLibrary` → `NativeRuntimeInputs.Create(mods=true)` → `NativeRuntimeInputs.Generate` → `Apply` → `ExternalRuntimeDeployment.ExportData`。把 Milfy 暂时禁用以单独部署候选包，再启用 Milfy 验证共存，随后正式删除 Milfy 并应用。基底与每次部署均在隔离的游戏和运行目录中执行。宿主副本的 SHA-256 与 1.6.4 清单相符。

`--sandbox-metadata` 使用 P0 的对照 DLL，SHA-256 为 `6961116060ADF087EA14890A7B3C47BC1F3E7281F69B39A8B9262F444CFB0E71`，允许忽略沙盒中的 `File.Replace` 元数据合并错误。角色库导入、校验和 Generator 装配均走原型内的正式调用；Generator 使用发行版原文件。两次运行均退出 0，原文均为 `RESTORE_BASELINE_MATCH files=2; transactions=5`、`GUI_ASSEMBLIES=0`、`PASS`。

哈希清单目录：

- 布莱默顿 [manifests](../../tmp/ias-w7/run-adaf2dbbf8284e6abe23df4a8c954288/manifests)，原始日志 `tmp/ias-w7/probe-vpk.log`。
- 龙炎 [manifests](../../tmp/ias-w7/run-5b1ea92fb3f04ea0be79fd9341d10c07/manifests)，原始日志 `tmp/ias-w7/probe-patch.log`。

表中数值为「profile 数 / 物理节点数」，物理节点按 profile 求和。每条路线的哈希清单均为上述对应目录内的文件。

| 步骤 | Milfy | 布莱默顿 | 龙炎 | 每步产物 SHA-256 清单 |
|---|---:|---:|---:|---|
| 初始快照、安装基底 | 0 / 0 | 0 / 0 | 0 / 0 | `00-initial.json`、`01-base.json` |
| 导入并部署 Milfy | 19 / 380 | 0 / 0 | 0 / 0 | `02a-milfy-library.json`、`02-milfy.json` |
| 导入候选并单独部署 | 0 / 0 | 11 / 188 | 11 / 0 | `02a-candidate-only-library.json`、`02b-candidate-only.json` |
| 启用 Milfy 并共存部署 | 19 / 380 | 11 / 188 | 11 / 0 | `03a-two-library.json`、`03-two.json` |
| 从库删除 Milfy 并部署 | 0 / 0 | 11 / 188 | 11 / 0 | `04a-remove-library.json`、`04-remove-milfy.json` |
| 恢复五笔事务并清空沙盒角色库 | 已恢复 | 已恢复 | 已恢复 | `05-restore-5.json` 至 `05-restore-1.json`、`06-final.json` |

双包位于 `tmp/ias-w7/final-vpk/` 和 `tmp/ias-w7/final-patch/`。各目录的 `offline-164.json` 为独立离线回读，分别确认 11 个骨架 Unit 和 12、6 个点网格 Unit。`compiled_profiles.json` 保存离线 profile。三个候选部署状态均逐一匹配离线 profile ID 和物理节点列表，部署的 `physics-nodes.txt` 与各 profile 相同。解码两个主包后，每个 Archive 的 Unit ID 集合与正式部署的装配合同一致。双包、主包内部资源 SHA-256 及逐阶段比对结果见 [verification.json](../../tmp/ias-w7/verification.json)。

## 构建与测试

所有 .NET 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。Solution 恢复成功后，单次 `dotnet build WpfApp1.sln -c Debug` 退出 1，末尾为 `生成失败。`、`0 个警告`、`0 个错误`，与 W4 的观察一致。`tmp/ias-w7/build-solution.log` 保留原文。切换为按项目构建。CLI、管理器和复制的验证原型均退出 0，CLI 与管理器日志覆盖 SourceMigration 及各自引用项目。原始日志为 `tmp/ias-w7/build-{cli,manager,probe}.log`。

```text
已成功生成。
    0 个警告
    0 个错误
```

按项目执行全量 Debug 测试，六个程序集均退出 0。下表为原文数字，逐项目日志为 `tmp/ias-w7/test-<程序集>.log`。

| 测试程序集 | 失败 | 通过 | 已跳过 | 总计 |
|---|---:|---:|---:|---:|
| HD2SourceMigration.Tests | 0 | 574 | 1 | 575 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| HD2ModCore.Tests | 0 | 1655 | 0 | 1655 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| 合计 | 0 | 2731 | 1 | 2732 |

```text
已通过! - 失败:     0，通过:   574，已跳过:     1，总计:   575，持续时间: 6 s - HD2SourceMigration.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 6 s - HD2ModCli.Tests.dll (net10.0)
Ran 3 tests in 0.000s
OK
```

.NET 跳过的是既有的 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。Python 的执行命令为 `python -B -m unittest discover -s HD2SourceMigration.Tests -p test_ias_project_identity.py -v`，日志为 `tmp/ias-w7/test-identity.log`。Lua 未涉及本次修改。未安装开发工具。保留任务原型、输入副本、产物、恢复过的沙盒和验收日志，便于复核。工具拒绝递归清理命令，因此 `tmp/ias-w7/tool-temp` 和 `tmp/ias-w7/tool-home` 仍有构建及测试缓存。无提交、切分支或推送。

## 待决

- 龙炎源目录顶层可转换输入是一个 patch 及 GPU、stream 侧车。CLI 原文为 `输入中没有可编译的 IAS 物理链，配套包仅包含骨架和绑定。` 龙炎当前验收覆盖真实源包的 11 个 profile。若需验收普通 Mod 的非零物理，需提供带可读取物理链的源样本或明确源物理格式。
- 共享身体物理若没有任何满足 AQSDK 预览见证条件的几何，本次实现会退出并报告 Archive。两份验收样本均已通过。要覆盖该边界，需要可复现样本和 IAS 认可的项目文件格式。
- 证据范围为无窗口角色库、元数据生成、磁盘部署、导出及恢复。完整原版客户端的 `File.Replace` 提交和游戏中物理效果未在沙盒验收。
