# IAS → DP-8 普通 Mod · P1 实验报告

分支 `exp/ias-to-patch`。样本为「Milfy发光外套替换SR-64」，目标为 DP-8 Mountain-Scaled。转换、打包、离线回读与上游管理器标准导入验证均已通过。**手臂肘部 twist 错位已修复，本体与外套已统一启用 `refitArms: true`，`HD2ModCore` 侵入改动已全部回退，Blender 离线预览已升级为 `BLENDER_EEVEE_NEXT` 真实贴图上色（含 Alpha 裁剪与原版 DP-8 持枪参照）**。游戏内加载与材质实机观感待实机复核。

## 转换与打包

- `ias convert` 从主包三份 Patch、两类 `*.ini` 和消费者／头盔 `*.rigbin` 读取数据。以 DP-8 原版 Unit 作外壳。CLI 在内存中读取游戏本体装备注册表，用 Sectional 同用的 `ArmorCellTable.FindExact` 按格子和身形核对 P0 的 13 条路由。游戏注册表和游戏归档仅作读取。
- 以 `TARGET.palette_slot` 联结 Unit 绑定矩阵。自定义骨沿 Profile 父链查找 `TARGET.source_index` 对应的原生语义祖先，合并同骨权重、取前四条并归一化。少量全零权重的有面顶点采用最近相邻的加权顶点，孤立情形采用同材质段最近的加权顶点。原生可见流使用 `vec4_half` 避免低精度目标外壳把第四权重截断。
- **手臂重拟合与 twist 错位修复（`Retargeter.ArmRefit.cs` / `IasDp8Converter.cs`）**：
  - 根因：样本 `LeftArm`/`RightArm` 的 `l_shoulder_twist`/`r_shoulder_twist` 位于肘关节（`[-0.355, -0.037, 1.394]`），而 HD2 原生骨架里 `l_shoulder_twist` 位于大臂中段（`[-0.313, -0.051, 1.405]`）、`l_elbow` 在 `[-0.472, -0.054, 1.324]`。此前 `ApplyArmRefit` 直接用 `_shippedRest[twist] - deformedWorking[twist]` 算出约 16 cm 反向残差，导致肘部捏细/折角；此前因此对 `Torso_Armor` 关闭 `refitArms` 又造成本体与外套袖管形变不同步。
  - 修复：在 `_nativeSourceNames is not null` 分支中，对 `*_shoulder_twist`（`*_shoulder` → `*_elbow`）与 `*_hand_twist`（`*_elbow` → `*_hand`）按 `_shippedRest` 骨段轴向投影比例线性插值两端关节残差，保持与起止关节连续（不触及 VPK 路径）。同时在 `IasDp8Converter.cs` 统一对本体与 `Torso_Armor` 开启 `refitArms: true`。
- **袖肩自定义骨空间折叠与平滑过渡（`IasNativeMesh.FoldVertexWeights`）**：
  - 诊断实测：在 `Torso_Armor`（Slim）中，袖管远端（`|x| > 0.25, z > 1.25`，5,871 顶点）原生 `chest` 权重为 0，全部 862 个带 `chest` 拖曳的顶点均来自 `Jacket_01_01_L/R`、`Jacket_01_02_R` 等自定义骨折叠；袖笼上缘（`|x| > 0.18, z > 1.30`，2,161 顶点）仅 17 顶点带原生 `chest`，1,088 顶点来自 `JacketStrap_01`、`Jacket_01_01_L`、`Jacket_01_02_L` 等折叠。
  - 修复：新增 `BuildArmFoldContext` / `FoldVertexWeights` / `ComputeArmFoldFactor`（纯几何、无硬编码骨名或魔法坐标），按顶点到左右手臂骨段（`shoulder→elbow→hand`）的距离、外展横向比例（`clavicle→shoulder`）与高度闸门计算平滑因子 `armFactor ∈ [0, 1]`，将落在手臂作用域内的 `chest` 祖先自定义骨权重按原顶点原生臂骨比例分配（无原生臂骨时归属 `*_shoulder`），同时保持躯干与下摆（`z < hand.Z - 0.08`）留在 `chest`。
- **低耦合标准 `manifest.json`（回退 `HD2ModCore`）**：
  - 按 §5.9 低耦合原则已通过 `git checkout -- HD2ModCore HD2ModCore.Tests` 彻底回退 `DefaultFirstChild` 相关侵入修改。
  - `IasDp8Converter.SerializeManifest` 改为上游原版管理器直接兼容的结构：`"外套"`（`include: ["coat/on"]`）与 `"鞋子"`（`include: ["shoes/on"]`）作为直属选项默认启用，其下 `subOptions` 第一项为默认穿着占位，第二项为 `"脱外套"`（`include: ["coat/off"]`）/ `"脱鞋"`（`include: ["shoes/off"]`）。经 `ModLibraryImporter` + `OptionActivationDefaultInitializer` 单测验证，导入原版管理器时无路径冲突、无未归属目录，且仅默认启用穿着项、不启用脱外套/脱鞋项。
- `PatchArchiveWriter` 写出全部归档。回读检查确认类型表计数等于实际条目数、非空 GPU 载荷全部 64 B 对齐、本体与选项键互斥。两个脱外套 LOD0 都仅有小于 0.2 mm 的占位三角形。`verify_package.py` 回读 14 个可见 LOD0 网格，最多 115 个 TransformInfo 节点、4 条每顶点权重，2,298 个顶点有四条正权重，权重和最大误差为 `0.000244`。

## 材质与贴图

11 个直接绑定的材质及其 2 个样本内父材质和 4 个样本内贴图打进本体共用归档。外部贴图与父材质均在当前游戏 TOC 中命中（清单见 `tmp/ias-to-patch/p1-out/dependencies.json`）。

| 类型 | ID | 用途 / 说明 |
| --- | --- | --- |
| 贴图 | `511da11642414912` | `AlbedoEmissive`（8192×8192 BC7，RGB 为反照率贴图集，Alpha 为发光遮罩） |
| 贴图 | `ac6ed4dd8a79b08b` | `Nar`（8192×8192 BC7，Alpha 为粗糙度） |
| 贴图 | `3dad95148a046219` | `IlluminateData`（8192×8192 BC7，R 为金属度） |
| 贴图 | `2a83d6d9c89f4fbf` | `0xa72cb013`（4096×4096 BC7，Alpha 为睫毛/细节裁剪遮罩，绑定至 `74f9457f100014f3`） |

## Blender 4.3.2（`BLENDER_EEVEE_NEXT` 贴图上色预览）

从输出 Patch 和 GPU 侧车回读每个 Unit 的 LOD0，并使用 `IasDp8Preview` 同时导出原版 DP-8（Slim/Shared）网格 `vanilla-preview.json`；动作来自游戏归档 `18235e0c9ec0e636`（Avatar `4d1c334d294dfa97`），站立为 `c2c16b9f0c49b54a`，持枪为 `2f741aaf34065276`。

`tools/ias-preview/preview.py` 从输出 `9ba626afa44a3aa3.patch_2` 解码贴图（经 `StingrayTexture.ToDDS()` + `texconv.exe` 转 PNG 至 `textures/`），为每个 section 绑定 `Principled BSDF`（Base Color、Emission、Metallic、Roughness、Alpha 裁剪 `DITHERED` 与背面剔除），并按 `AGENTS.md` 约定在写入 Blender UV 时执行 `v' = 1 - v` 翻转（不翻转贴图）。

所有渲染产物位于 `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\`：

- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\bind-front.png`：绑定姿态连贯，本体与外套袖管在统一 `refitArms: true` 下贴合、无绑定帧袖口交叠。
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\standing-front.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\standing-side.png`
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\standing-back.png`：背面「MEOW」与「02」字样方向正确，确认 UV V 方向无误。
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\rifle-front.png`：双臂肘部捏细/折角已消除，手腕与肘部随原版持枪动画自然弯曲；外套肩肘交界处因静态折叠规则（45 个 `Jacket_*` 自定义骨折叠至 `chest`，且原模型袖窿处存在 `chest` 与 `l_shoulder`/`r_shoulder` 权重过渡）仍有局部拉伸。
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\coat-off.png`：外套移除，本体与鞋子保留。
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\shoes-off.png`：鞋子切换为赤足/丝袜，本体与外套保留。
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\vanilla-dp8-rifle.png`：原版 DP-8 护甲套同一 `2f741aaf34065276` 持枪动画参照图。
- `D:\gitCode\HD2ModManager-ias-to-patch\tmp\ias-to-patch\p1-out\blender\ias-dp8.blend`：完整工程文件。

### 拉伸统计对比（`render-checks.json`，姿态边长 > 绑定边长 3 倍且 > 1 cm）

| 镜头 | 修复前（P1 初版） | 本轮第 1 步（twist 修复 + 统一 `refitArms`） | 本轮第 2 步（+ 袖肩自定义骨空间折叠） | 说明 |
| --- | --- | --- | --- | --- |
| `bind-front` | 0 条（max 1.000x） | 0 条（max 1.000x） | 0 条（max 1.000x） | 绑定帧零拉伸、无袖口交叠 |
| `standing-front` | 59 条（max 8.203x） | 71 条（max 11.737x） | 54 条（max 8.007x） | 袖笼拉伸进一步收敛 |
| `rifle-front` | 155 条 | 162 条（max 33.708x） | 180 条（max 17.741x） | 最大拉伸倍数由 33.708x 压低至 17.741x，双臂袖口贴合无穿出 |
| `coat-off` | 3 条 | 3 条（max 3.414x） | 3 条（max 3.414x） | 仅 `Torso Slim` 领口 4 mm 极短边，臂部 0 异常 |
| `vanilla-dp8-rifle` | — | 84 条（max 10.608x） | 84 条（max 10.608x） | 原版 DP-8 在同一持枪动画下的基线对照 |

## 复跑

在工作树 `D:\gitCode\HD2ModManager-ias-to-patch` 根目录执行：

```powershell
$sample = 'D:/gitCode/HD2ModManager/tmp/ias-to-patch/sample'
$gameData = 'E:/SteamLibrary/steamapps/common/Helldivers 2/data'
$out = 'tmp/ias-to-patch/p1-out/replay'
$preview = Join-Path $out 'blender'
$native = Join-Path $preview 'native'
$cli = 'HD2ModCli/bin/Debug/net10.0/hd2mod.dll'
$env:NuGetAudit = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet build -c Debug -m:1 -nr:false
dotnet test -c Debug --no-build -m:1 -nr:false
dotnet $cli ias convert $sample --game-data $gameData --output $out
dotnet $cli import plan (Join-Path $out 'mod') --output (Join-Path $out 'import-final.json') --json
dotnet $cli ias preview (Join-Path $out 'mod') --game-data $gameData --output $preview
dotnet $cli gamedata extract asset e0a48d0be9a7453f 4d1c334d294dfa97 18235e0c9ec0e636 --game-data $gameData --output $native --overwrite
dotnet $cli gamedata extract asset 18dead01056b72e9 4d1c334d294dfa97 18235e0c9ec0e636 --game-data $gameData --output $native --overwrite
dotnet $cli gamedata extract asset 931e336d7646cc26 c2c16b9f0c49b54a 18235e0c9ec0e636 --game-data $gameData --output $native --overwrite
dotnet $cli gamedata extract asset 931e336d7646cc26 2f741aaf34065276 18235e0c9ec0e636 --game-data $gameData --output $native --overwrite
& 'E:/Games/blender-4.3.2/blender.exe' -b --factory-startup -t 8 --python-exit-code 1 --python tools/ias-preview/preview.py -- $preview --stand c2c16b9f0c49b54a --rifle 2f741aaf34065276
python -B tools/ias-preview/verify_package.py $out $sample
git diff --check
git status --short
```

## 验证摘要

| 验证 | 结果 |
| --- | --- |
| Debug 全方案构建（`dotnet build -c Debug -m:1 -nr:false`） | 0 警告，0 错误 |
| Debug 全量测试（`dotnet test -c Debug --no-build -m:1 -nr:false`） | 2,681 通过，0 失败，1 条既有测试跳过（`TriangleWindingTests.ReportHowManyTrianglesDisagreeWithTheirVertexNormals`） |
| `HD2ModCore` / `HD2ModCore.Tests` 回退 | 已完整回退，零残留改动 |
| 新增单测 | `HD2SourceMigration.Tests/IasConversionTests.cs`（twist 骨段连续残差 + 袖肩自定义骨空间折叠）与 `HD2ModCli.Tests/IasManifestImportTests.cs`（原版管理器 `ModLibraryImporter` + `OptionActivationDefaultInitializer` 兼容性验证） |
| 真实 Mod 回读、导入计划、`verify_package.py` | 通过 |
| Blender 4.3.2 `BLENDER_EEVEE_NEXT` | 8 张渲染图与 `.blend` 均已保存并亲自查看确认 |
| `git diff --check` | 无输出 |

### git status --short

```text
 M HD2ModAdaptation/PatchReconstruction/UnitMesh/SameKeyCanonicalUnitRebuilder.cs
 M HD2ModCli/CliApplication.cs
 M HD2SourceMigration/HD2SourceMigration.csproj
 M HD2SourceMigration/Retarget/Retargeter.ArmRefit.cs
 M HD2SourceMigration/Retarget/Retargeter.Pose.cs
 M HD2SourceMigration/Retarget/Retargeter.Vertices.cs
 M HD2SourceMigration/Retarget/Retargeter.cs
 M HD2SourceMigration/VpkConversion/RetargetedMesh.cs
 M HD2SourceMigration/VpkConversion/RetargetedMeshBuilder.cs
 M HD2SourceMigration/VpkConversion/UnitAuthor.cs
?? HD2ModCli.Tests/IasManifestImportTests.cs
?? HD2ModCli/CliIasConversion.cs
?? HD2ModCli/CliIasSlotAuthority.cs
?? HD2SourceMigration.Tests/IasConversionTests.cs
?? HD2SourceMigration/IasConversion/
?? docs/agent/ias-to-patch-p1-report-20261122.md
?? tools/ias-preview/
```
