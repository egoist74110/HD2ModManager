using System.Buffers.Binary;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.IasConversion;

public sealed record IasDependency(ulong Id, string? GameArchive);
public sealed record IasDp8Result(string ModDirectory, int BaseUnits, int OptionUnits,
	IReadOnlyList<IasDependency> ExternalTextures, IReadOnlyList<IasDependency> ExternalMaterials,
	IasShoulderTargets ShoulderTargets, IReadOnlyDictionary<string, HD2SourceModel.Vec3> LegTargets);

public sealed class IasDp8Converter(Func<string, uint> boneHash, Func<ulong, bool, ulong> matchSlot)
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly PatchTocScanner scanner = new();
	private readonly PatchEntryPayloadReader payloadReader = new();

	public async Task<IasDp8Result> ConvertAsync(string sampleDirectory, string gameData, string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		var sample = Path.GetFullPath(sampleDirectory);
		var output = Path.GetFullPath(outputDirectory);
		var game = Path.GetFullPath(gameData);
		if (IsWithin(output, sample) || IsWithin(output, game))
			throw new ArgumentException("IAS output must be outside the sample and game data directories.", nameof(outputDirectory));
		var mod = Path.Combine(output, "mod");
		if (Directory.Exists(mod) || File.Exists(mod))
			throw new IOException($"IAS output already exists: {mod}");

		var main = Path.Combine(sample, "main");
		var companion = Path.Combine(sample, "companion", "HD2IndependentRig");
		var plan = IasDp8Routes.Read(companion);
		foreach (var slot in IasDp8Routes.Slots)
		{
			var target = matchSlot(slot.Source, slot.Helmet);
			if (target != slot.Target)
				throw new InvalidDataException($"SR-64 {slot.Part}/{slot.Body} resolved to DP-8 {target:x16}, expected {slot.Target:x16}.");
		}
		var bodyPath = FindSamplePatch(main, IasDp8Routes.SourceBody + ".patch_0");
		var helmetPath = FindSamplePatch(main, IasDp8Routes.SourceHelmet + ".patch_0");
		var materialsPath = FindSamplePatch(main, IasDp8Routes.Materials + ".patch_2");
		var inputs = new Dictionary<bool, IReadOnlyList<PatchTocEntry>>
		{
			[false] = await scanner.ScanEntriesAsync(bodyPath, cancellationToken).ConfigureAwait(false),
			[true] = await scanner.ScanEntriesAsync(helmetPath, cancellationToken).ConfigureAwait(false),
		};
		var sharedEntries = await scanner.ScanEntriesAsync(materialsPath, cancellationToken).ConfigureAwait(false);
		var resources = inputs.Values.SelectMany(entries => entries).Concat(sharedEntries)
			.Where(entry => entry.AssetKey.TypeId is MaterialDependencyResolver.MaterialTypeId or MaterialDependencyResolver.TextureTypeId)
			.ToDictionary(entry => entry.AssetKey);

		var profiles = ReadProfiles(companion);
		var physicsSources = Directory.EnumerateFiles(companion, "*.hd2phys")
			.Select(path => IasPhysicsReader.Read(File.ReadAllBytes(path))).ToDictionary(source => source.UnitId);
		var sharedPhysics = physicsSources.Values.SingleOrDefault(source => !profiles.ContainsKey(source.UnitId));
		var physicsProfiles = new Dictionary<ulong, IasRigProfile>(profiles);
		if (sharedPhysics is not null)
		{
			foreach (var path in Directory.EnumerateFiles(companion, "*.canonical.rigbin"))
			{
				using var stream = File.OpenRead(path);
				if (IasRigReader.Read(stream).TryGetValue(sharedPhysics.UnitId, out var canonical))
					physicsProfiles.Add(sharedPhysics.UnitId, canonical);
			}
			if (!physicsProfiles.ContainsKey(sharedPhysics.UnitId))
				throw new InvalidDataException($"Missing IAS canonical rig for physics unit {sharedPhysics.UnitId:x16}.");
		}
		var sourceTorso = plan.Base.First(route => route.Slot.Part == "Torso");
		var sourceHead = profiles[sourceTorso.Published].Nodes.Single(node => node.Name == "head").World.Origin;
		var resolver = new GameDataPackageResolver(game);
		var avatar = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var names = profiles.Values.SelectMany(profile => profile.SourceNames).Distinct(StringComparer.Ordinal).ToArray();
		var targetRig = Hd2RigReader.FromTransformInfo(avatar, new UnitBoneNames(names.Select(boneHash).ToArray(), names));
		var shoulderTargets = IasArmPose.ComputeShoulderTargets(targetRig, profiles[sourceTorso.Published].Nodes);
		var legTargets = IasArmPose.ComputeLegTargets(targetRig, profiles[sourceTorso.Published].Nodes);
		// 手臂与腿两条链互不重叠：合成一张逐骨平移表。
		// 独立骨（实验 2）：两条手臂整棵子树（肩以下，含手指）不做平移，顶点留在模型原位；
		// 按 Unit 的最终蒙皮权重选改名骨；腿照旧。
		var arms = IasIndependentBones.ArmRoots.SelectMany(root => IasIndependentBones.Subtree(targetRig, root)).ToHashSet();
		var independentBones = IasIndependentBones.Collect(targetRig, IasIndependentBones.ArmRoots);
		var packagedIndependentNames = new HashSet<string>(StringComparer.Ordinal);
		var boneShifts = IasArmPose.ComputeBoneShifts(targetRig, profiles[sourceTorso.Published].Nodes)
			.Concat(IasArmPose.ComputeLegShifts(targetRig, profiles[sourceTorso.Published].Nodes))
			.Where(pair => !arms.Contains(pair.Key))
			.ToDictionary(pair => pair.Key, pair => pair.Value);
		var skinRig = IasIndependentBones.Apply(avatar, targetRig, profiles[sourceTorso.Published].Nodes,
			independentBones, boneHash);
		var vanilla = new GameDataUnitMeshReader(resolver);
		var units = new Dictionary<ulong, GameDataUnitMesh>();
		var usedMaterials = new HashSet<ulong>();
		var staging = Path.Combine(output, $".p1-{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);
		try
		{
			var package = Path.Combine(staging, "mod");
			var template = Path.Combine(staging, "template");
			await WriteRoutesAsync(plan.Base, package).ConfigureAwait(false);
			foreach (var option in plan.Groups.SelectMany(group => group.Options))
				await WriteRoutesAsync(option.Routes, Path.Combine(package, option.Folder)).ConfigureAwait(false);

			var (dependencies, externalTextures, externalMaterials) = await ResolveMaterialsAsync(resources, usedMaterials, resolver, cancellationToken).ConfigureAwait(false);
			await WritePatchAsync(materialsPath, IasDp8Routes.Materials + ".patch_2", template, package, dependencies, cancellationToken).ConfigureAwait(false);
			await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), SerializeManifest(plan), cancellationToken).ConfigureAwait(false);
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
			await File.WriteAllTextAsync(Path.Combine(output, "arm-pose.json"), SerializeArmPose(shoulderTargets, legTargets), cancellationToken).ConfigureAwait(false);
			// 同一份数据放进 Mod 根目录：开了「手臂姿态修正」时，管理器部署会读它（HD2ModCore JointPoseData.FileName）。
			await File.WriteAllTextAsync(Path.Combine(package, "joint-pose.json"),
				SerializeIndependentBones(independentBones.Where(packagedIndependentNames.Contains).ToArray()), cancellationToken).ConfigureAwait(false);
			Directory.Move(package, mod);
			return new(mod, plan.Base.Count, plan.Groups.SelectMany(group => group.Options).Sum(option => option.Routes.Count),
				externalTextures, externalMaterials, shoulderTargets, legTargets);

			async Task WriteRoutesAsync(IReadOnlyList<IasRoute> routes, string directory)
			{
				foreach (var group in routes.GroupBy(route => route.Slot.Helmet))
				{
					var archive = group.Key ? IasDp8Routes.TargetHelmet : IasDp8Routes.TargetBody;
					var entries = new List<PatchArchiveAdditionalEntry>();
					var facts = new List<SourceJiggleBoneFact>();
					var writtenPhysics = new HashSet<ulong>();
					foreach (var route in group)
					{
						var key = new AssetKey(PatchUnitMeshReader.UnitTypeId, route.Slot.Target);
						if (!units.TryGetValue(route.Slot.Target, out var donor))
						{
							donor = await vanilla.ReadAsync(archive, key, cancellationToken: cancellationToken).ConfigureAwait(false);
							units.Add(route.Slot.Target, donor);
						}
						var source = inputs[group.Key].Single(entry => entry.AssetKey == new AssetKey(PatchUnitMeshReader.UnitTypeId, route.Published));
						var entry = donor.Payload.Entry;
						if (route.Hidden)
						{
							var hidden = new CanonicalHiddenUnitBuilder().Build(donor, avatar, minifyCullingMeshes: true).Entry;
							entries.Add(new(key, hidden.EffectiveTocData, [], hidden.EffectiveGpuData,
								entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
							continue;
						}
						var sourceUnit = await new PatchUnitMeshReader().ReadCanonicalSourceAsync(source, inputs[group.Key],
							cancellationToken: cancellationToken).ConfigureAwait(false);
						var profile = profiles[route.Published];
						var physicsSource = physicsSources.GetValueOrDefault(route.Published) ?? (group.Key ? null : sharedPhysics);
						var physics = physicsSource is null ? null
							: new IasPhysicsMigration(physicsSource, route.Published, profile, physicsProfiles[physicsSource.UnitId]);
						if (physics?.HasBones == false)
							physics = null;
						var (lods, materials) = IasNativeMesh.Convert(sourceUnit.Model, profile, targetRig, skinRig,
							sourceHead, boneHash, refitArms: true, boneShifts, physics);
						var weighted = lods.Values.SelectMany(mesh => mesh.Vertices)
							.SelectMany(vertex => vertex.Influences).Select(influence => influence.BoneHash).ToHashSet();
						physics?.KeepWeightedChains(weighted, avatar.NameHashes.Count);
						var independentNames = IasIndependentBones.SelectWeighted(targetRig, skinRig, independentBones,
							arms, weighted, physics?.NativeParents(avatar.NameHashes.Count));
						packagedIndependentNames.UnionWith(independentNames);
						var unitSkinRig = IasIndependentBones.Apply(avatar, targetRig, profiles[sourceTorso.Published].Nodes,
							independentNames, boneHash);
						var unitRig = physics?.Append(unitSkinRig, lods.Values, compact: group.Key) ?? unitSkinRig;
						var shell = IasNativeMesh.UseFourWeightStreams(donor.Model);
						var result = new UnitAuthor().Author(new UnitAuthorRequest(shell, donor.Payload.TocData, unitRig,
							lods[0], donor.Model.CustomizationInfo, key.FileId, materials), lods);
						if (!result.IsValid || result.Model!.TransformNameHashes.Count > 256)
							throw new InvalidDataException($"IAS Unit {route.Slot.Part}/{route.Slot.Body} -> {key.FileId:x16} ({result.Model?.TransformNameHashes.Count} nodes): {string.Join("; ", result.Diagnostics)}");
						if (physics is not null && writtenPhysics.Add(route.Published))
							physics.AddFacts(facts, targetRig, independentNames.ToHashSet(StringComparer.Ordinal));
						usedMaterials.UnionWith(materials.Values);
						entries.Add(new(key, result.TocData!, [], result.GpuData!, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
					}
					var inputPath = group.Key ? helmetPath : bodyPath;
					await WritePatchAsync(inputPath, archive + ".patch_0", template, directory, entries, cancellationToken).ConfigureAwait(false);
					if (facts.Count > 0)
					{
						var rigDirectory = Path.Combine(directory, "physics", archive);
						Directory.CreateDirectory(rigDirectory);
						var rigPath = PhysBoneRigSidecar.Write(rigDirectory, facts, null)!;
						var rig = await File.ReadAllBytesAsync(rigPath, cancellationToken).ConfigureAwait(false);
						await PhysBoneContainerEmbedder.EmbedNewPatchAsync(Path.Combine(directory, archive + ".patch_0"),
							rig, cancellationToken).ConfigureAwait(false);
					}
				}
			}
		}
		finally
		{
			Directory.Delete(staging, recursive: true);
		}
	}

	/// <summary>运行时手臂/腿部修正的参数：<c>armShoulderTarget</c> 原样就是 Lua 选项的形状。</summary>
	internal static string SerializeArmPose(IasShoulderTargets targets,
		IReadOnlyDictionary<string, HD2SourceModel.Vec3>? legTargets = null) => JsonSerializer.Serialize(new
	{
		armShoulderTarget = new { l = ToArray(targets.Left), r = ToArray(targets.Right) },
		// 骨名 → 父骨局部位置；按 thigh、knee、foot、ball 的顺序写回。
		legJointTarget = legTargets?.ToDictionary(pair => pair.Key, pair => ToArray(pair.Value)),
		shoulderShiftMeters = new { l = ToArray(targets.LeftShift), r = ToArray(targets.RightShift) },
	}, JsonOptions);

	/// <summary>
	/// 独立骨的运行时参数：改名后的骨 → avatar 上抄旋转的源骨，父骨在前。
	/// 部署时由 HD2ModCore <c>JointPoseData</c> 转成 Lua 选项 <c>independentBones</c>。
	/// </summary>
	internal static string SerializeIndependentBones(IReadOnlyList<string> bones) => JsonSerializer.Serialize(new
	{
		independentBones = bones.Select(name => new { bone = IasIndependentBones.Prefix + name, source = name }),
	}, JsonOptions);

	private static double[] ToArray(HD2SourceModel.Vec3 value) =>
		[Math.Round(value.X, 5), Math.Round(value.Y, 5), Math.Round(value.Z, 5)];

	internal static string SerializeManifest(IasPackagePlan plan) => JsonSerializer.Serialize(new
	{
		name = "Milfy · DP-8 Mountain-Scaled",
		description = "本体。外套与鞋子默认穿着，展开可切换脱外套／脱鞋。",
		options = plan.Groups.Select(group =>
		{
			var primary = group.Options[0];
			var subs = new List<object> { new { name = $"{primary.Name}（默认）" } };
			foreach (var secondary in group.Options.Skip(1))
				subs.Add(new { name = secondary.Name, include = new[] { secondary.Folder } });
			return new
			{
				name = group.Name,
				include = new[] { primary.Folder },
				subOptions = subs,
			};
		}),
	}, JsonOptions);

	private async Task<(IReadOnlyList<PatchArchiveAdditionalEntry>, IReadOnlyList<IasDependency>, IReadOnlyList<IasDependency>)> ResolveMaterialsAsync(
		IReadOnlyDictionary<AssetKey, PatchTocEntry> resources, IReadOnlySet<ulong> usedMaterials,
		GameDataPackageResolver resolver, CancellationToken cancellationToken)
	{
		var references = new StingrayMaterialReferenceReader();
		var textures = new HashSet<ulong>();
		var entries = new List<PatchArchiveAdditionalEntry>();
		var external = new HashSet<AssetKey>();
		var pending = new Queue<ulong>(usedMaterials.Order());
		var visited = new HashSet<ulong>();
		while (pending.TryDequeue(out var id))
		{
			if (!visited.Add(id))
				continue;
			var key = new AssetKey(MaterialDependencyResolver.MaterialTypeId, id);
			if (!resources.TryGetValue(key, out var source))
			{
				if (usedMaterials.Contains(id))
					throw new InvalidDataException($"Visible IAS material {id:x16} is missing.");
				external.Add(key);
				continue;
			}
			var payload = await payloadReader.ReadPayloadAsync(source, cancellationToken).ConfigureAwait(false);
			textures.UnionWith(references.ReadTextureIds(payload.TocData).Where(value => value != 0));
			var parent = BinaryPrimitives.ReadUInt64LittleEndian(payload.TocData.AsSpan(24, 8));
			if (parent != 0 && parent != id)
				pending.Enqueue(parent);
			entries.Add(ToAddition(payload));
		}
		foreach (var id in textures.Order())
		{
			var key = new AssetKey(MaterialDependencyResolver.TextureTypeId, id);
			if (resources.TryGetValue(key, out var source))
				entries.Add(ToAddition(await payloadReader.ReadPayloadAsync(source, cancellationToken).ConfigureAwait(false)));
			else
				external.Add(key);
		}
		var found = new Dictionary<AssetKey, string>();
		foreach (var archive in await resolver.GetPackageNamesAsync(cancellationToken).ConfigureAwait(false))
		{
			if (found.Count == external.Count)
				break;
			var toc = await resolver.GetPackageTocAsync(archive, cancellationToken).ConfigureAwait(false);
			if (toc is null)
				continue;
			var candidates = scanner.ScanEntries(toc.Data, archive, toc.UsesSlimEntryOffset);
			foreach (var entry in candidates)
			{
				var key = entry.AssetKey;
				if (external.Contains(key))
					found.TryAdd(key, archive);
			}
		}
		IasDependency[] ExternalOf(ulong type) => external.Where(key => key.TypeId == type).OrderBy(key => key.FileId)
			.Select(key => new IasDependency(key.FileId, found.GetValueOrDefault(key))).ToArray();
		return (entries, ExternalOf(MaterialDependencyResolver.TextureTypeId), ExternalOf(MaterialDependencyResolver.MaterialTypeId));
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

	private static Dictionary<ulong, IasRigProfile> ReadProfiles(string companion)
	{
		var paths = Directory.EnumerateFiles(companion, "*.consumers.rigbin")
			.Concat(Directory.EnumerateFiles(companion, $"*.{IasDp8Routes.SourceHelmet}.rigbin"));
		var profiles = new Dictionary<ulong, IasRigProfile>();
		foreach (var path in paths)
		{
			using var stream = File.OpenRead(path);
			foreach (var (id, profile) in IasRigReader.Read(stream))
				profiles.Add(id, profile);
		}
		return profiles;
	}

	private static PatchArchiveAdditionalEntry ToAddition(PatchEntryPayload payload) =>
		new(payload.Entry.AssetKey, payload.TocData, payload.StreamData, payload.GpuResourceData,
			payload.Entry.Unknown1, payload.Entry.Unknown2, payload.Entry.Unknown3, payload.Entry.Unknown4);

	private static bool IsWithin(string path, string directory)
	{
		var relative = Path.GetRelativePath(directory, path);
		return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static string FindSamplePatch(string main, string name) =>
		Directory.EnumerateFiles(main, name, SearchOption.AllDirectories).Single();
}
