using System.Buffers.Binary;
using System.Numerics;
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
	internal sealed record IndependentTranslation(float[] Avatar, float[] Model);
	internal sealed record IndependentPiece(IReadOnlyList<string> Bones, string Slot, string? Option, string? DisabledBy = null,
		IReadOnlyDictionary<string, IndependentTranslation>? Translations = null, IReadOnlyDictionary<string, double[]>? Grips = null,
		IReadOnlyList<ClothingRule>? PoseRules = null, IReadOnlyDictionary<string, LegCalibration>? Legs = null);
	internal sealed record ClothingDriver(string Bone, string? Reference, float[] Neutral, float[] Offset, float[] ReferenceOffset);
	internal sealed record ClothingRule(string Target, ClothingDriver Primary, ClothingDriver? Secondary, int Mode,
		float Influence, IReadOnlyList<IasPoseAxis> Axes, float[] Basis);
	internal sealed record LegCalibration(double LateralScale, double[] BendNormal);

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
		var hiddenOptions = plan.Groups.SelectMany(group => group.Options)
			.SelectMany(option => option.Routes.Where(route => route.Hidden).Select(route => (route.Slot.Target, option.Folder)))
			.ToDictionary(route => route.Target, route => route.Folder);
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
		foreach (var path in Directory.EnumerateFiles(companion, "*.canonical.rigbin"))
		{
			using var stream = File.OpenRead(path);
			foreach (var (id, profile) in IasRigReader.Read(stream))
				physicsProfiles.TryAdd(id, profile);
		}
		if (sharedPhysics is not null && !physicsProfiles.ContainsKey(sharedPhysics.UnitId))
			throw new InvalidDataException($"Missing IAS canonical rig for physics unit {sharedPhysics.UnitId:x16}.");
		var posePrograms = Directory.EnumerateFiles(companion, "*.hd2pose").ToDictionary(path => path,
			path => IasPoseReader.Read(File.ReadAllBytes(path), physicsProfiles));
		var poseByUnit = posePrograms.Values.ToDictionary(program => program.Unit);
		var sharedPose = posePrograms.Where(pair => pair.Key.EndsWith(".canonical.hd2pose", StringComparison.OrdinalIgnoreCase))
			.Select(pair => pair.Value).SingleOrDefault();
		var sourceTorso = plan.Base.First(route => route.Slot.Part == "Torso");
		var resolver = new GameDataPackageResolver(game);
		var avatar = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var names = profiles.Values.SelectMany(profile => profile.SourceNames).Distinct(StringComparer.Ordinal).ToArray();
		var targetRig = Hd2RigReader.FromTransformInfo(avatar, new UnitBoneNames(names.Select(boneHash).ToArray(), names));
		var shoulderTargets = IasArmPose.ComputeShoulderTargets(targetRig, profiles[sourceTorso.Published].Nodes);
		var legTargets = new Dictionary<string, HD2SourceModel.Vec3>();
		var candidates = IasIndependentBones.Collect(targetRig, IasIndependentBones.BodyRoots);
		var candidateIndices = candidates.Select(name => targetRig.Find(name)!.Index).ToHashSet();
		var candidateHashes = avatar.NameHashes.ToArray();
		foreach (var name in candidates)
			candidateHashes[targetRig.Find(name)!.Index] = boneHash(IasIndependentBones.Prefix + name);
		var skinRig = avatar with { NameHashes = candidateHashes };
		var packagedIndependentNames = new HashSet<string>(StringComparer.Ordinal);
		var independentPieces = new Dictionary<string, IndependentPiece>();
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
				await WriteRoutesAsync(option.Routes, Path.Combine(package, option.Folder), option.Folder).ConfigureAwait(false);

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
			await File.WriteAllTextAsync(Path.Combine(output, "arm-pose.json"), SerializeArmPose(shoulderTargets), cancellationToken).ConfigureAwait(false);
			// 同一份数据放进 Mod 根目录：开了「手臂姿态修正」时，管理器部署会读它（HD2ModCore JointPoseData.FileName）。
			await File.WriteAllTextAsync(Path.Combine(package, "joint-pose.json"),
				SerializeIndependentBones(candidates.Where(packagedIndependentNames.Contains).ToArray(),
					targetRig, IasIndependentBones.Apply(avatar, targetRig, profiles[sourceTorso.Published].Nodes,
						candidates.Where(packagedIndependentNames.Contains).ToArray(), boneHash), independentPieces),
				cancellationToken).ConfigureAwait(false);
			Directory.Move(package, mod);
			return new(mod, plan.Base.Count, plan.Groups.SelectMany(group => group.Options).Sum(option => option.Routes.Count),
				externalTextures, externalMaterials, shoulderTargets, legTargets);

			async Task WriteRoutesAsync(IReadOnlyList<IasRoute> routes, string directory, string? option = null)
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
						var program = poseByUnit.GetValueOrDefault(route.Published);
						if (program.Rules is null && !group.Key)
							program = sharedPose;
						var poseRules = program.Rules?.Where(rule => profile.Nodes.Any(node => node.Name == rule.Target)).ToArray();
						var physics = new IasPhysicsMigration(physicsSource, route.Published, profile,
							physicsSource is null ? null : physicsProfiles[physicsSource.UnitId], poseRules);
						if (!physics.HasBones)
							physics = null;
						var (lods, materials) = IasNativeMesh.Convert(sourceUnit.Model, profile, targetRig, skinRig, boneHash, physics);
						var weighted = lods.Values.SelectMany(mesh => mesh.Vertices)
							.SelectMany(vertex => vertex.Influences).Select(influence => influence.BoneHash).ToHashSet();
						physics?.KeepWeightedChains(weighted, avatar.NameHashes.Count);
						// The head profile describes just the head branch. Importing the other body
						// branches there would push the head's full physics rig past 256 nodes.
						var unitCandidates = group.Key
							? candidates.Where(name => profile.Nodes.Any(node => node.Name == name && (node.Flags & 1024) != 0)).ToArray()
							: candidates;
						var unitCandidateIndices = group.Key
							? unitCandidates.Select(name => targetRig.Find(name)!.Index).ToHashSet()
							: candidateIndices;
						var independentNames = IasIndependentBones.SelectWeighted(targetRig, skinRig, unitCandidates,
							unitCandidateIndices, weighted, (physics?.NativeParents(avatar.NameHashes.Count) ?? [])
								.Concat(profile.Nodes.Where(node => IasIndependentBones.ModelAttachments.Contains(node.Name))
									.Select(node => targetRig.Find(node.Name)!.Index)));
						packagedIndependentNames.UnionWith(independentNames);
						var unitSkinRig = IasIndependentBones.Apply(avatar, targetRig, profile.Nodes, independentNames, boneHash);
						if (independentNames.Count > 0)
						{
							var translating = profile.Nodes.Where(node => (node.Flags & 1) != 0)
								.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
							var translations = independentNames.Where(translating.Contains)
								.ToDictionary(name => IasIndependentBones.Prefix + name, name =>
								{
									var index = targetRig.Find(name)!.Index;
									return new IndependentTranslation(avatar.LocalTransforms[index].Position.ToArray(),
										unitSkinRig.LocalTransforms[index].Position.ToArray());
								});
							var pieceId = $"#ID[{key.FileId:x16}]";
							if (independentPieces.TryGetValue(pieceId, out var previous) && option is not null)
							{
								// A visible replacement (e.g. shoes/off) keeps the game Unit ID but has its own bind.
								independentPieces[pieceId] = previous with { DisabledBy = option };
								pieceId += "@" + option;
							}
							independentPieces.Add(pieceId, new(independentNames, route.Slot.Part, option,
								hiddenOptions.GetValueOrDefault(key.FileId), translations, ConvertGrips(profile, targetRig, unitSkinRig),
								poseRules is { Length: > 0 } ? ConvertPoseRules(poseRules, program.Basis, profile, targetRig, independentNames) : null,
								ConvertLegs(profile, targetRig, independentNames)));
						}
						var unitRig = physics?.Append(unitSkinRig, lods.Values, compact: group.Key,
							independentNames.Select(name => boneHash(IasIndependentBones.Prefix + name))) ?? unitSkinRig;
						var shell = IasNativeMesh.UseFourWeightStreams(donor.Model);
						if (group.Key)
						{
							// Preserve the donor's node indices while letting the expander replace
							// the bind in place, instead of keeping duplicate original head bones.
							var renamed = independentNames.ToDictionary(boneHash, name => boneHash(IasIndependentBones.Prefix + name));
							var hashes = shell.TransformNameHashes.Select(hash => renamed.GetValueOrDefault(hash, hash)).ToArray();
							shell = shell with { TransformNameHashes = hashes, TransformInfo = shell.TransformInfo with { NameHashes = hashes } };
						}
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
	internal static string SerializeIndependentBones(IReadOnlyList<string> bones, Hd2Rig? avatar = null,
		UnitTransformInfo? model = null, IReadOnlyDictionary<string, IndependentPiece>? pieces = null) => JsonSerializer.Serialize(new
	{
		independentBones = bones.Select(name => new { bone = IasIndependentBones.Prefix + name, source = name }),
		independentPieces = pieces?.ToDictionary(pair => pair.Key,
			pair => pair.Value.Bones.Select(name => IasIndependentBones.Prefix + name).ToArray()),
		independentPieceRoutes = pieces?.ToDictionary(pair => pair.Key,
			pair => new { pair.Value.Slot, pair.Value.Option, pair.Value.DisabledBy }),
		independentTranslations = pieces?.Where(pair => pair.Value.Translations != null)
			.ToDictionary(pair => pair.Key, pair => pair.Value.Translations),
		independentGrips = pieces?.Where(pair => pair.Value.Grips is { Count: > 0 })
			.ToDictionary(pair => pair.Key, pair => pair.Value.Grips),
		independentPoseRules = pieces?.Where(pair => pair.Value.PoseRules is { Count: > 0 })
			.ToDictionary(pair => pair.Key, pair => pair.Value.PoseRules),
		independentLegs = pieces?.Where(pair => pair.Value.Legs is { Count: > 0 })
			.ToDictionary(pair => pair.Key, pair => pair.Value.Legs),
		independentRest = avatar is null || model is null ? null : bones.Select(name =>
		{
			var bone = avatar.Find(name)!;
			var parent = avatar.Bones[bone.ParentIndex];
			return new
			{
				parent = parent.Name ?? throw new InvalidDataException($"Missing parent name for {name}."),
				avatar = Rotation(bone.WorldBind),
				// Use the authored unit bind axes. The mesh and its inverse binds use these same axes.
				model = Rotation(Hd2RigReader.FromHd2RowVector(model.Matrices[bone.Index].Values)),
				position = model.LocalTransforms[bone.Index].Position.ToArray(),
			};
		}),
	}, JsonOptions);

	internal static IReadOnlyDictionary<string, LegCalibration> ConvertLegs(IasRigProfile profile,
		Hd2Rig avatar, IReadOnlyList<string> independent)
	{
		var result = new Dictionary<string, LegCalibration>();
		var model = profile.Nodes.ToDictionary(node => node.Name, node => node.World, StringComparer.Ordinal);
		foreach (var side in new[] { "l", "r" })
		{
			var thigh = $"{side}_thigh";
			var knee = $"{side}_knee";
			var foot = $"{side}_foot";
			if (!new[] { "hips", thigh, knee, foot }.All(name => independent.Contains(name) && model.ContainsKey(name)))
				continue;
			var hipsRest = avatar.Find("hips")!.WorldBind;
			var thighRest = avatar.Find(thigh)!.WorldBind;
			var kneeRest = avatar.Find(knee)!.WorldOrigin;
			var ankleRest = avatar.Find(foot)!.WorldOrigin;
			var sourceLateral = hipsRest.Inverse().TransformPoint(ankleRest).Z;
			var modelLateral = model["hips"].Inverse().TransformPoint(model[foot].Origin).Z;
			var thighLocal = thighRest.Inverse();
			var restNormal = Vec3.Cross(thighLocal.TransformPoint(ankleRest), thighLocal.TransformPoint(kneeRest)).Normalized();
			if (Math.Abs(sourceLateral) > 1e-6 && restNormal.Length > 0.5)
				result.Add(side, new(modelLateral / sourceLateral, [restNormal.X, restNormal.Y, restNormal.Z]));
		}
		return result;
	}

	private static ClothingRule[] ConvertPoseRules(IReadOnlyList<IasPoseRule> rules, Mat4 authorBasis,
		IasRigProfile profile, Hd2Rig avatar, IReadOnlyList<string> independent)
	{
		var nodes = profile.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
		string Name(string name) => independent.Contains(name) ? IasIndependentBones.Prefix + name : name;
		Hd2RigBone Avatar(string name) => avatar.Find(name)
			?? throw new InvalidDataException($"Missing IAS clothing driver {name} in avatar rig.");
		float[] Offset(string name) => Rotation(Avatar(name).WorldBind.Inverse() * nodes[name].World);
		ClothingDriver ConvertDriver(IasPoseDriver driver) => new(Name(driver.Bone),
			driver.Reference is null ? null : Name(driver.Reference), driver.Neutral, Offset(driver.Bone),
			driver.Reference is null ? Rotation(authorBasis) : Offset(driver.Reference));
		return rules.Select(rule =>
		{
			var node = nodes[rule.Target];
			var parent = profile.ResolveSemanticNode(node.Parent);
			var basis = Avatar(profile.Nodes[parent].Name).WorldBind.Inverse() * node.World;
			return new ClothingRule(IasPhysicsMigration.PoseNodeName(rule.Target), ConvertDriver(rule.Primary),
				rule.Secondary is null ? null : ConvertDriver(rule.Secondary), rule.Mode, rule.Influence, rule.Axes, Rotation(basis));
		}).ToArray();
	}

	internal static IReadOnlyDictionary<string, double[]> ConvertGrips(IasRigProfile profile, Hd2Rig target,
		UnitTransformInfo model) => profile.Grips.ToDictionary(pair => pair.Key, pair =>
	{
		var hand = profile.Nodes.Single(node => node.Slot == pair.Value.Slot);
		var bind = Hd2RigReader.FromHd2RowVector(model.Matrices[target.Find(hand.Name)!.Index].Values);
		var grip = bind.Inverse() * hand.World * pair.Value.HandToGrip;
		return new[] { grip.M11, grip.M12, grip.M13, grip.M14, grip.M21, grip.M22, grip.M23, grip.M24,
			grip.M31, grip.M32, grip.M33, grip.M34 };
	});

	private static float[] Rotation(Mat4 world)
	{
		var row = new Matrix4x4(
			(float)world.M11, (float)world.M21, (float)world.M31, 0,
			(float)world.M12, (float)world.M22, (float)world.M32, 0,
			(float)world.M13, (float)world.M23, (float)world.M33, 0,
			0, 0, 0, 1);
		if (!Matrix4x4.Decompose(row, out _, out var rotation, out _))
			throw new InvalidDataException("Cannot extract IAS bind rotation.");
		return [rotation.X, rotation.Y, rotation.Z, rotation.W];
	}

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
