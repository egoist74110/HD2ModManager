using System.IO.Compression;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

public enum IasRoleSource { IasPackage, PatchMod, L4d2Vpk }

public sealed record IasRole(string Id, string Name, IasRoleSource Source, DateTimeOffset AddedAt, Guid[] ModIds,
	Guid[]? OwnedModIds = null, string? PackageIdentity = null, string? LibraryRevision = null)
{
	public IasRoleArmor? Armor { get; init; }
}

public enum IasAddDisposition { Installed, InstalledDisabled, Identical, Updated }

public sealed record IasAddResult(IasRole Role, IasAddDisposition Disposition, IasRoleConflict? Conflict = null)
{
	public IasRetargetChange? Retarget { get; init; }
	public string? RetargetHint { get; init; }
}

public sealed record IasRoleConflict(string[] Names, string[] Equipment)
{
	public string Occupants => string.Join("、", Names.Select(name => $"『{name}』"));
	public string EquipmentText => string.Join("、", Equipment);
	public string Hint => $"和{Occupants}占同一件护甲（{EquipmentText}）";
	public string SwapMessage(string role) => $"已启用『{role}』，并停用了{Occupants}（它们占同一件护甲：{EquipmentText}）。";
}

public sealed partial class IasDeploymentService
{
	private readonly StoragePaths _paths;
	private readonly string _root;
	private readonly string _game;
	private readonly string _data;
	private readonly IIasWorker _worker;
	private readonly IasComponentStore _components;
	private readonly IasFeedClient _feed;
	private readonly JsonModLibraryStore _store;
	private readonly OptionActivationStore? _optionActivations;
	private readonly IProfileMutationService _profiles = new ProfileMutationService();
	private readonly Func<Profile, LibrarySnapshot, CancellationToken, Task>? _deploy;
	private readonly Func<bool> _isGameRunning;
	private readonly IIasBodyRetargeter? _retargeter;
	private const string SystemLabel = "IAS 系统";
	private const string RoleLabel = IasLibraryScan.RoleLabel;

	public IasDeploymentService(StoragePaths paths, string gameRoot, HttpClient http, IIasWorker worker, Func<bool> isGameRunning,
		Func<Profile, LibrarySnapshot, CancellationToken, Task>? deploy = null, OptionActivationStore? optionActivations = null,
		IIasBodyRetargeter? retargeter = null)
	{
		_paths = paths;
		_game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
		_data = Path.Combine(_game, "data");
		_root = Path.Combine(Path.GetFullPath(paths.AppRootDirectory), "ias");
		_components = new(paths, http, _game);
		_feed = new(http);
		_worker = worker;
		_isGameRunning = isGameRunning;
		_deploy = deploy;
		_optionActivations = optionActivations;
		_retargeter = retargeter;
		_store = new(paths, allowLegacyMigration: false);
		IasTiming.Configure(Path.GetFullPath(paths.AppRootDirectory));
	}

	public string GameData => _data;
	public string? GeneratorDirectory => _components.GetInstalled() is { } installed
		? Expanded(installed, "Generator") : null;
	public IasInstalled? GetInstalled() => _components.GetInstalled();
	public Task<(IasModuleState State, IasFeedResult? Feed)> GetComponentStatusAsync(CancellationToken token) =>
		new IasStatusService(_components, _feed).InspectAsync(_game, true, token);
	public async Task<LibrarySnapshot> LoadModsAsync(CancellationToken token) =>
		await _store.TryLoadReadOnlyAsync(token).ConfigureAwait(false) ?? new(1, DateTimeOffset.UtcNow,
			new Dictionary<ModNodeId, ModNode>(), []);
	public IReadOnlyList<IasRole> GetRoles() => Read<IasRole[]>(At("library/roles.json")) ?? [];
	public string Sources(string id)
	{
		if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\'))
			throw new InvalidDataException("IAS 角色标识无效。");
		return IasFileTree.Under(At("library/sources"), id);
	}
	public Guid? ManagedModId => Read<Guid?>(At("library/managed-mod.json"));
	public bool HasManagedMod(Profile? profile) => ManagedModId is { } id &&
		profile?.Entries.Any(entry => entry.NodeId.Value == id) == true;
	public static Profile? Active(LibrarySnapshot snapshot) => snapshot.Profiles.SingleOrDefault(p => p.Id == snapshot.ActiveProfileId);

	public bool GraphicsRiskAcknowledged => Read<bool>(At("graphics-risk.json"));

	public async Task AcknowledgeGraphicsRiskAsync(CancellationToken token)
	{
		EnsureGameStopped();
		Directory.CreateDirectory(_root);
		using var lease = AcquireIasLease();
		await RecoverUnderIasLeaseAsync(token).ConfigureAwait(false);
		EnsureGameStopped();
		var path = At("graphics-risk.json");
		var pending = At("graphics-risk.json.tmp");
		try { Write(path, true); }
		finally { File.Delete(pending); }
	}

	public bool HasGraphicsPackages => GetInstalled() is { } installed && IasPaths.GraphicsComponents.All(name =>
		File.Exists(IasFileTree.Under(installed.ComponentsDirectory, IasPaths.RuntimeArchive(name))) &&
		File.Exists(IasFileTree.Under(Expanded(installed, name), "external-runtime.json")));

	public async Task<IasGraphicsState?> GetGraphicsStateAsync(CancellationToken token)
	{
		if (!Directory.Exists(_root)) return null;
		using var lease = AcquireIasLease();
		if (GetInstalled() is not { } installed) return null;
		var work = At("temp/graphics-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try { return await ReadGraphicsAsync(installed, work, token).ConfigureAwait(false); }
		finally { IasFileTree.Delete(work); }
	}

	public Task InstallAsync(string? offlinePackage, IProgress<IasProgress>? progress, CancellationToken token) =>
		ChangeAsync(async (tx, snapshot) =>
		{
			var installed = _components.GetInstalled();
			var enableInProfile = installed is null || ManagedModId is null || HasManagedMod(Active(snapshot));
			string package;
			IasRelease release;
			if (offlinePackage is null)
			{
				var feed = await _feed.GetLatestAsync(progress, token).ConfigureAwait(false);
				release = feed.Latest ?? throw new InvalidDataException(feed.Message);
				if (installed is not null && Version.Parse(release.Version) < Version.Parse(installed.Version))
					throw new InvalidDataException("IAS 清单版本早于已激活版本。");
				package = installed?.Version == release.Version && !NeedsGraphicsArchives(installed, release) ? string.Empty :
					await _components.DownloadAsync(release, progress, token).ConfigureAwait(false);
			}
			else
			{
				(release, package) = await ReadOfflineAsync(offlinePackage, tx.Work, token).ConfigureAwait(false);
			}
			RequireActive(snapshot);
			if (_worker is IasProcessWorker processWorker)
				await processWorker.EnsureRuntimeAsync(progress, token).ConfigureAwait(false);
			if (installed?.Version != release.Version)
			{
				await _components.ExtractCoreAsync(release, package, progress, token).ConfigureAwait(false);
				installed = (await _components.ActivateAsync(release.Version, progress, token).ConfigureAwait(false)).Installed;
			}
			else
			{
				if (NeedsGraphicsArchives(installed!, release))
					await _components.RepairGraphicsAsync(release, package, Path.Combine(tx.Work, "verified-graphics"),
						progress, token).ConfigureAwait(false);
				installed = (await _components.ActivateAsync(release.Version, progress, token).ConfigureAwait(false)).Installed;
			}
			Expand(installed, tx, token);
			await RunAsync(installed, tx, "prepare-base", null, true, token).ConfigureAwait(false);
			return (await RegenerateAsync(installed, tx, snapshot, token, addToProfile: enableInProfile).ConfigureAwait(false), enableInProfile);
		}, token, captureComponents: true);

	public Task<bool> SetReShadeAsync(bool enabled, CancellationToken token) =>
		ChangeGraphicsAsync(enabled, null, token);

	public Task SetDlssAsync(IasDlssMode mode, CancellationToken token) =>
		ChangeGraphicsAsync(null, mode, token);

	private Task<bool> ChangeGraphicsAsync(bool? reshade, IasDlssMode? dlss, CancellationToken token) =>
		ChangeAsync(async (tx, snapshot) =>
		{
			var installed = await RequireCompatibleAsync(token).ConfigureAwait(false);
			if (!HasManagedMod(RequireActive(snapshot))) throw new InvalidOperationException("请先打开 IAS。");
			var before = await ReadGraphicsAsync(installed, tx.Backup, token).ConfigureAwait(false);
			var expected = before with
			{
				ReShadeEnabled = reshade ?? before.ReShadeEnabled,
				Dlss = reshade == false ? IasDlssMode.Off : dlss ?? before.Dlss
			};
			if (expected.Dlss != IasDlssMode.Off && !expected.ReShadeEnabled)
				throw new InvalidOperationException("请先打开 ReShade 滤镜。");
			if (before == expected) return (snapshot, false, false);

			var (command, argument, package) = reshade switch
			{
				true => ("reshade-install", (string?)null, "ReShade"),
				false => ("reshade-remove", null, null),
				null => dlss switch
				{
					IasDlssMode.Off => ("dlss-remove", null, null),
					IasDlssMode.FrameGeneration => ("dlss-install", "fg", "DLSSFrameGeneration"),
					IasDlssMode.NeuralRendering => ("dlss-install", "nr", "DLSSNeuralRendering"),
					_ => throw new ArgumentOutOfRangeException(nameof(dlss))
				}
			};
			if (package is not null && !File.Exists(IasFileTree.Under(Expanded(installed, package), "external-runtime.json")))
				throw new InvalidOperationException("IAS 画面组件缺失，请先更新或修复 IAS。");
			if (reshade == true) tx.CaptureShaderToggler();
			await RunAsync(installed, tx, command, argument, false, token).ConfigureAwait(false);
			var next = await RegenerateAsync(installed, tx, snapshot, token, addToProfile: false).ConfigureAwait(false);
			if (await ReadGraphicsAsync(installed, tx.Backup, token).ConfigureAwait(false) != expected)
				throw new InvalidOperationException("IAS 画面组件的状态未按预期更新。");
			return (next, true, reshade == false && before.Dlss != IasDlssMode.Off);
		}, token);

	public Task SetEnabledInActiveProfileAsync(bool enabled, CancellationToken token) => ChangeAsync(async (tx, snapshot) =>
	{
		var profile = RequireActive(snapshot);
		var installed = enabled ? await RequireCompatibleAsync(token).ConfigureAwait(false) :
			GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
		var id = ManagedModId ?? throw new InvalidOperationException("请先安装 IAS。");
		var roles = GetRoles();
		Dictionary<string, bool>? activeRoles = null;
		if (enabled)
			activeRoles = roles.Count == 0 ? new(StringComparer.Ordinal) :
				await ReadRoleStatesAsync(installed, tx.Backup, roles, token).ConfigureAwait(false);
		return (SyncProfile(snapshot, profile, id, roles, activeRoles), true);
	}, token, captureRuntime: false);

	public Task<string> SetCharacterEnabledAsync(string id, bool enabled, CancellationToken token) => ChangeAsync(async (tx, snapshot) =>
	{
		var installed = await RequireCompatibleAsync(token).ConfigureAwait(false);
		var roles = GetRoles();
		var role = roles.SingleOrDefault(role => role.Id == id)
			?? throw new InvalidOperationException("找不到要切换的 IAS 角色。");
		var profile = Active(snapshot);
		var inProfile = HasManagedMod(profile);
		IasArchiveOwner[] displaced = [];
		if (enabled)
		{
			if (IsMissing(snapshot, role)) throw new InvalidOperationException($"『{role.Name}』的 Mod 库文件已删除。");
			displaced = await SwapConflictsAsync(installed, tx, role.Id, token).ConfigureAwait(false);
		}
		await RunAsync(installed, tx, "set-enabled", role.Id, false, token, enabled).ConfigureAwait(false);
		var states = await ReadRoleStatesAsync(installed, tx.Backup, roles, token).ConfigureAwait(false);
		if (states[role.Id] != enabled || displaced.Any(owner => !states.TryGetValue(owner.Id, out var active) || active))
			throw new InvalidOperationException("IAS 角色的启用状态未更新。");
		var next = await RegenerateAsync(installed, tx, snapshot, token, addToProfile: false).ConfigureAwait(false);
		if (inProfile)
		{
			var stopped = displaced.Select(owner => owner.Id).ToHashSet(StringComparer.Ordinal);
			if (!enabled) stopped.Add(role.Id);
			var removed = roles.Where(item => stopped.Contains(item.Id)).SelectMany(item => item.ModIds).ToHashSet();
			var retained = profile!.Entries.Select(entry => entry.NodeId).Where(key => !removed.Contains(key.Value));
			next = SetEntries(next, profile, enabled ? retained.Concat(role.ModIds.Select(key => new ModNodeId(key))) : retained);
		}
		var conflict = await DescribeConflictsAsync(displaced, token).ConfigureAwait(false);
		var message = conflict?.SwapMessage(role.Name) ?? (enabled ? $"已启用『{role.Name}』。" : $"已停用『{role.Name}』。");
		return (next, inProfile, message);
	}, token);

	public async Task<IReadOnlyList<(IasRole Role, bool? Enabled)>> GetRolesWithStateAsync(CancellationToken token)
	{
		if (!Directory.Exists(_root)) return [];
		using var lease = AcquireIasLease();
		var roles = GetRoles();
		if (roles.Count == 0 || GetInstalled() is not { } installed || !Directory.Exists(_game))
			return roles.Select(role => (role, (bool?)null)).ToArray();
		var work = At("temp/status-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try
		{
			var states = await ReadRoleStatesAsync(installed, work, roles, token).ConfigureAwait(false);
			return roles.Select(role => (role, (bool?)states[role.Id])).ToArray();
		}
		finally { IasFileTree.Delete(work); }
	}

	public async Task<IasRoleScan> ScanRolesAsync(CancellationToken token)
	{
		using var lease = Directory.Exists(_root) ? AcquireIasLease() : null;
		var snapshot = await LoadModsAsync(token).ConfigureAwait(false);
		var roles = GetRoles();
		var known = roles.SelectMany(role => role.OwnedModIds ?? role.ModIds).ToHashSet();
		if (ManagedModId is { } managed) known.Add(managed);
		var candidates = snapshot.Nodes.Values.Where(node => !known.Contains(node.Id.Value))
			.Select(node => ReadLibraryRole(node, token)).OfType<IasLibraryRole>().ToArray();
		var owners = Array.Empty<IasArchiveOwner>();
		Dictionary<string, bool>? states = null;
		if ((roles.Count > 0 || candidates.Length > 0) && GetInstalled() is { } installed &&
			Directory.Exists(_game) && Directory.Exists(At("library")))
		{
			// 占用查询只读官方登记与配套索引，不解析新包。
			owners = await ReadArchiveOwnersAsync(installed, At("library"), "*", token).ConfigureAwait(false);
			states = owners.ToDictionary(owner => owner.Id, owner => owner.Enabled, StringComparer.Ordinal);
		}
		var registered = roles.Select(role =>
		{
			var revisions = new List<string>();
			var missing = false;
			foreach (var id in role.OwnedModIds ?? role.ModIds)
			{
				if (ModDirectory(snapshot, id) is not { } directory || !Directory.Exists(directory))
				{
					missing = true;
					continue;
				}
				var files = IasLibraryScan.ReadFiles(directory, token);
				missing |= ArePatchFilesMissing(snapshot.Nodes[new(id)], files);
				revisions.Add(IasLibraryScan.Revision(directory, files, token));
			}
			var archives = owners.FirstOrDefault(owner => owner.Id == role.Id)?.Archives ??
				IasLibraryScan.ReadCompanionDirectory(Path.Combine(Sources(role.Id), "companion"))?.Archives ?? [];
			bool? enabled = states is not null && states.TryGetValue(role.Id, out var value) ? value : null;
			return new IasScannedRole(role, enabled, missing, archives, string.Join("/", revisions));
		}).ToArray();
		return new(registered, candidates, owners.Where(owner => owner.Enabled).ToArray());
	}

	public async Task<IasAddResult> AdoptLibraryRoleAsync(Guid modId, CancellationToken token)
	{
		EnsureGameStopped();
		var snapshot = await LoadModsAsync(token).ConfigureAwait(false);
		var candidate = ReadCandidate(snapshot);
		if (candidate.CompanionPath is not { } companion)
			throw new InvalidOperationException($"『{candidate.Name}』在库里没有配套包，请重新拖入主包和配套包。");
		var work = At("temp/adopt-" + Guid.NewGuid().ToString("N"));
		try
		{
			var stamp = await IasSourceStamp.CaptureAsync(candidate.Directory, $"库里的『{candidate.Name}』", token).ConfigureAwait(false);
			var companionStamp = await IasSourceStamp.CaptureAsync(companion, $"『{candidate.Name}』的 IAS 配套", token).ConfigureAwait(false);
			using var workspace = new IasDropWorkspace();
			var input = await workspace.InspectAsync(companion, token).ConfigureAwait(false);
			if (input.Companions.Count != 1)
				throw new InvalidDataException($"『{candidate.Name}』的 IAS 配套包无法读取。");
			var main = Path.Combine(work, "library-main");
			var prefix = Path.TrimEndingDirectorySeparator(companion) + Path.DirectorySeparatorChar;
			foreach (var file in IasLibraryScan.ReadFiles(candidate.Directory, token))
			{
				if (file.Equals(companion, StringComparison.OrdinalIgnoreCase) ||
					file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
				IasFileTree.Copy(file, IasFileTree.Under(main, Path.GetRelativePath(candidate.Directory, file)));
			}
			var package = IasCharacterPackage.ReadPrepared([input with { OriginalPath = candidate.Directory, Main = [main] }], work, token);
			return await CommitAddAsync(package, IasRoleSource.IasPackage, token, async (current, ct) =>
			{
				var latest = ReadCandidate(current);
				if (latest.Directory != candidate.Directory || latest.CompanionPath != companion)
					throw new InvalidOperationException($"『{candidate.Name}』在登记期间已移动，请刷新后重试。");
				await stamp.VerifyAsync(ct).ConfigureAwait(false);
				await companionStamp.VerifyAsync(ct).ConfigureAwait(false);
			}, candidate).ConfigureAwait(false);
		}
		finally { IasFileTree.Delete(work); }

		IasLibraryRole ReadCandidate(LibrarySnapshot current) =>
			current.Nodes.TryGetValue(new(modId), out var node) && ReadLibraryRole(node, token) is { } item
				? item : throw new InvalidOperationException("这个 IAS Mod 已从库里移除或更改，请刷新列表。");
	}

	private IasLibraryRole? ReadLibraryRole(ModNode node, CancellationToken token)
	{
		var candidate = IasLibraryScan.ReadCandidate(_paths, node, token);
		if (candidate is not { Legacy: true, CompanionPath: null }) return candidate;
		var sources = At("library/sources");
		if (!Directory.Exists(sources)) return candidate;
		var matches = new List<(string Path, string[] Archives)>();
		foreach (var directory in Directory.EnumerateDirectories(sources))
		{
			token.ThrowIfCancellationRequested();
			var safe = IasFileTree.Under(sources, Path.GetFileName(directory));
			var manifest = IasFileTree.Under(safe, "main/manifest.json");
			if (!IasLibraryScan.HasMainIdentity(manifest, node.Id.Value)) continue;
			var companion = IasFileTree.Under(safe, "companion");
			if (IasLibraryScan.ReadCompanionDirectory(companion) is { } group)
				matches.Add((companion, group.Archives));
		}
		return matches.Count == 1 ? candidate with { CompanionPath = matches[0].Path, Archives = matches[0].Archives } : candidate;
	}

	private string? ModDirectory(LibrarySnapshot snapshot, Guid id) =>
		snapshot.Nodes.TryGetValue(new(id), out var node) ? IasFileTree.Under(_paths.ModsDirectory, node.RelativePath) : null;

	private bool IsMissing(LibrarySnapshot snapshot, IasRole role) =>
		(role.OwnedModIds ?? role.ModIds).Any(id => !Directory.Exists(ModDirectory(snapshot, id)));

	private static bool ArePatchFilesMissing(ModNode node, string[] files)
	{
		if (node.PatchGroups.Count == 0) return false;
		var names = files.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return node.PatchGroups.Any(group =>
			!names.Contains($"{group.ArchiveHex16}.patch_{group.PatchIndex}"));
	}

	public async Task<IasRole> AddAsync(IReadOnlyList<string> paths, IasRoleSource source, CancellationToken token,
		Func<LibrarySnapshot, CancellationToken, Task>? verifyOrigin = null) =>
		(await AddAsync(paths, source, work => IasCharacterPackage.Read(paths, work, token), token, verifyOrigin)
			.ConfigureAwait(false)).Role;

	public Task<IasAddResult> AddPreparedAsync(IReadOnlyList<IasInputContents> inputs, CancellationToken token,
		Func<LibrarySnapshot, CancellationToken, Task>? verifyOrigin = null) =>
		AddAsync(inputs.Select(item => item.OriginalPath).ToArray(), IasRoleSource.IasPackage,
			work => Task.FromResult(IasCharacterPackage.ReadPrepared(inputs, work, token)), token, verifyOrigin);

	private async Task<IasAddResult> AddAsync(IReadOnlyList<string> paths, IasRoleSource source,
		Func<string, Task<IasCharacterPackage>> readPackage, CancellationToken token,
		Func<LibrarySnapshot, CancellationToken, Task>? verifyOrigin)
	{
		EnsureGameStopped();
		var work = At("temp/prepare-" + Guid.NewGuid().ToString("N"));
		try
		{
			var stamps = new List<IasSourceStamp>();
			if (verifyOrigin is null)
				foreach (var path in paths)
					stamps.Add(await IasSourceStamp.CaptureAsync(path, $"IAS 导入源「{Path.GetFileName(path)}」", token).ConfigureAwait(false));
			var package = await IasTiming.MeasureAsync("锁外准备 IAS 主包与配套包", () => readPackage(work)).ConfigureAwait(false);
			if (source != IasRoleSource.IasPackage)
			{
				using var lease = AcquireIasLease();
				var installed = GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
				var archives = await ReadArchiveDataAsync<string[]>(installed, work, "companion-archives",
					package.CompanionZip, token).ConfigureAwait(false);
				var owners = await ReadArchiveOwnersAsync(installed, work, null, token).ConfigureAwait(false);
				await RequireArchivesFreeAsync(archives, owners, null, token).ConfigureAwait(false);
			}
			var sources = Path.GetDirectoryName(package.Main)!;
			for (var i = 0; i < paths.Count; i++)
				if (File.Exists(paths[i]))
					IasFileTree.Copy(paths[i], IasFileTree.Under(sources, $"original/{i + 1}/{Path.GetFileName(paths[i])}"));
			var retarget = source == IasRoleSource.IasPackage && _retargeter is not null
				? await PrepareDroppedRetargetAsync(package, work, token).ConfigureAwait(false) : null;
			return await CommitAddAsync(package, source, token, async (snapshot, ct) =>
			{
				foreach (var stamp in stamps) await stamp.VerifyAsync(ct).ConfigureAwait(false);
				if (verifyOrigin is not null) await verifyOrigin(snapshot, ct).ConfigureAwait(false);
			}, retarget: retarget).ConfigureAwait(false);
		}
		finally { IasFileTree.Delete(work); }
	}

	private Task<IasAddResult> CommitAddAsync(IasCharacterPackage package, IasRoleSource source, CancellationToken token,
		Func<LibrarySnapshot, CancellationToken, Task> verifyOrigin, IasLibraryRole? adoption = null,
		RetargetPreparation? retarget = null)
	{
		IasRole? previous = null;
		var identity = source == IasRoleSource.IasPackage ? package.Identity : null;
		return ChangeAsync<IasAddResult>(async (tx, snapshot) =>
		{
			var profile = RequireActive(snapshot);
			var installed = await RequireCompatibleAsync(token).ConfigureAwait(false);
			if (!HasManagedMod(profile)) throw new InvalidOperationException("请先打开 IAS。");
			var roles = GetRoles();
			var enabled = previous is null ||
				(await ReadRoleStatesAsync(installed, tx.Backup, [previous], token).ConfigureAwait(false))[previous.Id];
			var selected = retarget;
			var retargetHint = retarget?.Hint;
			if (retarget is { Derived: not null } || retarget?.Manual == true)
			{
				if (retarget.Installed != installed) throw new InvalidOperationException("IAS 组件已变更，请重新选择护甲。");
				var owners = await ReadArchiveOwnersAsync(installed, tx.Backup, null, token).ConfigureAwait(false);
				var latest = ConflictsFor(previous?.Id ?? "", retarget.Archives, owners);
				if (retarget.Manual)
				{
					var body = retarget.Armor!.CurrentArchive;
					if (latest.Any(owner => !owner.Archives.Contains(body, StringComparer.OrdinalIgnoreCase)))
						throw new InvalidOperationException("原头盔仍被占用，请先处理头盔冲突。");
					if (!latest.Select(owner => owner.Id).ToHashSet(StringComparer.Ordinal).SetEquals(retarget.ExpectedOwners))
						throw new InvalidOperationException("护甲占用已变更，请重新选择。");
				}
				else if (latest.Length != 0 && (previous is null || enabled))
				{
					retargetHint = previous is null ? "可换的护甲已被占用" : "当前目标护甲已被占用，已退回原护甲";
					selected = retarget with { Derived = null, Armor = retarget.Armor is { } armor
						? armor with { CurrentArchive = armor.OriginalArchive, CurrentName = armor.OriginalName } : null, Change = null };
				}
			}
			var activePackage = selected?.Derived ?? package;
			if (previous is not null)
				await RunAsync(installed, tx, "remove", previous.Id, false, token).ConfigureAwait(false);
			var id = await RunAsync(installed, tx, "import", activePackage.CompanionZip, false, token,
				enabled: source != IasRoleSource.IasPackage).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(id))
				throw new InvalidDataException("IAS worker 未返回角色标识。");
			if (roles.Any(role => role.Id == id && role.Id != previous?.Id)) throw new InvalidOperationException("这个 IAS 角色已安装。");
			IasRoleConflict? conflict = null;
			if (source != IasRoleSource.IasPackage)
				await RequireRegisteredArchivesFreeAsync(installed, tx, id, token).ConfigureAwait(false);
			else if (adoption is not null)
				conflict = await DescribeConflictsAsync(await SwapConflictsAsync(installed, tx, id, token)
					.ConfigureAwait(false), token).ConfigureAwait(false);
			else
			{
				var conflicts = selected?.Manual == true
					? await SwapConflictsAsync(installed, tx, id, token, selected.ExpectedOwners).ConfigureAwait(false)
					: await ReadConflictsAsync(installed, tx, id, token).ConfigureAwait(false);
				conflict = await DescribeConflictsAsync(conflicts, token).ConfigureAwait(false);
				if (previous is null && conflict is not null) enabled = false;
				if (previous is not null && retargetHint is not null && conflict is not null) enabled = false;
			}
			if (enabled && source == IasRoleSource.IasPackage)
				await RunAsync(installed, tx, "set-enabled", id, false, token, true).ConfigureAwait(false);
			if (previous is not null) IasFileTree.Delete(Sources(previous.Id));
			var roleSources = Sources(id);
			IasFileTree.Copy(activePackage.Source, roleSources);
			if (source == IasRoleSource.IasPackage)
				File.Copy(activePackage.CompanionZip, Path.Combine(roleSources, OfficialCompanion), overwrite: true);
			if (selected?.Derived is not null)
			{
				var original = Path.Combine(roleSources, RetargetOriginal);
				IasFileTree.Copy(package.Source, original);
				File.Copy(package.CompanionZip, Path.Combine(original, OfficialCompanion), overwrite: true);
			}
			var name = previous?.Name ?? adoption?.Name ?? (selected?.Derived is not null ? ReadMainName(package) : package.Name);
			IasRole role;
			var next = snapshot;
			if (adoption is not null)
			{
				var root = snapshot.Nodes[new(adoption.ModId)];
				var owned = IasLibraryScan.PackagedOptions(snapshot, root).Prepend(root.Id).Select(key => key.Value).ToArray();
				var revisions = owned.Select(key =>
				{
					var directory = ModDirectory(snapshot, key)!;
					return IasLibraryScan.Revision(directory, IasLibraryScan.ReadFiles(directory, token), token);
				});
				role = new(id, name, source, adoption.AddedAt, [adoption.ModId], owned, identity, string.Join("/", revisions));
			}
			else
			{
				var registeredIds = roles.SelectMany(item => item.OwnedModIds ?? item.ModIds).ToHashSet();
				var imported = await IasTiming.MeasureAsync("导入模组库", () => tx.ImportAsync(snapshot, activePackage.Main,
					name, RoleLabel, null, token, previous, activePackage.CompanionZip, registeredIds)).ConfigureAwait(false);
				if (previous is null && imported.Roots.Length == 1)
					name = imported.Snapshot.Nodes[imported.Roots[0]].Metadata.Name;
				role = new(id, name, previous?.Source ?? source, previous?.AddedAt ?? DateTimeOffset.UtcNow,
					imported.Roots.Select(i => i.Value).ToArray(), imported.All.Select(i => i.Value).ToArray(), identity,
					tx.Revision(imported.Snapshot, imported.All, token)) { Armor = selected?.Armor };
				next = imported.Snapshot;
			}
			Write(At("library/roles.json"), previous is null ? roles.Append(role).ToArray() :
				roles.Select(item => item.Id == previous.Id ? role : item).ToArray());
			var disposition = previous is not null ? IasAddDisposition.Updated :
				enabled ? IasAddDisposition.Installed : IasAddDisposition.InstalledDisabled;
			next = await RegenerateAsync(installed, tx, next, token).ConfigureAwait(false);
			return (next, true, new(role, disposition, conflict)
			{
				Retarget = selected?.Change,
				RetargetHint = retargetHint
			});
		}, token, validate: verifyOrigin, readOnly: async ct =>
		{
			if (source != IasRoleSource.IasPackage) return (false, default!);
			var roles = GetRoles();
			previous = retarget?.Manual == true ? roles.SingleOrDefault(role => role.Id == retarget.PreviousId)
				: FindPackageRole(roles, identity);
			if (adoption is not null)
			{
				if (previous is not null || roles.Any(role => (role.OwnedModIds ?? role.ModIds).Contains(adoption.ModId)))
					throw new InvalidOperationException("这个 IAS 角色已登记，请刷新列表。");
				return (false, default!);
			}
			if (retarget is not null && (retarget.PreviousId != previous?.Id || retarget.PreviousRevision != previous?.LibraryRevision))
				throw new InvalidOperationException("角色在准备换护甲期间已更新，请刷新后重试。");
			if (retarget?.Manual != true && previous is not null && await package.HasSameContentAsync(OriginalSources(previous), ct).ConfigureAwait(false))
				return (true, new(previous, IasAddDisposition.Identical));
			return (false, default!);
		});
	}

	public Task RemoveAsync(string id, CancellationToken token) => ChangeAsync(async (tx, snapshot) =>
	{
		var installed = await RequireCompatibleAsync(token).ConfigureAwait(false);
		var roles = GetRoles();
		var role = roles.SingleOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("找不到要卸载的 IAS 角色。");
		await RunAsync(installed, tx, "remove", id, false, token).ConfigureAwait(false);
		IasFileTree.Delete(Sources(id));
		Write(At("library/roles.json"), roles.Where(r => r.Id != id).ToArray());
		var next = tx.RemoveMods(snapshot, role.OwnedModIds ?? role.ModIds);
		return (await RegenerateAsync(installed, tx, next, token, addToProfile: false).ConfigureAwait(false), true);
	}, token);

	public Task UninstallAsync(bool removeRoles, CancellationToken token) => ChangeAsync((tx, snapshot) =>
	{
		var ids = ManagedModId is { } dataId ? new[] { dataId } : [];
		if (removeRoles) ids = ids.Concat(GetRoles().SelectMany(role => role.OwnedModIds ?? role.ModIds)).ToArray();
		var next = tx.RemoveMods(snapshot, ids);
		var pulled = false;
		if (!removeRoles)
		{
			// 保留角色文件，但没有 IAS 时角色主 Mod 不能单独部署：从所有配置撤下，重装 IAS 后按启用状态加回。
			var roleMods = GetRoles().SelectMany(role => role.ModIds).Select(id => new ModNodeId(id)).ToHashSet();
			foreach (var profile in next.Profiles.ToArray())
			{
				var current = next.Profiles.Single(p => p.Id == profile.Id);
				if (!current.Entries.Any(entry => roleMods.Contains(entry.NodeId))) continue;
				next = SetEntries(next, current, current.Entries.Select(entry => entry.NodeId).Where(id => !roleMods.Contains(id)));
				pulled = true;
			}
		}
		return Task.FromResult((next, (ids.Length > 0 || pulled) && Active(snapshot) is not null, true));
	}, token, async () =>
	{
		await _components.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
		IasFileTree.Delete(At("Runtime"));
		IasFileTree.Delete(At("worker"));
		IasFileTree.Delete(At("library/managed-mod.json"));
		if (removeRoles) IasFileTree.Delete(At("library"));
	}, captureComponents: true);

	public async Task<Guid> ToStandardAsync(string id, Func<string, string, CancellationToken, Task<string>> convert, CancellationToken token)
	{
		EnsureGameStopped();
		const string missing = "找不到要转换的 IAS 角色，角色可能已卸载。";
		var role = GetRoles().SingleOrDefault(r => r.Id == id) ?? throw new InvalidOperationException(missing);
		var installed = GetInstalled();
		var source = Sources(id);
		var work = At("temp/prepare-" + Guid.NewGuid().ToString("N"));
		try
		{
			var stamp = await IasSourceStamp.CaptureAsync(source, "IAS 角色源文件", token).ConfigureAwait(false);
			var folder = await IasTiming.MeasureAsync("锁外转换普通 Mod",
				() => convert(source, Path.Combine(work, "standard"), token)).ConfigureAwait(false);
			return await ChangeAsync<Guid>(async (tx, snapshot) =>
			{
				var imported = await IasTiming.MeasureAsync("导入模组库", () => tx.ImportAsync(snapshot, folder, null, null, null, token)).ConfigureAwait(false);
				return (imported.Snapshot, false, imported.Roots.First().Value);
			}, token, captureRuntime: false, validate: async (_, ct) =>
			{
				var current = GetRoles().SingleOrDefault(r => r.Id == id);
				if (current is null) throw new InvalidOperationException(missing);
				if (current.AddedAt != role.AddedAt || !current.ModIds.SequenceEqual(role.ModIds) || GetInstalled() != installed)
					throw new InvalidOperationException("转换期间 IAS 角色或已安装组件发生变化，请重新转换。");
				await stamp.VerifyAsync(ct).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}
		finally { IasFileTree.Delete(work); }
	}

	private async Task<LibrarySnapshot> RegenerateAsync(IasInstalled installed, IasModTransaction tx,
		LibrarySnapshot snapshot, CancellationToken token, bool addToProfile = true)
	{
		string? export;
		try { export = await RunAsync(installed, tx, "regenerate", null, true, token).ConfigureAwait(false); }
		catch (InvalidOperationException error) when (OfficialConflictArchive(error.Message) is not null)
		{
			var archive = OfficialConflictArchive(error.Message)!;
			IasArchiveOwner[] owners;
			try { owners = await ReadArchiveOwnersAsync(installed, tx.Backup, null, token).ConfigureAwait(false); }
			catch (Exception lookupError) when (lookupError is not OperationCanceledException)
			{
				throw new InvalidOperationException($"启用的角色占用同一装备（{archive}），请先停用其中一个。\n{error.Message}", error);
			}
			var message = await ArchiveConflictMessageAsync(archive, OwnersOf(owners, archive), token).ConfigureAwait(false);
			throw new InvalidOperationException(message, error);
		}
		if (string.IsNullOrWhiteSpace(export)) throw new InvalidDataException("IAS worker 未生成 data 包。");
		export = IasFileTree.Under(tx.Export, Path.GetRelativePath(tx.Export, export));
		var id = ManagedModId;
		var imported = await IasTiming.MeasureAsync("导入模组库", () => tx.ImportAsync(snapshot, export, SystemLabel, SystemLabel, id, token)).ConfigureAwait(false);
		var root = imported.Roots.Single();
		Write(At("library/managed-mod.json"), root.Value);
		if (!addToProfile) return imported.Snapshot;
		var profile = RequireActive(imported.Snapshot);
		var roles = GetRoles();
		var states = roles.Count > 0 ? await ReadRoleStatesAsync(installed, tx.Backup, roles, token).ConfigureAwait(false)
			: new Dictionary<string, bool>(StringComparer.Ordinal);
		return SyncProfile(imported.Snapshot, profile, root.Value, roles, states);
	}

	public async Task<IReadOnlyList<IasArchiveOwner>> GetArchiveOccupancyAsync(CancellationToken token)
	{
		using var lease = AcquireIasLease();
		var installed = GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
		var work = At("temp/occupancy-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try { return await ReadArchiveOwnersAsync(installed, work, null, token).ConfigureAwait(false); }
		finally { IasFileTree.Delete(work); }
	}

	public async Task PreflightArchivesAsync(IReadOnlyList<string> archives, CancellationToken token)
	{
		if (archives.Count == 0) return;
		var owners = await GetArchiveOccupancyAsync(token).ConfigureAwait(false);
		await RequireArchivesFreeAsync(archives, owners, null, token).ConfigureAwait(false);
	}

	private async Task RequireRegisteredArchivesFreeAsync(IasInstalled installed, IasModTransaction tx, string id,
		CancellationToken token)
	{
		var owners = await ReadArchiveOwnersAsync(installed, tx.Backup, id, token).ConfigureAwait(false);
		var candidate = owners.SingleOrDefault(owner => owner.Id == id)
			?? throw new InvalidDataException($"官方 IAS 角色库中找不到 {id}。");
		await RequireArchivesFreeAsync(candidate.Archives, owners, id, token).ConfigureAwait(false);
	}

	private async Task<IasArchiveOwner[]> SwapConflictsAsync(IasInstalled installed, IasModTransaction tx, string id,
		CancellationToken token, string[]? expectedOwners = null)
	{
		var conflicts = await ReadConflictsAsync(installed, tx, id, token).ConfigureAwait(false);
		if (expectedOwners is not null && !conflicts.Select(owner => owner.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expectedOwners))
			throw new InvalidOperationException("护甲占用已变更，请重新选择。");
		foreach (var owner in conflicts)
			await RunAsync(installed, tx, "set-enabled", owner.Id, false, token, false).ConfigureAwait(false);
		return conflicts;
	}

	private async Task<IasArchiveOwner[]> ReadConflictsAsync(IasInstalled installed, IasModTransaction tx, string id, CancellationToken token)
	{
		var owners = await ReadArchiveOwnersAsync(installed, tx.Backup, id, token).ConfigureAwait(false);
		var candidate = owners.SingleOrDefault(owner => owner.Id == id)
			?? throw new InvalidDataException($"官方 IAS 角色库中找不到 {id}。");
		return ConflictsFor(id, candidate.Archives, owners);
	}

	public static IasArchiveOwner[] ConflictsFor(string id, string[] archives, IEnumerable<IasArchiveOwner> owners) =>
		owners.Where(owner => owner.Enabled && owner.Id != id)
			.Select(owner => owner with { Archives = owner.Archives.Intersect(archives, StringComparer.OrdinalIgnoreCase).ToArray() })
			.Where(owner => owner.Archives.Length > 0).ToArray();

	public static IasRoleConflict? DescribeConflicts(IReadOnlyList<IasArchiveOwner> owners, IReadOnlyDictionary<string, string> equipment) =>
		owners.Count == 0 ? null : new(owners.Select(owner => owner.Name).Distinct().ToArray(),
			owners.SelectMany(owner => owner.Archives).Distinct(StringComparer.OrdinalIgnoreCase)
				.Select(archive => equipment.GetValueOrDefault(archive, archive)).Distinct().ToArray());

	private async Task<IasRoleConflict?> DescribeConflictsAsync(IReadOnlyList<IasArchiveOwner> owners, CancellationToken token) =>
		owners.Count == 0 ? null : DescribeConflicts(owners, await ReadEquipmentNamesAsync(token).ConfigureAwait(false));

	public async Task<IReadOnlyDictionary<string, string>> ReadEquipmentNamesAsync(CancellationToken token)
	{
		try
		{
			var catalog = await CoreServices.CreateRuntimeArmorRegistryService(_paths).ReadArmorCatalogAsync(token).ConfigureAwait(false);
			return catalog.GroupBy(entry => entry.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);
		}
		catch (Microsoft.Data.Sqlite.SqliteException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
	}

	private async Task RequireArchivesFreeAsync(IReadOnlyList<string> archives, IReadOnlyList<IasArchiveOwner> owners,
		string? excluding, CancellationToken token)
	{
		foreach (var archive in archives)
		{
			var matches = OwnersOf(owners, archive, excluding);
			if (matches.Length != 0)
				throw new InvalidOperationException(await ArchiveConflictMessageAsync(archive, matches, token).ConfigureAwait(false));
		}
	}

	private static string[] OwnersOf(IReadOnlyList<IasArchiveOwner> owners, string archive, string? excluding = null) =>
		owners.Where(owner => owner.Enabled && owner.Id != excluding &&
			owner.Archives.Contains(archive, StringComparer.OrdinalIgnoreCase)).Select(owner => owner.Name).Distinct().ToArray();

	private async Task<string> ArchiveConflictMessageAsync(string archive, string[] owners, CancellationToken token)
	{
		string? name = null;
		try
		{
			var catalog = await CoreServices.CreateRuntimeArmorRegistryService(_paths).ReadArmorCatalogAsync(token).ConfigureAwait(false);
			name = catalog.FirstOrDefault(entry => entry.ArchiveId.Equals(archive, StringComparison.OrdinalIgnoreCase))?.DisplayName;
		}
		catch (Microsoft.Data.Sqlite.SqliteException) { /* 索引不可用时保留 Archive。 */ }
		var equipment = name is null ? archive : $"{name} / {archive}";
		var roles = owners.Length == 0 ? "启用的角色占用同一装备" :
			$"与已启用角色{string.Join("、", owners.Select(owner => $"『{owner}』"))}占用同一装备";
		return $"{roles}（{equipment}），请先停用其中一个。";
	}

	internal static string? OfficialConflictArchive(string message)
	{
		const string prefix = "启用的 Mod 占用同一装备 Archive，请取消勾选其中一个：";
		var offset = message.IndexOf(prefix, StringComparison.Ordinal);
		if (offset < 0 || message.Length - offset - prefix.Length < 16) return null;
		var archive = message.AsSpan(offset + prefix.Length, 16);
		return archive.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? archive.ToString() : null;
	}

	private async Task<IasArchiveOwner[]> ReadArchiveOwnersAsync(IasInstalled installed, string backup, string? includeRole,
		CancellationToken token)
	{
		var owners = await ReadArchiveDataAsync<IasArchiveOwner[]>(installed, backup, "archive-occupancy", includeRole, token)
			.ConfigureAwait(false);
		var names = GetRoles().ToDictionary(role => role.Id, role => role.Name, StringComparer.Ordinal);
		return owners.Select(owner => names.TryGetValue(owner.Id, out var name) ? owner with { Name = name } : owner).ToArray();
	}

	private async Task<T> ReadArchiveDataAsync<T>(IasInstalled installed, string backup, string command, string? argument,
		CancellationToken token, bool isolatedRead = false)
	{
		var request = new IasWorkerRequest(command, _game, installed.ComponentsDirectory, At("library"), backup,
			string.Empty, string.Empty, installed.Version, argument) { IsolatedRead = isolatedRead };
		var value = await RunWorkerAsync(request, token, measure: command != "archive-occupancy" || argument != "*").ConfigureAwait(false);
		return JsonSerializer.Deserialize<T>(value ?? "null")
			?? throw new InvalidDataException("IAS worker 未返回装备 Archive 信息。");
	}

	private LibrarySnapshot SyncProfile(LibrarySnapshot snapshot, Profile profile, Guid dataId, IReadOnlyList<IasRole> roles,
		IReadOnlyDictionary<string, bool>? states)
	{
		var managed = roles.SelectMany(role => role.ModIds).Append(dataId).Select(id => new ModNodeId(id)).ToHashSet();
		var enabled = states is null ? [] : roles.Where(role => states[role.Id]).SelectMany(role => role.ModIds)
			.Prepend(dataId).Select(id => new ModNodeId(id)).Where(snapshot.Nodes.ContainsKey).ToArray();
		var retained = profile.Entries.Select(entry => entry.NodeId).Where(id => !managed.Contains(id) || enabled.Contains(id));
		return SetEntries(snapshot, profile, retained.Concat(enabled));
	}

	private async Task<Dictionary<string, bool>> ReadRoleStatesAsync(IasInstalled installed, string backup,
		IReadOnlyList<IasRole> roles, CancellationToken token)
	{
		var request = new IasWorkerRequest("status", _game, installed.ComponentsDirectory, At("library"), backup,
			string.Empty, string.Empty, installed.Version);
		var value = await RunWorkerAsync(request, token).ConfigureAwait(false);
		using var document = JsonDocument.Parse(value ?? throw new InvalidDataException("IAS worker 未返回角色库状态。"));
		var states = document.RootElement.GetProperty("Library").GetProperty("entries").EnumerateArray()
			.ToDictionary(entry => entry.GetProperty("id").GetString()!, entry => entry.GetProperty("enabled").GetBoolean(), StringComparer.Ordinal);
		foreach (var role in roles)
			if (!states.ContainsKey(role.Id))
				throw new InvalidDataException($"官方 IAS 角色库中找不到 {role.Name}（{role.Id}）。");
		return states;
	}

	private async Task<IasGraphicsState> ReadGraphicsAsync(IasInstalled installed, string backup, CancellationToken token)
	{
		var request = new IasWorkerRequest("graphics-status", _game, installed.ComponentsDirectory, At("library"), backup,
			string.Empty, string.Empty, installed.Version);
		var value = await RunWorkerAsync(request, token).ConfigureAwait(false);
		using var document = JsonDocument.Parse(value ?? throw new InvalidDataException("IAS worker 未返回画面组件状态。"));
		var state = document.RootElement;
		var dlss = (IasDlssMode)state.GetProperty(nameof(IasGraphicsState.Dlss)).GetInt32();
		if (!Enum.IsDefined(dlss)) throw new InvalidDataException("IAS worker 返回了无效的 DLSS 模式。");
		return new(state.GetProperty(nameof(IasGraphicsState.ReShadeEnabled)).GetBoolean(), dlss);
	}

	private async Task<string?> RunAsync(IasInstalled installed, IasModTransaction tx, string command, string? argument,
		bool export, CancellationToken token, bool enabled = false)
	{
		EnsureGameStopped();
		var icon = Path.Combine(tx.Work, "icon.png");
		if (export && !File.Exists(icon))
		{
			// 本体尚无角色时也给官方导出器一张合法 PNG。
			File.WriteAllBytes(icon, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR1kAAAAASUVORK5CYII="));
		}
		var request = new IasWorkerRequest(command, _game, installed.ComponentsDirectory, At("library"), tx.Backup,
			tx.Export, icon, installed.Version, argument, enabled);
		return await RunWorkerAsync(request, token).ConfigureAwait(false);
	}

	private async Task<string?> RunWorkerAsync(IasWorkerRequest request, CancellationToken token, bool measure = true)
	{
		var result = measure
			? await IasTiming.MeasureAsync("官方 IAS：" + request.Command, () => _worker.RunAsync(request, token)).ConfigureAwait(false)
			: await _worker.RunAsync(request, token).ConfigureAwait(false);
		if (!result.Success) throw new InvalidOperationException(result.Error ?? $"IAS worker {request.Command} 失败。");
		return result.Value;
	}

	private async Task<IasInstalled> RequireCompatibleAsync(CancellationToken token)
	{
		var installed = _components.GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
		var result = await IasGameCompatibility.CheckAsync(_game, installed.ComponentsDirectory, token).ConfigureAwait(false);
		if (result.Status != IasGameCompatibilityStatus.Compatible) throw new IasGameCompatibilityException(result);
		return installed;
	}

	private static Profile RequireActive(LibrarySnapshot snapshot) =>
		Active(snapshot) ?? throw new InvalidOperationException("请先在管理器中选择当前配置。");

	private LibrarySnapshot SetEntries(LibrarySnapshot snapshot, Profile profile, IEnumerable<ModNodeId> ids)
	{
		var result = _profiles.ReplaceEntries(snapshot, profile.Id, ids.Distinct().ToArray());
		return result.Succeeded ? result.Snapshot : throw new InvalidOperationException($"无法更新当前配置：{result.Message}");
	}

	private Task ChangeAsync(Func<IasModTransaction, LibrarySnapshot, Task<(LibrarySnapshot Mods, bool Deploy)>> operation,
		CancellationToken token, bool captureRuntime = true, bool captureComponents = false) => ChangeAsync(async (tx, snapshot) =>
		{
			var (mods, deploy) = await operation(tx, snapshot).ConfigureAwait(false);
			return (mods, deploy, true);
		}, token, captureRuntime: captureRuntime, captureComponents: captureComponents);

	private async Task<T> ChangeAsync<T>(Func<IasModTransaction, LibrarySnapshot, Task<(LibrarySnapshot Mods, bool Deploy, T Value)>> operation,
		CancellationToken token, Func<Task>? afterDeploy = null, bool captureRuntime = true, bool captureComponents = false,
		Func<LibrarySnapshot, CancellationToken, Task>? validate = null,
		Func<CancellationToken, Task<(bool Handled, T Value)>>? readOnly = null)
	{
		EnsureGameStopped();
		Directory.CreateDirectory(_root);
		var prepare = System.Diagnostics.Stopwatch.StartNew();
		using var iasLease = AcquireIasLease();
		await using var lease = await CoreServices.CreateLibraryWriteLeaseProvider().AcquireAsync(_paths.ModsDirectory, token).ConfigureAwait(false);
		await RecoverWithDiagnosticsAsync(token).ConfigureAwait(false);
		var snapshot = await LoadModsAsync(token).ConfigureAwait(false);
		IasTiming.Record("加锁与读取模组库", prepare.Elapsed);
		if (validate is not null)
			await IasTiming.MeasureAsync("锁内复核转换源", () => validate(snapshot, token)).ConfigureAwait(false);
		token.ThrowIfCancellationRequested();
		EnsureGameStopped();
		if (readOnly is not null)
		{
			var result = await readOnly(token).ConfigureAwait(false);
			if (result.Handled) return result.Value;
		}
		using var tx = IasTiming.Measure("拍快照", () => new IasModTransaction(_paths, _root, _game, captureRuntime, captureComponents, _optionActivations));
		IGameDataDeploymentLease? deploymentLease = null;
		ActivationState? previousDeployment = null;
		try
		{
			EnsureGameStopped();
			var result = await IasTiming.MeasureAsync("IAS 处理（含官方步骤）", () => operation(tx, snapshot)).ConfigureAwait(false);
			token.ThrowIfCancellationRequested();
			EnsureGameStopped();
			if (result.Deploy && _deploy is null)
			{
				deploymentLease = await CoreServices.CreateGameDataDeploymentLeaseProvider().AcquireAsync(_data, token).ConfigureAwait(false);
				previousDeployment = await CoreServices.CreateActivationStateStore().TryLoadAsync(_data, token).ConfigureAwait(false);
			}
			if (!ReferenceEquals(snapshot, result.Mods))
				await IasTiming.MeasureAsync("写模组库", () => tx.PublishAsync(result.Mods, _store, token)).ConfigureAwait(false);
			if (result.Deploy)
			{
				var profile = RequireActive(result.Mods);
				EnsureGameStopped();
				if (_deploy is null)
					tx.RecordDeployment(new(previousDeployment is null || previousDeployment is { Completed: true, ProfileId: not null },
						previousDeployment?.ProfileId?.Value));
				await IasTiming.MeasureAsync("部署到游戏", () => ApplyAsync(profile, result.Mods, tx.Work, token)).ConfigureAwait(false);
			}
			if (afterDeploy is not null) await afterDeploy().ConfigureAwait(false);
			IasTiming.Measure("收尾", () => { tx.Finish("committed"); return 0; });
			return result.Value;
		}
		catch (Exception failure)
		{
			try
			{
				await tx.RollbackAsync().ConfigureAwait(false);
				if (tx.PriorDeployment is { } prior)
					await RestoreDeploymentAsync(prior, snapshot, tx.Work).ConfigureAwait(false);
				tx.Finish("rolled back");
			}
			catch (Exception restore)
			{
				throw RecoveryFailure("IAS 操作失败：" + failure.Message + "\n", restore, failure);
			}
			throw;
		}
		finally
		{
			if (deploymentLease is not null) await deploymentLease.DisposeAsync().ConfigureAwait(false);
		}
	}

	private void EnsureGameStopped()
	{
		if (_isGameRunning()) throw new IasGameRunningException();
	}

	public async Task<bool> RecoverAsync(CancellationToken token)
	{
		EnsureGameStopped();
		FileStream iasLease;
		try
		{
			if (!IasSnapshot.Exists(At("snapshots/previous"))) return false;
			iasLease = AcquireIasLease();
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw RecoveryFailure(string.Empty, error);
		}
		using (iasLease)
			return await RecoverUnderIasLeaseAsync(token).ConfigureAwait(false);
	}

	private async Task<bool> RecoverUnderIasLeaseAsync(CancellationToken token)
	{
		if (!IasSnapshot.Exists(At("snapshots/previous"))) return false;
		ILibraryWriteLease lease;
		try { lease = await CoreServices.CreateLibraryWriteLeaseProvider().AcquireAsync(_paths.ModsDirectory, token).ConfigureAwait(false); }
		catch (Exception error) when (error is not OperationCanceledException)
		{
			throw RecoveryFailure(string.Empty, error);
		}
		await using (lease)
			return await RecoverWithDiagnosticsAsync(token).ConfigureAwait(false);
	}

	private async Task<bool> RecoverWithDiagnosticsAsync(CancellationToken token)
	{
		EnsureGameStopped();
		token.ThrowIfCancellationRequested();
		try
		{
			var previous = At("snapshots/previous");
			if (!IasSnapshot.Exists(previous)) return false;
			if (File.Exists(IasFileTree.Under(previous, "finished")))
			{
				IasSnapshot.RemoveFinished(previous);
				return false;
			}

			var saved = new IasSnapshot(previous, _root, _paths, _game);
			var manifest = saved.Load(out var failures);
			await saved.RestoreFilesAsync(manifest, _optionActivations, failures).ConfigureAwait(false);
			if (manifest.PriorDeployment is { } prior)
			{
				EnsureGameStopped();
				await using var deploymentLease = await CoreServices.CreateGameDataDeploymentLeaseProvider()
					.AcquireAsync(_data, CancellationToken.None).ConfigureAwait(false);
				var work = At("temp/recover-" + Guid.NewGuid().ToString("N"));
				try { await RestoreDeploymentAsync(prior, await LoadModsAsync(CancellationToken.None).ConfigureAwait(false), work).ConfigureAwait(false); }
				finally { IasSnapshot.Clean(work); }
			}
			saved.Finish("recovered");
			IasSnapshot.RemoveFinished(previous);
			return true;
		}
		catch (Exception error)
		{
			throw RecoveryFailure(string.Empty, error);
		}
	}

	private async Task RestoreDeploymentAsync(IasSnapshot.Deployment prior, LibrarySnapshot snapshot, string work)
	{
		EnsureGameStopped();
		if (!prior.CanRestore) throw new InvalidOperationException("上一次部署状态不完整，无法自动恢复游戏部署。");
		if (prior.ProfileId is { } id)
		{
			var profile = snapshot.Profiles.SingleOrDefault(value => value.Id.Value == id)
				?? throw new InvalidOperationException($"找不到回滚所需的配置：{id}。");
			await ApplyAsync(profile, snapshot, work, CancellationToken.None).ConfigureAwait(false);
		}
		else
		{
			var result = await CoreServices.CreateApplyExecutor().DeactivateAsync(_data, CancellationToken.None).ConfigureAwait(false);
			if (!result.Success)
				throw new InvalidOperationException($"无法撤回新部署的配置：{string.Join("；", result.Issues.Select(issue => issue.Message))}");
		}
	}

	private InvalidOperationException RecoveryFailure(string prefix, Exception error, Exception? operationFailure = null)
	{
		var detail = error is AggregateException aggregate
			? string.Join("\n", aggregate.Flatten().InnerExceptions.Select(failure => failure.Message))
			: error.Message;
		string log;
		try
		{
			var path = At($"logs/recovery-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, $"{DateTimeOffset.Now:O}\n快照：{Path.Combine(_root, "snapshots", "previous")}\n{operationFailure}\n{error}");
			log = $"诊断日志：{path}";
		}
		catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			log = $"无法写入 IAS 诊断日志（{Path.Combine(_root, "logs")}）：{logError.Message}";
			System.Diagnostics.Trace.TraceError($"{log}\n{operationFailure}\n{error}");
		}
		return new InvalidOperationException($"{prefix}IAS 恢复失败，快照已保留。{log}\n{detail}", error);
	}

	private FileStream AcquireIasLease() =>
		new(At(".deployment.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

	private async Task ApplyAsync(Profile profile, LibrarySnapshot snapshot, string work, CancellationToken token)
	{
		if (_deploy is not null)
		{
			await _deploy(profile, snapshot, token).ConfigureAwait(false);
			return;
		}
		await using var reader = CoreServices.CreateModInformationReader();
		await using var center = CoreServices.CreateModInformationCenter(_paths, reader);
		// 读取现有护甲设置，部署副作用留在本次 IAS 暂存目录。
		var registry = CoreServices.CreateRuntimeArmorRegistryService(_paths);
		var portableRoot = Path.Combine(work, "profile-registry");
		IasFileTree.Delete(portableRoot);
		foreach (var name in new[] { RuntimeArmorRegistryService.ProtocolFileName, RuntimeArmorRegistryService.OwnerFileName,
			"wardrobe.links", "kit-instance.rules", "kit-instance.mapping.rules" })
			IasFileTree.Copy(Path.Combine(registry.RegistryRoot, name), Path.Combine(portableRoot, name));
		var lua = new LuaDeploymentService(armorRegistry: CoreServices.CreateRuntimeArmorRegistryService(_paths, portableRoot));
		var apply = new ProfileApplyService(center, CoreServices.CreateApplyPlanner(), CoreServices.CreateApplyExecutor(),
			CoreServices.CreateDeploymentCapabilityService(), _paths, _optionActivations ?? CoreServices.CreateOptionActivationStore(_paths), lua, registry);
		var result = await apply.ApplyAsync(profile, snapshot,
			_paths.ModsDirectory, _data, token).ConfigureAwait(false);
		if (!result.Success)
			throw new InvalidOperationException($"无法部署 IAS 所在配置：{string.Join("；", result.Issues.Select(issue => issue.Message))}");
	}

	private static void Expand(IasInstalled installed, IasModTransaction tx, CancellationToken token)
	{
		foreach (var name in new[] { "ArmorBase", "ArmorAssets", "Generator" }.Concat(IasPaths.GraphicsComponents))
		{
			var archive = IasFileTree.Under(installed.ComponentsDirectory, IasPaths.RuntimeArchive(name));
			var destination = Expanded(installed, name);
			if (IasPaths.GraphicsComponents.Contains(name, StringComparer.Ordinal) && !File.Exists(archive))
			{
				IasFileTree.Delete(destination);
				continue;
			}
			var stage = Path.Combine(tx.Work, name);
			IasFileTree.Extract(archive, stage, token);
			IasFileTree.Delete(destination);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			Directory.Move(stage, destination);
		}
	}

	private static bool NeedsGraphicsArchives(IasInstalled installed, IasRelease release) =>
		release.Files.Any(file => IasPaths.GraphicsArchives.Contains(file.Path, StringComparer.Ordinal) &&
			!File.Exists(IasFileTree.Under(installed.ComponentsDirectory, file.Path)));

	private static string Expanded(IasInstalled installed, string name) => IasPaths.Expanded(installed, name);
	private string At(string relative) => IasFileTree.Under(_root, relative.Replace('/', Path.DirectorySeparatorChar));

	private static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
	private static void Write<T>(string path, T value)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var pending = path + ".tmp";
		File.WriteAllText(pending, JsonSerializer.Serialize(value));
		File.Move(pending, path, overwrite: true);
	}

	private static async Task<(IasRelease Release, string Package)> ReadOfflineAsync(string path, string work, CancellationToken token)
	{
		using var archive = ZipFile.OpenRead(path);
		var feedEntry = archive.GetEntry("feed.signed.json") ?? throw new InvalidDataException("IAS 离线包缺少签名清单。");
		if (feedEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException("IAS 离线包签名清单过大。");
		using var feed = new MemoryStream();
		await using (var source = feedEntry.Open())
			await source.CopyToAsync(feed, token).ConfigureAwait(false);
		var verified = IasFeedClient.Verify(feed.ToArray());
		var release = verified.Latest ?? throw new InvalidDataException(verified.Message);
		var entry = archive.GetEntry("release.zip") ?? throw new InvalidDataException("IAS 离线包缺少 release.zip。");
		if (entry.Length != release.ZipBytes) throw new InvalidDataException("IAS 离线整包大小与签名清单不符。");
		var target = Path.Combine(work, "release.zip");
		await using var input = entry.Open();
		await using var output = File.Create(target);
		await input.CopyToAsync(output, token).ConfigureAwait(false);
		return (release, target);
	}
}

