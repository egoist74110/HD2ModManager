using System.Globalization;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

using static IasModelCli.IasAssets;

namespace IasModelCli;

public static class VpkExtractor
{
	private static readonly IReadOnlyDictionary<string, BodyRegion> SlotToRegion = new Dictionary<string, BodyRegion>(StringComparer.Ordinal)
	{
		["Head"] = BodyRegion.Head,
		["Torso"] = BodyRegion.Chest,
		["Hips"] = BodyRegion.Hips,
		["LeftArm"] = BodyRegion.LeftArm,
		["RightArm"] = BodyRegion.RightArm,
		["LeftLeg"] = BodyRegion.LeftLeg,
		["RightLeg"] = BodyRegion.RightLeg,
	};

	public static async Task RunAsync(IReadOnlyDictionary<string, string> opts, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var vpkPath = opts["--vpk"];
		var gameDataDir = opts["--game-data"];
		var boneHashPath = opts["--bone-hash"];
		var partCatalogPath = opts["--part-catalog"];
		var bindingCatalogPath = opts["--binding-catalog"];
		var bodyArchive = opts["--body-archive"];
		var helmetArchive = opts["--helmet-archive"];
		var l4d2Root = opts.GetValueOrDefault("--l4d2-root");
		var outDir = opts["--out"];

		var partSlots = ReadPartCatalog(partCatalogPath);
		var bindingTargets = ReadBindingTargets(bindingCatalogPath);
		if (!bindingTargets.TryGetValue(bodyArchive, out var bodyTarget) || bodyTarget.Domain != "body")
			throw new ArgumentException($"Unsupported IAS body archive: {bodyArchive}");
		if (!bindingTargets.TryGetValue(helmetArchive, out var helmetTarget) || helmetTarget.Domain != "helmet")
			throw new ArgumentException($"Unsupported IAS helmet archive: {helmetArchive}");

		Directory.CreateDirectory(outDir);
		Console.WriteLine($"[IasModelCli] Loading stock Avatar rig from {gameDataDir}...");

		var resolver = new GameDataPackageResolver(gameDataDir);
		var tocScanner = new PatchTocScanner();
		var unitMeshReader = new GameDataUnitMeshReader(resolver, tocScanner);
		var avatarReader = new CanonicalAvatarRigReader(resolver, tocScanner);

		var stockAvatarTransform = await avatarReader.ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var boneNames = SourceRetargetPipeline.ReadBoneHash(boneHashPath);
		var nameByHash = BuildHashLookup(boneNames);
		var rig = Hd2RigReader.FromTransformInfo(stockAvatarTransform, boneNames);

		Console.WriteLine($"[IasModelCli] Loading VPK {vpkPath}...");
		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, opts.GetValueOrDefault("--model-stem"));

		var customSolution = CustomProportionRetarget.Solve(inputs, rig);
		var iasRetarget = customSolution.Retarget;

		// Apply BatchTool palm-frame solve_grip_bones (rigging.py:766-860) on custom ShippedRest
		var customWorldByIndex = new Dictionary<int, Mat4>(customSolution.CustomWorldByIndex);
		SolveGripBones(rig, customWorldByIndex);

		// Rebuild AvatarTransformInfo from customWorldByIndex and assert strict FK == World agreement across ALL bones
		var customAvatarTransform = BuildCustomTransformInfo(stockAvatarTransform, customWorldByIndex);
		UnitTransformInfoAppender.AssertLocalAndWorldAgree(customAvatarTransform, fromIndex: 0, tolerance: 1e-3f);

		foreach (var extra in iasRetarget.ExtraBones)
			nameByHash[extra.NameHash] = extra.Name;

		// Export stock archives (Body + Helmet) for Python hd2_system resolve_stock
		var stockBodyDir = Path.Combine(outDir, "stock_body");
		var stockHelmetDir = Path.Combine(outDir, "stock_helmet");
		var stockBodyManifest = await DumpStockArchiveAsync(resolver, tocScanner, bodyArchive, stockBodyDir, cancellationToken).ConfigureAwait(false);
		var stockHelmetManifest = await DumpStockArchiveAsync(resolver, tocScanner, helmetArchive, stockHelmetDir, cancellationToken).ConfigureAwait(false);

		// Convert VPK Materials & BC7 Textures using VpkPatchAssembler + SourceMaterialConverter
		Console.WriteLine("[IasModelCli] Synthesizing HD2 materials & BC7 textures from VPK...");
		var texconvPath = Bc7Encoder.Locate(opts.GetValueOrDefault("--texconv") ?? Environment.GetEnvironmentVariable("HD2_TEXCONV"))
			?? throw new InvalidOperationException("Could not locate texconv.exe via Bc7Encoder.Locate.");
		var templates = Hd2MaterialTemplateLibrary.Locate()
			?? throw new InvalidOperationException("Could not locate Hd2MaterialTemplateLibrary.");
		var pak01 = l4d2Root is null ? null : Path.Combine(l4d2Root, "pak01_dir.vpk");
		var matSources = pak01 is not null && File.Exists(pak01) ? new[] { pak01, vpkPath } : new[] { vpkPath };
		var matSource = SourceMaterialSource.Open(matSources);
		var matConverter = new SourceMaterialConverter(matSource, new Bc7Encoder(texconvPath), templates);

		var matOutDir = Path.Combine(outDir, "materials_patch");
		Directory.CreateDirectory(matOutDir);
		var assembler = new VpkPatchAssembler();
		var matPkgResult = await assembler.AssembleAsync(
			plans: [],
			retarget: null,
			geometry: inputs.Geometry,
			avatarTransformInfo: null,
			headerTemplateTocData: stockBodyManifest.TocBytes,
			outputDirectory: matOutDir,
			patchFileName: null,
			modelKey: "ias-model-cli-materials",
			cancellationToken: cancellationToken,
			sourceMaterialNames: inputs.Model.MaterialPaths,
			materialConverter: matConverter,
			materialPackFileName: "9ba626afa44a3aa3.patch_0",
			materialScope: matSource.ScopedTo(vpkPath)).ConfigureAwait(false);

		var matByStem = matPkgResult.Materials
			.Where(m => m.MaterialId != 0)
			.GroupBy(m => Path.GetFileNameWithoutExtension(m.SourceMaterialName.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First().MaterialId, StringComparer.OrdinalIgnoreCase);

		var materialIdsBySourceMaterial = new Dictionary<int, ulong>();
		for (var mIdx = 0; mIdx < inputs.Model.MaterialPaths.Count; mIdx++)
		{
			var stem = Path.GetFileNameWithoutExtension(inputs.Model.MaterialPaths[mIdx].Replace('\\', '/'));
			if (matByStem.TryGetValue(stem, out var matId))
			{
				materialIdsBySourceMaterial[mIdx] = matId;
			}
		}

		// Reproject eye UVs identically to VpkPatchAssembler
		var (geometry, _) = EyeUv.Reproject(inputs.Geometry, inputs.Model.MaterialPaths, matConverter.ResolveEyeColorSource, null);

		// Segment VPK and merge default bodygroup triangles per BodyRegion (1 clean MeshGroup per PartSlot)
		var splitResult = SourceSegmentation.Split(inputs.Model, geometry, new BodyRegionResolver(rig), iasRetarget.Resolved);
		var regionGroups = BuildMergedRegionGroups(splitResult.Groups);

		var extraBaseCount = customAvatarTransform.Matrices.Count;
		var fullCustomTransform = UnitTransformInfoAppender.Append(customAvatarTransform, iasRetarget.ExtraBones)!;
		UnitTransformInfoAppender.AssertLocalAndWorldAgree(fullCustomTransform, extraBaseCount);

		var authoredUnitsDir = Path.Combine(outDir, "units_authored");
		var pointUnitsDir = Path.Combine(outDir, "units_point");
		Directory.CreateDirectory(authoredUnitsDir);
		Directory.CreateDirectory(pointUnitsDir);

		var bodyUnitsMeta = await BuildDomainUnitsAsync(
			domain: "BODY",
			archiveName: bodyArchive,
			stockUnitIds: stockBodyManifest.UnitIds,
			ownedEquipmentUnits: bodyTarget.OwnedUnits,
			partSlots: partSlots,
			regionGroups: regionGroups,
			geometry: geometry,
			iasRetarget: iasRetarget,
			customAvatarTransform: customAvatarTransform,
			fullCustomTransform: fullCustomTransform,
			extraBaseCount: extraBaseCount,
			materialIdsBySourceMaterial: materialIdsBySourceMaterial,
			unitMeshReader: unitMeshReader,
			nameByHash: nameByHash,
			authoredDir: authoredUnitsDir,
			pointDir: pointUnitsDir,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		var helmetUnitsMeta = await BuildDomainUnitsAsync(
			domain: "HELMET",
			archiveName: helmetArchive,
			stockUnitIds: stockHelmetManifest.UnitIds,
			ownedEquipmentUnits: helmetTarget.OwnedUnits,
			partSlots: partSlots,
			regionGroups: regionGroups,
			geometry: geometry,
			iasRetarget: iasRetarget,
			customAvatarTransform: customAvatarTransform,
			fullCustomTransform: fullCustomTransform,
			extraBaseCount: extraBaseCount,
			materialIdsBySourceMaterial: materialIdsBySourceMaterial,
			unitMeshReader: unitMeshReader,
			nameByHash: nameByHash,
			authoredDir: authoredUnitsDir,
			pointDir: pointUnitsDir,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		// Build authoring_project.json (HD2PhysBoneAuthoringProject1) for unmodified unit_rig_profiles.build_rig_document
		var authoringProject = BuildAuthoringProject(stockAvatarTransform, fullCustomTransform, nameByHash);
		await File.WriteAllTextAsync(
			Path.Combine(outDir, "authoring_project.json"),
			JsonSerializer.Serialize(authoringProject, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

		var summaryManifest = new
		{
			vpkPath,
			modelStem = inputs.ModelStem,
			bodyArchive,
			helmetArchive,
			materialPackTocPath = matPkgResult.MaterialPackPath,
			materialCount = matPkgResult.MaterialCount,
			textureCount = matPkgResult.TextureCount,
			body = bodyUnitsMeta,
			helmet = helmetUnitsMeta,
		};
		await File.WriteAllTextAsync(
			Path.Combine(outDir, "authored_manifest.json"),
			JsonSerializer.Serialize(summaryManifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

		Console.WriteLine($"[IasModelCli] Done! Wrote raw IAS inputs to {outDir}");
	}

	private static void SolveGripBones(Hd2Rig stockRig, Dictionary<int, Mat4> customWorld)
	{
		var byName = stockRig.Bones
			.Where(b => b.HasName)
			.ToDictionary(b => b.Name!, b => b, StringComparer.Ordinal);

		(Vec3 Origin, Mat3 Frame, double ForwardLen, double WidthLen) PalmFrame(Func<int, Mat4> worldOf, string side)
		{
			var hand = byName[$"{side}_hand"];
			var landmarks = new[]
			{
				byName[$"{side}_index_finger1"],
				byName[$"{side}_middle_finger1"],
				byName[$"{side}_ring_finger1"],
				byName[$"{side}_pinky_finger1"],
			};
			var origin = worldOf(hand.Index).Origin;
			var center = Vec3.Zero;
			foreach (var lm in landmarks)
				center += worldOf(lm.Index).Origin;
			center /= landmarks.Length;

			var forward = center - origin;
			var width = worldOf(landmarks[0].Index).Origin - worldOf(landmarks[^1].Index).Origin;
			var fLen = forward.Length;
			var wLen = width.Length;
			if (fLen <= 1e-6 || wLen <= 1e-6)
				throw new InvalidOperationException($"Degenerate palm landmarks on {side}_hand.");

			forward = forward.Normalized();
			width -= forward * Vec3.Dot(width, forward);
			width = width.Normalized();
			var normal = Vec3.Cross(forward, width).Normalized();
			width = Vec3.Cross(normal, forward).Normalized();

			// Columns are (forward, width, normal), matching Matrix((forward, width, normal)).transposed() in rigging.py:793
			var rot = new Mat3(
				forward.X, width.X, normal.X,
				forward.Y, width.Y, normal.Y,
				forward.Z, width.Z, normal.Z);
			return (origin, rot, fLen, wLen);
		}

		Mat4? desiredAttachR = null;

		foreach (var side in new[] { "l", "r" })
		{
			var attachName = $"attach_hand_{side}";
			if (!byName.TryGetValue(attachName, out var attachBone))
				continue;

			var (refOrigin, refFrame, refForward, refWidth) = PalmFrame(idx => stockRig.Bones[idx].WorldBind, side);
			var (dstOrigin, dstFrame, dstForward, dstWidth) = PalmFrame(idx => customWorld[idx], side);

			var refAttachPos = stockRig.Bones[attachBone.Index].WorldBind.Origin;
			var refOffset = refFrame.Transposed().Transform(refAttachPos - refOrigin);
			var scaleX = dstForward / refForward;
			var scaleY = dstWidth / refWidth;
			var scaleZ = Math.Sqrt(scaleX * scaleY);
			var scaledOffset = new Vec3(refOffset.X * scaleX, refOffset.Y * scaleY, refOffset.Z * scaleZ);
			var newHead = dstOrigin + dstFrame.Transform(scaledOffset);

			var curMat = customWorld[attachBone.Index];
			var delta = newHead - curMat.Origin;
			var updated = RetargetMath.Trans(delta) * curMat;
			customWorld[attachBone.Index] = updated;
			if (side == "r")
				desiredAttachR = updated;
		}

		if (desiredAttachR is { } attachR
			&& byName.TryGetValue("attach_hand_r", out var refAttachRBone)
			&& byName.TryGetValue("weapon_aim", out var aimBone))
		{
			var aimLocal = stockRig.Bones[refAttachRBone.Index].WorldBind.Inverse() * stockRig.Bones[aimBone.Index].WorldBind;
			var aimFull = attachR * aimLocal;
			var curAim = customWorld[aimBone.Index];
			var deltaAim = aimFull.Origin - curAim.Origin;
			customWorld[aimBone.Index] = RetargetMath.Trans(deltaAim) * curAim;
		}

	}

	private static UnitTransformInfo BuildCustomTransformInfo(
		UnitTransformInfo stockAvatar,
		IReadOnlyDictionary<int, Mat4> customWorldByIndex)
	{
		var count = stockAvatar.Matrices.Count;
		var locals = new List<UnitLocalTransform>(count);
		var matrices = new List<UnitTransformMatrix>(count);
		var entries = new List<UnitTransformEntry>(stockAvatar.Entries);
		var hashes = new List<uint>(stockAvatar.NameHashes);

		for (var i = 0; i < count; i++)
		{
			var worldCol = customWorldByIndex[i];
			var parentIdx = entries[i].ParentIndex;
			var isRoot = i == 0 || parentIdx == i || entries[i].Increment == 0;
			var localCol = isRoot ? worldCol : customWorldByIndex[parentIdx].Inverse() * worldCol;

			locals.Add(new UnitLocalTransform(
				[
					(float)localCol.M11, (float)localCol.M21, (float)localCol.M31,
					(float)localCol.M12, (float)localCol.M22, (float)localCol.M32,
					(float)localCol.M13, (float)localCol.M23, (float)localCol.M33,
				],
				[(float)localCol.M14, (float)localCol.M24, (float)localCol.M34],
				[1f, 1f, 1f],
				stockAvatar.LocalTransforms[i].Padding));

			matrices.Add(new UnitTransformMatrix(
			[
				(float)worldCol.M11, (float)worldCol.M21, (float)worldCol.M31, 0f,
				(float)worldCol.M12, (float)worldCol.M22, (float)worldCol.M32, 0f,
				(float)worldCol.M13, (float)worldCol.M23, (float)worldCol.M33, 0f,
				(float)worldCol.M14, (float)worldCol.M24, (float)worldCol.M34, 1f,
			]));
		}

		return new UnitTransformInfo(
			stockAvatar.Reserved0,
			stockAvatar.Reserved1,
			stockAvatar.Reserved2,
			locals,
			matrices,
			entries,
			hashes);
	}

	private static Dictionary<BodyRegion, MeshGroup> BuildMergedRegionGroups(
		IReadOnlyList<MeshGroup> splitGroups)
	{
		var byRegion = new Dictionary<BodyRegion, (List<int> Tris, SortedSet<int> Verts)>();
		foreach (var group in splitGroups)
		{
			// For mutually-exclusive L4D2 bodygroup components, keep default model index 0
			if (group.Kind == MeshGroupKind.Component && group.SourceVariantCount > 1 && group.SourceModelIndex > 0)
				continue;
			if (!byRegion.TryGetValue(group.Region, out var bucket))
			{
				bucket = ([], new SortedSet<int>());
				byRegion[group.Region] = bucket;
			}
			bucket.Tris.AddRange(group.TriangleIndices);
			foreach (var vIdx in group.VertexIndices)
				bucket.Verts.Add(vIdx);
		}

		var result = new Dictionary<BodyRegion, MeshGroup>();
		foreach (var (region, bucket) in byRegion)
		{
			var distinctTris = bucket.Tris.Distinct().OrderBy(t => t).ToArray();
			if (distinctTris.Length == 0)
				continue;
			result[region] = new MeshGroup(
				Name: region.ToString(),
				Kind: MeshGroupKind.BodyPart,
				Region: region,
				SourceBodyPartIndex: 0,
				SourceVariantCount: 1,
				TriangleIndices: distinctTris,
				VertexIndices: bucket.Verts.ToArray());
		}

		return result;
	}

	private static async Task<object> BuildDomainUnitsAsync(
		string domain,
		string archiveName,
		IReadOnlySet<ulong> stockUnitIds,
		IReadOnlySet<ulong> ownedEquipmentUnits,
		IReadOnlyDictionary<string, HashSet<ulong>> partSlots,
		IReadOnlyDictionary<BodyRegion, MeshGroup> regionGroups,
		SourceGeometry geometry,
		RetargetResult iasRetarget,
		UnitTransformInfo customAvatarTransform,
		UnitTransformInfo fullCustomTransform,
		int extraBaseCount,
		IReadOnlyDictionary<int, ulong> materialIdsBySourceMaterial,
		GameDataUnitMeshReader unitMeshReader,
		IReadOnlyDictionary<uint, string> nameByHash,
		string authoredDir,
		string pointDir,
		CancellationToken cancellationToken)
	{
		var targetSlots = domain == "HELMET"
			? new[] { "Head" }
			: new[] { "Hips", "LeftArm", "RightArm", "Torso", "LeftLeg", "RightLeg" };

		var authoredRecords = new List<object>();
		var coveredNativeIds = new HashSet<ulong>();

		foreach (var slotName in targetSlots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var region = SlotToRegion[slotName];
			if (!regionGroups.TryGetValue(region, out var meshGroup))
				continue;
			if (!partSlots.TryGetValue(slotName, out var knownSlotIds))
				continue;

			var matchingNatives = stockUnitIds.Intersect(knownSlotIds).OrderBy(x => x).ToArray();
			if (matchingNatives.Length == 0)
				continue;

			var builtMesh = RetargetedMeshBuilder.Build(meshGroup, geometry, iasRetarget, fullCustomTransform);
			var uvNorm = UvTileNormalizer.Apply(builtMesh);
			builtMesh = uvNorm.Mesh;

			var unitTransformInfo = customAvatarTransform;
			var selectedExtraNames = new List<string>();
			if (iasRetarget.ExtraBones.Count > 0)
			{
				var selection = ExtraBoneSubset.Select(iasRetarget.ExtraBones, extraBaseCount, builtMesh);
				selectedExtraNames.AddRange(selection.Extras.Select(e => e.Name));
				if (fullCustomTransform.Matrices.Count <= 256)
				{
					// Retain the full skeleton when it fits the IAS 256-node budget.
					unitTransformInfo = fullCustomTransform;
				}
				else
				{
					unitTransformInfo = UnitTransformInfoAppender.Append(customAvatarTransform, selection.Extras)!;
					UnitTransformInfoAppender.AssertLocalAndWorldAgree(unitTransformInfo, extraBaseCount);
				}
			}

			var weightedHashes = CollectWeightedBoneHashes(builtMesh);
			var weightedNames = weightedHashes
				.Select(h => nameByHash.TryGetValue(h, out var n) ? n : h.ToString(CultureInfo.InvariantCulture))
				.Distinct(StringComparer.Ordinal)
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToArray();

			foreach (var nativeId in matchingNatives)
			{
				var donorRead = await unitMeshReader.ReadAsync(
					archiveName,
					new AssetKey(PatchUnitMeshReader.UnitTypeId, nativeId),
					dependencyArchiveNames: null,
					allowGlobalDependencySearch: true,
					cancellationToken: cancellationToken).ConfigureAwait(false);

				var customization = donorRead.Model.CustomizationInfo;
				var author = new UnitAuthor();
				var authored = author.Author(new UnitAuthorRequest(
					donorRead.Model,
					donorRead.Payload.TocData,
					unitTransformInfo,
					builtMesh,
					customization,
					nativeId,
					materialIdsBySourceMaterial));

				if (!authored.IsValid || authored.TocData is null || authored.GpuData is null || authored.Model is null)
				{
					throw new InvalidOperationException(
						$"Failed to author {domain} slot {slotName} (0x{nativeId:x16}): {string.Join("; ", authored.Diagnostics)}");
				}

				var fixedToc = FixSelfParentedTransformEntries(authored.TocData);
				var reReadModel = new UnitMeshReader().Read(fixedToc, authored.GpuData);
				var decId = nativeId.ToString(CultureInfo.InvariantCulture);
				var tocFile = Path.Combine(authoredDir, $"{decId}.toc");
				var gpuFile = Path.Combine(authoredDir, $"{decId}.gpu");
				await File.WriteAllBytesAsync(tocFile, fixedToc, cancellationToken).ConfigureAwait(false);
				await File.WriteAllBytesAsync(gpuFile, authored.GpuData, cancellationToken).ConfigureAwait(false);

				var snapshotNodes = BuildSnapshotNodes(reReadModel.TransformInfo, nameByHash);
				coveredNativeIds.Add(nativeId);
				authoredRecords.Add(new
				{
					partSlot = slotName,
					nativeUnitIdDecimal = decId,
					nativeUnitIdHex = nativeId.ToString("x16", CultureInfo.InvariantCulture),
					tocPath = tocFile,
					gpuPath = gpuFile,
					vertexCount = builtMesh.VertexCount,
					triangleCount = builtMesh.TriangleCount,
					extraBones = selectedExtraNames,
					weightedBones = weightedNames,
					snapshotNodes,
				});
			}
		}

		// Build IAS 1-vertex point units for all remaining equipment-owned native units in this archive
		var cleanupCandidates = stockUnitIds.Intersect(ownedEquipmentUnits).Except(coveredNativeIds).OrderBy(x => x).ToArray();
		var pointRecords = new List<object>();
		foreach (var nativeId in cleanupCandidates)
		{
			var donorRead = await unitMeshReader.ReadAsync(
				archiveName,
				new AssetKey(PatchUnitMeshReader.UnitTypeId, nativeId),
				dependencyArchiveNames: null,
				allowGlobalDependencySearch: true,
				cancellationToken: cancellationToken).ConfigureAwait(false);

			var (ptToc, ptGpu) = BuildIasPointUnit(donorRead.Model, donorRead.Payload.TocData);
			var decId = nativeId.ToString(CultureInfo.InvariantCulture);
			var tocFile = Path.Combine(pointDir, $"{decId}.toc");
			var gpuFile = Path.Combine(pointDir, $"{decId}.gpu");
			await File.WriteAllBytesAsync(tocFile, ptToc, cancellationToken).ConfigureAwait(false);
			await File.WriteAllBytesAsync(gpuFile, ptGpu, cancellationToken).ConfigureAwait(false);

			pointRecords.Add(new
			{
				nativeUnitIdDecimal = decId,
				nativeUnitIdHex = nativeId.ToString("x16", CultureInfo.InvariantCulture),
				tocPath = tocFile,
				gpuPath = gpuFile,
			});
		}

		return new
		{
			archive = archiveName,
			stockUnitIdsDecimal = stockUnitIds.OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
			authoredUnits = authoredRecords,
			pointUnits = pointRecords,
		};
	}

	private static HashSet<uint> CollectWeightedBoneHashes(RetargetedMesh mesh)
	{
		var set = new HashSet<uint>();
		foreach (var v in mesh.Vertices)
		{
			foreach (var inf in v.Influences)
			{
				if (inf.Weight > 1e-4f)
					set.Add(inf.BoneHash);
			}
		}

		return set;
	}

	private static List<object> BuildSnapshotNodes(UnitTransformInfo info, IReadOnlyDictionary<uint, string> nameByHash)
	{
		var nodes = new List<object>(info.NameHashes.Count);
		for (var i = 0; i < info.NameHashes.Count; i++)
		{
			var hash = info.NameHashes[i];
			var name = nameByHash.TryGetValue(hash, out var known) ? known : hash.ToString(CultureInfo.InvariantCulture);
			var v = info.Matrices[i].Values;
			var world3x4 = new[]
			{
				v[0], v[4], v[8], v[12],
				v[1], v[5], v[9], v[13],
				v[2], v[6], v[10], v[14],
			};
			var rawParent = (int)info.Entries[i].ParentIndex;
			var parent = i == 0 || info.Entries[i].Increment == 0 || rawParent == i ? -1 : rawParent;
			nodes.Add(new
			{
				name,
				name_hash = hash,
				parent,
				world = world3x4,
			});
		}

		return nodes;
	}

	private static object BuildAuthoringProject(
		UnitTransformInfo stockAvatar,
		UnitTransformInfo fullCustomTransform,
		IReadOnlyDictionary<uint, string> nameByHash)
	{
		static double[] ToMat34(Mat4 m) =>
		[
			m.M11, m.M12, m.M13, m.M14,
			m.M21, m.M22, m.M23, m.M24,
			m.M31, m.M32, m.M33, m.M34,
		];

		var stockCount = stockAvatar.NameHashes.Count;
		var stockWorld = new Mat4[stockCount];
		for (var i = 0; i < stockCount; i++)
			stockWorld[i] = Hd2RigReader.FromHd2RowVector(stockAvatar.Matrices[i].Values);

		var totalCount = fullCustomTransform.NameHashes.Count;
		var customWorld = new Mat4[totalCount];
		for (var i = 0; i < totalCount; i++)
			customWorld[i] = Hd2RigReader.FromHd2RowVector(fullCustomTransform.Matrices[i].Values);

		string NameOf(int idx)
		{
			var h = fullCustomTransform.NameHashes[idx];
			return nameByHash.TryGetValue(h, out var n) ? n : h.ToString(CultureInfo.InvariantCulture);
		}

		var sharedBones = new List<object>(totalCount);
		for (var i = 0; i < totalCount; i++)
		{
			var entry = fullCustomTransform.Entries[i];
			var hasParent = i > 0 && entry.Increment != 0 && entry.ParentIndex != i;
			var parentIdx = hasParent ? (int)entry.ParentIndex : -1;
			var parentName = parentIdx >= 0 ? NameOf(parentIdx) : null;

			var curLocal = parentIdx < 0 ? customWorld[i] : customWorld[parentIdx].Inverse() * customWorld[i];
			var stockLocal = i < stockCount
				? (parentIdx < 0 ? stockWorld[i] : stockWorld[parentIdx].Inverse() * stockWorld[i])
				: curLocal;

			sharedBones.Add(new
			{
				name = NameOf(i),
				parent = parentName,
				is_avatar_source = i < stockCount,
				rest_local = ToMat34(curLocal),
				avatar_source_rest_local = ToMat34(stockLocal),
			});
		}

		return new
		{
			schema = "HD2PhysBoneAuthoringProject1",
			shared_bones = sharedBones,
			chains = Array.Empty<object>(),
			colliders = Array.Empty<object>(),
		};
	}
}
