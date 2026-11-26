using System.Globalization;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

public sealed record IasDependency(ulong Id, string? GameArchive);
public sealed record IasStandardResult(string ModDirectory, int BaseUnits, int OptionUnits,
	IReadOnlyList<IasDependency> ExternalTextures, IReadOnlyList<IasDependency> ExternalMaterials);

public sealed class IasStandardConverter(Func<string, uint> boneHash, IasTargetResolver resolveTarget,
	IReadOnlyDictionary<ulong, byte[]> materialTemplates)
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private static readonly PatchTocScanner scanner = new();
	private readonly PatchEntryPayloadReader payloadReader = new();

	public async Task<IasStandardResult> ConvertAsync(string sampleDirectory, string gameData, string outputDirectory,
		CancellationToken cancellationToken = default, bool mergeHelmet = false, string? displayName = null)
	{
		var sample = Path.GetFullPath(sampleDirectory);
		var output = Path.GetFullPath(outputDirectory);
		var game = Path.GetFullPath(gameData);
		if (IsWithin(output, sample) || IsWithin(output, game))
			throw new ArgumentException("IAS 输出目录必须位于源包和游戏数据目录之外。", nameof(outputDirectory));
		var mod = Path.Combine(output, "mod");
		if (Directory.Exists(mod) || File.Exists(mod))
			throw new IOException($"IAS 输出目录已存在：{mod}。");

		var main = Path.Combine(sample, "main");
		var companion = Path.Combine(sample, "companion", "HD2IndependentRig");
		var plans = IasPackageRoutes.Read(companion);
		if (!Directory.Exists(main))
			throw new InvalidDataException("IAS 包缺少 main 目录。");
		var patches = await ReadPatchesAsync(main, cancellationToken).ConfigureAwait(false);

		// 同一归档由较新的补丁覆盖。跨归档的材质 ID 不可静默选取。
		var resources = new Dictionary<AssetKey, PatchTocEntry>();
		foreach (var patch in patches.OrderBy(patch => patch.Number))
			foreach (var entry in patch.Entries.Where(entry => entry.AssetKey.TypeId is
				MaterialDependencyResolver.MaterialTypeId or MaterialDependencyResolver.TextureTypeId))
			{
				if (resources.TryGetValue(entry.AssetKey, out var previous) &&
					!string.Equals(ArchiveName(previous), ArchiveName(entry), StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException($"IAS 主包在不同归档重复定义材质或贴图 {entry.AssetKey.FileId:x16}。");
				resources[entry.AssetKey] = entry;
			}

		var profiles = ReadProfiles(companion, plans.Select(plan => plan.Equipment).ToArray());
		var physicsSources = new Dictionary<ulong, IasPhysicsSource>();
		var sharedPhysics = new Dictionary<ulong, IasPhysicsSource>();
		var physicsProfiles = new Dictionary<ulong, IasRigProfile>(profiles);
		foreach (var path in Directory.EnumerateFiles(companion, "*.hd2phys").Order(StringComparer.Ordinal))
		{
			var source = IasPhysicsReader.Read(File.ReadAllBytes(path));
			if (!physicsSources.TryAdd(source.UnitId, source))
				throw new InvalidDataException($"IAS 物理资料重复定义 Unit {source.UnitId:x16}。");
			const string canonicalPhysicsSuffix = ".canonical.hd2phys";
			if (!path.EndsWith(canonicalPhysicsSuffix, StringComparison.OrdinalIgnoreCase))
				continue;
			var stem = path[..^canonicalPhysicsSuffix.Length];
			var canonicalPath = stem + ".canonical.rigbin";
			var consumersPath = stem + ".consumers.rigbin";
			if (!File.Exists(canonicalPath) || !File.Exists(consumersPath))
				throw new InvalidDataException($"IAS 共享物理资料缺少配套骨骼文件：{Path.GetFileName(path)}。");
			using var canonical = File.OpenRead(canonicalPath);
			if (!IasRigReader.Read(canonical).TryGetValue(source.UnitId, out var canonicalProfile))
				throw new InvalidDataException($"IAS 共享物理缺少权威骨骼 Unit {source.UnitId:x16}。");
			physicsProfiles[source.UnitId] = canonicalProfile;
			using var consumers = File.OpenRead(consumersPath);
			foreach (var unit in IasRigReader.Read(consumers).Keys)
				if (!sharedPhysics.TryAdd(unit, source))
					throw new InvalidDataException($"IAS Unit {unit:x16} 属于多份共享物理资料。");
		}

		var resolved = new List<(IasPackagePlan Plan, IasTargetKit Target, Dictionary<ulong, PublishedUnit> Units)>();
		var unitReader = new PatchUnitMeshReader();
		foreach (var plan in plans)
		{
			var routes = plan.Base.Concat(plan.Groups.SelectMany(group => group.Options).SelectMany(option => option.Routes)).ToArray();
			var ids = routes.Select(route => route.Published).Distinct().ToArray();
			var target = await resolveTarget(plan.Equipment, routes.Select(route => route.Source).Distinct().ToArray(), cancellationToken)
				.ConfigureAwait(false);
			var inputs = await ReadPublishedUnitsAsync(plan.Equipment, ids, patches, cancellationToken).ConfigureAwait(false);
			foreach (var (id, input) in inputs)
				if (!input.Hidden && !profiles.ContainsKey(id))
					throw new InvalidDataException($"IAS 可见 Unit {id:x16} 缺少骨骼资料（rigbin）。");
			resolved.Add((plan, target, inputs));
		}

		var merge = mergeHelmet ? BuildHelmetMergePlan(resolved) : null;
		var resolver = new GameDataPackageResolver(game);
		var avatar = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var names = profiles.Values.SelectMany(profile => profile.SourceNames).Distinct(StringComparer.Ordinal).ToArray();
		var targetRig = Hd2RigReader.FromTransformInfo(avatar, new UnitBoneNames(names.Select(boneHash).ToArray(), names));
		var vanilla = new GameDataUnitMeshReader(resolver);
		var units = new Dictionary<ulong, GameDataUnitMesh>();
		var usedMaterials = new HashSet<ulong>();
		var staging = Path.Combine(output, $".ias-{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);
		try
		{
			var package = Path.Combine(staging, "mod");
			var template = Path.Combine(staging, "template");
			var baseCount = 0;
			var optionCount = 0;
			var manifestGroups = new List<IasOptionGroup>();
			foreach (var (plan, target, inputs) in resolved)
			{
				if (merge is not null && target.Helmet) continue;
				baseCount += await WriteRoutesAsync(plan.Base, target, inputs, package, merge?.HelmetPlan.Base).ConfigureAwait(false);
				foreach (var group in plan.Groups.Where(group => merge?.BodyGroups.Contains(group) != true))
				{
					manifestGroups.Add(group);
					foreach (var option in group.Options)
						optionCount += await WriteOptionAsync(group.Name, option, target, inputs).ConfigureAwait(false);
				}
				if (merge is { Options.Count: > 0 })
				{
					var groupName = string.Join(" / ", merge.BodyGroups.Concat(merge.HelmetPlan.Groups).Select(group => group.Name));
					manifestGroups.Add(new(groupName, merge.Options.Select(option => option.Body).ToArray()));
					foreach (var option in merge.Options)
						optionCount += await WriteOptionAsync(groupName, option.Body, target, inputs, option.Helmet).ConfigureAwait(false);
				}
			}

			async Task<int> WriteOptionAsync(string groupName, IasOption option, IasTargetKit target,
				IReadOnlyDictionary<ulong, PublishedUnit> inputs, IReadOnlyList<IasRoute>? helmetRoutes = null)
			{
				var written = await WriteRoutesAsync(option.Routes, target, inputs,
					Path.Combine(package, option.Folder), helmetRoutes).ConfigureAwait(false);
				return written != 0 ? written : throw new InvalidDataException(
					$"目标装备 {target.Name} 没有对应格子，无法保留选项“{groupName} / {option.Name}”。");
			}

			var (dependencies, externalTextures, externalMaterials) = await ResolveMaterialsAsync(resources, usedMaterials, resolver, cancellationToken).ConfigureAwait(false);
			var sharedResourcePatches = patches.Where(patch =>
				!patch.Entries.Any(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId) &&
				patch.Entries.Any(entry => resources.ContainsKey(entry.AssetKey)))
				.GroupBy(patch => patch.Archive).ToArray();
			var sharedResourcePatch = sharedResourcePatches.Length == 1
				? sharedResourcePatches[0].OrderByDescending(patch => patch.Number).First() : null;
			foreach (var group in dependencies.GroupBy(entry =>
				merge?.BodyArchive.ToString("x16") ?? sharedResourcePatch?.Archive.ToString("x16") ?? ArchiveName(resources[entry.AssetKey])))
			{
				var sourcePath = sharedResourcePatch?.Path ?? resources[group.First().AssetKey].SourceFilePath;
				var number = resolved.Any(item => item.Target.Archive.ToString("x16") == group.Key) ? 2 :
					int.Parse(Path.GetFileName(sourcePath).Split(".patch_")[1], CultureInfo.InvariantCulture);
				await WritePatchAsync(sourcePath, $"{group.Key}.patch_{number}", template, package, group.ToArray(), cancellationToken)
					.ConfigureAwait(false);
			}
			var metadata = await ReadPackageMetadataAsync(sample, main, displayName, cancellationToken).ConfigureAwait(false);
			var targets = string.Join(" / ", resolved.Where(item => merge is null || !item.Target.Helmet).Select(item => item.Target.Name).Distinct(StringComparer.Ordinal));
			var manifestName = string.IsNullOrWhiteSpace(targets)
				? $"{metadata.Name}（由 IAS 转换）"
				: $"{metadata.Name} · {targets}（由 IAS 转换）";
			Directory.CreateDirectory(package);
			string? iconPath = null;
			if (metadata.IconSourcePath is { } iconSource)
			{
				var iconFileName = "icon" + Path.GetExtension(iconSource).ToLowerInvariant();
				File.Copy(iconSource, Path.Combine(package, iconFileName), overwrite: true);
				iconPath = iconFileName;
			}
			await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), SerializeManifest(manifestName,
				manifestGroups, metadata.Description, iconPath), cancellationToken).ConfigureAwait(false);
			await File.WriteAllTextAsync(Path.Combine(output, "dependencies.json"), JsonSerializer.Serialize(new
			{
				Materials = usedMaterials.Order().Select(id => id.ToString("x16")),
				PackagedMaterials = dependencies.Where(entry => entry.AssetKey.TypeId == MaterialDependencyResolver.MaterialTypeId)
					.Select(entry => entry.AssetKey.FileId.ToString("x16")),
				PackagedTextures = dependencies.Where(entry => entry.AssetKey.TypeId == MaterialDependencyResolver.TextureTypeId)
					.Select(entry => entry.AssetKey.FileId.ToString("x16")),
				ExternalTextures = externalTextures.Select(entry => new { Id = entry.Id.ToString("x16"), entry.GameArchive }),
				ExternalMaterials = externalMaterials.Select(entry => new { Id = entry.Id.ToString("x16"), entry.GameArchive }),
			}, JsonOptions), cancellationToken).ConfigureAwait(false);
			Directory.Move(package, mod);
			return new(mod, baseCount, optionCount, externalTextures, externalMaterials);

			async Task<int> WriteRoutesAsync(IReadOnlyList<IasRoute> routes, IasTargetKit target,
				IReadOnlyDictionary<ulong, PublishedUnit> inputs, string directory, IReadOnlyList<IasRoute>? helmetRoutes = null)
			{
				var archive = target.Archive.ToString("x16");
				var entries = new List<PatchArchiveAdditionalEntry>();
				var facts = new List<SourceJiggleBoneFact>();
				var writtenPhysics = new HashSet<ulong>();
				var mapped = MapRoutes(routes, target, inputs);
				var attachments = new List<(IasRoute Route, PatchUnitMesh Mesh)>();
				if (merge is not null && helmetRoutes is not null && mapped.Keys.Any(merge.Hosts.Contains))
				{
					foreach (var (route, slot) in MapRoutes(helmetRoutes, merge.HelmetTarget, merge.HelmetUnits).Values)
					{
						var input = merge.HelmetUnits[route.Published];
						if (!input.Hidden)
							attachments.Add((route, await AuthorAsync(merge.HelmetTarget, route, slot, input).ConfigureAwait(false)));
					}
				}
				foreach (var (route, slot) in mapped.Values.OrderBy(item => item.Slot.WriteOrder).ThenBy(item => item.Slot.Unit))
				{
					var input = inputs[route.Published];
					var donor = await GetDonorAsync(target, slot).ConfigureAwait(false);
					var entry = donor.Payload.Entry;
					var hasAttachments = attachments.Count > 0 && merge!.Hosts.Contains(slot.Unit);
					if (input.Hidden && !hasAttachments)
					{
						var hidden = new CanonicalHiddenUnitBuilder().Build(donor, avatar, minifyCullingMeshes: true).Entry;
						entries.Add(new(donor.AssetKey, hidden.EffectiveTocData, [], hidden.EffectiveGpuData,
							entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
						continue;
					}
					var model = input.Hidden
						? await AuthorAsync(target, attachments[0].Route, slot, merge!.HelmetUnits[attachments[0].Route.Published]).ConfigureAwait(false)
						: await AuthorAsync(target, route, slot, input).ConfigureAwait(false);
					var toc = model.Payload.TocData;
					var gpu = model.Payload.GpuResourceData;
					if (hasAttachments && (!input.Hidden || attachments.Count > 1))
					{
						var edit = AppendHelmet(model, attachments.Skip(input.Hidden ? 1 : 0).Select(item => item.Mesh).ToArray(), avatar);
						toc = edit.TocData;
						gpu = edit.GpuResourceData;
					}
					entries.Add(new(donor.AssetKey, toc, [], gpu, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
				}
				if (entries.Count == 0)
					return 0;
				var inputPath = inputs[mapped.Values.First().Route.Published].Patch.Path;
				await WritePatchAsync(inputPath, archive + ".patch_0", template, directory, entries, cancellationToken).ConfigureAwait(false);
				if (facts.Count > 0)
					await EmbedPhysicsAsync(Path.Combine(directory, archive + ".patch_0"), facts, cancellationToken).ConfigureAwait(false);
				return entries.Count;

				async Task<PatchUnitMesh> AuthorAsync(IasTargetKit kit, IasRoute route, IasTargetSlot slot, PublishedUnit source)
				{
					var donor = await GetDonorAsync(kit, slot).ConfigureAwait(false);
					var entry = donor.Payload.Entry;
					var sourceUnit = await unitReader.ReadCanonicalSourceAsync(source.Entry, source.Patch.Entries,
						cancellationToken: cancellationToken).ConfigureAwait(false);
					var profile = profiles[route.Published];
					var physicsSource = physicsSources.GetValueOrDefault(route.Published) ?? sharedPhysics.GetValueOrDefault(route.Published);
					var physics = physicsSource is null ? null :
						new IasPhysicsMigration(physicsSource, route.Published, profile, physicsProfiles[physicsSource.UnitId]);
					if (physics is { HasBones: false })
						physics = null;
					var (lods, materials) = ConvertMesh(sourceUnit.Model, profile, physics, route.Published);
					physics?.KeepWeightedChains(lods.Values.SelectMany(mesh => mesh.Vertices)
						.SelectMany(vertex => vertex.Influences).Select(influence => influence.BoneHash).ToHashSet(),
						avatar.NameHashes.Count);
					var unitRig = physics?.Append(avatar, lods.Values, compact: kit.Helmet) ?? avatar;
					var shell = IasNativeMesh.UseFourWeightStreams(donor.Model);
					var result = new UnitAuthor().Author(new UnitAuthorRequest(shell, donor.Payload.TocData, unitRig,
						lods[0], donor.Model.CustomizationInfo, slot.Unit, materials), lods, preserveAbsentCustomization: true);
					if (!result.IsValid || result.Model!.TransformNameHashes.Count > 256)
						throw new InvalidDataException($"IAS Unit {slot.Cell}/{slot.Body} -> {slot.Unit:x16} ({result.Model?.TransformNameHashes.Count} nodes): {string.Join("; ", result.Diagnostics)}");
					if (physics is not null && writtenPhysics.Add(route.Published))
						physics.AddFacts(facts, targetRig);
					usedMaterials.UnionWith(materials.Values);
					return new(entry, new(entry, result.TocData!, [], result.GpuData!), result.Model!);
				}
			}

			async Task<GameDataUnitMesh> GetDonorAsync(IasTargetKit kit, IasTargetSlot slot)
			{
				if (units.TryGetValue(slot.Unit, out var donor)) return donor;
				var key = new AssetKey(PatchUnitMeshReader.UnitTypeId, slot.Unit);
				donor = await vanilla.ReadAsync(kit.Archive.ToString("x16"), key, cancellationToken: cancellationToken).ConfigureAwait(false);
				units.Add(slot.Unit, donor);
				return donor;
			}

			(IReadOnlyDictionary<int, RetargetedMesh>, IReadOnlyDictionary<int, ulong>) ConvertMesh(
				UnitMeshModel model, IasRigProfile profile, IasPhysicsMigration? physics, ulong published)
			{
				try
				{
					return IasNativeMesh.Convert(model, profile, targetRig, avatar, boneHash, physics);
				}
				catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
				{
					throw new InvalidDataException($"无法解码 IAS 发布 Unit {published:x16} 的可见网格或骨骼：{error.Message}", error);
				}
			}
		}
		finally
		{
			Directory.Delete(staging, recursive: true);
		}
	}

	private sealed record HelmetMergePlan(ulong BodyArchive, IReadOnlySet<ulong> Hosts, IReadOnlyList<IasOptionGroup> BodyGroups,
		IasPackagePlan HelmetPlan, IasTargetKit HelmetTarget, IReadOnlyDictionary<ulong, PublishedUnit> HelmetUnits,
		IReadOnlyList<(IasOption Body, IReadOnlyList<IasRoute> Helmet)> Options);

	private static HelmetMergePlan BuildHelmetMergePlan(
		IReadOnlyList<(IasPackagePlan Plan, IasTargetKit Target, Dictionary<ulong, PublishedUnit> Units)> resolved)
	{
		var (bodyPlan, body, bodyUnits) = resolved.SingleOrDefault(item => !item.Target.Helmet);
		var (helmetPlan, helmet, helmetUnits) = resolved.SingleOrDefault(item => item.Target.Helmet);
		if (bodyPlan is null || helmetPlan is null)
			throw new InvalidDataException("头盔并进护甲需要包里同时有护甲和头盔。");
		if (!helmetUnits.Values.Any(unit => !unit.Hidden))
			throw new InvalidDataException("头盔并进护甲失败：包里没有可见的头盔 Unit。");
		var defaults = bodyPlan.Base.Concat(bodyPlan.Groups.SelectMany(group => group.Options[0].Routes)).ToArray();
		var bodyRoutes = MapRoutes(defaults, body, bodyUnits);
		var torso = bodyRoutes.Values.Where(item => item.Slot.IsTorso);
		var hosts = torso.GroupBy(item => item.Slot.Body)
			.Select(group => group.OrderBy(item => bodyUnits[item.Route.Published].Hidden)
				.ThenBy(item => item.Slot.WriteOrder).ThenBy(item => item.Slot.Unit).First().Slot)
			.ToArray();
		if (hosts.Any(slot => slot.Body == "Any")) hosts = hosts.Where(slot => slot.Body == "Any").ToArray();
		if (hosts.Length == 0)
			throw new InvalidDataException("头盔并进护甲失败：找不到胸部 Unit。");

		var hostRoutes = hosts.Select(slot => bodyRoutes[slot.Unit].Route).DistinctBy(route => route.Source).ToArray();
		var bodyGroups = bodyPlan.Groups.Where(group => group.Options[0].Routes
			.Any(route => hostRoutes.Any(host => host.Source == route.Source))).ToArray();
		var groups = bodyGroups.Concat(helmetPlan.Groups).ToArray();
		var options = new List<(IasOption, IReadOnlyList<IasRoute>)>();
		if (groups.Length > 0)
		{
			var combinations = groups.Aggregate(new[] { Array.Empty<IasOption>() }.AsEnumerable(),
				(prefixes, group) => prefixes.SelectMany(prefix => group.Options.Select(option => prefix.Append(option).ToArray())));
			foreach (var combination in combinations)
			{
				var armor = Overlay(hostRoutes, combination.Take(bodyGroups.Length).SelectMany(option => option.Routes));
				var head = Overlay(helmetPlan.Base, combination.Skip(bodyGroups.Length).SelectMany(option => option.Routes));
				var option = new IasOption(string.Join(" / ", combination.Select(item => item.Name)),
					"options/helmet-to-armor/" + options.Count.ToString(CultureInfo.InvariantCulture), armor);
				options.Add((option, head));
			}
		}
		return new(body.Archive, hosts.Select(slot => slot.Unit).ToHashSet(), bodyGroups, helmetPlan, helmet, helmetUnits, options);

		static IasRoute[] Overlay(IEnumerable<IasRoute> baseline, IEnumerable<IasRoute> overrides) =>
			baseline.Concat(overrides).GroupBy(route => route.Source).Select(group => group.Last()).ToArray();
	}

	private static Dictionary<ulong, (IasRoute Route, IasTargetSlot Slot)> MapRoutes(
		IReadOnlyList<IasRoute> routes, IasTargetKit target, IReadOnlyDictionary<ulong, PublishedUnit> inputs)
	{
		var mapped = new Dictionary<ulong, (IasRoute Route, IasTargetSlot Slot)>();
		foreach (var route in routes)
		foreach (var slot in target.Match(route.Source, inputs[route.Published].Hidden))
		{
			if (!mapped.TryAdd(slot.Unit, (route, slot)) && mapped[slot.Unit].Route.Published != route.Published)
				throw new InvalidDataException($"多个 IAS Unit 对应目标 {slot.Cell}/{slot.Body} {slot.Unit:x16}，无法同时替换。");
		}
		return mapped;
	}

	private static PatchUnitMeshEditResult AppendHelmet(PatchUnitMesh body, IReadOnlyList<PatchUnitMesh> helmets, UnitTransformInfo avatar)
	{
		var anchor = UnitLodFamilySource.ResolveAnchor(body.Model)
			?? throw new InvalidDataException("头盔并进护甲失败：胸部 Unit 没有可见网格。");
		var inputs = new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>>();
		foreach (var (raw, mesh) in UnitLodFamilySource.ResolveVisibleLodFamily(body.Model, anchor.Mesh))
		{
			var fragments = new List<CanonicalDecorationAppendInput>();
			foreach (var helmet in helmets)
			{
				var source = UnitLodFamilySource.ResolveVisibleMeshAtLod(helmet.Model, raw.LodIndex)
					?? UnitLodFamilySource.ResolveAnchor(helmet.Model)
					?? throw new InvalidDataException("头盔并进护甲失败：头盔 Unit 没有可见网格。");
				var fragment = UnitLodFamilySource.CreateFragment(helmet, source.Mesh, source.Raw);
				fragments.Add(new(fragment with { RawMesh = fragment.RawMesh with { LodIndex = raw.LodIndex } }, helmet.Entry.AssetKey.FileId));
			}
			inputs.Add(mesh.Index, fragments);
		}
		var append = new CanonicalDecorationUnitAppender().TryAppendLodFamily(body, inputs, avatar, allowTargetOwnedBones: true);
		return append.IsValid && append.Edit is { } edit ? edit :
			throw new InvalidDataException($"头盔并进护甲失败：{string.Join("；", append.Diagnostics.Select(item => item.Message))}");
	}

	public static async Task<IReadOnlyList<IasOriginalEquipment>> ReadOriginalEquipmentAsync(string source, CancellationToken ct)
	{
		var plans = IasPackageRoutes.Read(Path.Combine(source, "companion", "HD2IndependentRig"));
		var patches = await ReadPatchesAsync(Path.Combine(source, "main"), ct).ConfigureAwait(false);
		var equipment = new List<IasOriginalEquipment>();
		foreach (var plan in plans)
		{
			var routes = plan.Base.Concat(plan.Groups.SelectMany(group => group.Options).SelectMany(option => option.Routes)).ToArray();
			var inputs = await ReadPublishedUnitsAsync(plan.Equipment, routes.Select(route => route.Published).Distinct(),
				patches, ct).ConfigureAwait(false);
			equipment.Add(new(plan.Equipment, routes.GroupBy(route => route.Source)
				.ToDictionary(group => group.Key, group => group.All(route => inputs[route.Published].Hidden))));
		}
		return equipment;
	}

	private static async Task<IReadOnlyList<InputPatch>> ReadPatchesAsync(string main, CancellationToken cancellationToken)
	{
		if (!Directory.Exists(main))
			throw new InvalidDataException("IAS 包缺少 main 目录。");
		var patches = new List<InputPatch>();
		foreach (var path in Directory.EnumerateFiles(main, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
		{
			var split = Path.GetFileName(path).Split(".patch_", StringSplitOptions.None);
			if (split.Length != 2 || split[0].Length != 16 ||
				!ulong.TryParse(split[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var archive) ||
				!int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
				continue;
			if (patches.Any(patch => patch.Archive == archive && patch.Number == number))
				throw new InvalidDataException($"IAS 主包包含多个同名补丁 {archive:x16}.patch_{number}。");
			patches.Add(new(path, archive, number, await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false)));
		}
		if (patches.Count == 0)
			throw new InvalidDataException("IAS 主包内没有可读的补丁。");

		return patches;
	}

	private static async Task<Dictionary<ulong, PublishedUnit>> ReadPublishedUnitsAsync(IasEquipment equipment,
		IEnumerable<ulong> ids, IReadOnlyList<InputPatch> patches, CancellationToken cancellationToken)
	{
		var unitReader = new PatchUnitMeshReader();
		var inputs = new Dictionary<ulong, PublishedUnit>();
		foreach (var id in ids)
		{
			var candidates = patches.SelectMany(patch => patch.Entries
				.Where(entry => entry.AssetKey == new AssetKey(PatchUnitMeshReader.UnitTypeId, id))
				.Select(entry => (Patch: patch, Entry: entry))).ToArray();
			var byArchive = candidates.Where(candidate => candidate.Patch.Archive == equipment.Archive).ToArray();
			if (byArchive.Length != 0)
				candidates = byArchive;
			if (candidates.Length == 0 || candidates.Select(candidate => candidate.Patch.Archive).Distinct().Count() != 1)
				throw new InvalidDataException($"IAS 主包缺少发布 Unit {id:x16}，或多个归档同时定义了它。");
			var selected = candidates.OrderByDescending(candidate => candidate.Patch.Number).First();
			var hidden = false;
			// 空壳只有很小的网格。复用几何判定，同时避免为可见的大网格提前解码。
			if (selected.Entry.GpuResourceSize <= 65536)
			{
				var mesh = await unitReader.ReadCanonicalSourceAsync(selected.Entry, selected.Patch.Entries,
					cancellationToken: cancellationToken).ConfigureAwait(false);
				hidden = UnitSourceVisibilityClassifier.Classify(mesh).IsHidden ||
					(mesh.Model.RawMeshData.Count != 0 && mesh.Model.RawMeshData.Where(raw => raw.LodIndex >= 0)
						.All(raw => !UnitGeometryFactsBuilder.HasRenderableGeometry(raw)));
			}
			inputs.Add(id, new(selected.Patch, selected.Entry, hidden));
		}
		return inputs;
	}

	private static async Task EmbedPhysicsAsync(string patchPath, IReadOnlyList<SourceJiggleBoneFact> facts,
		CancellationToken cancellationToken)
	{
		var gpuPath = patchPath + ".gpu_resources";
		var prefixLength = new FileInfo(gpuPath).Length;
		var rig = PhysBoneSpringRig.Serialize(facts);
		var container = PhysBoneContainer.Build([new(PhysBoneContainerEmbedder.RigRecordName, rig)], checked((ulong)prefixLength));
		await PhysBoneCarrier.AppendContainerToFileAsync(gpuPath, container, cancellationToken).ConfigureAwait(false);
		var written = await PhysBoneCarrier.TryReadContainerFromFileAsync(gpuPath, cancellationToken).ConfigureAwait(false);
		if (written is null || !written.AsSpan().SequenceEqual(container))
			throw new InvalidDataException($"Could not verify the physbone container in {gpuPath}.");
	}

	internal static string SerializeManifest(string name, IEnumerable<IasOptionGroup> groups,
		string? description = null, string? iconPath = null)
	{
		var manifest = new Dictionary<string, object?>
		{
			["name"] = name,
			["sectionalPatchNumbering"] = "ias-per-archive-v1",
		};
		if (!string.IsNullOrWhiteSpace(description))
			manifest["description"] = description;
		if (!string.IsNullOrWhiteSpace(iconPath))
			manifest["iconPath"] = iconPath;
		manifest["options"] = groups.Select(group =>
		{
			var primary = group.Options[0];
			var subs = new List<object> { new { name = primary.Name } };
			foreach (var secondary in group.Options.Skip(1))
				subs.Add(new { name = secondary.Name, include = new[] { secondary.Folder } });
			return new
			{
				name = group.Name,
				include = new[] { primary.Folder },
				subOptions = subs,
			};
		});
		return JsonSerializer.Serialize(manifest, JsonOptions);
	}

	private async Task<(IReadOnlyList<PatchArchiveAdditionalEntry>, IReadOnlyList<IasDependency>, IReadOnlyList<IasDependency>)> ResolveMaterialsAsync(
		IReadOnlyDictionary<AssetKey, PatchTocEntry> resources, IReadOnlySet<ulong> usedMaterials,
		GameDataPackageResolver resolver, CancellationToken cancellationToken)
	{
		var references = new StingrayMaterialReferenceReader();
		var textures = new HashSet<ulong>();
		var entries = new List<PatchArchiveAdditionalEntry>();
		var external = new HashSet<AssetKey>();
		var found = new Dictionary<AssetKey, PatchTocEntry>();
		var missing = new List<AssetKey>();
		var defaults = new IasMaterialDefaults();
		using var archives = (await resolver.GetPackageNamesAsync(cancellationToken).ConfigureAwait(false)).GetEnumerator();
		foreach (var (parent, template) in materialTemplates)
		{
			var key = new AssetKey(MaterialDependencyResolver.MaterialTypeId, parent);
			var current = await ReadGameMaterialAsync(key).ConfigureAwait(false);
			if (current is null)
				throw new InvalidDataException($"游戏数据缺少 IAS 默认模板对应的父材质 {parent:x16}。");
			defaults.Add(parent, template, current);
		}

		var pending = new Queue<ulong>(usedMaterials.Order());
		var visited = new HashSet<ulong>();
		while (pending.TryDequeue(out var id))
		{
			if (!visited.Add(id))
				continue;
			var key = new AssetKey(MaterialDependencyResolver.MaterialTypeId, id);
			byte[]? material;
			if (resources.TryGetValue(key, out var source))
			{
				var payload = await payloadReader.ReadPayloadAsync(source, cancellationToken).ConfigureAwait(false);
				material = defaults.Migrate(payload.TocData);
				var missingIds = new HashSet<ulong>();
				foreach (var texId in references.ReadTextureIds(material))
				{
					if (texId == 0)
						continue;
					var texKey = new AssetKey(MaterialDependencyResolver.TextureTypeId, texId);
					if (!resources.ContainsKey(texKey) && await FindGameEntryAsync(texKey).ConfigureAwait(false) is null)
						missingIds.Add(texId);
				}
				if (missingIds.Count > 0)
				{
					var sourceParent = IasMaterialDefaults.ReadParent(material);
					var parentMaterial = sourceParent != 0
						? await ReadGameMaterialAsync(new AssetKey(MaterialDependencyResolver.MaterialTypeId, sourceParent)).ConfigureAwait(false)
						: null;
					var parentSlots = parentMaterial is not null ? IasMaterialDefaults.ReadSlots(parentMaterial) : null;
					material = defaults.MigrateMissing(material, missingIds.Contains, parentSlots);
				}
				entries.Add(ToAddition(payload with { TocData = material }));
			}
			else
			{
				external.Add(key);
				material = await ReadGameMaterialAsync(key).ConfigureAwait(false);
				if (material is null)
				{
					missing.Add(key);
					continue;
				}
			}
			textures.UnionWith(references.ReadTextureIds(material).Where(value => value != 0));
			var parent = IasMaterialDefaults.ReadParent(material);
			if (parent != 0 && parent != id)
				pending.Enqueue(parent);
		}
		foreach (var id in textures.Order())
		{
			var key = new AssetKey(MaterialDependencyResolver.TextureTypeId, id);
			if (resources.TryGetValue(key, out var source))
				entries.Add(ToAddition(await payloadReader.ReadPayloadAsync(source, cancellationToken).ConfigureAwait(false)));
			else
			{
				external.Add(key);
				if (await FindGameEntryAsync(key).ConfigureAwait(false) is null)
					missing.Add(key);
			}
		}

		if (missing.Count > 0)
			throw new InvalidDataException($"IAS 材质链缺少材质或贴图：{string.Join("、", missing.Select(key => $"{key.TypeId:x16}:{key.FileId:x16}"))}。");
		IasDependency[] ExternalOf(ulong type) => external.Where(key => key.TypeId == type).OrderBy(key => key.FileId)
			.Select(key => new IasDependency(key.FileId, found[key].SourceFileName)).ToArray();
		return (entries, ExternalOf(MaterialDependencyResolver.TextureTypeId), ExternalOf(MaterialDependencyResolver.MaterialTypeId));

		async ValueTask<PatchTocEntry?> FindGameEntryAsync(AssetKey key)
		{
			while (!found.ContainsKey(key) && archives.MoveNext())
			{
				var archive = archives.Current;
				var toc = await resolver.GetPackageTocAsync(archive, cancellationToken).ConfigureAwait(false);
				if (toc is null)
					continue;
				foreach (var entry in scanner.ScanEntries(toc.Data, archive, toc.UsesSlimEntryOffset))
					if (entry.AssetKey.TypeId is MaterialDependencyResolver.MaterialTypeId or MaterialDependencyResolver.TextureTypeId)
						found.TryAdd(entry.AssetKey, entry);
			}
			return found.GetValueOrDefault(key);
		}

		async ValueTask<byte[]?> ReadGameMaterialAsync(AssetKey key) =>
			await FindGameEntryAsync(key).ConfigureAwait(false) is { TocDataSize: > 0 } entry
				? await resolver.GetPackageResourceAsync(entry.SourceFileName, entry.TocDataOffset, entry.TocDataSize, cancellationToken)
					.ConfigureAwait(false)
				: null;
	}

	private async Task WritePatchAsync(string samplePatch, string fileName, string templateDirectory, string outputDirectory,
		IReadOnlyList<PatchArchiveAdditionalEntry> entries, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(templateDirectory);
		var template = Path.Combine(templateDirectory, fileName);
		// Only copy the TOC. No sidecars are available to the writer as retention sources.
		File.Copy(samplePatch, template, overwrite: true);
		var removed = await scanner.ScanEntriesAsync(template, cancellationToken).ConfigureAwait(false);
		await new PatchArchiveWriter().WriteAsync(template, outputDirectory, [], entries, removed,
			preserveOriginalStream: false, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private static Dictionary<ulong, IasRigProfile> ReadProfiles(string companion, IReadOnlyList<IasEquipment> equipment)
	{
		var archives = equipment.Where(item => item.Archive != 0).Select(item => item.Archive.ToString("x16")).ToArray();
		var profiles = new Dictionary<ulong, IasRigProfile>();
		foreach (var path in Directory.EnumerateFiles(companion, "*.rigbin").Order(StringComparer.Ordinal))
		{
			var name = Path.GetFileName(path);
			if (!name.EndsWith(".consumers.rigbin", StringComparison.OrdinalIgnoreCase) &&
				!archives.Any(archive => name.EndsWith($".{archive}.rigbin", StringComparison.OrdinalIgnoreCase)) &&
				!(name.Split('.') is [_, var archivePart, "rigbin"] && archivePart.Length == 16 &&
					ulong.TryParse(archivePart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
				continue;
			using var stream = File.OpenRead(path);
			foreach (var (id, profile) in IasRigReader.Read(stream))
				if (!profiles.TryAdd(id, profile))
					throw new InvalidDataException($"IAS 骨骼资料重复定义 Unit {id:x16}。");
		}
		return profiles;
	}

	private static string ArchiveName(PatchTocEntry entry) => Path.GetFileNameWithoutExtension(entry.SourceFilePath).ToLowerInvariant();

	internal sealed record PackageMetadata(string Name, string? Description, string? IconSourcePath);

	internal static async Task<PackageMetadata> ReadPackageMetadataAsync(string sample, string main,
		string? preferredName, CancellationToken token)
	{
		var companion = Path.Combine(sample, "companion");
		var (companionName, companionDescription) = await ReadJsonSummaryAsync(Path.Combine(companion, "mod_group.json"), token).ConfigureAwait(false);
		var (mainName, mainDescription, mainIcon) = await ReadMainManifestAsync(main, token).ConfigureAwait(false);
		var name = FirstNonBlank(preferredName, companionName, mainName) ?? "IAS 角色";
		var description = FirstNonBlank(mainDescription, await ReadDescriptionFileAsync(main, token).ConfigureAwait(false), companionDescription);
		var iconSource = ResolveIconSource(main, companion, mainIcon);
		return new(name, description, iconSource);
	}

	private static async Task<(string? Name, string? Description)> ReadJsonSummaryAsync(string path, CancellationToken token)
	{
		if (!File.Exists(path)) return (null, null);
		try
		{
			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
			if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
			return (TryGetStringProperty(document.RootElement, "name"), TryGetStringProperty(document.RootElement, "description"));
		}
		catch (JsonException) { return (null, null); }
	}

	private static async Task<(string? Name, string? Description, string? IconPath)> ReadMainManifestAsync(string main, CancellationToken token)
	{
		var path = Path.Combine(main, "manifest.json");
		if (!File.Exists(path)) return (null, null, null);
		try
		{
			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
			if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
			var root = document.RootElement;
			return (
				TryGetStringProperty(root, "name"),
				TryGetStringProperty(root, "description"),
				FirstNonBlank(TryGetStringProperty(root, "iconPath"), TryGetStringProperty(root, "icon_path")));
		}
		catch (JsonException) { return (null, null, null); }
	}

	private static async Task<string?> ReadDescriptionFileAsync(string main, CancellationToken token)
	{
		var path = Path.Combine(main, "Description.txt");
		if (!File.Exists(path)) return null;
		var text = (await File.ReadAllTextAsync(path, token).ConfigureAwait(false)).Trim();
		return text.Length > 0 ? text : null;
	}

	private static string? ResolveIconSource(string main, string companion, string? manifestIconPath)
	{
		if (!string.IsNullOrWhiteSpace(manifestIconPath))
		{
			var candidate = Path.GetFullPath(Path.Combine(main, manifestIconPath.Replace('/', Path.DirectorySeparatorChar)));
			if (IsWithin(candidate, main) && File.Exists(candidate) && IsSupportedIconExtension(candidate))
				return candidate;
		}
		return FindStandardIconFile(companion) ?? FindStandardIconFile(main);
	}

	private static string? FindStandardIconFile(string directory)
	{
		if (!Directory.Exists(directory)) return null;
		foreach (var ext in SupportedIconExtensions)
		{
			var path = Path.Combine(directory, "icon" + ext);
			if (File.Exists(path)) return path;
		}
		return null;
	}

	private static readonly string[] SupportedIconExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

	private static bool IsSupportedIconExtension(string path) =>
		SupportedIconExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

	private static string? TryGetStringProperty(JsonElement element, string name)
	{
		foreach (var property in element.EnumerateObject())
			if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
				property.Value.ValueKind == JsonValueKind.String &&
				property.Value.GetString() is { } value &&
				!string.IsNullOrWhiteSpace(value))
				return value.Trim();
		return null;
	}

	private static string? FirstNonBlank(params string?[] values) =>
		values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

	private static PatchArchiveAdditionalEntry ToAddition(PatchEntryPayload payload) =>
		new(payload.Entry.AssetKey, payload.TocData, payload.StreamData, payload.GpuResourceData,
			payload.Entry.Unknown1, payload.Entry.Unknown2, payload.Entry.Unknown3, payload.Entry.Unknown4);

	private static bool IsWithin(string path, string directory)
	{
		var relative = Path.GetRelativePath(directory, path);
		return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	private sealed record InputPatch(string Path, ulong Archive, int Number, IReadOnlyList<PatchTocEntry> Entries);
	private sealed record PublishedUnit(InputPatch Patch, PatchTocEntry Entry, bool Hidden);
}
