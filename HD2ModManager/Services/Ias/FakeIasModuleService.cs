#if DEBUG
using System.IO;
using HD2ModCore.Ias;

namespace HD2ModManager.Services.Ias;

internal sealed class FakeIasModuleService : IIasModuleService
{
    public bool IsGameRunning() => false;
    public Task<bool> RecoverPendingAsync(CancellationToken ct) => Task.FromResult(false);

    private const string CurrentVersion = "1.6.4";
    private const string UpdateVersion = "1.6.5";
    // 演示用：覆盖普通 / 求生 VPK / 素体 / 不可转 / 当前配置 / 库里其他各类卡片。
    internal static readonly IasLibraryCandidate[] Candidates =
    [
        new("demo-milfy", "Milfy SR-64", null, "普通 Mod")
            { ArmorName = "SR-64", OptionCount = 3, InActiveProfile = true },
        new("demo-bremerton", "布莱默顿", null, "普通 Mod") { ArmorName = "DP-8", InActiveProfile = true },
        new("demo-vpk", "少前2可露凯婚纱（原版礼服配色与长名字用来测试两行省略）", null, "求生 VPK")
            { Source = IasLibrarySource.L4d2Vpk, MaterialPackCount = 2, Number = "2606520120", InActiveProfile = true },
        new("demo-o3", "尘白-胧嫣原皮换 O-3 甲", null, "普通 Mod")
            { ArmorName = "O-3", OptionCount = 3, Number = "AR479939" },
        new("demo-vpk2", "泳装", null, "求生 VPK") { Source = IasLibrarySource.L4d2Vpk, MaterialPackCount = 1 },
        new("demo-body", "自定义素体", null, "素体")
            { Source = IasLibrarySource.CustomModel, UnavailableReason = IasLibraryCandidateReader.CustomModelReason },
        new("demo-role", "Eku", null, "普通 Mod")
            { InActiveProfile = true, UnavailableReason = IasLibraryCandidateReader.AlreadyRoleReason },
    ];

    private readonly List<IasCharacterInfo> _characters = [];
    private IasStatusKind _kind;
    private string? _version;
    private bool _physicsOn;
    private bool _builtInLuaOn;
    private bool _enabled;
    private IasGraphicsState _graphics = new(false, IasDlssMode.Off);
    private bool _graphicsRiskAcknowledged;
    private readonly bool _mappingInUse;

    public FakeIasModuleService(string? initialStatus)
    {
        var parts = (initialStatus ?? string.Empty).Split('+', StringSplitOptions.TrimEntries);
        _kind = Enum.TryParse<IasStatusKind>(parts[0], ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind : IasStatusKind.NotInstalled;
        _physicsOn = parts.Skip(1).Contains("Physics", StringComparer.OrdinalIgnoreCase);
        _builtInLuaOn = parts.Skip(1).Contains("Lua", StringComparer.OrdinalIgnoreCase);
        _mappingInUse = parts.Skip(1).Contains("Mapping", StringComparer.OrdinalIgnoreCase);
        if (_kind is IasStatusKind.Installed or IasStatusKind.UpdateAvailable or IasStatusKind.GameMismatch
            or IasStatusKind.MissingMdl3 or IasStatusKind.NotInProfile or IasStatusKind.FeedUnavailable)
        {
            _version = CurrentVersion;
            _enabled = _kind != IasStatusKind.NotInProfile && !parts.Skip(1).Contains("Off", StringComparer.OrdinalIgnoreCase);
            AddCharacter("Milfy SR-64", IasCharacterSource.IasPackage);
            AddCharacter("布莱默顿", IasCharacterSource.L4d2Vpk, enabled: false);
        }
    }

    public async Task<IasPageStatus> GetStatusAsync(CancellationToken ct)
    {
        await Task.Delay(250, ct);
        return new IasPageStatus(_kind, _version, _kind == IasStatusKind.UpdateAvailable ? UpdateVersion : CurrentVersion,
            249 * 1024 * 1024, _kind == IasStatusKind.UpdateAvailable ? "演示更新说明：更新 IAS 护甲部件。" : null,
            IasModuleService.OfficialReleaseUrl,
            _kind == IasStatusKind.FeedUnavailable ? "演示：连接超时。" : _kind == IasStatusKind.Error ? "演示：读取 IAS 状态失败。" : null,
            _kind == IasStatusKind.GameMismatch ? " 当前游戏版本尚未受到支持。" : null)
        {
            EnabledInActiveProfile = _enabled,
            PhysicsOn = _physicsOn,
            BuiltInLuaOn = _builtInLuaOn,
            MappingInUse = _mappingInUse,
            Graphics = _version is not null ? _graphics : null,
            HasGraphicsPackages = _version is not null,
            GraphicsRiskAcknowledged = _graphicsRiskAcknowledged,
        };
    }

    public Task<IReadOnlyList<IasCharacterInfo>> GetCharactersAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IasCharacterInfo>>(_characters.ToArray());
    }

    public Task<IReadOnlyList<IasLibraryCandidate>> GetConvertibleLibraryModsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IasLibraryCandidate>>(Candidates);
    }

    public IasDropClassification Classify(IReadOnlyList<string> paths)
        => new(paths.Select(path => (path, ClassifyPath(path))).ToArray());

    private static IasDropItemKind ClassifyPath(string path)
    {
        if (Directory.Exists(path)) return IasDropItemKind.PatchMod;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".zip" when Path.GetFileName(path).Contains("ias", StringComparison.OrdinalIgnoreCase) => IasDropItemKind.IasPackage,
            ".zip" => IasDropItemKind.PatchMod,
            ".vpk" => IasDropItemKind.L4d2Vpk,
            _ => IasDropItemKind.Unsupported,
        };
    }

    public async Task<IasOperationResult> InstallOrUpdateAsync(IProgress<IasPageProgress> progress, CancellationToken ct)
    {
        for (var step = 0; step < 12; step++)
        {
            var percent = step * 100d / 12;
            progress.Report(new("正在安装 IAS…", percent, step < 10
                ? $"正在下载 {249 * step / 10} / 249 MB"
                : step == 10 ? "正在校验…" : "正在部署…", true));
            await Task.Delay(250, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (_version is null) _enabled = true;
        _version = _kind == IasStatusKind.UpdateAvailable ? UpdateVersion : CurrentVersion;
        _kind = IasStatusKind.Installed;
        return new(true, $"已安装 IAS {_version}。", []);
    }

    public Task<IasOperationResult> UninstallAsync(bool removeCharacters, IProgress<IasPageProgress> progress, CancellationToken ct)
        => CompleteAsync("正在卸载 IAS…", progress, ct, () =>
        {
            if (removeCharacters) _characters.Clear();
            _version = null;
            _kind = IasStatusKind.NotInstalled;
            _enabled = false;
            _graphics = new(false, IasDlssMode.Off);
            return new(true, "已卸载 IAS。", []);
        });

    public Task<IasOperationResult> SetEnabledInActiveProfileAsync(bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct)
        => CompleteAsync(enabled ? "正在启用 IAS…" : "正在停用 IAS…", progress, ct, () =>
        {
            _enabled = enabled;
            if (_kind == IasStatusKind.NotInProfile) _kind = IasStatusKind.Installed;
            return new(true, enabled ? "已在当前配置启用 IAS。" : "已在当前配置停用 IAS。", []);
        });

    public Task<IasOperationResult> SetReShadeAsync(bool enabled, bool acknowledgeRisk, IProgress<IasPageProgress> progress, CancellationToken ct)
        => CompleteAsync("正在切换 ReShade 滤镜…", progress, ct, () =>
        {
            if (!_enabled) return new(false, "请先打开 IAS。", []);
            _graphicsRiskAcknowledged |= enabled && acknowledgeRisk;
            var removedDlss = !enabled && _graphics.Dlss != IasDlssMode.Off;
            _graphics = new(enabled, enabled ? _graphics.Dlss : IasDlssMode.Off);
            return new(true, enabled ? "已打开 ReShade 滤镜。" : removedDlss
                ? "已关闭 ReShade 滤镜，并同时关闭 DLSS。" : "已关闭 ReShade 滤镜。", []);
        });

    public Task<IasOperationResult> SetDlssAsync(IasDlssMode mode, IProgress<IasPageProgress> progress, CancellationToken ct)
        => CompleteAsync("正在切换 DLSS…", progress, ct, () =>
        {
            if (!_enabled || !_graphics.ReShadeEnabled) return new(false, "请先打开 ReShade 滤镜。", []);
            _graphics = _graphics with { Dlss = mode };
            return new(true, "DLSS 已更新。", []);
        });

    public Task<IasOperationResult> SetCharacterEnabledAsync(string characterId, bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct)
        => CompleteAsync("正在切换 IAS 角色…", progress, ct, () =>
        {
            var index = _characters.FindIndex(item => item.Id == characterId);
            if (index < 0) return new(false, "找不到要切换的 IAS 角色。", []);
            _characters[index] = _characters[index] with { Enabled = enabled };
            return new(true, enabled ? "IAS 角色已启用。" : "IAS 角色已停用。", []);
        });

    public Task<IasOperationResult> TurnOffManagerFeaturesAsync(bool physics, bool builtInLua, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (physics) _physicsOn = false;
        if (builtInLua) _builtInLuaOn = false;
        return Task.FromResult(new IasOperationResult(true, "已更新管理器设置，并请求重新部署当前配置。", []));
    }

    public Task<IasOperationResult> AddAsync(IReadOnlyList<string> paths, IProgress<IasPageProgress> progress, CancellationToken ct,
        IasVpkTargets? vpkTargets = null)
    {
        var classification = Classify(paths);
        return CompleteAsync("正在添加角色…", progress, ct, () =>
        {
            var warnings = new List<string>();
            foreach (var (path, kind) in classification.Items)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                switch (kind)
                {
                    case IasDropItemKind.IasPackage: AddCharacter(Path.GetFileNameWithoutExtension(name), IasCharacterSource.IasPackage); break;
                    case IasDropItemKind.PatchMod: AddCharacter(Directory.Exists(path) ? name : Path.GetFileNameWithoutExtension(name), IasCharacterSource.PatchMod); break;
                    case IasDropItemKind.L4d2Vpk: AddCharacter(Path.GetFileNameWithoutExtension(name), IasCharacterSource.L4d2Vpk); break;
                    default: warnings.Add($"不支持：{name}"); break;
                }
            }
            return classification.Supported == 0
                ? new(false, "不支持这些文件", warnings)
                : new(true, $"已添加 {classification.Supported} 个 IAS 角色。", warnings);
        });
    }

    public Task<IasOperationResult> AddFromLibraryAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct,
        IasVpkTargets? vpkTargets = null)
        => CompleteAsync("正在转换并安装角色…", progress, ct, () =>
        {
            var candidate = Candidates.FirstOrDefault(item => item.ModId == modId);
            if (candidate is null) return new(false, "找不到要转换的 Mod。", []);
            AddCharacter(candidate.Name, IasCharacterSource.PatchMod);
            return new(true, $"已添加角色：{candidate.Name}", []);
        });

    public Task<IasOperationResult> RemoveCharacterAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct)
        => CompleteAsync("正在卸载角色…", progress, ct, () =>
            _characters.RemoveAll(item => item.Id == characterId) > 0
                ? new(true, "已卸载角色。", []) : new(false, "找不到要卸载的角色。", []));

    public Task<IasVpkTargetCatalog> GetVpkTargetCatalogAsync(CancellationToken ct) =>
        Task.FromResult(new IasVpkTargetCatalog([], []));

    public Task<IasStandardTargetCatalog> GetStandardTargetCatalogAsync(string characterId, CancellationToken ct) =>
        Task.FromResult(new IasStandardTargetCatalog(new("测试护甲", 1, []), new("测试头盔", 2, [])));

    public Task<IasOperationResult> ConvertToStandardModAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct,
        string? targetArmor = null, string? targetHelmet = null, bool mergeHelmet = false)
        => CompleteAsync("正在转为普通 Mod…", progress, ct, () =>
        {
            var character = _characters.FirstOrDefault(item => item.Id == characterId);
            return character is null ? new(false, "找不到要转换的角色。", [])
                : new(true, $"已放进 Mod 库：{character.Name}", []);
        });

    private void AddCharacter(string name, IasCharacterSource source, bool enabled = true)
        => _characters.Add(new(Guid.NewGuid().ToString("N"), name, null, source, DateTimeOffset.Now, enabled));

    private static async Task<IasOperationResult> CompleteAsync(string title, IProgress<IasPageProgress> progress,
        CancellationToken ct, Func<IasOperationResult> complete)
    {
        progress.Report(new(title, null, null, false));
        await Task.Delay(450, ct);
        return complete();
    }
}
#endif
