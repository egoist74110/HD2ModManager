# W4 转换器移植与 IAS 1.6.4 回读报告

工作树 `D:\gitCode\HD2ModManager-ias-conv`，分支 `feat/ias-module-conv`，基线 `3eed986`。按总规划 §0、§2、§5、§6 执行。移植代码来自 `262be9f` 和只读 IAS 实验目录。改动留在工作区。

## 交付状态

| 事项 | 状态 | 结果 |
|---|---|---|
| P1 普通护甲 Mod → IAS | 部分完成 | 龙炎 AR479939 已出双包并通过 1.6.4 回读。提供的旧 patch 提取器没有物理链导出路径，此样本只验证骨架与装配。 |
| P1 求生 VPK → IAS | 完成 | 布莱默顿已出双包并通过 1.6.4 回读。配套包含 IAS 原生物理。 |
| 依赖定位、1.6.4 与 CLI | 完成 | 使用给定目录与官方 Generator Python，缺依赖给出下载出处。实际传入 1.6.3，CLI 返回 2 且无输出目录。 |
| P0③ 回读 | 完成 | 旧布莱默顿及本次两个新产物均通过。 |
| 详情页动作、独立工具工程 | 范围外 | 服务位于 `HD2SourceMigration`，前端接入留给 IAS 模块。 |

## 文件清单

| 文件 | 作用 |
|---|---|
| `HD2SourceMigration/IasPackaging/IasPackagingService.cs`、`IasPackagingOptions.cs` | UI 无关入口、选项、进度、取消、警告及错误结果。同卷暂存，检查齐两份 ZIP 后整目录发布。 |
| `HD2SourceMigration/IasPackaging/IasDependencyLocator.cs` | 可替换的依赖接口与目录实现。识别 Generator 目录或含 Generator 的发行根目录。 |
| `HD2SourceMigration/IasPackaging/PatchExtractor.cs`、`IasAssets.cs` | 提取普通 patch 的 Unit、权重和材质。按装备目录匹配并补齐槽位。 |
| `HD2SourceMigration/IasPackaging/VpkExtractor.cs`、`CustomProportionRetarget.cs` | 求生模型、材质、比例与姿态移植，输出摆动骨和物理末端。 |
| `HD2SourceMigration/IasPackaging/Scripts/ias_package.py`、`package_model.py` | 调用 AQSDK 做资源隔离、骨架和物理编译，通过官方 Generator 的归档编解码器写入并校验 patch。 |
| `HD2SourceMigration/IasPackaging/Scripts/ias_physics/{format_v1.py,reference.py,LICENSE,NOTICE.txt}` | `HD2-PhysBoneTool@ebb91bc` 的两份 GPLv3 离线 HD2PHY1 编码源文件与出处。转换期生成 IAS 的 `.hd2phys` 和共享物理资产。 |
| `HD2SourceMigration/HD2SourceMigration.csproj`、`HD2ModManager/HD2ModManager.csproj` | 随 CLI 与管理器复制脚本，单文件发布保留物理文件。管理器使用发行更新器允许的 `data/resources/ias`。 |
| `HD2SourceMigration/VpkConversion/Bc7Encoder.cs` | 增加供 IAS 调用的内部临时目录参数，把 texconv 中间文件放进转换暂存。现有公开构造参数及默认路径行为沿用。 |
| `HD2ModCli/CliIasPackaging.cs`、`CliApplication.cs`、`CliArguments.cs` | 注册两条打包命令、选项、帮助、JSON 输出和 Ctrl+C 取消。 |
| `HD2SourceMigration.Tests/IasPackagingServiceTests.cs` | 8 个测试覆盖输入校验、缺失依赖、拒绝覆盖、失败清理。 |
| `HD2SourceMigration.Tests/Hd2MaterialTemplateTests.cs` | 原有内嵌模板测试使用盘符根下的未创建路径。仓库内 TEMP 曾使命中仓库自带模板，修正后依旧验证内嵌副本，测试也无需写盘符根。 |
| `docs/agent/ias-module-conv-report-20261124.md` | 本报告。 |

## API 与 CLI

```csharp
var service = new IasPackagingService(); // 可传入 IasDependencyLocator
var options = new IasPackagingOptions(outputDirectory, gameDataDirectory)
{
    GeneratorDirectory = generatorDirectory,
    AqSdkDirectory = aqSdkDirectory,
    TexconvPath = texconvPath,
    Name = name,
    RigGender = IasRigGender.Female,
    WithIasPhys = true,
    ModelId = existingModelId,
};

IasPackagingResult patch = await service.ConvertPatchModAsync(patchDirectory, options, progress, ct);
// VPK 转换应使用另一个全新的 outputDirectory。
IasPackagingResult vpk = await service.ConvertL4d2VpkAsync(vpkPath, options, progress, ct);
```

两条入口返回 `Task<IasPackagingResult>`。结果含 `MainModZip`、`CompanionZip`、`ModelId`、`Name`、`Warnings`、`Errors` 和 `IsSuccess`。失败时两份路径为空。取消按 .NET 约定传播 `OperationCanceledException`。可选 `BoneHashPath` 覆盖随程序分发的哈希表。Patch 目录按旧管理器入口只读顶层 `.patch_*` 及其侧车，避免递归混合互斥选项。

```text
hd2mod ias pack hd2 <patch目录> --output <新目录> --game-data <data目录> --ias <Generator目录> --aqsdk <AQSDK目录> --json
hd2mod ias pack l4d2 <vpk> --output <新目录> --game-data <data目录> --ias <Generator目录> --aqsdk <AQSDK目录> --texconv <texconv.exe> --json
```

名称默认取输入名，UUID 默认新建，可用 `--name`、`--id` 覆盖。`--rig-gender female|male` 默认 `female`。默认尝试编译 IAS 物理，`--no-ias-phys` 关闭该次编译。`--l4d2-root` 指定含 `pak01_dir.vpk` 的共享材质目录，`--model-stem` 选择多模型 VPK 内的模型。只有 VPK 链路需要 texconv，缺省使用给定 AQSDK 下的 `deps/texconv.exe`。

Generator 的 `generator.json` 不暴露发行版本。定位器用官方 1.6.4 `library_catalog.py` 的 SHA-256 校验，配合 Python、归档脚本和清单存在性，支持单独提供 Generator 目录。AQSDK 本轮校验所需入口、模块和数据表，样本运行使用标记为 `86b7252` 的解压目录。下载和提交固定由后续依赖实现处理。

## P0③ 官方 Generator 1.6.4

使用官方 `Generator/python/python.exe -B -X utf8`。按实验验证脚本把 Companion 成员按 SHA-256 放入隔离 CAS，对真实包调用 `metadata_generation.generate`，再解码主包的 patch，对所有 Unit 调用 `unit_geometry.inspect`。物理判据是 1.6.4 编译出的 profile 中的 `physics_nodes`，表中节点数为各 profile 的节点数合计。

| 样本 | metadata_generation | profile | assembly | rig Unit / 占位 Unit | 有物理的 profile / 物理节点 | 错误原文 |
|---|---|---:|---:|---:|---:|---|
| 旧布莱默顿 1.6.3 `*_WithIasPhys.zip` 原样 | PASS | 11 | 2 | 11 / 12 | 11 / 94 | 无 |
| 新布莱默顿 `2505895990.vpk` | PASS | 11 | 2 | 11 / 12 | 11 / 188 | 无 |
| 新龙炎 AR479939 patch | PASS | 11 | 2 | 11 / 6 | 0 / 0 | 无 |

旧布莱默顿 MainMod 与 WithIasPhys Companion 无需重新打包，即可被 1.6.4 的元数据生成器和 Unit 校验器读取。新布莱默顿的物理节点数与旧包不同，离线通过只证明格式和绑定可被识别。龙炎实际复制的是源库中 `(抖动)尘白-胧嫣原皮换O-3甲_AR479939` 的 patch 与 GPU、stream 侧车，CLI 原文为 `输入中没有可编译的 IAS 物理链，配套包仅包含骨架和绑定。`

实际运行新包使用模型 UUID `7ec305f6-7bb5-4763-ba9c-540dd63b2a59`（布莱默顿）和 `bb87dba0-6ce8-4de3-9897-b47898304505`（龙炎），CLI 返回值与主包清单都保留了指定 UUID。新布莱默顿 Companion 中共有 3 个 `.hd2phys` 或 `.hd2shared` 文件。两份新 Companion 的 Lua 文件和 `physbone-rig.bin` 均为 0。双包分别保存在 `tmp/ias-w4/final-vpk/` 和 `tmp/ias-w4/final-patch/`，回读记录为同级 `old164.json`、`final-vpk164.json` 和 `final-patch164.json`。

## 构建与回归

所有 .NET 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。本机 solution 单次调用失败，设置本机已缓存的 `NUGET_PACKAGES` 后还原成功，solution 构建仍退出 1，末尾原文为 `生成失败。`、`0 个警告`、`0 个错误`。solution 的 `dotnet test ... --no-build` 同样退出 1 且无测试输出。此前未指定缓存时出现 `error NU1301: 无法加载源 https://api.nuget.org/v3/index.json 的服务索引。`

改用 `dotnet build <csproj> -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false -p:UseSharedCompilation=false` 逐项目覆盖 solution 的全部 14 个工程。每个退出 0，输出原文均为：

```text
已成功生成。
    0 个警告
    0 个错误
```

逐个对 solution 的全部六个测试项目运行 `dotnet test <csproj> -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false -p:UseSharedCompilation=false`。原文数字见下表。

| 测试程序集 | 失败 | 通过 | 已跳过 | 总计 |
|---|---:|---:|---:|---:|
| HD2ModCore.Tests | 0 | 1655 | 0 | 1655 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| HD2SourceMigration.Tests | 0 | 574 | 1 | 575 |
| 合计 | 0 | 2731 | 1 | 2732 |

六个测试项目均退出 0。唯一跳过的 .NET 用例是 `TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。构建和测试原始日志分别为 `tmp/ias-w4/all-build-final.log`、`tmp/ias-w4/test-all-final.log`。

本地 `dotnet publish` 的 win-x64 单文件 CLI 退出 0，发布目录包含打包脚本、离线编码器、许可证与哈希表，发布版 `hd2mod ias pack --help` 退出 0。管理器 Debug 输出中的 `data/resources/ias` 也包含全部脚本。两次最终样本转换由管理器输出目录里的 `hd2mod.exe` 执行。

Lua 回归用 `python tools/physbone-harness/run_physbone_regressions.py`，退出 0，原文合计 `PASS=925 FAIL=0；红=0`。`conform-stub-test.lua` 因缺夹具跳过。原始日志在 `tmp/ias-w4/lua-final.log`。未安装开发工具。

本次转换的 `.ias-pack-*` 暂存目录已清空，复跑前的重复 ZIP 和临时脚本已清理。沙盒拒绝递归清理命令，部分构建、测试缓存和复制的输入仍留在 Git 忽略的 `tmp/ias-w4/`。

## 待决

- 普通 Mod 的物理输入合同。当前给出的旧 patch 提取器只生成骨架 authoring 数据，龙炎历史配套包也是 RigOnly。当前 patch 路径保留原自定义骨与权重，但没有从陌生骨名推断物理参数。需要一份用户实测含物理的原始普通 Mod 与对应 IAS 配套包，或明确可读取的源物理格式，才能补齐普通 Mod 的物理链提取并验收。
- 新旧布莱默顿的物理节点覆盖有差异，需要实机对照实际摆动。按照任务限制仅用官方 Generator 离线回读。

