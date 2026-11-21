# Mod 来源信息自动追踪实施方案（2026-11-18）

> 状态：**P0 已实施并通过全量回归验证（2026-11-18）**
> 定位：将来源追踪作为转换与派生流水线的**数据完整性（Data Lineage / Provenance）基础能力**建设，与具体游戏/目标格式解耦。当前 P0 优先闭环 `L4D2 → Sectional → 派生/合并/克隆 → 双路径导出`，底层协议原生预留未来 `HD2IAS`、`GMod` 及其他格式扩展。

---

## 1. 核心原则与设计边界

1. **自动、透明、低干扰**：导入、转换、合并、克隆、重构建与导出全流程自动提取与继承来源信息，**不要求用户手动填写任何表单**，不因来源信息缺失或无法核验而阻断正常转换、使用与导出。
2. **只做事实保留，不做版权裁决或发布审批**：管理器如实记录可验证的文件元数据、路径线索与工具转换链路，不内置发布锁、审批流或加密防篡改对抗（UI 与 CLI 不提供清除原始来源的功能，本地磁盘文件不做签名锁死）。明确提示：**自动署名仅代表来源追溯，不代表原作者已授权修改或重新分发**。
3. **证据分级，绝不编造署名**：严格区分「文件内自声明」「Workshop 目录路径推断」「孤立数字文件名候选（未验证）」「上游继承」与「未知」；区分「作品声明作者」与「Workshop 上传者」；过滤 SDK 模板占位词。
4. **当前有效来源与历史来源严格区分**：多来源 Mod 在部分源文件被移除、或重打包/拆件仅选取部分资产后，已退出的来源转为 `historical`（保留历史记录备查），**展示主区与导出署名文件只宣称当前实际参与产物的 `active` 来源**，杜绝错误挂名。
5. **隐私安全与外链安全**：
   - **严禁读取操作系统用户名（`Environment.UserName`）或本机绝对路径中的用户目录名**写入溯源记录；转换者个人署名仅通过设置页可选昵称显式配置，留空则只记录工具名称与版本。
   - 所有提取和展示的外部链接强制校验 `Uri.UriSchemeHttp` / `Uri.UriSchemeHttps`，拦截 `file:`、`javascript:`、UNC 路径或可执行命令，防止恶意包通过点击链接触发命令执行或凭据泄露。

---

## 2. 实施范围划分（P0 / P1 / P2）

| 阶段 | 范围 |
|---|---|
| **P0（本期实施）** | 1. 独立于格式的 `.hd2modmanager-provenance.json` 存储协议（支持多来源 `sources[]`、`active`/`historical` 参与态、`steps[]` 历史与重复重构建折叠）。<br>2. L4D2 `.vpk` 离线元数据提取（`addoninfo.txt` 全字段 UTF-8/CP936 解析、占位词过滤、Workshop 目录路径推断、孤立数字文件名候选分级、SHA-256 指纹）。<br>3. 全量 14 条导入、重构建、克隆、家族归拢、Sectional 转换、头甲合并、跨护甲迁移、同键重构、护甲规整、重打包、材质拆分/合并与双导出路径接入。<br>4. 导出自动生成可读署名文件 `CREDITS.txt` + 机读 `.hd2modmanager-provenance.json`（无来源且无转换记录的纯普通 Mod 不生成空白文件）。<br>5. 历史存量 Mod 按需惰性补全（Lazy Backfill，冷启动零额外扫描）。<br>6. 设置页「转换署名昵称（可选）」、Mod 详情页「来源与贡献」卡片、CLI 查询投影及 `README.md` 社区声明。 |
| **P1（后续扩展）** | 1. Steam Workshop 公开 API 异步非阻塞补全（补全网页标题、上传者昵称并与本地文件交叉核验，升级为 `verified_remote`）。<br>2. 接入未来 **HD2IAS** 兼容转换与精简 Runtime 产物的来源透传。<br>3. 接入未来 **GMod**（`.gma` 包头 / `addon.json` / `4000/<id>/`）武器及模型转换流水线。 |
| **P2（远期规划）** | 1. 部件/材质槽位级细粒度署名映射（精确到具体 `Unit` / `SectionGroup` 归属哪个源包）。<br>2. 上游 Workshop 原作更新检测提示。 |

---

## 3. 存储协议与数据模型设计

### 3.1 存储位置与系统兼容性不变量

- **存储路径**：每个 Mod 节点根目录下存储 **`.hd2modmanager-provenance.json`**（命名对齐现有的 `.hd2modmanager-mod.json`）。
- **为何定在节点根目录**：
  1. `sectional/`（旧名 `vpk2patch/`）与 `Overwrite/` 属于派生层，在 `VpkRebuildService`、`Legacy2SectionalConverter` 与 `ModCompileService` 中会被整目录替换；放在根目录天然免受换载荷误删。
  2. 随 Mod 文件夹物理迁移、备份与跨库同步（`ModLibrarySynchronizer`）一同移动，不依赖单一 `library.json`。
- **与现有子系统的零冲突核对**：
  - `ModStorageKindResolver`：仅检查 `*.vpk`、`sectional-source.json`、基底目录、根 Patch TOC 与逻辑容器标记，`.hd2modmanager-provenance.json` 与 `CREDITS.txt` **不改变任何节点的 `ModStorageKind`**。
  - `ExternalInstallPathMapper` / `ModDeployableFiles`：仅识别 `.addon64`、`.addon`、`.ini` 与合法 Patch 文件，**绝不会把溯源 JSON 或 `CREDITS.txt` 部署进游戏 `data/` 或 `bin/`**。
  - `ModLibraryResidualCleanupService`：`.json` 与 `.txt` 已在 `HarmlessResidualExtensions` 白名单中，节点删除后若有残留不会阻碍空壳回收。
  - **只读属性兼容**：`ModLibraryImporter` 入库后会将文件标为 `ReadOnly`。`ModProvenanceFile.TryWrite` 采用「写入同目录 `.tmp` 临时文件 → 若目标已存在则先清除目标 `ReadOnly` 属性 → `File.Move(..., overwrite: true)` 原子替换 → 按目录既有状态恢复属性」，杜绝 Windows 下 `UnauthorizedAccessException`。

### 3.2 JSON Schema（`hd2mod.provenance/1`）

反序列化配置统一使用 `JsonSerializerDefaults.Web`（camelCase），**忽略未知字段**以保证跨版本向前兼容：

```json
{
  "schema": "hd2mod.provenance/1",
  "updatedUtc": "2026-11-18T06:00:00Z",
  "sources": [
    {
      "sourceId": "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
      "participation": "active",
      "deactivatedUtc": null,
      "deactivatedReason": null,
      "sourceGame": "l4d2",
      "sourceKind": "l4d2_vpk",
      "role": "model",
      "originalFileName": "2876543210.vpk",
      "byteLength": 48234496,
      "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
      "originalTitle": "Bremerton Azur Lane [Rochelle]",
      "titleEvidence": "file_declared",
      "originalAuthor": "ExampleAuthor",
      "authorRole": "author_declared",
      "authorSteamId": "76561198000000000",
      "authorEvidence": "file_declared",
      "originalVersion": "1.2",
      "platform": "steam_workshop",
      "workshopId": "2876543210",
      "workshopEvidence": "workshop_path",
      "candidateWorkshopId": null,
      "sourceUrl": "https://steamcommunity.com/sharedfiles/filedetails/?id=2876543210",
      "urlEvidence": "workshop_path",
      "licenseNotes": "Model by Official / Ported by ExampleAuthor. Do not redistribute commercially.",
      "firstSeenUtc": "2026-11-18T05:50:00Z"
    }
  ],
  "steps": [
    {
      "stepIndex": 1,
      "operation": "l4d2_vpk_import",
      "toolName": "HD2ModManager",
      "toolVersion": "1.5.19",
      "contributor": null,
      "summary": "从 3 个 L4D2 VPK 导入并转换为自定义素体（1 个模型包、1 个材质包、1 个组件开关包）",
      "activeSourceIds": [
        "sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
      ],
      "firstExecutedUtc": "2026-11-18T05:50:00Z",
      "lastExecutedUtc": "2026-11-18T05:50:00Z",
      "runCount": 1
    }
  ]
}
```

### 3.3 核心枚举与证据优先级

1. **来源生态与格式（开放字符串常量，支持扩展）**：
   - `sourceGame`：`"l4d2"` | `"hd2"` | `"gmod"` | `"unknown"`
   - `sourceKind`：`"l4d2_vpk"` | `"hd2_patch"` | `"hd2_sectional"` | `"hd2_ias"` | `"gmod_gma"` | `"gmod_folder"` | `"unknown"`
   - `role`：`"model"` | `"material"` | `"component_switch"` | `"patch"` | `"mixed"`
2. **参与状态（`ProvenanceParticipation`）**：
   - `"active"`：当前产物实际包含或依赖该来源的资产。
   - `"historical"`：历史上曾导入或参与构建，但在后续重构建、拆件筛选或重打包中已被移除，**不再参与当前产物**。
3. **证据等级（`ProvenanceEvidenceKind`，按可信度由高到低排序）**：
   - `verified_remote`（5，P1 预留）：联网与平台接口及文件特征核验一致。
   - `file_declared`（4）：原始文件内部元数据（如 `addoninfo.txt`）明确声明且通过占位词过滤。
   - `workshop_path`（3）：从标准创意工坊目录结构（如 `steamapps/workshop/content/550/<id>/` 或 `addons/workshop/<id>.vpk`）提取。
   - `inherited`（2）：从上游包附带的机读溯源文件继承（保留原证据字段；若上游仅含摘要清单则记 `inherited`）。
   - `unverified_filename_candidate`（1）：仅从孤立数字文件名提取的候选 ID，**只写入 `candidateWorkshopId`，绝不写入 `workshopId` 或 `sourceUrl`**。
   - `unknown`（0）：未获取到有效信息。

---

## 4. 提取、过滤、合并与生命周期规则

### 4.1 L4D2 VPK 元数据提取（`VpkAddonMetadataReader`）

在 `HD2SourceMigration` 中新增统一的 `VpkAddonMetadataReader`（并让 `VpkImportPlanner.ReadAddonTitle` 复用同一解析入口，消除双份解析）：

1. **`addoninfo.txt` 解析**：
   - 读取 VPK 顶层 `addoninfo.txt`；优先按 UTF-8 解码，若出现替换符 `\uFFFD` 则回退至 `CodePagesEncodingProvider` GBK（CP936）。
   - 支持 Valve KeyValues 单行与跨行引号字符串、无引号单token值及 `//` 注释剥离。
   - 提取键（大小写不敏感）：`addontitle`、`addonauthor`、`addonauthorsteamid`、`addonversion`、`addonurl0`、`addondescription`（清理控制字符，最长保留 2,000 字符）。
2. **模板占位符与脏数据过滤**：
   - 作者黑名单（Trim 后大小写不敏感全词匹配）：`valve`、`author`、`authorname`、`your name`、`yourname`、`unknown`、`none`、`null`、`n/a`、`tbd`、`-`、`...`。命中视为空。
   - 标题黑名单：`untitled`、`addon`、`my addon`、`template`。命中视为空（界面已有文件名兜底展示，原始标题如实记空/未知）。
   - URL 校验与黑名单：仅接受 `Uri.TryCreate` 成功且 Scheme 为 `http` 或 `https` 的绝对 URL；域名为 `example.com`、`www.example.com`、`localhost` 或纯官方主页 `www.valvesoftware.com` / `developer.valvesoftware.com` 的模板默认链接一律视为空。
3. **Workshop ID 识别与分级（不设 9～10 位硬编码门槛）**：
   - **优先级 1（文件自声明 `file_declared`）**：从合法的 `addonURL0`（或 `addondescription`）中匹配 `steamcommunity.com/(sharedfiles|workshop)/filedetails/?id=<digits>`（`<digits>` 为正整数），写入 `workshopId = <digits>`、`workshopEvidence = "file_declared"`，保留原 URL。
   - **优先级 2（目录结构推断 `workshop_path`）**：在解压或拷入暂存目录**之前**，保留每个 `.vpk` 的原始外部路径（或压缩包内相对路径）：
     - 匹配 `.../steamapps/workshop/content/550/<digits>/.../*.vpk` 或 `.../addons/workshop/<digits>.vpk`（`<digits>` 全为 ASCII 数字且非全零）；
     - 命中时写入 `workshopId = <digits>`、`workshopEvidence = "workshop_path"`；若 `sourceUrl` 为空则填充 `https://steamcommunity.com/sharedfiles/filedetails/?id=<digits>`（`urlEvidence = "workshop_path"`）。
   - **优先级 3（孤立数字文件名候选 `unverified_filename_candidate`）**：
     - 当上述两条均未命中，且文件名（不含 `.vpk`，排除分卷 `*_000`）全部由 ASCII 数字构成时：
     - **不以 9～10 位作为硬性条件**；仅排除明显的局部编号与日期噪声（长度 `< 6` 的短序号如 `01`、`1002`，或恰好 8 位且能解析为 `19980101`..`20991231` 合法日历日期的文件名如 `20261118.vpk`）；
     - 其余数字文件名**仅写入 `candidateWorkshopId = <digits>`**，`workshopId` 与 `sourceUrl` 保持 `null`。界面与导出文本统一标为「未验证的候选 Workshop ID（仅由文件名推测）」。

### 4.2 非破坏性合并（`MergeSources`）

当同一来源文件被重复导入、二次扫描或跨节点合并时：
- **同源匹配键**：优先按 `sha256`（非空时）精确匹配；若旧记录无 `sha256`，退回按 `(sourceKind, originalFileName, byteLength)` 匹配。
- **字段合并铁律（未知不覆盖已知，低可信不覆盖高可信）**：
  - 新值为 `null`/空字符串时，永远保留已有非空值；
  - 新旧均有值时，比较对应 `*Evidence` 权重，高权重胜出；同权重保留较早确认的值（除非是同一文件的 `addoninfo.txt` 更新且新值非空）。

### 4.3 当前有效来源对账（`ReconcileActiveSources`）

解决「移除某个来源文件或子集筛选后，不能继续错误宣称该来源参与当前产物」：

1. **VPK 模组重构建对账（`VpkRebuildService`）**：
   - 每次重构建成功替换 `sectional/` 后，收集本次实际输入给转换器的全部源文件集合（根上模型/材质 `.vpk` + 归属开关 `.vpk` + 本次 `materialSources` 中实际提供并写出材质的同族兄弟 `.vpk`）。
   - 计算本次有效集合的匹配键集合 `activeKeys`：
     - 凡命中 `activeKeys` 的来源条目：`participation = "active"`，清空 `deactivatedUtc` / `deactivatedReason`（若之前被移走后又放回重构建，可自动恢复为 `active`）；
     - 原记录中 `sourceKind == "l4d2_vpk"` 且属于本地根源文件、但不在 `activeKeys` 中的条目：更新为 `participation = "historical"`，记录 `deactivatedUtc` 与 `deactivatedReason = "重构建时该来源文件已不在输入集中"`。
   - **边界保护**：对于不含任何本地 `.vpk` 原件的已导出成品包重导入节点（纯 Patch 形态），不走本地 VPK 缺失降级，其从上游继承的 `active` 来源保持 `active`。
2. **Sectional 拆件转换对账（`Legacy2SectionalConverter`）**：
   - `SectionalConversionPlanner.Plan` 产出的 `plan.Unit.Selections` 与 `plan.Patches` 精确记录了每个被选中资产来自哪个 `SourcePatch` 文件。
   - 对照 `sectional-source.json` 中每个 `SectionalSourceEntry.PatchFiles`：只有至少贡献了 1 个选中 Unit 或材质组的父节点，其上游 `active` 来源才作为新产物（或重构建后产物）的 `active` 来源；完全未命中白名单的父节点来源不标为 `active`（若在重构建前曾是 `active`，则降为 `historical`）。
3. **多 Mod 资源重打包对账（`PatchResourceRepackagingService`）**：
   - 用户可能在左侧勾选了多个候选 Mod，但在右侧资产表中取消了其中某个 Mod 的全部 `AssetKey`。
   - 生成重打包输出时，**仅按用户最终勾选的 `AssetRows` 实际引用的源 Mod 集合**提取并合并 `active` 来源；未被取用任何资产的 Mod 绝不混入署名。

### 4.4 `steps[]` 历史记录与连续重构建折叠规则

每条 `ProvenanceStepRecord` 遵循以下规则：

| 操作标识（`operation`） | 触发场景 | 记录策略 |
|---|---|---|
| `l4d2_vpk_import` | L4D2 VPK 初次导入入库 | **独立记录** |
| `vpk_rebuild` | VPK 模组从根 `.vpk` 重构建 | **条件折叠**：若 `steps[]` 末尾同为 `vpk_rebuild`（或刚完成首次 `l4d2_vpk_import` 后的紧随自动首轮构建）且本次有效来源集合 `activeSourceIds` 未发生变化，则就地更新 `lastExecutedUtc`、`toolVersion`、`contributor` 与 `runCount++`；若有效来源集合发生增删变化，则**追加新记录**以标记构成变更 |
| `legacy_to_sectional` | Legacy Mod 生成 Sectional 版（首次） | **独立记录** |
| `sectional_rebuild` | 含 `sectional-source.json` 的模组重跑拆件与编译 | **条件折叠**（规则同 `vpk_rebuild`） |
| `custom_model_convert` / `custom_model_sectional` | CLI / 服务生成自定义素体或分件素体 | **独立记录** |
| `clone_variant` | `ModCloneService` 复制为独立命名空间变体 | **独立记录**（记录源 Mod 名与新变体命名空间） |
| `family_merge` | `ModFamilyMerger` 归拢同命名空间模型与材质包 | **独立记录**（记录并入的材质包数量） |
| `helmet_to_armor_merge` | 头盔合并到护甲独立输出 | **独立记录** |
| `cross_armor_transfer` | 跨护甲迁移生成替换其他护甲的模组 | **独立记录**（记录目标护甲名称） |
| `same_key_reconstruction` | 同键升级 / 修复过时 Unit Patch | **独立记录** |
| `armor_normalization` | 规整护甲 Patch 包 | **独立记录** |
| `patch_repackage` | 多 Mod Patch 资源重打包 | **独立记录**（记录实际贡献资产的源 Mod 数） |
| `material_split` / `material_merge` | 拆分内嵌材质 / 嵌入或替换外部材质包 | **独立记录** |

---

## 5. 历史存量 Mod 兼容与按需惰性补全（Lazy Backfill）

1. **冷启动零额外开销**：`ModLibraryService.LoadAsync` 与库同步阶段**绝不扫描全库 `.vpk` 或补写溯源文件**，保证百级大库启动耗时不变。
2. **可落盘的惰性补全时机**：
   - 用户在 GUI 打开某个 Mod 的详情页（后台异步读取：若该目录无 `.hd2modmanager-provenance.json`，但根上存在 `*.vpk` 或 `vpk2patch.json`，当场提取并原子落盘，再刷新卡片）；
   - 对某个历史 Mod 执行重构建、克隆、家族归拢或作为父节点派生新 Mod 时，先在内存补全父节点溯源，有写权限的场景顺带落盘。
3. **只读不落盘的内存补算时机（关键防回归边界）**：
   - 在 **标准导出（`ModExporter` / `CliModExportExecutor`）** 与 **文件夹结构导出（`ModFolderTreeExporter`）** 以及 **CLI 只读查询** 中：若历史节点磁盘上尚无 `.hd2modmanager-provenance.json`，仅在**内存中**从根上 `*.vpk` / `vpk2patch.json` 补算溯源结果并直接写入导出的 `.zip` 流，**严禁在导出过程中回写修改库内源目录**——否则会改变源目录文件列表与哈希，导致 `CliModExportExecutor` 触发 `ModExportInputChangedDuringApply` 校验失败。
4. **纯 `.patch_0` 历史旧包**：既无 `.hd2modmanager-provenance.json` 也无 `.vpk` / `vpk2patch.json` 时，保持完全兼容：详情页不强行弹出突兀警告（卡片显示「来源未知 / 未附带跨游戏转换记录」或按紧凑态展示），导出时不生成空白 `CREDITS.txt`。

---

## 6. 全链路代码修改入口核对清单（共 14 处入口）

### 6.1 `HD2SourceMigration` 层（源包解析）
1. **新增 `HD2SourceMigration/VpkConversion/VpkAddonMetadataReader.cs`**：
   - 实现 `VpkAddonMetadata` 记录类型与 `Read(VpkArchive archive, string vpkPath, string? originalContextPath = null)`：完成 `addoninfo.txt` 全字段读取（UTF-8/CP936）、模板占位词过滤、安全 URL 过滤、三级 Workshop ID / 候选 ID 识别。
2. **修改 `HD2SourceMigration/VpkConversion/VpkImportPlanner.cs`**：
   - `ReadAddonTitle` 改为委托 `VpkAddonMetadataReader`，保持开关包命名行为完全一致。

### 6.2 `HD2ModCore` 层（溯源核心服务、流水线与双导出）
3. **新增 `HD2ModCore/Domain/ModProvenance.cs` 与 `HD2ModCore/Infrastructure/ModProvenanceService.cs`**：
   - 定义不可变领域模型：`ModProvenanceDocument`、`ProvenanceSourceRecord`、`ProvenanceStepRecord` 及对应枚举常量。
   - 实现 `ModProvenanceFile`（`TryRead`、`TryWrite` 含只读属性安全处理）。
   - 实现 `ModProvenanceService`：
     - `ExtractFromVpkFilesAsync`（流式计算 SHA-256 + 调用 `VpkAddonMetadataReader`）；
     - `ReadOrBackfillInMemoryAsync` / `EnsurePersistedAsync`（从目录现有 `.hd2modmanager-provenance.json`、`*.vpk`、`vpk2patch.json` 读取或惰性恢复）；
     - `MergeAndRecordStep` / `ReconcileActiveSources`（多父节点继承合并、`active`/`historical` 对账、`steps[]` 折叠与追加）；
     - `FormatCreditsText`（生成人类可读的 `CREDITS.txt` 文本，仅当存在至少 1 条 `active` 来源或至少 1 条 `steps` 记录时返回非空内容）。
   - 提供静态配置属性 `ModProvenanceService.ContributorNickname`（与 `VpkRebuildService.HeadScale` 同模式，由 `SettingsService` 与 CLI 启动时同步，默认 `null`）。
4. **修改 `HD2ModCore/Infrastructure/VpkRebuildService.cs`（入口 2：VPK 重构建）**：
   - 重构建成功后，读取/补全现有溯源，按本次实际转换成功的源文件与提供材质的同族源文件执行 `ReconcileActiveSources`，记录/折叠 `vpk_rebuild` 步骤并写回根目录 `.hd2modmanager-provenance.json`。
5. **修改 `HD2ModCore/Infrastructure/ModCloneService.cs`（入口 5：复制为变体）**：
   - 合并被复制源目录与 `CopySiblingSources` 带入的同族材质目录的溯源记录，在副本重构建完成后写入独立的 `clone_variant` 步骤。
6. **修改 `HD2ModCore/Infrastructure/ModFamilyMerger.cs`（入口 6：家族归拢）**：
   - 在移动材质包 `.vpk` 前先收集各材质包目录的溯源记录（无文件则从其 `.vpk` 提取），并入宿主模型目录的溯源记录并追加 `family_merge` 步骤。
7. **修改 `HD2ModCore/Infrastructure/SectionalSourceAssembler.cs`、`Legacy2SectionalConverter.cs`、`CustomModelConversionService.cs`、`SectionalModBuilder.cs`（入口 4：生成 Sectional 版 / 自定义素体）**：
   - `SectionalSourceAssembler` 装配新目录时，汇总各候选父节点的溯源记录写入初始 `.hd2modmanager-provenance.json`；
   - `Legacy2SectionalConverter.RunAsync` 按实际入选白名单的 `SourcePatch` 对账各父节点来源的 `active`/`historical` 状态，记录首次 `legacy_to_sectional` 或折叠 `sectional_rebuild`；
   - `CustomModelConversionService.GenerateAsync` / `SectionalModBuilder.BuildAsync` 从 `facts.SourcePatchTocPaths` 所在源目录继承溯源并记录 `custom_model_convert` / `custom_model_sectional`（`RelocateToPayloadDirectory` 保持只搬 patch 与 `custom-model.json`，将 `.hd2modmanager-provenance.json` 留在根目录）。
8. **修改派生输出服务（入口 7～12）**：
   - `HelmetToArmorMergeService.cs`（独立输出目录与变体子目录继承源节点溯源 + `helmet_to_armor_merge` 步骤 + 按需写 `CREDITS.txt`）；
   - `CanonicalCrossArmorOrchestrator.cs` 或调用端 `CrossArmorCandidateOutputPageViewModel` / `CliCrossArmorTransferExecutor`（输出候选目录继承源溯源 + `cross_armor_transfer` 步骤 + 按需写 `CREDITS.txt`）；
   - `CanonicalSameKeyReconstructionService.cs` / `ModRepairBatchService.cs`（输出目录继承源溯源 + `same_key_reconstruction` 步骤）；
   - `ArmorNormalizationService.cs`（输出目录继承源溯源 + `armor_normalization` 步骤）；
   - `PatchResourceRepackagingService.cs`（仅合并实际被选中 `AssetKey` 所属源 Mod 的 `active` 来源 + `patch_repackage` 步骤 + 按需写 `CREDITS.txt`）；
   - `MaterialPackagingApplicationService.cs`（`SplitAsync` 两个输出目录继承源溯源 + `material_split`；`MergeAsync` 合并主体与材质包双方的 `active` 来源 + `material_merge`）。
9. **修改 `HD2ModCore/Infrastructure/ModLibraryImporter.cs` 与 `StandardModManifest.cs`、`Domain/Manifest/ExportManifest.cs`（入口 3：标准包/文件夹重导入继承）**：
   - `ExportManifest` 与 `StandardModManifest` 增加可选 `Provenance` 字段（用于根容器逻辑包兜底携带）；
   - `PersistFlattenedTree` 在复制节点文件后，若源包根或 `manifest.json` 含上游溯源信息且节点目录尚无 `.hd2modmanager-provenance.json`（或同节点覆盖更新），执行非破坏性合并落盘。
10. **修改双导出器（入口 14：`ModExporter.cs` 与 `ModFolderTreeExporter.cs`）**：
    - `ModExporter.ExportToZipAsync`：内存按需补算导出树各节点溯源；若节点磁盘尚无 `.hd2modmanager-provenance.json` 且补算非空，直接向 zip 对应节点路径写入 `.hd2modmanager-provenance.json`；若整棵导出树含有效溯源且根上无同名文件，向 zip 根写入汇总 `CREDITS.txt`，并在 `manifest.json` 带上根 `Provenance` 摘要。**全程不改写库内磁盘文件**。
    - `ModFolderTreeExporter.ExportAsync`：内存按需补算导出树各节点溯源；当存在有效来源或转换步骤时，在 zip 根目录写出汇总 `CREDITS.txt`，并在对应节点目录写出 `.hd2modmanager-provenance.json`；当整棵树无任何来源且无任何转换步骤时，不写任何多余文件。

### 6.3 `HD2ModCli` 层（命令行对齐）
11. **修改 `HD2ModCli` 相关入口**：
    - 启动执行命令时从 `CliSettings` 读取可选 `ConverterNickname` 注入 `ModProvenanceService.ContributorNickname`；
    - `vpk2patch` 直接转换输出时写出初始 `.hd2modmanager-provenance.json`；
    - `CliProjections` / `mod inspect`（或 `mod metadata`）JSON 与文本输出增加 `provenance` 摘要字段，方便 CLI 用户与自动化测试直接核验。

### 6.4 `HD2ModManager` 层（GUI 导入、替换、设置页与详情页）
12. **修改 `HD2ModManager/Services/VpkImportService.cs` 与 `ImportService.cs`（入口 1：VPK 导入与同源更新）**：
    - `VpkImportSource` 增加 `OriginalContextPaths` 字典（记录每个解压后 `.vpk` 对应的原始外部路径或压缩包内相对路径，防止摊平解压丢失 `workshop/<id>` 目录层级）；
    - `ConvertAsync` 组装 `pkg/<group>` 时提取该组所有归属 VPK 的元数据写出初始 `.hd2modmanager-provenance.json`；
    - `ImportService.ReplaceSourcesAsync`（同源二次导入更新已有 Mod）将新包提取的溯源与目标目录已有 `.hd2modmanager-provenance.json` 执行非破坏性合并（不抹除历史步骤与已知字段）。
13. **修改 `HD2ModManager/Services/ModLibraryService.cs`（`ReplaceStoredFilesAsync` 守卫）**：
    - 在 `ReplaceStoredFilesAsync`（被同键修复与护甲规整的「导入并替换当前 Mod」调用）中，除保留原有非 Patch 文件外，若 `sourceDirectory` 带有新生成的 `.hd2modmanager-provenance.json`，将其与原目录溯源合并后写入 `targetDirectory`，避免刚追加的修复/规整步骤被旧文件盖回。
14. **设置页与 Mod 详情页 UI**：
    - **设置页（`SettingsService.cs`、`SettingsPageViewModel.cs`、`SettingsPageView.xaml`）**：
      - `SettingsModel` 增加 `string? ConverterNickname { get; set; }`（长度上限 64 字符，Trim 处理，空串归一为 `null`），启动及修改时同步至 `ModProvenanceService.ContributorNickname`；
      - 在「通用」卡片增加一行简洁输入项：`转换署名昵称（可选）`，辅助说明：`留空则仅记录“HD2ModManager 自动转换”，不会读取系统用户名。`
    - **Mod 详情页（`CardKind.cs`、`ModDetailsPageViewModel.Cards.cs`、`ModDetailsPageViewModel.Provenance.cs`、`Themes/CardTemplates.xaml`）**：
      - `CardKind` 新增 `ModProvenance`（`Order = 75`，位于「当前状态」与「资产标签」之间）；
      - 可见性规则：当该 Mod 存在有效来源记录、存在转换步骤记录、或是 VPK / Sectional 转换类 Mod 时显示；纯普通无来源 Legacy 补丁包隐藏该卡，保持详情页清爽，不制造满屏「未知」噪声；
      - 卡片展示内容：
        - **当前有效来源列表（`active`）**：逐条展示原始作品名、原作者/上传者（带角色与证据标签，如 `[文件声明]` / `[创意工坊路径]`）、来源游戏徽标（如 `L4D2`）、可点击访问的原始链接（仅 `http/https` 可点）、候选 ID 提示（若有 `candidateWorkshopId`，显式灰字标注 `候选 Workshop ID: xxx（仅由文件名推测，未验证）`）及原始说明摘要；
        - **转换与适配记录（`steps[]`）**：时间线展示工具、转换/合并/克隆动作及可选贡献者昵称（连续重构建显示如 `重构建 ×3`）；
        - **历史曾用来源（`historical`，默认折叠）**：仅在非空时显示一行可展开的次要提示 `曾参与但已移出当前产物的历史来源（N 项）`；
        - 卡片右上角提供「复制署名说明」按钮（调用 `ClipboardService` 复制 `FormatCreditsText` 生成的纯文本，方便用户发帖粘贴）。

---

## 7. 导出 `CREDITS.txt` 格式与社区声明文案

### 7.1 `CREDITS.txt` 输出模板（UTF-8 无 BOM）

仅列出 `participation == "active"` 的当前有效来源；缺失字段如实写 `未知`，未验证候选 ID 明确加注警告，绝不伪造确定链接：

```text
========================================================================
Mod 来源与转换署名说明 (Generated by HD2ModManager)
========================================================================
当前导出名称：<当前 Mod 显示名称>
导出时间 (UTC)：2026-11-18 06:00:00

[原始作品来源（当前产物实际包含）]
1. <原始作品名 或 原始文件名>
   - 来源游戏：Left 4 Dead 2
   - 原作者 / 发布者：<作者名>（来源：addoninfo.txt 自声明）
   - 原始作品链接：<URL>（来源：Workshop 目录路径推断）
   - 原始文件：<文件名> (SHA-256 前 12 位: 9f86d081884c)
   - 原作说明摘录：<如有则摘录单行/多行缩进，无则省略该行>

2. <材质包名称>
   - 来源游戏：Left 4 Dead 2
   - 原作者 / 发布者：未知
   - 原始作品链接：未知（文件名含未验证候选 ID：2899999999，未经核实）
   - 原始文件：<文件名> (SHA-256 前 12 位: a1b2c3d4e5f6)

[转换与派生记录]
1. [2026-11-18] L4D2 VPK → HD2 Sectional 自定义素体（HD2ModManager 1.5.19 自动转换）
2. [2026-11-18] 复制为独立命名空间变体（HD2ModManager 1.5.19，操作者：<用户昵称；未填则不显示此段>）

[权利与分享提示]
本文件由 HD2ModManager 自动生成，用于保留原始创作者署名与转换来源链。
模型转换、骨骼重定向、材质适配与重打包不会改变原始作品的权利归属。
自动附带本署名说明不代表原作者已授权公开再分发；分享前请确认并遵守原作者的授权条款。
========================================================================
```

> 多节点选项树导出说明：若导出的主体 Mod 下挂载了子选项（`Options`），根目录 `CREDITS.txt` 先列出主体来源，再按子选项名称分段列出各子选项的 `active` 来源（若子选项与主体完全同源则自动合并去重说明），确保发往 Nexus Mods 的单压缩包顶层一份文件即可看全整包署名。

### 7.2 `README.md` 补充社区声明

在仓库根目录 `README.md` 增加「第三方 Mod 来源与署名声明（Third-Party Mod Provenance & Attribution）」章节（采用已确认的克制表述：尊重创作者劳动、自动识别并保留来源与后续转换贡献、格式转换不改变原始素材权利归属、不主张第三方素材所有权、提醒用户分享时保留署名并遵守原作再分发许可）。

---

## 8. 测试与验收标准

### 8.1 构建与自动化测试门禁
- `dotnet build -c Debug`：**0 警告、0 错误**。
- `dotnet test -c Debug`：全量现有测试 + 新增单元测试全部通过。

### 8.2 新增专项测试覆盖清单
1. **`VpkAddonMetadataReaderTests`（`HD2SourceMigration.Tests`）**：
   - UTF-8 与 GBK（CP936）编码中文 `addoninfo.txt` 解析；
   - `Valve` / `Author` / `example.com` 等模板占位符过滤与非 `http(s)` 危险 URI 拦截；
   - Workshop ID 三级规则验证：`addonURL0` 提取（`file_declared`）、`550/<id>/` 与 `addons/workshop/<id>.vpk` 路径提取（`workshop_path`）、8 位/9 位/10 位/11 位孤立数字文件名均只进 `candidateWorkshopId`（`unverified_filename_candidate`）且不设死 9～10 位门槛、`20261118.vpk` 日期名与 `01.vpk` 短序号排除。
2. **`ModProvenanceServiceTests`（`HD2ModCore.Tests`）**：
   - 多来源合并幂等性：重复扫描同一文件不产生重复条目，未知空值不覆盖已有作者/链接，低证据等级不覆盖高证据等级；
   - **移除来源动态对账**：3 个 VPK 导入后移走第 3 个 VPK 并重构建，第 3 个来源自动转为 `historical`，`FormatCreditsText` 不再宣称第 3 个来源参与当前产物，但 JSON 历史记录仍在；重新放回重构建后恢复为 `active`；
   - `steps[]` 折叠与保留：连续 5 次同源 `vpk_rebuild` 折叠为 1 条（`runCount == 5`），而跨格式转换（`legacy_to_sectional`）、克隆（`clone_variant`）、合并（`family_merge` / `material_merge` / `patch_repackage`）均保留独立记录；
   - 隐私红线断言：未设置昵称时，生成的 JSON 与 `CREDITS.txt` 均不包含 `Environment.UserName`。
3. **全链路集成测试（`HD2ModCore.Tests` & `HD2ModCli.Tests`）**：
   - `ModCloneService` 复制变体后保留全部有效来源并记录 `clone_variant`；
   - `SectionalSourceAssembler` + `Legacy2SectionalConverter` 多来源合并及未入选子级过滤；
   - `PatchResourceRepackaging` 仅继承被选中 `AssetKey` 所属源 Mod 的来源；
   - `ModExporter` 与 `ModFolderTreeExporter` 双导出验证：
     - 跨游戏转换 Mod 导出后 zip 内同时包含 `CREDITS.txt` 与 `.hd2modmanager-provenance.json`，且**源库目录在导出前后字节与文件指纹零变化**（通过 `CliModExportExecutor` 严格指纹校验）；
     - 导出的 zip 再次通过 `ModLibraryImporter` 导入新库后，原始 `sources[]` 与 `steps[]` 完整保留；
     - 无来源且无转换记录的普通 `.patch_0` Mod 导出时，不产生空白 `CREDITS.txt`。
   - 存量历史 Mod 兼容：根上有 `.vpk` 但无溯源文件的旧目录可按需惰性补全；纯 `.patch_0` 旧目录正常读取、部署与导出无报错。

### 8.3 业务验收逐条核对表

- [x] 导入可识别的 L4D2 Mod（单包或多 VPK 整合包）后，无需手动填写即自动提取并保留多来源 `sources[]`。
- [x] Workshop ID 不锁定 9～10 位硬条件；孤立数字文件名仅标为「未验证的候选 ID」，不误标为已确认作品链接。
- [x] 经历 VPK 重构建、Legacy 转 Sectional、复制变体、家族归拢、头甲合并、跨护甲迁移、同键重构、规整、资源重打包、材质拆分/合并、重命名后，已确认的原始来源均不丢失。
- [x] 连续重复重构建自动折叠计数，跨格式转换、来源合并、克隆派生保留独立历史步骤。
- [x] 多来源 Mod 移除某个来源文件重构建，或重打包未取用某源 Mod 资产时，该来源转入历史状态，当前署名与导出的 `CREDITS.txt` 不再错误宣称其参与当前产物。
- [x] 标准包导出与文件夹结构（Nexus 发布包）导出均自动附带 `CREDITS.txt` 与机读溯源文件，重导入后来源链不断裂；纯普通未知 Mod 不生成空白署名文件。
- [x] 默认不读取系统用户名；历史存量 Mod 冷启动零额外扫描，按需惰性补全；来源未知时不影响任何转换、部署与导出功能。

---

## 9. 实施落地校准记录（与初版规划的工程细节对齐）

1. **`VpkAddonMetadataReader` 分层落点优化**：
   - 初版规划拟直接放在 `HD2SourceMigration`，但实测工程依赖关系中 `HD2ModCore` 刻意不引用 `HD2SourceMigration`（后者包含网格重定向与 `texconv` 重型转换依赖），而 `HD2SourceModel` 为零外部依赖的纯 Source 归档读取层（`VpkArchive` 所在层）。
   - 实际落地将核心解析器置于 `HD2SourceModel/VpkAddonMetadataReader.cs`（完全不含任何 HD2 概念，严守零依赖边界），由 `HD2ModCore` 直接引用以实现毫秒级进程内惰性补全（无需在导出或看详情页时拉起 `hd2mod.exe` 子进程），同时在 `HD2SourceMigration/VpkConversion/VpkAddonMetadataReader.cs` 提供同名转发门面供规划器与测试统一调用。
2. **详情页卡片模板落点对齐**：
   - 仓库真实结构中 Mod 详情页专属卡片模板（`ModHeader`、`SectionalActions`、`VpkComponents`、`UserStatus` 等）均集中在 `HD2ModManager/Views/ModDetailsPageView.xaml` 的 `<UserControl.Resources>` 内（而非全局 `Themes/CardTemplates.xaml`）。因此 `CardTemplate.ModProvenance` 按现有页面约定落在 `ModDetailsPageView.xaml`，并同步补齐 `tools/UiSnapshot` 离屏快照桩验证。
3. **`SectionalSourceEntry` 增加可选 `ProvenanceSourceIds` 字段**：
   - 在 `sectional-source.json` 的每个来源条目中增加向后兼容的可选字段 `ProvenanceSourceIds`，记录装配时该父节点携带的来源 ID。这样即使父 Mod 日后从库中被用户删除，`Legacy2SectionalConverter` 重构建时仍能按实际入选白名单的 `PatchFiles` 精确对账 `active` 与 `historical` 来源。
