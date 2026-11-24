# IAS 页面设计（2026-11-24）

上位规划：`ias-module-plan-20261124.md`（§0 用户决定、§1 用户看到的样子）。本文是前端实现的唯一依据：布局、文案、状态、交互、与后端的接口。

设计原则（来自用户）：**简单、明了、方便**；管理器**不控制 IAS**，只替用户做下载、安装、导入、转换；不动主线，现有页面除顶栏多一个入口外不改。

## 1. 入口

- 顶栏右侧：`护甲映射 | IAS | 设置`。文字「IAS」，提示文字「HD2 综合护甲系统」。
- 图标 `NavIcon.Ias`（盾牌，16×16 线条，与现有图标同风格）：
  `M8,1.5 L13.5,3.5 L13.5,8 C13.5,11 11,13.5 8,14.5 C5,13.5 2.5,11 2.5,8 L2.5,3.5 Z M5.5,8 L7.3,9.8 L10.5,6.4`
- 新增 `WorkspaceMode.Ias`、`NavIconKey.Ias`、`WorkspacePageType.Ias`，照「护甲映射」的接法（`ShellViewModel.Nav.cs` 加一行、`SyncNav` 加一行、`MainWindow.xaml` 加一个 DataTemplate）。

## 2. 页面结构

页面复用现有样式：`AppPageTitle`、`CardContainer` + `CardStackMargin`、`AppCardTitle`、`AppSupportingText`、`AppPrimaryButton`、`AppSecondaryButton`、`AppToggleSwitch`、`BoolToVis`。外层 `ScrollViewer` + `scroll:SmoothScrollBehavior.IsEnabled="True"`，和设置页一致。

```
IAS 综合护甲系统                                                  ← AppPageTitle
管理器帮你下载、安装和导入 IAS；穿什么外观，仍在游戏里按 F4 用 IAS 面板选。   ← AppSupportingText

┌ IAS 本体 ───────────────────────────────────────────────────────┐
│ ● 已安装 1.6.4                                     [ 主按钮 ]    │  ← 状态行：圆点颜色 + 状态文字（SemiBold）
│   说明行（版本、提示、错误原因）                                  │  ← AppSupportingText
│   ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ 62%                          │  ← 只在忙时出现
│   正在下载 154 / 249 MB                         [ 取消 ]          │
│   ▸ 查看更新说明                                                 │  ← Expander，默认收起；有更新说明才出现
│   打开官方页面 · 卸载 IAS                                         │  ← 文字按钮，已安装时才出现「卸载 IAS」
└─────────────────────────────────────────────────────────────────┘

┌ 角色（2） ─────────────────────────────── [ 从库里选 Mod 转 IAS ] ┐
│ ┌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌┐   │
│ ╎   把 IAS Mod、普通护甲 Mod 或求生 VPK 拖到这里                ╎   │  ← 拖放区（虚线框），有角色时高 72，没有时高 180
│ ╎   IAS Mod 直接安装；另外两种先转成 IAS 再安装                  ╎   │
│ └╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌┘   │
│ [图] Milfy SR-64            IAS Mod        [转为普通 Mod] [卸载]  │  ← 每个角色一行
│ [图] 布莱默顿               由求生 VPK 转   [转为普通 Mod] [卸载]  │
└─────────────────────────────────────────────────────────────────┘
```

## 3. 「IAS 本体」卡片的状态

状态由后端 `IasPageStatus.Kind` 决定；前端只负责照表显示，不自己推断。

| Kind | 圆点 | 状态文字 | 说明行 | 主按钮 |
|---|---|---|---|---|
| `Checking` | 灰 | 正在检查… | — | 无 |
| `NotInstalled` | 灰 | IAS 未安装 | 将从 IAS 官方 GitHub 下载约 {大小}，只装护甲需要的部件。 | **安装 IAS** |
| `Installed` | 绿 | 已安装 {版本} | 已是最新版本。 | 无 |
| `UpdateAvailable` | 蓝 | 有新版本 {新版本} | 当前 {旧版本}。 | **更新** |
| `GameMismatch` | 黄 | 游戏已更新，IAS 尚未适配 | 等 IAS 作者发布新版本后再更新。{Detail} | 无 |
| `MissingMdl3` | 黄 | 缺少前置 MDL3 | 先在首页把 MDL3 加进当前配置。 | 无 |
| `NotInProfile` | 黄 | 当前配置未启用 IAS | 切换配置后需要把 IAS 加进来。 | **加入当前配置** |
| `FeedUnavailable` | 灰 | 已安装 {版本}（或：IAS 未安装） | 暂时连不上 IAS 官方源：{Message} | **重试** |
| `Error` | 红 | 出错了 | {Message} | **重试** |
| `Busy` | 蓝（转圈） | {Progress.Title} | — | 无（进度区显示「取消」，`CanCancel=false` 时不显示） |

- 圆点颜色用现有主题里的成功/警告/错误/强调色资源；找不到就用 `#3FB950` 绿、`#D29922` 黄、`#E5484D` 红、`#4493F8` 蓝、`#8B949E` 灰，集中定义在页面资源里。
- 「查看更新说明」：`ReleaseNotes` 非空时出现，Expander 内是只读文本，最大高 220 可滚动（照 `AppUpdateCard`）。
- 「卸载 IAS」：弹确认框：标题「卸载 IAS」，正文「将移除 IAS 部件，并从当前配置撤下 IAS。」，复选框「同时删除已导入的 {n} 个角色」（默认不勾），按钮「卸载」「取消」。
- 「打开官方页面」：打开 `ReleaseUrl`（后端给，默认 IAS 仓库 Releases 页）。
- 下载来源跟随「设置 → 程序更新 → 下载来源 / 镜像」的设置（后端处理，页面不另设选项）。在说明行末尾加一句 AppSupportingText：「下载来源与程序更新相同，可在设置里改。」，只在 `NotInstalled` / `UpdateAvailable` 时出现。

## 4. 「角色」卡片

- 标题「角色（{n}）」；右上「从库里选 Mod 转 IAS」（`AppSecondaryButton`）。
- **IAS 未就绪**（Kind ∉ {Installed, UpdateAvailable, FeedUnavailable 且已装}）：整张卡片半透明（Opacity 0.5），禁用拖放和角色操作，角色列表保持只读。拖放区按状态显示：
  - `NotInstalled`，以及未安装时的 `Error` / `FeedUnavailable`：「先安装 IAS 才能添加角色」
  - `GameMismatch`：「游戏更新后 IAS 暂不可用，等 IAS 更新后再添加角色」
  - `MissingMdl3`：「先把 MDL3 加进当前配置」
  - `NotInProfile`：「先把 IAS 加入当前配置」
  - 已安装时的 `Error`：「先重试检查 IAS 状态」
- **拖放区**：虚线圆角框（`StrokeDashArray="4 3"`、圆角 8、内边距 16），两行居中文字（第一行 SemiBold）。整张卡片都接受拖放（不只虚线框）。
  - 拖进来时（`DragEnter/DragOver`）：调用 `ClassifyAsync`（同步结果缓存到拖动结束），虚线框高亮（边框换强调色、背景 8% 强调色），第一行文字换成：
    - 全是 IAS 包：「松开以安装 {n} 个 IAS 角色」
    - 有需要转换的：「松开以转换并安装 {n} 个角色」
    - 混合有不支持的：「松开以添加 {n} 个，{m} 个不支持会跳过」
    - 全不支持：「不支持这些文件」，`Effects=None`
  - 松开：调用 `AddAsync(paths)`；忙时整页进入 `Busy`。
- **从库里选**：弹出一个小窗口（照现有 `AppDialog` 风格）列出 `GetConvertibleLibraryModsAsync()` 的结果（缩略图 40、名称、类型），支持搜索框过滤，单选，按钮「转为 IAS 并安装」「取消」。
- **角色行**：图标 48×48（无图标用占位，复用 `ImagePlaceholderConverter`）、名称（SemiBold）、来源标签（小圆角胶囊：`IAS Mod` / `由普通 Mod 转` / `由求生 VPK 转`）、右侧两个按钮：
  - 「转为普通 Mod」→ 确认框「把 {名称} 转为普通 Mod 放进 Mod 库？IAS 里的这个角色不受影响。」→ `ConvertToStandardModAsync`。成功后提示「已放进 Mod 库：{新 Mod 名}」，带「去看看」按钮（跳到该 Mod 详情；跳转用现有导航方法，找不到就只提示）。
  - 「卸载」→ 确认框「卸载角色 {名称}？会从 IAS 和当前配置里移除。」→ `RemoveCharacterAsync`。
- 空列表时：不显示列表，只显示（较高的）拖放区。

## 5. 忙碌、结果与错误

- 同一时刻只允许一个操作；忙时所有按钮禁用（取消除外），拖放不接受。
- 进度区在「IAS 本体」卡片里（即使操作是“添加角色”也在这里显示），因为所有操作最终都会重新部署 IAS。
- 操作结束：成功 → 卡片说明行显示一行绿色结果文字 6 秒，同时发一条消息中心通知（复用现有消息中心）；失败 → 说明行显示红色错误（保留到下次操作），并发通知。错误文字直接用后端 `IasOperationResult.Message`（后端保证是中文、可读）。
- 后端返回 `Warnings` 非空：结果文字后追加「（{k} 条提示）」，点击展开列表。
- 页面每次进入、以及每次操作结束后刷新状态（`GetStatusAsync` + `GetCharactersAsync`）。

## 6. 前端与后端的接口（放 `HD2ModManager/Services/Ias/`）

前端只依赖这个接口；后端实现以后通过适配层接上。**前端先用假实现跑通全部状态**。

```csharp
namespace HD2ModManager.Services.Ias;

public interface IIasModuleService
{
    Task<IasPageStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<IasCharacterInfo>> GetCharactersAsync(CancellationToken ct);
    Task<IReadOnlyList<IasLibraryCandidate>> GetConvertibleLibraryModsAsync(CancellationToken ct);
    IasDropClassification Classify(IReadOnlyList<string> paths);   // 只看文件名/扩展名/压缩包目录，必须很快（<50ms），不能做重活

    Task<IasOperationResult> InstallOrUpdateAsync(IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> UninstallAsync(bool removeCharacters, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> EnableInActiveProfileAsync(IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> AddAsync(IReadOnlyList<string> paths, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> AddFromLibraryAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> RemoveCharacterAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> ConvertToStandardModAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct);
}

public enum IasStatusKind { Checking, NotInstalled, Installed, UpdateAvailable, GameMismatch, MissingMdl3, NotInProfile, FeedUnavailable, Error }

public sealed record IasPageStatus(
    IasStatusKind Kind,
    string? InstalledVersion,
    string? LatestVersion,
    long? DownloadBytes,        // NotInstalled / UpdateAvailable 时的下载大小
    string? ReleaseNotes,
    string ReleaseUrl,
    string? Message,            // FeedUnavailable / Error 的原因
    string? Detail);            // GameMismatch 的细节

public enum IasCharacterSource { IasPackage, PatchMod, L4d2Vpk }

public sealed record IasCharacterInfo(string Id, string Name, string? IconPath, IasCharacterSource Source, DateTimeOffset AddedAt);

public sealed record IasLibraryCandidate(string ModId, string Name, string? ThumbnailPath, string KindText);

public enum IasDropItemKind { IasPackage, PatchMod, L4d2Vpk, Unsupported }
public sealed record IasDropClassification(IReadOnlyList<(string Path, IasDropItemKind Kind)> Items)
{
    public int PairedItems { get; init; }
    public int Supported => Items.Count(i => i.Kind != IasDropItemKind.Unsupported) - Math.Max(0, PairedItems - 1);
    public int Unsupported => Items.Count(i => i.Kind == IasDropItemKind.Unsupported);
    public bool NeedsConversion => Items.Any(i => i.Kind is IasDropItemKind.PatchMod or IasDropItemKind.L4d2Vpk);
}

public sealed record IasPageProgress(string Title, double? Percent, string? Detail, bool CanCancel);

public sealed record IasOperationResult(bool Success, string Message, IReadOnlyList<string> Warnings, string? CreatedModId = null);
```

- 假实现 `FakeIasModuleService`：内存状态机，能按顺序演示：未安装 → 安装（假进度 3 秒，可取消）→ 已安装 → 添加角色（拖什么都按扩展名分类，`.zip` 名字含 `ias` 视为 IAS 包，`.vpk` 视为求生，其他目录/zip 视为普通 Mod）→ 卸载角色 → 转普通 Mod → 卸载 IAS；另有开关能切到 `GameMismatch`、`MissingMdl3`、`NotInProfile`、`FeedUnavailable`、`Error`。
- 假实现只在 Debug 构建里、并且设置了环境变量 `HD2MM_IAS_FAKE=1` 时启用；正式构建里 IAS 页拿到的是“未接后端”的占位实现：状态 `Error`，消息「IAS 功能尚未接通」。（后端接好后替换占位实现。）

## 7. 不做

- 不加任何 IAS 设置项（物理、插帧、修正频率等都归 IAS 自己的 F4 面板）。
- 不改首页、库、详情页、设置页、主窗口拖放。
- 不做 IAS 的外观预选。

## 8. 开关（2026-11-24 追加，用户需求：IAS 可切换关闭，不是卸载；角色也能单独启用/关闭）

### 8.1 IAS 总开关
- 位置：「IAS 本体」卡片状态行右侧，用 `AppToggleSwitch`，标签「启用 IAS」。只在已安装且与游戏兼容时出现（`Installed` / `UpdateAvailable` / 已安装的 `FeedUnavailable`）；`UpdateAvailable` 时开关和「更新」主按钮并排。
- 范围：**当前配置**。切换配置后显示那个配置里的状态（和管理器其他东西一样按配置走）。
- 关：把 IAS 的 data 包和所有 IAS 角色的主 Mod 从当前配置撤下并重新部署。下载的部件、角色、角色库全部保留。不弹确认（随时能开回来）。
  - 关闭后状态行：灰点，「IAS 已停用（当前配置）」；说明行「IAS 和角色都已从当前配置撤下，部件和角色都还在，打开开关即可恢复。」
  - 冲突警告条随之隐藏。
- 开：把 IAS data 包和所有**已启用**角色的主 Mod 加回当前配置并重新部署（缺 MDL3 时仍按 `MissingMdl3` 提示）；成功后照 W10 规则弹「关掉管理器物理？」确认框。
- 原来的 `NotInProfile` 状态与「加入当前配置」按钮由这个开关取代：已安装但不在当前配置 = 开关处于关。

### 8.2 角色开关
- 位置：每个角色行，按钮区最左边一个 `AppToggleSwitch`（无文字，ToolTip「启用这个角色」/「停用这个角色」）。
- 范围：**全局**（IAS 角色库只有一份，官方的启用标记本来就是全局的）。
- 关：IAS 库里把这个角色标为停用（worker `set-enabled false`），重新生成 IAS data 包；当前配置撤下这个角色的主 Mod；重新部署。角色和源文件保留。
- 开：反过来；当前配置的 IAS 总开关是开着时才把主 Mod 加回当前配置。
- 停用的角色行：整行 Opacity 0.6，名称后加灰色胶囊「已停用」；「转为普通 Mod」「卸载」仍可用。
- IAS 总开关关着时：角色开关全部禁用（ToolTip「先打开 IAS」），只显示各自的全局状态。
- 至少要留一个？不限制：全部停用时 IAS 照常部署（游戏里没有可选的角色外观）。

### 8.3 忙碌与结果
- 开关操作和其他操作一样串行；忙时开关禁用。切换期间开关保持用户点下去的位置并显示进度，失败时弹回原位置并按第 5 节显示错误。

### 8.4 接口增补
- `IIasModuleService.SetEnabledInActiveProfileAsync(bool enabled, ...)`、`SetCharacterEnabledAsync(string id, bool enabled, ...)`。
- `IasPageStatus` 增加 `EnabledInActiveProfile`；`IasCharacterInfo` 增加 `Enabled`。
- CLI：`hd2mod ias enable|disable`、`hd2mod ias role enable|disable <id>`。

## 9. 不装 IAS 也能把 IAS 包转普通 Mod（2026-11-24 追加，用户选定）
- 在 IAS 页以外的页面（普通拖入 / 「导入」按钮，即 `ShellViewModel.ProcessImportQueueAsync` 这条路）拖入 IAS 包（主包 + 配套包，可在 zip/rar/7z 里）时：自动转成普通 Mod 再导入 Mod 库。不需要安装 IAS。
- 在 IAS 页拖入：装进 IAS（第 4 节、W11）。两个方向各走各的页。
- 识别与压缩包处理复用 W11 的 IAS 包识别；转换复用现有「转为普通 Mod」转换器（输出无独立骨、无手臂姿势，见 W9）。需要游戏数据目录时用设置里的 GameDataFolder（只读）；未设置时给出中文提示。
- 普通导入对其他文件的行为逐字不变；只在识别为 IAS 包时分流。
- 导入结果提示照普通导入；在名称后标注「（由 IAS 转换）」。

## 10. IAS 启用时也建议关掉管理器内置 Lua（2026-11-24 追加，用户需求）
- 和物理同一套规则（W10），只是多一项：设置里的「内置 Lua」总开关（`SettingsService.GetBuiltInLuaEnabled/SetBuiltInLuaEnabled`，设置页 `BuiltInLuaEnabled`）。
- 警告条：IAS 在当前配置启用时，若内置 Lua 开着，追加一句「管理器的内置 Lua 也建议关闭，IAS 自带运行时，两边会抢启动入口。」并给按钮「关掉内置 Lua」。物理那句和按钮保持不变；两项都开着时并列两个按钮。
- 确认框：原「关掉管理器物理？」改成按实际开着的项生成——
  - 只有物理开：保持 W10 文案。
  - 只有 Lua 开：标题「关掉管理器内置 Lua？」，正文「IAS 自带运行时，和管理器内置 Lua 会冲突。关掉后可在设置里重新打开。」
  - 两项都开：标题「关掉管理器的物理和内置 Lua？」，正文合并说明；主按钮「全部关掉」，次按钮「先不关」。
- 关 Lua 走设置页同一条路径（保存 + 与设置页相同的后续部署），设置页同步显示；不另写逻辑。只建议、需确认，不自动关。

## 11. 画面增强（可选）——ReShade / DLSS（2026-11-24 追加，用户同意，排在 W13 之后）
见 docs/agent/ias-optional-graphics-eval-20261124.md。IAS 页第三张卡「画面增强（可选）」，仅 IAS 已安装、总开关开时可用：
- 「ReShade 滤镜」开关：用 IAS 官方 ReShade 组件与预设（含隐藏披风插件）；下面小字「游戏内按 Home 打开 ReShade 面板调整。」首次打开弹一次风险说明：「ReShade 和 DLSS 是 IAS 作者提供的可选/实验功能，会注入游戏画面；联网游戏存在被反作弊误判的可能，出问题请关闭。」按钮「我知道了，打开」「取消」。
- 「DLSS」：关 / 插帧 / 神经渲染 三选一（单选按钮），ReShade 关时禁用。小字：插帧「NVIDIA 30/40/50 系；游戏内抗锯齿用 TAA。」神经渲染「NVIDIA 30/40/50 系；需 DX12，游戏内抗锯齿改为 DLSS。」
- 游戏 bin 已有别的 dxgi.dll/ReShade：沿用官方 ReShadeInstallCheck 的中文说明，不覆盖。
- 组件来源：同一个已验签的 release.zip，组件库多保留 ReShade 与两个 DLSS zip；不额外下载。
- 设置范围：全局（IAS 运行目录只有一份）。

## 12. 警告条措辞修订（2026-11-24，用户口述，尚未实测，以实测为准）
- 冲突范围讲清楚：冲突的是**潜兵（玩家角色）自己穿的护甲**——IAS 和护甲映射都会改潜兵身上的护甲，所以会冲突；**NPC 不受影响**。
- 内置 Lua：NPC 那边虽不受影响，但 IAS 启用时仍建议关闭管理器内置 Lua。
- 警告条定稿文字（替换第 10 节与 W10 的对应句子）：
  第一行「IAS 已启用：潜兵身上的护甲由 IAS 接管，和护甲映射、带物理的普通 Mod 会冲突，请不要同时使用。NPC 不受影响。」
  内置 Lua 开着时追加「也建议关掉管理器的内置 Lua。」+ 按钮「关掉内置 Lua」。
  物理开着时按钮「关掉管理器物理」；有映射规则在用时追加「当前有护甲映射规则在用，建议在护甲映射页停用（只影响潜兵，NPC 的映射可保留）。」
