# W30 「从库里选 Mod 转 IAS」对话框：重做样式、扩充来源、多选

## 做了什么

**后端**

- 候选扩充：普通 Mod（沿用「有护甲资产」规则）、求生 VPK 导入的 Mod、素体，三类都进列表。IAS 角色 Mod 与 IAS 管理 Mod 也返回，但标「不可转」并给原因，前端灰显。
- 来源识别全部用现成判定，不看名字：
  - 求生 VPK = Mod 根上有来源 `.vpk`（与 `ModStorageKind.Vpk2Patch` 同一口径；在 `VpkRebuildService` 里新增只读包装 `FindSourceVpkPaths`，只改可见性，行为不变）。
  - 素体 = `VpkCustomModelFile.TryReadModel` 读得到 `custom-model.json`，且根上没有来源 VPK。
  - 其余为普通 Mod，仍按护甲资产规则筛。
- 候选新增字段（读不到就不填）：来源、是否在当前配置、套在哪件护甲、材质包数、选项数、编号、不可转原因。
  - 护甲名：普通 Mod 取资产里的护甲显示名（多件时「X 等 N 件」）；VPK/素体取 `wardrobe.json` 的 `targetItemId` 再查护甲目录 `IasEquipmentCatalog`。
  - 材质包数：`sectional/patches.json` 里 role=material 的条目，不含「模型内置材质」。
  - 选项数：库里该节点下 Option 子节点个数。
  - 编号：只读库目录名的两种固定形态，纯数字目录（创意工坊 ID）和以 `_AR` 加数字结尾（站点编号）。不拆显示名。
- `AddFromLibraryAsync`：
  - 求生 VPK 来源 → 用原 VPK（及同目录材质 VPK）走拖入 VPK 的同一条流程（`AddDroppedAsync`，由原 `AddAsync` 的函数体原样抽出，规划分组、W29 的「模型 VPK + 材质 VPK 列表」、底子目标、发布前复核全部沿用）。角色名用库里 Mod 的名字。发布前再复核库里节点未被改动。
  - 其余 → PatchMod 路径。素体 Mod 目录里 `sectional/`、`Overwrite/` 与根上补丁同名，所以素体只喂根上的原始补丁（`PreparePatchInput` 新增 `rootOnly`）。
  - 新增可选参数 `IasVpkTargets? vpkTargets`，页面把当前选的底子护甲/头盔传下去，与拖入一致。
- 多选：对话框返回勾选的全部候选。ViewModel 用一次确认，再为每个候选各排一个队列项（沿用原队列，每项一条消息中心记录）。已在队列的跳过并提示，不可转的过滤掉。文案：多个时「把 N 个 Mod 加入队列，转换为 IAS 角色并安装？」，只选一个时仍写「把「名字」加入队列，转换为 IAS 角色并安装？」。
- 候选在对话框内异步加载（先开窗显示「正在读取 Mod 库…」），读取失败在对话框里显示原因；页面离开时取消令牌会关掉对话框。接口 `IIasPageDialogs.ChooseLibraryMod` 改为 `ChooseLibraryMods(loadCandidates, ct)`，返回候选列表。

**前端**（照设计稿，新窗口 `IasLibraryPickerDialog`，不再复用 `IasPageDialog`，后者只剩确认框）

- 宽 620 固定，高度可拖（最小 460），自绘标题区 + × 关闭，可拖动；搜索框（放大镜、占位「搜索名字或编号」，只匹配名字和编号）；5 段等宽分段筛选；「当前配置里的」「库里其他的」两组，各带图标、数量、延伸分隔线，空组隐藏；两列等宽卡片网格。
- 卡片：56×72 竖版封面（`ImagePlaceholderConverter`）、名字两行省略加 ToolTip（含编号）、元信息一行、来源胶囊（普通蓝/VPK 青/素体紫）、淡色编号、右上 20px 勾选圈。悬停加深并微灰；选中 2px 蓝框并少 1px 内边距防跳动，淡蓝底；不可转灰底、封面和名字半透明、不显示勾选圈、黄色原因胶囊、点击无反应。
- 键盘：搜索框按 ↓ 进入卡片；方向键在网格里移动（上下 ±2，跨组到相邻组同一列）；空格切换勾选；Enter 由默认按钮确认；Esc 取消。
- 空状态、加载中、读取失败三种居中提示；底栏：已选头像叠放（最多 4 个、26px、压 8px）、「点卡片选择，可以多选」/「已选 N 个」、取消、「转为 IAS 并安装 (N)」，没选时禁用。
- 颜色全部取自 `Themes/Controls.xaml`；只有三种来源胶囊色、黄色原因胶囊和选中淡蓝底是对话框自带资源。

## 改动文件

新增：
- `HD2ModManager/Services/Ias/IasLibraryCandidateReader.cs`
- `HD2ModManager/ViewModels/IasLibraryPickerViewModel.cs`
- `HD2ModManager/Views/IasLibraryPickerDialog.xaml`、`.xaml.cs`
- `HD2ModCore.Tests/IasLibraryPickerTests.cs`、`IasLibraryPickerRenderTests.cs`

修改：
- `HD2ModCore/Infrastructure/VpkRebuildService.cs`（仅新增 `FindSourceVpkPaths` 包装）
- `HD2ModManager/Services/Ias/IIasModuleService.cs`（候选记录、枚举、`AddFromLibraryAsync` 新参数）
- `HD2ModManager/Services/Ias/IasModuleService.cs`（候选识别、`AddDroppedAsync` 抽取、库内转换分支、`rootOnly`）
- `HD2ModManager/Services/Ias/IIasPageDialogs.cs`、`FakeIasModuleService.cs`
- `HD2ModManager/ViewModels/IasPageViewModel.cs`（多选排队）
- `HD2ModManager/Views/IasPageDialog.xaml`、`.xaml.cs`（去掉旧列表面板）
- `HD2ModCli/HD2ModCli.csproj`（CLI 链接编译了 `IasModuleService.cs`，需把新的读取器文件链进去）
- `HD2ModCore.Tests/IasPageViewModelTests.cs`（桩更新；新增多选、单选文案、取消三个用例）、`HD2ModCore.Tests.csproj`（net8 目标排除两个新测试文件）

没碰物理、Lua 共用代码，也没改普通（非 IAS）Mod 的行为。

## 验证

- `dotnet build WpfApp1.sln -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false`：`已成功生成。`、`0 个警告`、`0 个错误`。
- `dotnet test WpfApp1.sln -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false`，各项目原文：

| 测试项目 | 失败 | 通过 | 跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| Decryptor.Tests | 0 | 5 | 0 | 5 |
| HD2SourceModel.Tests | 0 | 72 | 0 | 72 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 | 212 |
| HD2SourceMigration.Tests | 0 | 595 | 1 | 596 |
| HD2ModCli.Tests | 0 | 213 | 0 | 213 |
| HD2ModCore.Tests | 0 | 1833 | 0 | 1833 |

  跳过的 1 项是 W29 报告里同一个三角形绕序诊断。`HD2ModCore.Tests` 连续跑了两次都是 1833 通过。
- 新增测试覆盖：
  - 候选识别（沙盒 `tmp/w30/tests/`，用完删除）：VPK 导入 Mod、素体（无根补丁/有根补丁/原 VPK 丢失）、普通补丁 Mod、分卷 VPK 不当来源、编号规则 7 例。
  - 选择器：筛选（5 段）、搜索（名字+编号，不匹配来源/元信息文字）、分组与空组隐藏、多选与「已选 N 个」/「转为 IAS 并安装 (N)」、不可转选不上、选择跨筛选保留、头像最多 4 个、加载失败、元信息文案。
  - 页面：多选只确认一次并排 N 个队列项（消息中心 N 条记录，重复/不可转被过滤，底子目标透传）、单选文案、取消不排队。
- 渲染测试新开一个没有 StartupUri 的临时 `Application` 装主题资源，渲染完清掉静态引用；与 `IasPageViewModelTests` 放进同一 xunit collection，避免并行时别的测试拿到它的 Dispatcher（首轮没加时 8 个页面测试因此偶发失败，已修）。

## 截图（假数据，渲染测试输出）

- `D:\gitCode\HD2ModManager-ias-module\tmp\w30\screens\picker-default.png`：分组、选中、不可转、两行省略、底栏头像
- `D:\gitCode\HD2ModManager-ias-module\tmp\w30\screens\picker-filtered-vpk.png`：筛选「求生 VPK」
- `D:\gitCode\HD2ModManager-ias-module\tmp\w30\screens\picker-empty.png`：搜不到的空状态
- `D:\gitCode\HD2ModManager-ias-module\tmp\w30\screens\picker-loading.png`：加载中

静态渲染看不到悬停与键盘焦点态；滚动条是系统默认样式（App.xaml 的全局细滚动条没载入测试）。

## 真实库只读识别结果

只读扫描 `E:\SteamLibrary\steamapps\common\HD2ModManager\mods`，对 `library.json` 里全部 447 个目录跑 `IasLibraryCandidateReader.InspectAsync`，没写库里任何文件。原始输出：`D:\gitCode\HD2ModManager-ias-module\tmp\w30\real-library-scan.txt`。

- 求生 VPK：**14 个**。例：`2606520120`（l4d2光辉，1 个 VPK）、`2505895990`（布莱默顿）、`airi初代恶堕战斗员zoey`（10 个 VPK，2 个材质包）。
- 素体：**12 个**，全部根上留着原始补丁，所以判为可转。例：`Salt 公式服 替换 TG-8`（1 个材质包）、`少前2可露凯婚纱（独立）`、`尘白-胧嫣`。
- 其余 421 个按普通 Mod 处理（是否进列表取决于资产里有没有护甲，这一步没在真实库上跑，因为资产清单读取会写缓存）。

库里没有任何一个 Mod 被判成「素体但不可转」。

## 转换实测

用 `hd2mod ias pack hd2`（与库内 PatchMod 路径同一个打包服务）、IAS 官方 1.6.4 Generator、AQ SDK 快照，把真实 Mod 的文件拷到沙盒做三次输入，输出都在沙盒（后已清理）：

| 输入 | 结果 |
| --- | --- |
| VPK 转出来的素体，只用 `Overwrite/` 产物（`l4d2光辉`） | 失败：「Could not match mod Unit IDs to any supported IAS equipment archive.」 → 日志 `tmp/w30/pack-body.log` |
| `Salt 公式服 替换 TG-8` 根上的原始补丁 | 成功，退出码 0 → `tmp/w30/pack-salt.log` |
| `少前2可露凯婚纱（独立）` 根上的原始补丁 | 成功，退出码 0 → `tmp/w30/pack-dok.log` |

所以规则是：素体根上有原始补丁就用原始补丁转；只有转换产物（没有原始补丁，也没有原 VPK）就标不可转，原因按是否有 `vpk2patch.json` 区分为「原 VPK 已不在，无法转」或「素体没有对应的原版装备，暂不能转」。这两种情况在当前真实库里一个都没有，只在沙盒测试里出现。

没有做的实测：没有在装了 IAS、开着 IAS 的环境里点完整的「库里转换并安装」（需要写游戏目录与 `%LOCALAPPDATA%`，任务禁止）。求生 VPK 分支复用拖入 VPK 的整条流程，素体分支复用 PatchMod 路径，打包层已按上表实测，安装与发布层沿用现有代码。

## 写死项清单

- Mod 参数、名字、护甲名：新增写死 0 项。护甲名来自资产或护甲目录，编号来自库目录名，材质包数来自 `patches.json`，选项数来自库节点。
- 固定规则：库目录名编号的两种形态（纯数字、`_AR` 加数字）、`wardrobe.json` 的 `targetItemId` 字段名、不可转原因文案、三色胶囊与界面尺寸。
- 渲染测试里的假封面颜色与假候选名字只在测试中使用。

## 待决

1. **编号来源**：库节点没有 Nexus/创意工坊 ID 字段。现在只认库目录名的两种固定形态（纯数字目录、`_AR` 加数字结尾）。若以后要显示真正的站点 ID，需要导入时把 ID 存进元数据；现在 `_AR` 这个站点前缀是按真实库里看到的目录命名推的。
2. **「套在哪件护甲」**：VPK 与素体只在 `wardrobe.json` 有 `targetItemId` 时显示；真实库里的 14 个 VPK Mod 目前都没有（它们的目标是素体而非护甲），所以这些卡片只显示材质包数。普通 Mod 取资产里的护甲显示名。
3. **素体用原始补丁转**：素体 Mod 转出的 IAS 角色来自根上的原始补丁，不含管理器里叠加的材质选项或 Overwrite 里的改动。若要带上这些，要另行设计，会碰到共用的重建逻辑，所以没做。
4. **仅一个 Mod 时的确认文案**：多个写「N 个 Mod」，一个写 Mod 名字（沿用原有写法）。若要严格统一成 N 个 Mod，改 `IasPageViewModel.ChooseLibraryModAsync` 一处即可。
5. **全局细滚动条**：对话框用的是应用的全局滚动条样式，实际运行时会是细胶囊；截图里是系统默认的。
6. **`HD2ModCli.csproj` 链接编译**：CLI 项目按文件链接了 `IasModuleService.cs`，今后凡是给它新增依赖文件都要同步在 csproj 里加一行。
