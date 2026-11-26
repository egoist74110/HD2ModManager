using HD2ModCore.Ias;

namespace HD2ModManager.Services.Ias;

public interface IIasModuleService
{
    bool IsGameRunning();
    Task<bool> RecoverPendingAsync(CancellationToken ct);
    Task<IasPageStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<IasCharacterInfo>> GetCharactersAsync(CancellationToken ct);
    Task<IReadOnlyList<IasLibraryCandidate>> GetConvertibleLibraryModsAsync(CancellationToken ct);
    Task<IasVpkTargetCatalog> GetVpkTargetCatalogAsync(CancellationToken ct);
    Task<IasStandardTargetCatalog> GetStandardTargetCatalogAsync(string characterId, CancellationToken ct);
    Task<IasRetargetAnalysis> GetRetargetChoicesAsync(string characterId, CancellationToken ct) =>
        Task.FromResult(new IasRetargetAnalysis(null, null, [], "当前没有可读取的护甲选项。"));
    Task<IasOperationResult> RetargetAsync(string characterId, IasRetargetChoice choice, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        Task.FromResult(new IasOperationResult(false, "当前无法换护甲。", []));
    IasDropClassification Classify(IReadOnlyList<string> paths);

    Task<IasOperationResult> InstallOrUpdateAsync(IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> UninstallAsync(bool removeCharacters, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> SetEnabledInActiveProfileAsync(bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> SetCharacterEnabledAsync(string characterId, bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> AdoptLibraryRoleAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> SetReShadeAsync(bool enabled, bool acknowledgeRisk, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> SetDlssAsync(IasDlssMode mode, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> TurnOffManagerFeaturesAsync(bool physics, bool builtInLua, CancellationToken ct);
    Task<IasOperationResult> AddAsync(IReadOnlyList<string> paths, IProgress<IasPageProgress> progress, CancellationToken ct,
        IasVpkTargets? vpkTargets = null);
    Task<IasOperationResult> AddFromLibraryAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct,
        IasVpkTargets? vpkTargets = null);
    Task<IasOperationResult> RemoveCharacterAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct);
    Task<IasOperationResult> ConvertToStandardModAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct,
        string? targetArmor = null, string? targetHelmet = null, bool mergeHelmet = false);
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
    string? Detail)             // GameMismatch 的细节
{
    public bool EnabledInActiveProfile { get; init; }
    public bool PhysicsOn { get; init; }
    public bool BuiltInLuaOn { get; init; }
    public bool MappingInUse { get; init; }
    public IasGraphicsState? Graphics { get; init; }
    public bool HasGraphicsPackages { get; init; }
    public bool GraphicsRiskAcknowledged { get; init; }
    public string? GraphicsError { get; init; }
}

public enum IasCharacterSource { IasPackage, PatchMod, L4d2Vpk }

public sealed record IasCharacterInfo(string Id, string Name, string? IconPath, IasCharacterSource Source, DateTimeOffset AddedAt, bool? Enabled)
{
    public string? LibraryModId { get; init; }
    public string? RegisteredModId { get; init; }
    public string? LibraryOrigin { get; init; }
    public bool MissingFromLibrary { get; init; }
    public string? Equipment { get; init; }
    public string? Conflict { get; init; }
    public string Revision { get; init; } = "";
    public string? RegisteredRevision { get; init; }
    public IasRoleArmor? Armor { get; init; }
}

/// <summary>库里 Mod 的来源：决定转换走哪条路。</summary>
public enum IasLibrarySource { Standard, L4d2Vpk, CustomModel }

/// <summary>
/// 「从库里选 Mod 转 IAS」对话框的一张卡片。除四个位置参数外，其余字段都只在能从数据里读到时才填。
/// </summary>
public sealed record IasLibraryCandidate(string ModId, string Name, string? ThumbnailPath, string KindText)
{
    public IasLibrarySource Source { get; init; } = IasLibrarySource.Standard;
    /// <summary>是否在当前配置里启用。</summary>
    public bool InActiveProfile { get; init; }
    /// <summary>套在哪件护甲上（护甲目录里的名字）。</summary>
    public string? ArmorName { get; init; }
    public int? MaterialPackCount { get; init; }
    public int? OptionCount { get; init; }
    /// <summary>Mod 库里记录的编号（创意工坊 ID 等）；读不到为 null。</summary>
    public string? Number { get; init; }
    /// <summary>不为 null 表示不能转换；文字就是给用户看的原因。</summary>
    public string? UnavailableReason { get; init; }

    public bool IsConvertible => UnavailableReason is null;

    public string SourceText => Source switch
    {
        IasLibrarySource.L4d2Vpk => "求生 VPK",
        IasLibrarySource.CustomModel => "素体",
        _ => "普通 Mod",
    };

    /// <summary>卡片第二行：套在哪件护甲、材质包数、选项数；一项都没有时为空。</summary>
    public string MetaText => string.Join(" · ", new[]
    {
        ArmorName is { Length: > 0 } armor ? $"套在 {armor}" : null,
        MaterialPackCount is > 0 ? $"{MaterialPackCount} 个材质包" : null,
        OptionCount is > 0 ? $"{OptionCount} 个选项" : null,
    }.Where(part => part is not null));

    public bool HasMeta => MetaText.Length != 0;

    /// <summary>搜索只匹配名字和编号。</summary>
    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) ||
        Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        Number?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;
}

public enum IasDropItemKind { IasPackage, PatchMod, L4d2Vpk, Unsupported, Archive }
public sealed record IasDropClassification(IReadOnlyList<(string Path, IasDropItemKind Kind)> Items)
{
    public int PairedItems { get; init; }
    public int Supported => Items.Count(i => i.Kind != IasDropItemKind.Unsupported) - Math.Max(0, PairedItems - 1);
    public int Unsupported => Items.Count(i => i.Kind == IasDropItemKind.Unsupported);
    public bool NeedsConversion => Items.Any(i => i.Kind is IasDropItemKind.PatchMod or IasDropItemKind.L4d2Vpk);
}

public sealed record IasPageProgress(string Title, double? Percent, string? Detail, bool CanCancel);

public sealed record IasOperationResult(bool Success, string Message, IReadOnlyList<string> Warnings, string? CreatedModId = null)
{
    /// <summary>失败原因是下载连不上（换下载来源可能解决）。</summary>
    public bool DownloadFailed { get; init; }
    public bool Warning { get; init; }
    public string? SwapRoleId { get; init; }
    public string? RetargetRoleId { get; init; }
    public string? FlashRoleId { get; init; }
    public string? UpdatedRoleId { get; init; }
    public bool SkipLibraryReload { get; init; }
}
