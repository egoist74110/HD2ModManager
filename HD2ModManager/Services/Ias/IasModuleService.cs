using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using HD2GameItemAssets;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCore.Domain;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2SourceMigration.IasConversion;
using HD2SourceMigration.IasPackaging;
using HD2SourceMigration.VpkConversion;

namespace HD2ModManager.Services.Ias;

public sealed class IasModuleService(
    Func<StoragePaths> getPaths, Func<string?> getGameRoot, HttpClient http,
    Func<Task>? refreshLibrary = null, Func<PhysicsMode>? getPhysicsMode = null,
    Func<ArmorMappingMode>? getMappingMode = null, Func<bool>? getBuiltInLuaEnabled = null,
    Func<bool, bool, Task>? turnOffManagerFeatures = null,
    OptionActivationStore? optionActivations = null,
    Func<IReadOnlyList<RuntimeArmorCatalogEntry>, CancellationToken, Task<IReadOnlyDictionary<string, string?>>>? readArmorIcons = null) : IIasModuleService
{
    public const string OfficialReleaseUrl = "https://github.com/Estecsky/HD2-Integrated-Armor-System/releases";

    public bool IsGameRunning() => GameProcessProbe.IsGameRunning();

    public async Task<bool> RecoverPendingAsync(CancellationToken ct)
    {
        EnsureGameStopped();
        return await Task.Run(() => RecoverAndRefreshAsync(Open(getPaths(), requireGame: false), ct), ct).ConfigureAwait(false);
    }

    private async Task<bool> RecoverAndRefreshAsync(IasDeploymentService service, CancellationToken ct)
    {
        var recovered = await service.RecoverAsync(ct).ConfigureAwait(false);
        if (recovered && refreshLibrary is not null) await refreshLibrary().ConfigureAwait(false);
        return recovered;
    }

    private void EnsureGameStopped()
    {
        if (IsGameRunning()) throw new IasGameRunningException();
    }

    private IasDeploymentService Open(StoragePaths paths, bool requireGame = true)
    {
        var game = getGameRoot();
        if (string.IsNullOrWhiteSpace(game))
        {
            if (requireGame) throw new InvalidOperationException("请先在设置中选择游戏目录。");
            game = Path.Combine(paths.AppRootDirectory, "ias", "missing-game");
        }
        var worker = new IasProcessWorker(IasProcessWorker.Locate(AppContext.BaseDirectory),
            Path.Combine(paths.AppRootDirectory, "ias", "worker"), http);
        return new(paths, game, http, worker, IsGameRunning, optionActivations: optionActivations);
    }

    public async Task<IasPageStatus> GetStatusAsync(CancellationToken ct)
    {
        using (IasTiming.Begin("刷新 IAS 页"))
            return await GetStatusCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task<IasPageStatus> GetStatusCoreAsync(CancellationToken ct)
    {
        string? version = null;
        var conflicts = (Enabled: false, PhysicsOn: false, BuiltInLuaOn: false, MappingInUse: false);
        try
        {
            var paths = getPaths();
            var service = Open(paths, requireGame: false);
            version = service.GetInstalled()?.Version;
            var (state, feed) = await IasTiming.MeasureAsync("检查组件与官方更新", () => service.GetComponentStatusAsync(ct)).ConfigureAwait(false);
            var canCheckMdl3 = state is IasModuleState.Installed or IasModuleState.UpdateAvailable;
            if (!canCheckMdl3 && version is null)
                return ProjectStatus(state, feed, version, false, false);
            var snapshot = await service.LoadModsAsync(ct).ConfigureAwait(false);
            var profile = IasDeploymentService.Active(snapshot);
            var inProfile = service.HasManagedMod(profile);
            conflicts.Enabled = !string.IsNullOrWhiteSpace(version) && inProfile;
            if (conflicts.Enabled)
            {
                conflicts.PhysicsOn = getPhysicsMode?.Invoke() is { } mode && mode != PhysicsMode.Off;
                conflicts.BuiltInLuaOn = getBuiltInLuaEnabled?.Invoke() == true;
                if (getMappingMode is not null)
                    conflicts.MappingInUse = await HasMappingInUseAsync(paths, getMappingMode(), ct).ConfigureAwait(false);
            }
            var hasMdl3 = canCheckMdl3 && await HasMdl3Async(paths, snapshot, profile, ct).ConfigureAwait(false);
            var pageStatus = ProjectStatus(state, feed, version, hasMdl3, inProfile) with
            {
                EnabledInActiveProfile = conflicts.Enabled,
                PhysicsOn = conflicts.PhysicsOn,
                BuiltInLuaOn = conflicts.BuiltInLuaOn,
                MappingInUse = conflicts.MappingInUse,
            };
            if (!canCheckMdl3) return pageStatus;
            try
            {
                return pageStatus with
                {
                    Graphics = await service.GetGraphicsStateAsync(ct).ConfigureAwait(false),
                    HasGraphicsPackages = service.HasGraphicsPackages,
                    GraphicsRiskAcknowledged = service.GraphicsRiskAcknowledged,
                };
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return pageStatus with { GraphicsError = $"无法检查画面增强状态：{error.Message}" };
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(IasStatusKind.Error, version, null, null, null, OfficialReleaseUrl, $"无法检查 IAS 状态：{error.Message}", null)
            {
                EnabledInActiveProfile = conflicts.Enabled,
                PhysicsOn = conflicts.PhysicsOn,
                BuiltInLuaOn = conflicts.BuiltInLuaOn,
                MappingInUse = conflicts.MappingInUse,
            };
        }
    }

    private static async Task<bool> HasMappingInUseAsync(StoragePaths paths, ArmorMappingMode mode, CancellationToken ct)
    {
        var registry = CoreServices.CreateRuntimeArmorRegistryService(paths);
        if (KitInstanceRulesFile.CountMappingRules(registry.RegistryRoot) > 0) return true;

        try
        {
            var hideRulesPath = KitInstanceRulesFile.GetPath(registry.RegistryRoot);
            if (File.Exists(hideRulesPath) && new FileInfo(hideRulesPath).Length <= KitInstanceRulesProtocol.MaximumBytes)
            {
                var text = await File.ReadAllTextAsync(hideRulesPath, ct).ConfigureAwait(false);
                if (KitInstanceRulesProtocol.TryParse(text, out var hidden, out _) && hidden!.Rules.Count > 0)
                    return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        if (!mode.UsesRuntimeLua()) return false;
        try
        {
            if (!await registry.IsManagedDeploymentEnabledAsync(ct).ConfigureAwait(false)) return false;
            var document = (await registry.ReadSnapshotAsync(ct).ConfigureAwait(false)).Document;
            return document.Enabled && (document.Mappings.Count > 0 || document.PieceOverrideDefinitions.Count > 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static IasPageStatus ProjectStatus(IasModuleState state, IasFeedResult? feed, string? version, bool hasMdl3, bool inProfile)
    {
        var latest = feed?.Latest;
        var unavailable = feed is { Latest: null };
        var kind = state switch
        {
            IasModuleState.Error => IasStatusKind.Error,
            IasModuleState.Busy => IasStatusKind.Checking,
            IasModuleState.GameMismatch => IasStatusKind.GameMismatch,
            IasModuleState.NotInstalled => unavailable ? IasStatusKind.FeedUnavailable : IasStatusKind.NotInstalled,
            _ when !hasMdl3 => IasStatusKind.MissingMdl3,
            IasModuleState.UpdateAvailable => IasStatusKind.UpdateAvailable,
            _ when unavailable => IasStatusKind.FeedUnavailable,
            _ => IasStatusKind.Installed
        };
        return new(kind, version, latest?.Version, latest?.ZipBytes, latest?.Notes, latest?.ReleaseUrl.AbsoluteUri ?? OfficialReleaseUrl,
            state is IasModuleState.Error error ? error.Message : unavailable ? feed!.Message : null,
            state is IasModuleState.GameMismatch mismatch ? mismatch.Details.Message : null)
        {
            EnabledInActiveProfile = version is not null && inProfile
        };
    }

    private static async Task<bool> HasMdl3Async(StoragePaths paths, LibrarySnapshot snapshot, Profile? profile, CancellationToken ct)
    {
        if (profile is null) return false;
        var scanner = new HD2ModAdaptation.PatchReconstruction.PatchTocScanner();
        var payloads = new HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader();
        var boot = $"{LuaAssetKeys.BootArchiveId:x16}";
        var activations = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(
            Path.Combine(paths.ModsDirectory, "option-activations.json"), ct).ConfigureAwait(false);
        var effective = ProfileEffectiveProfileResolver.Resolve(profile, snapshot, activations);
        foreach (var entry in effective.Entries)
        {
            if (!snapshot.Nodes.TryGetValue(entry.NodeId, out var node)) continue;
            var dir = Path.GetFullPath(Path.Combine(paths.ModsDirectory, node.RelativePath));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, boot + ".patch_*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                if (!IasPackageDetector.IsPatch(file) || Path.GetExtension(file) is ".stream" or ".gpu_resources") continue;
                var entries = await scanner.ScanEntriesAsync(file, ct).ConfigureAwait(false);
                var lua = entries.FirstOrDefault(e => LuaAssetKeys.IsBoot(e.AssetKey));
                if (lua is null) continue;
                if (node.Metadata.Name.Contains("MDL3", StringComparison.OrdinalIgnoreCase)) return true;
                if (lua.TocDataSize > 4 * 1024 * 1024 || lua.StreamSize != 0 || lua.GpuResourceSize != 0) continue;
                var payload = await payloads.ReadPayloadAsync(lua, ct).ConfigureAwait(false);
                var content = Encoding.UTF8.GetString(payload.TocData);
                if (content.Contains("MDL3", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("-- HD2-Addon: mods/codex/hd2_data_hub", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    public Task<IReadOnlyList<IasCharacterInfo>> GetCharactersAsync(CancellationToken ct) =>
        Task.Run<IReadOnlyList<IasCharacterInfo>>(async () =>
        {
            var service = Open(getPaths(), requireGame: false);
            var roles = await service.GetRolesWithStateAsync(ct).ConfigureAwait(false);
            return roles.Select(item =>
            {
                var (role, enabled) = item;
                var icon = Path.Combine(service.Sources(role.Id), "companion", "icon.png");
                return new IasCharacterInfo(role.Id, role.Name, File.Exists(icon) ? icon : null, (IasCharacterSource)role.Source, role.AddedAt, enabled);
            }).ToArray();
        }, ct);

    public async Task<IReadOnlyList<IasLibraryCandidate>> GetConvertibleLibraryModsAsync(CancellationToken ct)
    {
        var paths = getPaths();
        var service = Open(paths);
        var snapshot = await service.LoadModsAsync(ct).ConfigureAwait(false);
        var roleMods = service.GetRoles().SelectMany(role => role.ModIds).ToHashSet();
        var managed = service.ManagedModId;
        var active = IasDeploymentService.Active(snapshot)?.Entries.Select(entry => entry.NodeId.Value).ToHashSet() ?? [];
        // 护甲名从护甲映射页同一份目录里取；目录还没建好就不显示名字，不影响候选。
        IasEquipmentCatalog? catalog = null;
        try { catalog = await IasEquipmentCatalog.ReadAsync(paths, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        await using var reader = CoreServices.CreateModInformationReader();
        await using var center = CoreServices.CreateModInformationCenter(paths, reader);
        var projector = CoreServices.CreateModAssetSummaryProjector(paths, center);
        var found = new List<IasLibraryCandidate>();
        foreach (var node in snapshot.Nodes.Values.Where(node => node.Metadata.Kind == ModNodeKind.Standard))
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(paths.ModsDirectory, node.RelativePath);
            var facts = await IasLibraryCandidateReader.InspectAsync(dir, ct).ConfigureAwait(false);
            var reason = node.Id.Value == managed ? IasLibraryCandidateReader.ManagedModReason
                : roleMods.Contains(node.Id.Value) ? IasLibraryCandidateReader.AlreadyRoleReason
                : IasLibraryCandidateReader.UnavailableReason(facts);
            string? armor = facts.WardrobeTargetItemId is { } item ? catalog?.NameOf(item) : null;
            if (facts.Source == IasLibrarySource.Standard)
            {
                var assets = await center.RequestAssetInventoryAsync(node, paths.ModsDirectory,
                    new(ModInformationKind.AssetInventory, "IAS"), ct).ConfigureAwait(false);
                if (assets.Data is null) continue;
                var summary = await projector.ProjectAsync(node, assets.Data, ct).ConfigureAwait(false);
                var armors = summary.Assets.Where(asset => asset.ArchiveCategory.Equals("Armor", StringComparison.OrdinalIgnoreCase) ||
                    asset.ArchiveCategory.Equals("护甲", StringComparison.Ordinal)).ToArray();
                if (armors.Length == 0) continue;
                var names = armors.Select(asset => asset.ArchiveDisplayName).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal).ToArray();
                armor = names.Length switch { 0 => null, 1 => names[0], _ => $"{names[0]} 等 {names.Length} 件" };
            }
            var options = node.Children.Count(child => snapshot.Nodes.TryGetValue(child, out var childNode) &&
                childNode.Metadata.Kind == ModNodeKind.Option);
            var candidate = new IasLibraryCandidate(node.Id.Value.ToString("D"), node.Metadata.Name,
                ModIconLocator.TryResolve(dir), "") with
            {
                Source = facts.Source,
                InActiveProfile = active.Contains(node.Id.Value),
                ArmorName = armor,
                MaterialPackCount = facts.MaterialPackCount,
                OptionCount = options,
                Number = IasLibraryCandidateReader.NumberOf(node.RelativePath),
                UnavailableReason = reason,
            };
            found.Add(candidate with { KindText = candidate.SourceText });
        }
        return found.OrderBy(item => item.Name, StringComparer.CurrentCulture).ToArray();
    }

    public Task<IasVpkTargetCatalog> GetVpkTargetCatalogAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var storage = getPaths();
        var service = Open(storage);
        if (service.GetInstalled() is null)
            throw new InvalidOperationException("请先安装 IAS。");
        var stage = ConversionStage(storage);
        try
        {
            var sdk = await IasAqSdk.GetAsync(storage, stage, ct).ConfigureAwait(false);
            var targets = await IasPackagingService.GetVpkTargetsAsync(sdk, service.GameData, ct).ConfigureAwait(false);
            var catalog = await IasEquipmentCatalog.ReadAsync(storage, ct).ConfigureAwait(false);
            var armor = catalog.ByArchive(false, targets.BodyArchives);
            var helmet = catalog.ByArchive(true, targets.HelmetArchives);
            var icons = await ReadIconsAsync(armor.Concat(helmet), ct).ConfigureAwait(false);
            return new IasVpkTargetCatalog(WithIcons(armor, icons), WithIcons(helmet, icons));
        }
        finally { IasFileTree.Delete(stage); }
    }, ct);

    public Task<IasStandardTargetCatalog> GetStandardTargetCatalogAsync(string characterId, CancellationToken ct) => Task.Run(async () =>
    {
        var storage = getPaths();
        var service = Open(storage);
        if (!service.GetRoles().Any(role => role.Id == characterId))
            throw new InvalidOperationException("找不到要转换的 IAS 角色。");
        var equipment = await IasStandardConverter.ReadOriginalEquipmentAsync(service.Sources(characterId), ct).ConfigureAwait(false);
        var authority = await IasSlotAuthority.ReadAsync(service.GameData, ct).ConfigureAwait(false);
        var catalog = await IasEquipmentCatalog.ReadAsync(storage, ct).ConfigureAwait(false);
        IasStandardTargetDomain? armor = null;
        IasStandardTargetDomain? helmet = null;
        foreach (var item in equipment)
        {
            var (isHelmet, archive, id) = item.Equipment;
            var source = authority.FindOriginal(isHelmet, archive, id, item.HiddenByUnit.Keys.ToArray());
            var allowed = authority.GetCompatibleTargets(isHelmet, archive, id, item.HiddenByUnit);
            var domain = new IasStandardTargetDomain(source is null ? null : catalog.NameOf(source.Id) ?? source.Name,
                source?.Id, catalog.ByItemId(isHelmet, allowed));
            if (isHelmet) helmet = domain;
            else armor = domain;
        }
        var icons = await ReadIconsAsync((armor?.Choices ?? []).Concat(helmet?.Choices ?? []), ct).ConfigureAwait(false);
        return new IasStandardTargetCatalog(
            armor is null ? null : armor with { Choices = WithIcons(armor.Choices, icons) },
            helmet is null ? null : helmet with { Choices = WithIcons(helmet.Choices, icons) });
    }, ct);

    private async Task<IReadOnlyDictionary<string, string?>> ReadIconsAsync(IEnumerable<IasTargetChoice> choices, CancellationToken ct) =>
        readArmorIcons is null ? new Dictionary<string, string?>() :
        await readArmorIcons(choices.SelectMany(choice => choice.Members).ToArray(), ct).ConfigureAwait(false);

    private static IReadOnlyList<IasTargetChoice> WithIcons(IReadOnlyList<IasTargetChoice> choices, IReadOnlyDictionary<string, string?> icons) =>
        choices.Select(choice => choice with
        {
            IconPath = choice.Members.Select(member => icons.GetValueOrDefault(member.ArchiveId)).FirstOrDefault(path => path is not null)
        }).ToArray();

    public IasDropClassification Classify(IReadOnlyList<string> paths)
    {
        var items = paths.Select(path => (Path: path, Kind: (IasDropItemKind)IasPackageDetector.Classify(path))).ToArray();
        if (items.Any(item => item.Kind == IasDropItemKind.IasPackage))
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Kind == IasDropItemKind.PatchMod && IasPackageDetector.IsNamedMain(items[i].Path))
                    items[i].Kind = IasDropItemKind.IasPackage;
            }
            return new(items) { PairedItems = items.Count(item => item.Kind == IasDropItemKind.IasPackage) };
        }
        return new(items);
    }

    public Task<IasOperationResult> InstallOrUpdateAsync(IProgress<IasPageProgress> progress, CancellationToken ct) =>
        InstallAsync(null, progress, ct);

    public Task<IasOperationResult> InstallAsync(string? offline, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync("安装 IAS", progress, ct, async (service, _) =>
        {
            await service.InstallAsync(offline, new ProgressAdapter(progress), ct).ConfigureAwait(false);
            return new(true, "IAS 部件已就绪。", []);
        });

    public Task<IasOperationResult> UninstallAsync(bool removeCharacters, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync("卸载 IAS", progress, ct, async (service, _) =>
        {
            await service.UninstallAsync(removeCharacters, ct).ConfigureAwait(false);
            return new(true, "IAS 已卸载。", []);
        });

    public Task<IasOperationResult> SetEnabledInActiveProfileAsync(bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync(enabled ? "启用 IAS" : "停用 IAS", progress, ct, async (service, _) =>
        {
            await service.SetEnabledInActiveProfileAsync(enabled, ct).ConfigureAwait(false);
            return new(true, enabled ? "已在当前配置启用 IAS。" : "已在当前配置停用 IAS。", []);
        });

    public Task<IasOperationResult> SetCharacterEnabledAsync(string characterId, bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync(enabled ? "启用 IAS 角色" : "停用 IAS 角色", progress, ct, async (service, _) =>
        {
            await service.SetCharacterEnabledAsync(characterId, enabled, ct).ConfigureAwait(false);
            return new(true, enabled ? "IAS 角色已启用。" : "IAS 角色已停用。", []);
        });

    public Task<IasOperationResult> SetReShadeAsync(bool enabled, bool acknowledgeRisk, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync(enabled ? "打开 ReShade 滤镜" : "关闭 ReShade 滤镜", progress, ct, async (service, _) =>
        {
            ct.ThrowIfCancellationRequested();
            if (enabled && acknowledgeRisk) await service.AcknowledgeGraphicsRiskAsync(ct).ConfigureAwait(false);
            var removedDlss = await service.SetReShadeAsync(enabled, ct).ConfigureAwait(false);
            return new(true, enabled ? "已打开 ReShade 滤镜。" : removedDlss
                ? "已关闭 ReShade 滤镜，并同时关闭 DLSS。" : "已关闭 ReShade 滤镜。", []);
        }, keepErrorMessage: true);

    public Task<IasOperationResult> SetDlssAsync(IasDlssMode mode, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync("切换 DLSS", progress, ct, async (service, _) =>
        {
            await service.SetDlssAsync(mode, ct).ConfigureAwait(false);
            return new(true, mode switch
            {
                IasDlssMode.FrameGeneration => "已启用 DLSS 插帧。",
                IasDlssMode.NeuralRendering => "已启用 DLSS 神经渲染。",
                _ => "已关闭 DLSS。"
            }, []);
        }, keepErrorMessage: true);

    public async Task<IasOperationResult> TurnOffManagerFeaturesAsync(bool physics, bool builtInLua, CancellationToken ct)
    {
        await RecoverPendingAsync(ct);
        if (turnOffManagerFeatures is null)
            return new(false, "无法通过当前入口修改管理器设置。", []);
        var service = Open(getPaths(), requireGame: false);
        var snapshot = await service.LoadModsAsync(ct);
        if (service.GetInstalled() is null || !service.HasManagedMod(IasDeploymentService.Active(snapshot)))
            return new(false, "IAS 已不在当前配置启用。", []);

        ct.ThrowIfCancellationRequested();
        EnsureGameStopped();
        await turnOffManagerFeatures(physics, builtInLua);
        var features = physics && builtInLua ? "管理器物理和内置 Lua" : physics ? "管理器物理" : "管理器内置 Lua";
        return new(true, $"已关掉{features}，并请求重新部署当前配置。", []);
    }

    public Task<IasOperationResult> AddAsync(IReadOnlyList<string> paths, IProgress<IasPageProgress> progress, CancellationToken ct,
        IasVpkTargets? vpkTargets = null) =>
        ExecuteAsync("添加 IAS 角色", progress, ct, (service, storage) =>
            AddDroppedAsync(service, storage, paths, progress, ct, vpkTargets));

    /// <summary>
    /// 拖入与「从库里选」共用的添加流程。<paramref name="libraryName"/> 非空表示输入来自库里的求生 VPK Mod：
    /// 角色用 Mod 自己的名字；<paramref name="verifyLibrary"/> 在发布前再核一次库里那个 Mod 没被改动。
    /// </summary>
    private async Task<IasOperationResult> AddDroppedAsync(IasDeploymentService service, StoragePaths storage,
        IReadOnlyList<string> paths, IProgress<IasPageProgress> progress, CancellationToken ct, IasVpkTargets? vpkTargets,
        string? libraryName = null, Func<LibrarySnapshot, CancellationToken, Task>? verifyLibrary = null)
    {
        EnsureEnabledForAdd(service, await service.LoadModsAsync(ct).ConfigureAwait(false));
        using var workspace = new IasDropWorkspace();
        var inputs = new List<IasInputContents>();
        var origins = new Dictionary<string, IasSourceStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            progress.Report(new("正在识别 IAS 拖入文件…", null, Path.GetFileName(path), true));
            origins[path] = await IasTiming.MeasureAsync("记录拖入源",
                () => IasSourceStamp.CaptureAsync(path, $"拖入源「{Path.GetFileName(path)}」", ct)).ConfigureAwait(false);
            inputs.Add(await IasTiming.MeasureAsync("识别与解包拖入文件",
                () => workspace.InspectAsync(path, ct)).ConfigureAwait(false));
        }
        var direct = inputs.Where(item => item.Companions.Count != 0 || item.HasNamedMain).ToList();
        if (direct.Any(item => item.Companions.Count != 0) && direct.All(item => item.Main.Count == 0))
            direct.AddRange(inputs.Where(item => item.Main.Count != 0));

        var conversions = new List<DropConversion>();
        var warnings = new List<string>();
        var vpkOrigins = new Dictionary<string, IasInputContents>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inputs.Except(direct))
        {
            if (item.Main.Count != 0)
                conversions.AddRange(item.Main.Select(path => new DropConversion(
                    path, IasDropItemKind.PatchMod, DropName(item.OriginalPath), [origins[item.OriginalPath]], [])));
            else if (item.Vpks.Count != 0)
            {
                foreach (var path in item.Vpks)
                    vpkOrigins.TryAdd(Path.GetFullPath(path), item);
            }
            else
                warnings.Add($"已跳过不支持的文件：{Path.GetFileName(item.OriginalPath)}");
        }

        string? noModels = null;
        if (vpkOrigins.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new("正在分组并去重 IAS 模型…", null, null, true));
            var plan = IasTiming.Measure("规划 IAS VPK 主体组", () => PlanDroppedVpks([.. vpkOrigins.Keys], warnings));
            noModels = plan.NoModelsMessage;
            var sharedNames = plan.Groups.GroupBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in plan.Groups)
            {
                var item = vpkOrigins[group.ModelVpkPath];
                var name = sharedNames.Contains(group.DisplayName)
                    ? $"{group.DisplayName}（{Path.GetFileName(Path.GetDirectoryName(group.ModelVpkPath))}）"
                    : libraryName is not null ? (plan.Groups.Count == 1 ? libraryName : $"{libraryName}（{group.DisplayName}）")
                    : item.Vpks.Count == 1 ? DropName(item.OriginalPath) : group.DisplayName;
                // 跨拖入项借用材质时，发布前也要复核材质的原始文件或压缩包。
                var stamps = group.MaterialVpkPaths.Prepend(group.ModelVpkPath)
                    .Select(path => origins[vpkOrigins[path].OriginalPath]).Distinct().ToArray();
                conversions.Add(new(group.ModelVpkPath, IasDropItemKind.L4d2Vpk, name, stamps, group.MaterialVpkPaths));
            }
        }

        string? sdk = null;
        string? vpkSetupError = null;
        IasVpkTargetCatalog? vpkCatalog = null;
        if (conversions.Any(item => item.Kind == IasDropItemKind.L4d2Vpk))
        {
            var stage = ConversionStage(storage);
            try
            {
                if (vpkTargets?.Error is { } targetError) throw new InvalidDataException(targetError);
                progress.Report(new("正在检查 IAS 底子装备…", null, null, true));
                sdk = await IasTiming.MeasureAsync("获取 AQ SDK",
                    () => IasAqSdk.GetAsync(storage, stage, ct)).ConfigureAwait(false);
                var targets = await IasTiming.MeasureAsync("查找可用 IAS 底子",
                    () => IasPackagingService.GetVpkTargetsAsync(sdk, service.GameData, ct)).ConfigureAwait(false);
                var catalog = await IasEquipmentCatalog.ReadAsync(storage, ct).ConfigureAwait(false);
                vpkCatalog = new(catalog.ByArchive(false, targets.BodyArchives), catalog.ByArchive(true, targets.HelmetArchives));
                var preferred = new IasVpkTargets(
                    vpkTargets?.Body ?? vpkCatalog.Armor.FirstOrDefault()?.Primary?.ArchiveId,
                    vpkTargets?.Helmet ?? vpkCatalog.Helmet.FirstOrDefault()?.Primary?.ArchiveId);
                vpkTargets = IasVpkTargets.Resolve(preferred, targets);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                vpkSetupError = error.Message;
            }
            finally { IasFileTree.Delete(stage); }
        }

        ct.ThrowIfCancellationRequested();
        var count = 0;
        if (direct.Count != 0)
        {
            progress.Report(new("正在添加 IAS 主包与配套包…", null, null, true));
            try
            {
                await service.AddPreparedAsync(direct, ct, async (_, token) =>
                {
                    foreach (var item in direct)
                        await origins[item.OriginalPath].VerifyAsync(token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                count++;
            }
            catch (Exception error) when (conversions.Count != 0 && error is not OperationCanceledException)
            {
                warnings.Add($"添加 IAS 主包与配套包失败：{error.Message}");
            }
        }
        var reserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return await AddDropConversionsAsync(conversions, count, warnings, noModels, async (conversion, token) =>
        {
            var name = conversion.Name ?? DropName(conversion.Path);
            if (conversion.Kind != IasDropItemKind.L4d2Vpk) return await Convert(null).ConfigureAwait(false);
            if (vpkSetupError is not null) throw new InvalidDataException(vpkSetupError);
            var occupancy = await service.GetArchiveOccupancyAsync(token).ConfigureAwait(false);
            return await IasVpkAllocation.ConvertAsync(vpkTargets!, vpkCatalog!, occupancy, reserved, name, Convert)
                .ConfigureAwait(false);

            Task<IReadOnlyList<string>> Convert(IasVpkTargets? targets) =>
                ConvertAndAddAsync(service, storage, conversion.Path, conversion.Kind, progress, token, name,
                    sdk, targets, async (current, commitToken) =>
                {
                    if (verifyLibrary is not null) await verifyLibrary(current, commitToken).ConfigureAwait(false);
                    foreach (var origin in conversion.Origins)
                        await origin.VerifyAsync(commitToken).ConfigureAwait(false);
                }, conversion.MaterialVpks);
        }, ct).ConfigureAwait(false);
    }

    internal sealed record DropConversion(string Path, IasDropItemKind Kind, string? Name,
        IReadOnlyList<IasSourceStamp> Origins, IReadOnlyList<string> MaterialVpks);

    internal static (IReadOnlyList<VpkModelGroup> Groups, string? NoModelsMessage) PlanDroppedVpks(
        IReadOnlyList<string> paths, List<string> warnings)
    {
        var plan = VpkImportPlanner.Plan(paths);
        var unassigned = plan.UnassignedMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in plan.Candidates)
        {
            var reason = candidate.Kind switch
            {
                VpkImportKind.ComponentSwitch => "当前 IAS 转换无法生成对应的组件隐藏选项（$no_draw）。",
                VpkImportKind.MaterialVariant => "普通 VPK 规划器将它识别为仅覆盖渲染参数的变体。",
                _ => candidate.SkipReason
            };
            if (unassigned.Contains(candidate.VpkPath))
                reason = "找不到使用这份材质的角色主体。";
            if (reason is not null)
                warnings.Add($"已跳过「{DescribeDropVpk(candidate.VpkPath)}」：{reason}");
        }
        if (plan.Groups.Count != 0)
            return (plan.Groups, null);
        var onlyMaterials = plan.Candidates.All(candidate => !candidate.HasModelContent
            && candidate.Kind is VpkImportKind.Material or VpkImportKind.ComponentSwitch or VpkImportKind.MaterialVariant);
        return ([], onlyMaterials
            ? "拖入的 VPK 只有材质包，没有角色模型。请将模型 VPK 一并拖入。"
            : "拖入的 VPK 中没有可转换的 IAS 角色模型，请查看详情。");
    }

    private static string DescribeDropVpk(string path) =>
        Path.Combine(Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty, Path.GetFileName(path));

    private static string DropName(string path) => Directory.Exists(path)
        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);

    internal static async Task<IasOperationResult> AddDropConversionsAsync(IReadOnlyList<DropConversion> conversions,
        int count, List<string> warnings, string? noModels,
        Func<DropConversion, CancellationToken, Task<IReadOnlyList<string>>> convert, CancellationToken ct)
    {
        string? firstFailure = null;
        foreach (var conversion in conversions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                warnings.AddRange(await convert(conversion, ct).ConfigureAwait(false));
                count++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var source = conversion.Kind == IasDropItemKind.L4d2Vpk ? DescribeDropVpk(conversion.Path) : Path.GetFileName(conversion.Path);
                var failure = $"添加「{source}」失败：{error.Message}";
                firstFailure ??= failure;
                warnings.Add(failure);
            }
        }
        return count == 0
            ? new(false, firstFailure ?? noModels ?? "没有可安装的 IAS 角色。", warnings)
            : new(true, $"已添加 {count} 个 IAS 角色。", warnings);
    }

    public Task<IasOperationResult> AddFromLibraryAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct,
        IasVpkTargets? vpkTargets = null) =>
        ExecuteAsync("从 Mod 库添加 IAS 角色", progress, ct, async (service, paths) =>
        {
            var snapshot = await service.LoadModsAsync(ct).ConfigureAwait(false);
            EnsureEnabledForAdd(service, snapshot);
            if (!Guid.TryParse(modId, out var id) || !snapshot.Nodes.TryGetValue(new(id), out var node) ||
                node.Metadata.Kind != ModNodeKind.Standard || service.ManagedModId == id ||
                service.GetRoles().Any(role => role.ModIds.Contains(id)))
                throw new InvalidOperationException("找不到可转换的 Mod。");
            var input = Path.Combine(paths.ModsDirectory, node.RelativePath);
            var facts = await IasLibraryCandidateReader.InspectAsync(input, ct).ConfigureAwait(false);
            if (IasLibraryCandidateReader.UnavailableReason(facts) is { } unavailable)
                throw new InvalidOperationException($"「{node.Metadata.Name}」{unavailable}。");

            Task VerifyNode(LibrarySnapshot current)
            {
                if (!current.Nodes.TryGetValue(new(id), out var currentNode) ||
                    currentNode.RelativePath != node.RelativePath || currentNode.Metadata.Name != node.Metadata.Name ||
                    currentNode.Metadata.CreatedUtc != node.Metadata.CreatedUtc ||
                    currentNode.Metadata.ModifiedUtc != node.Metadata.ModifiedUtc)
                    throw new InvalidOperationException("转换期间模组库中的源 Mod 已移除或改动，请重新选择。");
                return Task.CompletedTask;
            }

            // 求生 VPK 导入的 Mod：直接用原 VPK 走求生 VPK 转换（含同目录里的材质 VPK），与拖入 VPK 同一条流程。
            if (facts.Source == IasLibrarySource.L4d2Vpk)
            {
                var result = await AddDroppedAsync(service, paths, facts.SourceVpks, progress, ct, vpkTargets,
                    node.Metadata.Name, (current, _) => VerifyNode(current)).ConfigureAwait(false);
                return result with { Message = result.Success ? "角色已转换并安装到 IAS。" : result.Message };
            }

            var origin = await IasSourceStamp.CaptureAsync(input, "模组库中的普通 Mod", ct).ConfigureAwait(false);
            // 素体 Mod 的目录里还有 sectional/、Overwrite/ 两份转换产物，补丁重名；只用根上的原始补丁。
            var warnings = await ConvertAndAddAsync(service, paths, input, IasDropItemKind.PatchMod, progress, ct, node.Metadata.Name,
                verifyOrigin: async (current, token) =>
                {
                    await VerifyNode(current).ConfigureAwait(false);
                    await origin.VerifyAsync(token).ConfigureAwait(false);
                }, rootPatchesOnly: facts.Source == IasLibrarySource.CustomModel).ConfigureAwait(false);
            return new(true, "角色已转换并安装到 IAS。", warnings);
        });

    private static void EnsureEnabledForAdd(IasDeploymentService service, LibrarySnapshot snapshot)
    {
        if (service.GetInstalled() is null || !service.HasManagedMod(IasDeploymentService.Active(snapshot)))
            throw new InvalidOperationException("请先在当前配置打开 IAS。");
    }

    private static string ConversionStage(StoragePaths paths) =>
        IasFileTree.Under(Path.Combine(Path.GetFullPath(paths.AppRootDirectory), "ias"), "temp/convert-" + Guid.NewGuid().ToString("N"));

    private static async Task<IReadOnlyList<string>> ConvertAndAddAsync(IasDeploymentService service, StoragePaths paths,
        string input, IasDropItemKind kind, IProgress<IasPageProgress> progress, CancellationToken ct, string? name = null,
        string? sdk = null, IasVpkTargets? vpkTargets = null,
        Func<LibrarySnapshot, CancellationToken, Task>? verifyOrigin = null, IReadOnlyList<string>? materialVpks = null,
        bool rootPatchesOnly = false)
    {
        var stage = ConversionStage(paths);
        try
        {
            var packaging = new IasPackagingService();
            var vpk = kind == IasDropItemKind.L4d2Vpk;
            var installed = service.GetInstalled();
            var generator = service.GeneratorDirectory ?? throw new InvalidOperationException("请先安装 IAS。");
            var output = Path.Combine(stage, "output");
            var options = new IasPackagingOptions(output, service.GameData)
            {
                GeneratorDirectory = generator,
                AqSdkDirectory = sdk ?? await IasAqSdk.GetAsync(paths, stage, ct).ConfigureAwait(false),
                BodyArchive = vpk ? vpkTargets?.Body : null,
                HelmetArchive = vpk ? vpkTargets?.Helmet : null,
                RecordTiming = IasTiming.Record,
                BoneHashPath = File.Exists(paths.BoneHashesPath) ? paths.BoneHashesPath : null,
                L4d2Directory = Environment.GetEnvironmentVariable("HD2_L4D2_DIR"),
                MaterialVpkPaths = materialVpks ?? [],
                Name = name ?? (Directory.Exists(input)
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(input)) : Path.GetFileNameWithoutExtension(input))
            };
            var conversionProgress = new ConversionProgress(progress);
            IasPackagingResult converted;
            if (vpk)
                converted = await packaging.ConvertL4d2VpkAsync(input, options, conversionProgress, ct).ConfigureAwait(false);
            else
            {
                var patches = FindPatchInputs(input, ct, rootPatchesOnly);
                converted = await ConvertPatchWithPreflightAsync(service, patches.Values, options.AqSdkDirectory!,
                    () => packaging.ConvertPatchModAsync(PreparePatchInput(patches, stage, ct), options, conversionProgress, ct),
                    ct).ConfigureAwait(false);
            }
            if (!converted.IsSuccess) throw new InvalidDataException(string.Join("；", converted.Errors));
            await service.AddAsync([converted.MainModZip!, converted.CompanionZip!],
                vpk ? IasRoleSource.L4d2Vpk : IasRoleSource.PatchMod, ct, async (snapshot, token) =>
                {
                    if (service.GetInstalled() != installed)
                        throw new InvalidOperationException("转换期间已安装的 IAS 组件发生变化，请重新转换。");
                    if (verifyOrigin is not null) await verifyOrigin(snapshot, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            return converted.Warnings;
        }
        finally { IasFileTree.Delete(stage); }
    }

    internal static async Task<IasPackagingResult> ConvertPatchWithPreflightAsync(IasDeploymentService service,
        IEnumerable<string> patchFiles, string sdk, Func<Task<IasPackagingResult>> convert, CancellationToken ct)
    {
        var archives = await IasPackagingService.ReadPatchTargetArchivesAsync(patchFiles, sdk, ct).ConfigureAwait(false);
        await service.PreflightArchivesAsync(archives, ct).ConfigureAwait(false);
        return await convert().ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> FindPatchInputs(string input, CancellationToken ct, bool rootOnly)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parser = CoreServices.CreatePatchFileNameParser();
        foreach (var file in Directory.EnumerateFiles(input, "*", new EnumerationOptions
        {
            RecurseSubdirectories = !rootOnly, AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (!parser.TryParse(fileName, out _)) continue;
            if (!files.TryAdd(fileName, file))
                throw new InvalidDataException($"转换输入中有重名补丁：{fileName}。请先选择一个选项目录。");
        }
        return files;
    }

    private static string PreparePatchInput(IReadOnlyDictionary<string, string> patches, string work, CancellationToken ct)
    {
        var target = Path.Combine(work, "patch-input");
        Directory.CreateDirectory(target);
        foreach (var (name, path) in patches)
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(path, Path.Combine(target, name));
        }
        return target;
    }

    public Task<IasOperationResult> RemoveCharacterAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct) =>
        ExecuteAsync("卸载 IAS 角色", progress, ct, async (service, _) =>
        {
            await service.RemoveAsync(characterId, ct).ConfigureAwait(false);
            return new(true, "IAS 角色已卸载。", []);
        });

    public Task<IasOperationResult> ConvertToStandardModAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct,
        string? targetArmor = null, string? targetHelmet = null, bool mergeHelmet = false) =>
        ExecuteAsync("转为普通 Mod", progress, ct, async (service, _) =>
        {
            var id = await service.ToStandardAsync(characterId,
                (source, output, token) => ConvertStandardPackageAsync(source, service.GameData, output,
                    service.GetInstalled(), token, targetArmor, targetHelmet, mergeHelmet),
                ct).ConfigureAwait(false);
            return new(true, "已转换为普通 Mod 并导入库。", [], id.ToString("D"));
        });

    internal static async Task<string> ConvertStandardPackageAsync(string source, string gameData, string output,
        IasInstalled? installed, CancellationToken ct,
        string? targetArmor = null, string? targetHelmet = null, bool mergeHelmet = false) =>
        (await ConvertStandardAsync(source, gameData, output, installed, ct, targetArmor, targetHelmet, mergeHelmet).ConfigureAwait(false)).ModDirectory;

    public static async Task<IasStandardResult> ConvertStandardAsync(string source, string gameData, string output,
        IasInstalled? installed, CancellationToken ct,
        string? targetArmor = null, string? targetHelmet = null, bool mergeHelmet = false)
    {
        if (installed is null)
            throw new InvalidOperationException("找不到已安装的官方 IAS 组件，无法读取材质默认模板。请先在管理器安装 IAS。");
        var templates = await IasMaterialTemplates.ReadAsync(installed, ct).ConfigureAwait(false);
        var authority = await IasSlotAuthority.ReadAsync(gameData, ct).ConfigureAwait(false);
        var converter = new IasStandardConverter(HashBone, (equipment, units, token) =>
        {
            token.ThrowIfCancellationRequested();
            var (original, target) = authority.Resolve(equipment.Helmet, equipment.Archive, equipment.Equipment,
                units, equipment.Helmet ? targetHelmet : targetArmor);
            var match = IasSlotAuthority.MatchSlots(original, target);
            return Task.FromResult(new IasTargetKit(target.ArchiveHash, target.Name, equipment.Helmet,
                (unit, hidden) => match(unit, hidden).Select(slot =>
                    new IasTargetSlot(slot.Unit, slot.Cell, slot.Body, slot.WriteOrder) { IsTorso = slot.IsTorso }).ToArray()));
        }, templates);
        return await converter.ConvertAsync(source, gameData, output, ct, mergeHelmet).ConfigureAwait(false);
    }

    public static uint HashBone(string name) =>
        uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var hash)
            ? hash : (uint)(StingrayHash.Murmur64A(name) >> 32);

    private Task<IasOperationResult> ExecuteAsync(string title, IProgress<IasPageProgress> progress, CancellationToken ct,
        Func<IasDeploymentService, StoragePaths, Task<IasOperationResult>> operation, bool keepErrorMessage = false) => Task.Run(async () =>
    {
        progress.Report(new(title, null, null, true));
        using var timing = IasTiming.Begin(title);
        IasOperationResult result;
        try
        {
            EnsureGameStopped();
            var paths = getPaths();
            var service = Open(paths);
            await IasTiming.MeasureAsync("恢复检查", () => RecoverAndRefreshAsync(service, ct)).ConfigureAwait(false);
            result = await operation(service, paths).ConfigureAwait(false);
        }
        catch (IasGameRunningException error) { return new(false, error.Message, []); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(false, keepErrorMessage ? error.Message : $"{title}失败：{error.Message}", []);
        }
        if (result.Success && refreshLibrary is not null)
        {
            try { await IasTiming.MeasureAsync("刷新管理器模组库", refreshLibrary).ConfigureAwait(false); }
            catch (Exception error) { return result with { Warnings = [.. result.Warnings, $"刷新 Mod 库失败：{error.Message}"] }; }
        }
        return result;
    }, ct);

    private sealed class ProgressAdapter(IProgress<IasPageProgress> progress) : IProgress<IasProgress>
    {
        public void Report(IasProgress value) =>
            progress.Report(new(value.Message, value.TotalBytes > 0 ? (double)value.CompletedBytes / value.TotalBytes * 100 : null, null, true));
    }

    private sealed class ConversionProgress(IProgress<IasPageProgress> progress) : IProgress<string>
    {
        public void Report(string value) => progress.Report(new(value, null, null, true));
    }
}

