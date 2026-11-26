using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration;
using HD2SourceMigration.IasConversion;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

using static HD2SourceMigration.IasPackaging.IasAssets;

namespace HD2SourceMigration.IasPackaging;

internal static class VpkExtractor
{
	// AQ SDK 的角色格子与求生模型分区对应。同一个胸部只写进一个有原版 Unit 的格子。
	private static readonly (BodyRegion Region, string[] Slots)[] RegionSlots =
	[
		(BodyRegion.Head, ["Head"]),
		(BodyRegion.Hips, ["Hips"]),
		(BodyRegion.LeftArm, ["LeftArm"]),
		(BodyRegion.RightArm, ["RightArm"]),
		(BodyRegion.Chest, ["Torso", "Torso_Armor"]),
		(BodyRegion.LeftLeg, ["LeftLeg"]),
		(BodyRegion.RightLeg, ["RightLeg"]),
	];

	public static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyDictionary<string, string> opts,
		IReadOnlyList<string> materialVpks, bool withIasPhysics,
		PackagingTiming timing, CancellationToken cancellationToken)
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

		var resolver = new GameDataPackageResolver(gameDataDir);
		var tocScanner = new PatchTocScanner();
		var unitMeshReader = new GameDataUnitMeshReader(resolver, tocScanner);
		var avatarReader = new CanonicalAvatarRigReader(resolver, tocScanner);

		var stockAvatarTransform = await timing.MeasureAsync("读取游戏骨架",
			async () => await avatarReader.ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
		var boneNames = SourceRetargetPipeline.ReadBoneHash(boneHashPath);
		var nameByHash = BuildHashLookup(boneNames);
		var rig = Hd2RigReader.FromTransformInfo(stockAvatarTransform, boneNames);

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, opts.GetValueOrDefault("--model-stem"), timing.Record);

		var customSolution = timing.Measure("几何重定向", () => CustomProportionRetarget.Solve(inputs, rig));
		var iasRetarget = customSolution.Retarget;

		// 在自定义静止姿态上应用 BatchTool 的掌部坐标系握持骨求解（rigging.py:766-860）。
		var customWorldByIndex = new Dictionary<int, Mat4>(customSolution.CustomWorldByIndex);
		SolveGripBones(rig, customWorldByIndex);

		// 用自定义世界矩阵重建骨架，并验证每根骨的 FK 和世界矩阵一致。
		var customAvatarTransform = BuildCustomTransformInfo(stockAvatarTransform, customWorldByIndex);
		UnitTransformInfoAppender.AssertLocalAndWorldAgree(customAvatarTransform, fromIndex: 0, tolerance: 1e-3f);

		// 为 IAS 原生 .hd2phys 编译添加无顶点权重的末端骨。
		var extraBaseCount = customAvatarTransform.Matrices.Count;
		var allExtrasWithTips = new List<RetargetExtraBone>(iasRetarget.ExtraBones);
		var tipBoneByJiggleName = new Dictionary<string, RetargetExtraBone>(StringComparer.Ordinal);
		for (var eIdx = 0; withIasPhysics && eIdx < iasRetarget.ExtraBones.Count; eIdx++)
		{
			var eb = iasRetarget.ExtraBones[eIdx];
			var tipName = MakeSafeTipName(eb.Name);
			var tipWorld = customSolution.JiggleTipWorldByBoneName[eb.Name];
			var tipBone = new RetargetExtraBone(
				Name: tipName,
				NameHash: Hd2NameHash.Of(tipName),
				ParentIndex: extraBaseCount + eIdx,
				WorldOrigin: tipWorld,
				AnchorIndex: eb.AnchorIndex);
			allExtrasWithTips.Add(tipBone);
			tipBoneByJiggleName[eb.Name] = tipBone;
		}

		// 登记摆动骨和末端骨的名称，供导出快照时查找。
		foreach (var extra in allExtrasWithTips)
		{
			nameByHash[extra.NameHash] = extra.Name;
		}

		if (withIasPhysics)
		{
			var jiggleFacts = SourceModelInspector.JiggleFacts(inputs.Model);
			// 导出 IAS .hd2phys 编译所需的摆动骨事实。
			var exportedJiggleRows = BuildJiggleExportRows(jiggleFacts, iasRetarget, fullTransformBaseCount: extraBaseCount, tipBoneByJiggleName);
			await File.WriteAllTextAsync(
				Path.Combine(outDir, "jiggle_facts.json"),
				JsonSerializer.Serialize(exportedJiggleRows, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
		}

		// 导出身体与头盔的原版资源，供 AQSDK 查表。
		var stockBodyDir = Path.Combine(outDir, "stock_body");
		var stockHelmetDir = Path.Combine(outDir, "stock_helmet");
		var stockBodyManifest = await timing.MeasureAsync("读取原版身体归档",
			() => DumpStockArchiveAsync(resolver, tocScanner, bodyArchive, stockBodyDir, cancellationToken, inventoryOnly: true)).ConfigureAwait(false);
		var stockHelmetManifest = await timing.MeasureAsync("读取原版头盔归档",
			() => DumpStockArchiveAsync(resolver, tocScanner, helmetArchive, stockHelmetDir, cancellationToken, inventoryOnly: true)).ConfigureAwait(false);

		// 沿用现有流程生成 VPK 材质和 BC7 贴图。
		var texconvPath = opts["--texconv"];
		var templates = Hd2MaterialTemplateLibrary.Locate()
			?? throw new InvalidOperationException("Could not locate Hd2MaterialTemplateLibrary.");
		var pak01 = l4d2Root is null ? null : Path.Combine(l4d2Root, "pak01_dir.vpk");
		var warnings = new List<string>();
		var switchPaths = VpkImportPlanner.FindComponentSwitches(materialVpks)
			.Select(item => item.VpkPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var path in materialVpks.Where(switchPaths.Contains))
			warnings.Add($"已跳过组件开关包「{Path.GetFileName(path)}」：当前 IAS 转换无法生成对应的隐藏选项。");
		var materialLayers = materialVpks.Where(path => !switchPaths.Contains(path)
			&& !path.Equals(vpkPath, StringComparison.OrdinalIgnoreCase))
			.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
		foreach (var layer in materialLayers)
		{
			var selected = layer.Last();
			foreach (var path in layer.Distinct(StringComparer.OrdinalIgnoreCase)
				.Where(path => !path.Equals(selected, StringComparison.OrdinalIgnoreCase)))
				warnings.Add($"合成未使用材质来源「{path}」：普通导入平铺来源时由同文件名的「{selected}」覆盖。");
		}
		var layers = materialLayers.Select(layer => layer.Last()).Prepend(vpkPath)
			.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
		// 普通导入按文件名升序部署，后层覆盖；材质索引先命中优先，因此倒序读。
		var matSources = layers.Reverse();
		if (pak01 is not null)
			matSources = matSources.Append(pak01);
		var readSources = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		var matSource = timing.Measure("索引 VPK 材质与贴图", () => SourceMaterialSource.Open(matSources,
			materialVpks.Count == 0 ? null : path => readSources.TryAdd(path, 0)));
		warnings.AddRange(matSource.Failures.Select(failure => $"无法索引材质来源：{failure}"));
		long bc7Ticks = 0;
		int bc7Count = 0;
		var encoder = new Bc7Encoder(texconvPath, outDir, elapsed =>
		{
			Interlocked.Add(ref bc7Ticks, elapsed.Ticks);
			Interlocked.Increment(ref bc7Count);
		});
		var matConverter = new SourceMaterialConverter(matSource, encoder, templates);

		// EyeUv 只改变顶点 UV。先分区，材质转换只处理本次会写入的默认 bodygroup。
		var splitResult = timing.Measure("几何分区",
			() => SourceSegmentation.Split(inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), iasRetarget.Resolved));
		var regionGroups = BuildMergedRegionGroups(splitResult.Groups);
		var requiredMaterialIndices = RegionSlots.Where(slot => regionGroups.ContainsKey(slot.Region))
			.SelectMany(slot => regionGroups[slot.Region].TriangleIndices)
			.Select(index => inputs.Geometry.Triangles[index].MaterialIndex)
			.Distinct().Order().ToArray();

		var matOutDir = Path.Combine(outDir, "materials_patch");
		Directory.CreateDirectory(matOutDir);
		var assembler = new VpkPatchAssembler();
		var matPkgResult = await timing.MeasureAsync("材质与贴图转换",
			async () => await assembler.AssembleAsync(
			plans: [],
			retarget: null,
			geometry: inputs.Geometry,
			avatarTransformInfo: null,
			headerTemplateTocData: stockBodyManifest.TocBytes,
			outputDirectory: matOutDir,
			patchFileName: null,
			modelKey: Path.GetFileNameWithoutExtension(vpkPath),
			cancellationToken: cancellationToken,
			sourceMaterialNames: inputs.Model.MaterialPaths,
			materialConverter: matConverter,
			materialPackFileName: $"{bodyArchive}.patch_1",
			materialSourceIndices: requiredMaterialIndices).ConfigureAwait(false)).ConfigureAwait(false);
		timing.Record($"BC7 编码（{bc7Count} 次，并发累计）", TimeSpan.FromTicks(bc7Ticks));
		if (!matPkgResult.IsValid)
			throw new InvalidOperationException($"VPK 材质包生成失败：{string.Join("；", matPkgResult.Diagnostics)}");

		var materialIdsBySourceMaterial = matPkgResult.Materials
			.Where(material => material.SourceMaterialIndex >= 0 && material.Emitted && material.MaterialId != 0)
			.ToDictionary(material => material.SourceMaterialIndex, material => material.MaterialId);
		var missing = requiredMaterialIndices.Where(index => !materialIdsBySourceMaterial.ContainsKey(index)).ToArray();
		if (missing.Length != 0)
			throw new InvalidDataException($"IAS 网格缺少随包材质：{string.Join("、", missing.Select(index =>
				(uint)index < inputs.Model.MaterialPaths.Count ? inputs.Model.MaterialPaths[index] : $"#{index}"))}。");

		// 沿用 VpkPatchAssembler 的眼睛 UV 重投影口径。
		var (geometry, _) = timing.Measure("眼睛 UV 投影",
			() => EyeUv.Reproject(inputs.Geometry, inputs.Model.MaterialPaths, matConverter.ResolveEyeColorSource, null));

		var fullCustomTransform = UnitTransformInfoAppender.Append(customAvatarTransform, allExtrasWithTips)!;
		UnitTransformInfoAppender.AssertLocalAndWorldAgree(fullCustomTransform, extraBaseCount);

		var authoredUnitsDir = Path.Combine(outDir, "units_authored");
		var pointUnitsDir = Path.Combine(outDir, "units_point");
		Directory.CreateDirectory(authoredUnitsDir);
		Directory.CreateDirectory(pointUnitsDir);

		var unitsStart = System.Diagnostics.Stopwatch.GetTimestamp();
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
			tipBoneByJiggleName: tipBoneByJiggleName,
			materialIdsBySourceMaterial: materialIdsBySourceMaterial,
			unitMeshReader: unitMeshReader,
			nameByHash: nameByHash,
			authoredDir: authoredUnitsDir,
			pointDir: pointUnitsDir,
			withPreviewGeometry: withIasPhysics && tipBoneByJiggleName.Count > 0,
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
			tipBoneByJiggleName: tipBoneByJiggleName,
			materialIdsBySourceMaterial: materialIdsBySourceMaterial,
			unitMeshReader: unitMeshReader,
			nameByHash: nameByHash,
			authoredDir: authoredUnitsDir,
			pointDir: pointUnitsDir,
			withPreviewGeometry: false,
			cancellationToken: cancellationToken).ConfigureAwait(false);
		timing.Record("编写身体与头盔 Unit", System.Diagnostics.Stopwatch.GetElapsedTime(unitsStart));

		// 生成 AQSDK 的骨架 profile 编译所需的 authoring_project.json。
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
		if (layers.Length > 1)
		{
			warnings.Add($"「{Path.GetFileName(vpkPath)}」材质按普通导入默认顺序合成（从低到高优先级）："
				+ $"{string.Join(" → ", layers.Select(Path.GetFileName))}。");
			foreach (var path in layers.Where(path => !path.Equals(vpkPath, StringComparison.OrdinalIgnoreCase)))
				warnings.Add(readSources.ContainsKey(path)
					? $"合成已读取材质来源「{Path.GetFileName(path)}」。"
					: $"合成未读取材质来源「{Path.GetFileName(path)}」：模型未引用其中可用的资源，或同名资源已被后层覆盖。");
		}
		return [.. warnings, .. matPkgResult.Warnings];

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

			// 列的次序为前向、宽度方向、法向，与 rigging.py:793 一致。
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
			// L4D2 互斥 bodygroup 保留默认模型（索引 0）。
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
		IReadOnlyDictionary<string, RetargetExtraBone> tipBoneByJiggleName,
		IReadOnlyDictionary<int, ulong> materialIdsBySourceMaterial,
		GameDataUnitMeshReader unitMeshReader,
		IReadOnlyDictionary<uint, string> nameByHash,
		string authoredDir,
		string pointDir,
		bool withPreviewGeometry,
		CancellationToken cancellationToken)
	{
		var authoredRecords = new List<object>();
		var coveredNativeIds = new HashSet<ulong>();

		foreach (var (region, slots) in RegionSlots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ((region == BodyRegion.Head) != (domain == "HELMET"))
				continue;
			if (!regionGroups.TryGetValue(region, out var meshGroup))
				continue;
			var (slotName, matchingNatives) = slots.Where(partSlots.ContainsKey)
				.Select(slot => (slot, stockUnitIds.Intersect(partSlots[slot]).Intersect(ownedEquipmentUnits).Order().ToArray()))
				.FirstOrDefault(candidate => candidate.Item2.Length != 0);
			if (matchingNatives is null)
				throw new InvalidDataException($"IAS 底子归档 {archiveName} 缺少可接收 {region} 几何的格子（{string.Join("、", slots)}）。");

			var builtMesh = RetargetedMeshBuilder.Build(meshGroup, geometry, iasRetarget, fullCustomTransform);
			var uvNorm = UvTileNormalizer.Apply(builtMesh);
			builtMesh = uvNorm.Mesh;

			var unitTransformInfo = customAvatarTransform;
			var selectedExtraNames = new List<string>();
			var selectedTipNames = new List<string>();
			if (iasRetarget.ExtraBones.Count > 0)
			{
				var selection = ExtraBoneSubset.Select(iasRetarget.ExtraBones, extraBaseCount, builtMesh);
				selectedExtraNames.AddRange(selection.Extras.Select(e => e.Name));
				if (fullCustomTransform.Matrices.Count <= 256)
				{
					// 在 IAS 256 节点限制内时，每个 Unit 都保留完整的共享链。
					unitTransformInfo = fullCustomTransform;
					selectedTipNames.AddRange(tipBoneByJiggleName.Values.Select(t => t.Name));
				}
				else
				{
					var combinedExtras = new List<RetargetExtraBone>(selection.Extras);
					for (var sIdx = 0; sIdx < selection.Extras.Count; sIdx++)
					{
						var sel = selection.Extras[sIdx];
						if (tipBoneByJiggleName.TryGetValue(sel.Name, out var tipTemplate))
						{
							var remappedTip = tipTemplate with { ParentIndex = extraBaseCount + sIdx };
							combinedExtras.Add(remappedTip);
							selectedTipNames.Add(remappedTip.Name);
						}
					}
					unitTransformInfo = UnitTransformInfoAppender.Append(customAvatarTransform, combinedExtras)!;
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
				// 同一接缝可能跨多个 Unit。统一用半精度四槽，避免某个外壳
				// 沿用 10:10:10:2 权重并在编码时丢掉第四根骨的影响。
				var donor = IasNativeMesh.UseFourWeightStreams(donorRead.Model, onlyPacked: true);
				var authored = author.Author(new UnitAuthorRequest(
					donor,
					donorRead.Payload.TocData,
					unitTransformInfo,
					builtMesh,
					customization,
					nativeId,
					materialIdsBySourceMaterial), null, preserveAbsentCustomization: true);

				if (!authored.IsValid || authored.TocData is null || authored.GpuData is null || authored.Model is null)
				{
					throw new InvalidOperationException(
						$"Failed to author {domain} slot {slotName} (0x{nativeId:x16}): {string.Join("; ", authored.Diagnostics)}");
				}

				var fixedToc = FixSelfParentedTransformEntries(authored.TocData);
				var reReadModel = new UnitMeshReader().Read(fixedToc, authored.GpuData, options: UnitMeshReadOptions.StructureOnly);
				VerifyVisualMaterials(reReadModel, builtMesh, materialIdsBySourceMaterial, nativeId);
				var decId = nativeId.ToString(CultureInfo.InvariantCulture);
				var tocFile = Path.Combine(authoredDir, $"{decId}.toc");
				var gpuFile = Path.Combine(authoredDir, $"{decId}.gpu");
				await File.WriteAllBytesAsync(tocFile, fixedToc, cancellationToken).ConfigureAwait(false);
				await File.WriteAllBytesAsync(gpuFile, authored.GpuData, cancellationToken).ConfigureAwait(false);

				var snapshotNodes = BuildSnapshotNodes(reReadModel.TransformInfo, nameByHash);
				string? previewModelPath = null;
				if (withPreviewGeometry)
				{
					previewModelPath = Path.Combine(authoredDir, $"{decId}.preview.json");
					await File.WriteAllTextAsync(previewModelPath, JsonSerializer.Serialize(BuildPreviewModel(reReadModel)),
						cancellationToken).ConfigureAwait(false);
				}
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
					extraBones = selectedExtraNames.Concat(selectedTipNames).ToArray(),
					weightedBones = weightedNames,
					snapshotNodes,
					previewModelPath,
				});
			}
		}

		// 把该装备剩余的原版 Unit 转成 IAS 单顶点占位 Unit。
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

	private static void VerifyVisualMaterials(UnitMeshModel unit, RetargetedMesh source,
		IReadOnlyDictionary<int, ulong> materialIds, ulong unitId)
	{
		var bindings = unit.Materials.GroupBy(binding => binding.SectionId)
			.ToDictionary(group => group.Key, group => group.Select(binding => binding.MaterialId).Distinct().ToArray());
		foreach (var mesh in unit.Meshes.Where(mesh => mesh.LodIndex >= 0))
		{
			var sections = mesh.Sections.Where(section => section.NumIndices != 0).ToArray();
			if (sections.Length != source.Sections.Count)
				throw new InvalidDataException($"IAS Unit {unitId:x16} 的网格 {mesh.Index} 读回段数和源网格不符。");
			for (var i = 0; i < sections.Length; i++)
			{
				var index = source.Sections[i].SourceMaterialIndex;
				if (!materialIds.TryGetValue(index, out var expected) ||
					!bindings.TryGetValue(sections[i].MaterialSlotId, out var actual) || actual.Length != 1 || actual[0] != expected)
					throw new InvalidDataException($"IAS Unit {unitId:x16} 的网格 {mesh.Index} 第 {i} 段材质与源索引 {index} 不符。");
			}
		}
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

	// 将回读的 Unit 表映射到 AQSDK 预览编译器读取的字段，无需 Blender。
	private static object BuildPreviewModel(UnitMeshModel model) => new
	{
		TransformInfo = new
		{
			TransformMatrices = model.TransformInfo.Matrices.Select(matrix => new { v = matrix.Values }),
			TransformEntries = model.TransformInfo.Entries.Select(entry => new { ParentBone = entry.ParentIndex }),
		},
		MeshInfoArray = model.Meshes.Select(mesh => new
		{
			mesh.LodIndex, mesh.StreamIndex, mesh.Sections,
		}),
		StreamInfoArray = model.Streams.Select(stream => new
		{
			IndexBuffer_Type = stream.IndexBufferType,
			stream.VertexStride, stream.IndexBufferOffset, stream.VertexBufferOffset,
		}),
		BoneInfoArray = model.BoneInfos.Select(info => new
		{
			info.RealIndices,
			Remaps = info.Remaps.Select(remap => remap.FakeIndices),
			Bones = info.BoneMatrices.Select(matrix => new
			{
				v = Enumerable.Range(0, 16).Select(i => BinaryPrimitives.ReadSingleLittleEndian(matrix.AsSpan(i * sizeof(float)))),
			}),
		}),
	};

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
	private static string MakeSafeTipName(string boneName)
	{
		const string suffix = "_HD2PB_Tip";
		var utf8 = System.Text.Encoding.UTF8.GetBytes(boneName);
		var maxStem = 63 - suffix.Length;
		var stem = utf8.Length <= maxStem
			? boneName
			: System.Text.Encoding.UTF8.GetString(utf8, 0, maxStem);
		return stem + suffix;
	}

	private static List<object> BuildJiggleExportRows(
		IReadOnlyList<SourceJiggleBoneFact> jiggleFacts,
		RetargetResult retarget,
		int fullTransformBaseCount,
		IReadOnlyDictionary<string, RetargetExtraBone> tipBoneByJiggleName)
	{
		var factByName = jiggleFacts.ToDictionary(f => f.BoneName, f => f, StringComparer.Ordinal);
		var rows = new List<object>(retarget.ExtraBones.Count);
		foreach (var extra in retarget.ExtraBones)
		{
			string parentName;
			if (extra.ParentIndex < fullTransformBaseCount)
			{
				parentName = "unknown";
				if (factByName.TryGetValue(extra.Name, out var f0)
					&& retarget.JiggleAnchors.TryGetValue(f0.ParentName, out var anchored))
				{
					parentName = anchored;
				}
			}
			else
			{
				parentName = retarget.ExtraBones[extra.ParentIndex - fullTransformBaseCount].Name;
			}

			var p = factByName.TryGetValue(extra.Name, out var fact) ? fact.Parameters : null;
			var rawStiff = p is null
				? 100f
				: Math.Max(Math.Max(p.YawStiffness, p.PitchStiffness), p.BaseStiffness > 0 ? p.BaseStiffness : 80f);
			var rawDamp = p is null
				? 4f
				: Math.Max(Math.Max(p.YawDamping, p.PitchDamping), p.BaseDamping > 0 ? p.BaseDamping : 3f);
			var angleLimRad = p is not null && p.HasAngleConstraint && p.AngleLimit > 0.01f
				? Math.Clamp((double)p.AngleLimit, 0.05, 1.40)
				: Math.PI / 6.0;

			rows.Add(new
			{
				bone = extra.Name,
				parentInExtra = extra.ParentIndex >= fullTransformBaseCount ? parentName : null,
				tipBone = tipBoneByJiggleName[extra.Name].Name,
				sourceStiffness = rawStiff,
				sourceDamping = rawDamp,
				sourceAngularDamping = p is { IsFlexible: true } ? Math.Max(p.YawDamping, p.PitchDamping) : (float?)null,
				angleLimitRad = Math.Round(angleLimRad, 5),
				flags = fact?.FlagNames ?? [],
			});
		}

		return rows;
	}
}
