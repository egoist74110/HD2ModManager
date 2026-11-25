using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration;
using HD2SourceMigration.Retarget;
using HD2SourceModel;

using static HD2SourceMigration.IasPackaging.IasAssets;

namespace HD2SourceMigration.IasPackaging;

/// <summary>
/// 提取 IAS 所需的护甲网格与蒙皮权重，按 Unit ID 匹配护甲和头盔，空余槽位用单顶点 Unit 占位。
/// </summary>
internal static class PatchExtractor
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453fUL;
	private const ulong PackageTypeId = 0xad9c6d9ed1e5e77aUL;
	private static readonly Regex PatchFileName = new(
		@"^[0-9a-f]{16}\.patch_([0-9]+)$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private sealed record RawPatchEntry(ulong TypeId, ulong FileId, string Archive, byte[] TocData, byte[] GpuData, byte[] StreamData);

	public static async Task ExtractAsync(string modDir, string gameDataDir, string boneHashPath,
		string partCatalogPath, string bindingCatalogPath, string outDir, SearchOption searchOption,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(outDir);

		var tocScanner = new PatchTocScanner();
		// 同一 AssetKey 出现在多个 patch 时，以序号靠后的 patch 为准。
		var modEntries = ReadModPatchEntries(modDir, tocScanner, searchOption, cancellationToken);
		var modUnits = modEntries.Values.Where(e => e.TypeId == UnitTypeId).ToDictionary(e => e.FileId, e => e);
		var modNonUnits = modEntries.Values
			.Where(e => e.TypeId != UnitTypeId && e.TypeId != PackageTypeId)
			.OrderBy(e => e.TypeId)
			.ThenBy(e => e.FileId)
			.ToArray();

		if (modUnits.Count == 0)
			throw new InvalidOperationException("No Unit entries found in mod patches.");

		var resolver = new GameDataPackageResolver(gameDataDir);
		var unitMeshReader = new GameDataUnitMeshReader(resolver, tocScanner);
		var avatarReader = new CanonicalAvatarRigReader(resolver, tocScanner);
		var stockAvatarTransform = await avatarReader.ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var nameByHash = BuildHashLookup(SourceRetargetPipeline.ReadBoneHash(boneHashPath));

		// 自动识别护甲和头盔的原版归档。
		var partSlots = ReadPartCatalog(partCatalogPath);
		var (bodyTarget, helmetTarget) = SelectTargets(bindingCatalogPath,
			modUnits.ToDictionary(pair => pair.Key, pair => pair.Value.Archive));
		if (bodyTarget is null && helmetTarget is null)
			throw new InvalidOperationException("Could not match mod Unit IDs to any supported IAS equipment archive.");

		// 3. 将材质和贴图交给 package_model.py 做资源隔离。
		var rawMatDir = Path.Combine(outDir, "raw_materials");
		await DumpRawEntriesAsync(modNonUnits, rawMatDir, cancellationToken).ConfigureAwait(false);

		var authoredUnitsDir = Path.Combine(outDir, "units_authored");
		var pointUnitsDir = Path.Combine(outDir, "units_point");
		Directory.CreateDirectory(authoredUnitsDir);
		Directory.CreateDirectory(pointUnitsDir);

		// 收集各 Unit 的自定义骨，供 authoring_project.json 使用。
		var extraSharedBones = new Dictionary<string, (string? Parent, double[] RestLocal)>(StringComparer.Ordinal);

		object? bodyMeta = null;
		if (bodyTarget is not null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var stockBodyDir = Path.Combine(outDir, "stock_body");
			var stockBodyManifest = await DumpStockArchiveAsync(resolver, tocScanner, bodyTarget.Archive, stockBodyDir, cancellationToken).ConfigureAwait(false);
			bodyMeta = await ProcessDomainAsync(
				domain: "BODY",
				target: bodyTarget,
				stockUnitIds: stockBodyManifest.UnitIds,
				partSlots: partSlots,
				modUnits: modUnits,
				unitMeshReader: unitMeshReader,
				stockAvatar: stockAvatarTransform,
				nameByHash: nameByHash,
				extraSharedBones: extraSharedBones,
				authoredDir: authoredUnitsDir,
				pointDir: pointUnitsDir,
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}

		object? helmetMeta = null;
		if (helmetTarget is not null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var stockHelmetDir = Path.Combine(outDir, "stock_helmet");
			var stockHelmetManifest = await DumpStockArchiveAsync(resolver, tocScanner, helmetTarget.Archive, stockHelmetDir, cancellationToken).ConfigureAwait(false);
			helmetMeta = await ProcessDomainAsync(
				domain: "HELMET",
				target: helmetTarget,
				stockUnitIds: stockHelmetManifest.UnitIds,
				partSlots: partSlots,
				modUnits: modUnits,
				unitMeshReader: unitMeshReader,
				stockAvatar: stockAvatarTransform,
				nameByHash: nameByHash,
				extraSharedBones: extraSharedBones,
				authoredDir: authoredUnitsDir,
				pointDir: pointUnitsDir,
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}

		// 将原版 Avatar 骨骼和 Mod 的自定义骨写入骨架编译输入。
		var authoringProject = BuildAuthoringProject(stockAvatarTransform, nameByHash, extraSharedBones);
		await File.WriteAllTextAsync(
			Path.Combine(outDir, "authoring_project.json"),
			JsonSerializer.Serialize(authoringProject, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

		var summaryManifest = new
		{
			sourceModDir = modDir,
			bodyArchive = bodyTarget?.Archive,
			helmetArchive = helmetTarget?.Archive,
			rawMaterialsDir = modNonUnits.Length > 0 ? rawMatDir : null,
			nonUnitEntryCount = modNonUnits.Length,
			customBoneCount = extraSharedBones.Count,
			body = bodyMeta,
			helmet = helmetMeta,
		};
		await File.WriteAllTextAsync(
			Path.Combine(outDir, "authored_manifest.json"),
			JsonSerializer.Serialize(summaryManifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

	}

	private static async Task<object> ProcessDomainAsync(
		string domain,
		BindingTargetInfo target,
		HashSet<ulong> stockUnitIds,
		IReadOnlyDictionary<string, HashSet<ulong>> partSlots,
		IReadOnlyDictionary<ulong, RawPatchEntry> modUnits,
		GameDataUnitMeshReader unitMeshReader,
		UnitTransformInfo stockAvatar,
		Dictionary<uint, string> nameByHash,
		Dictionary<string, (string? Parent, double[] RestLocal)> extraSharedBones,
		string authoredDir,
		string pointDir,
		CancellationToken cancellationToken)
	{
		var stockHashes = new HashSet<uint>(stockAvatar.NameHashes);
		var authoredRecords = new List<object>();
		var coveredNativeIds = new HashSet<ulong>();

		foreach (var (slotName, knownSlotIds) in partSlots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			// Head 是 AQ SDK 的头盔协议槽，其它槽由当前 SDK 数据声明。
			if ((slotName == "Head") != (domain == "HELMET"))
				continue;

			var matchingNatives = stockUnitIds.Intersect(knownSlotIds).Intersect(target.OwnedUnits).OrderBy(x => x).ToArray();
			if (matchingNatives.Length == 0)
				continue;

			// 筛出含有效三角面的 Unit，排除折叠的隐藏占位。
			var realModUnits = new Dictionary<ulong, (byte[] FixedToc, byte[] Gpu, UnitMeshModel Model)>();
			foreach (var nativeId in matchingNatives)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!modUnits.TryGetValue(nativeId, out var rawUnit))
					continue;
				var fixedToc = FixSelfParentedTransformEntries(rawUnit.TocData);
				var parsed = new UnitMeshReader().Read(fixedToc, rawUnit.GpuData);
				if (HasRealGeometry(parsed))
				{
					realModUnits[nativeId] = NormalizeEmptyMeshes(parsed, fixedToc, rawUnit.GpuData);
				}
			}

			if (realModUnits.Count == 0)
				continue;

			// AQSDK 会展开槽位的所有变体。Mod 若只提供一种体型，补齐缺失变体并沿用各自的原版定制数据。
			var fallbackVariant = realModUnits.OrderBy(kv => kv.Key).First().Value;
			foreach (var nativeId in matchingNatives)
			{
				cancellationToken.ThrowIfCancellationRequested();
				byte[] finalToc;
				byte[] finalGpu;
				UnitMeshModel finalModel;

				var hasExact = realModUnits.TryGetValue(nativeId, out var exact);
				if (hasExact && exact.Model!.CustomizationInfoOffset != 0)
				{
					(finalToc, finalGpu, finalModel) = (exact.FixedToc!, exact.Gpu!, exact.Model);
				}
				else
				{
					var stockDonor = await unitMeshReader.ReadAsync(
						target.Archive,
						new AssetKey(UnitTypeId, nativeId),
						dependencyArchiveNames: null,
						allowGlobalDependencySearch: true,
						cancellationToken: cancellationToken).ConfigureAwait(false);
					(finalToc, finalGpu, finalModel) = hasExact
						? (exact.FixedToc!, exact.Gpu!, exact.Model!)
						: CloneVariantWithDonorCustomization(fallbackVariant.Model, fallbackVariant.FixedToc, stockDonor.Model.CustomizationInfo);
					if (finalModel.CustomizationInfoOffset == 0)
					{
						finalToc = CopyStockCustomization(finalToc, stockDonor.Payload.TocData, stockDonor.Model.CustomizationInfoOffset);
						finalModel = new UnitMeshReader().Read(finalToc, finalGpu);
					}
				}

				RegisterUnitBones(finalModel.TransformInfo, stockHashes, nameByHash, extraSharedBones);
				var (weightedNames, extraNames) = ExtractWeightedAndExtraBones(finalModel, stockHashes, nameByHash);
				var snapshotNodes = BuildSnapshotNodes(finalModel.TransformInfo, nameByHash);

				var decId = nativeId.ToString(CultureInfo.InvariantCulture);
				var tocFile = Path.Combine(authoredDir, $"{decId}.toc");
				var gpuFile = Path.Combine(authoredDir, $"{decId}.gpu");
				await File.WriteAllBytesAsync(tocFile, finalToc, cancellationToken).ConfigureAwait(false);
				await File.WriteAllBytesAsync(gpuFile, finalGpu, cancellationToken).ConfigureAwait(false);

				coveredNativeIds.Add(nativeId);
				var totalVerts = finalModel.RawMeshData.Sum(m => m.Vertices.Count);
				var totalTris = finalModel.RawMeshData.Sum(m => m.Triangles.Count);

				authoredRecords.Add(new
				{
					partSlot = slotName,
					nativeUnitIdDecimal = decId,
					nativeUnitIdHex = nativeId.ToString("x16", CultureInfo.InvariantCulture),
					tocPath = tocFile,
					gpuPath = gpuFile,
					vertexCount = totalVerts,
					triangleCount = totalTris,
					extraBones = extraNames,
					weightedBones = weightedNames,
					snapshotNodes,
				});
			}
		}

		if (authoredRecords.Count == 0)
			throw new InvalidOperationException($"No real geometry units found for {domain} archive {target.Archive}.");

		// 把该装备其余 Unit 转为 IAS 单顶点占位，包括原 Mod 中的隐藏网格。
		var cleanupCandidates = stockUnitIds.Intersect(target.OwnedUnits).Except(coveredNativeIds).OrderBy(x => x).ToArray();
		var pointRecords = new List<object>();
		foreach (var nativeId in cleanupCandidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var donorRead = await unitMeshReader.ReadAsync(
				target.Archive,
				new AssetKey(UnitTypeId, nativeId),
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
			archive = target.Archive,
			stockUnitIdsDecimal = stockUnitIds.OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
			authoredUnits = authoredRecords,
			pointUnits = pointRecords,
		};
	}

	private static bool HasRealGeometry(UnitMeshModel model)
	{
		var totalVerts = 0;
		double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
		double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

		foreach (var raw in model.RawMeshData)
		{
			if (raw.Triangles.Count == 0)
				continue;
			totalVerts += raw.Vertices.Count;
			foreach (var v in raw.Vertices)
			{
				var pos = v.Components.FirstOrDefault(c => c.Type == 0);
				if (pos is null || pos.FloatValues.Length < 3)
					continue;
				double x = pos.FloatValues[0], y = pos.FloatValues[1], z = pos.FloatValues[2];
				minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
				minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
				minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
			}
		}

		if (totalVerts <= 4 || minX > maxX)
			return false;
		var diagSq = ((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)) + ((maxZ - minZ) * (maxZ - minZ));
		return diagSq > 1e-5; // > ~3.1 mm bounding box diagonal
	}

	private static (byte[] Toc, byte[] Gpu, UnitMeshModel Model) NormalizeEmptyMeshes(
		UnitMeshModel model, byte[] toc, byte[] gpu)
	{
		if (model.RawMeshData.All(mesh => mesh.Triangles.Count > 0))
			return (toc, gpu, model);

		// IAS 校验每个网格记录。保留有索引的几何，空网格或 LOD 用退化三角面占位。
		var meshes = model.RawMeshData.Select(mesh => mesh.Triangles.Count > 0
			? mesh
			: PreparePointMesh(mesh, model.Streams.Single(stream => stream.Index == (int)mesh.StreamIndex))).ToArray();
		return RebuildUnit(model, toc, meshes);
	}

	private static (byte[] Toc, byte[] Gpu, UnitMeshModel Model) CloneVariantWithDonorCustomization(
		UnitMeshModel sourceModel,
		byte[] sourceToc,
		UnitCustomizationInfo targetCustomization)
	{
		return RebuildUnit(sourceModel with { CustomizationInfo = targetCustomization }, sourceToc, sourceModel.RawMeshData);
	}

	private static (byte[] Toc, byte[] Gpu, UnitMeshModel Model) RebuildUnit(
		UnitMeshModel model, byte[] toc, IReadOnlyList<UnitRawMeshData> meshes)
	{
		var bones = model.BoneInfos.Select((b, idx) => new CanonicalBoneInfoRebuild(idx, b)).ToArray();
		var rebuilder = new CanonicalUnitRebuilder();
		var rebuilt = rebuilder.TryRebuild(model, toc, meshes, bones, model.Materials);
		if (!rebuilt.IsValid || rebuilt.Output is null)
			throw new InvalidOperationException($"Failed to prepare IAS Unit geometry: {string.Join("; ", rebuilt.Diagnostics.Select(d => d.Message))}");
		var fixedToc = FixSelfParentedTransformEntries(rebuilt.Output.TocData);
		return (fixedToc, rebuilt.Output.GpuData, new UnitMeshReader().Read(fixedToc, rebuilt.Output.GpuData));
	}

	private static void RegisterUnitBones(
		UnitTransformInfo info,
		HashSet<uint> stockHashes,
		Dictionary<uint, string> nameByHash,
		Dictionary<string, (string? Parent, double[] RestLocal)> extraSharedBones)
	{
		static double[] ToMat34(Mat4 m) =>
		[
			m.M11, m.M12, m.M13, m.M14,
			m.M21, m.M22, m.M23, m.M24,
			m.M31, m.M32, m.M33, m.M34,
		];

		var count = info.NameHashes.Count;
		var worlds = new Mat4[count];
		for (var i = 0; i < count; i++)
		{
			var h = info.NameHashes[i];
			if (!nameByHash.ContainsKey(h))
				nameByHash[h] = $"custom_0x{h:x8}";
			worlds[i] = Hd2RigReader.FromHd2RowVector(info.Matrices[i].Values);
		}

		for (var i = 0; i < count; i++)
		{
			var h = info.NameHashes[i];
			if (stockHashes.Contains(h))
				continue;
			var name = nameByHash[h];
			if (extraSharedBones.ContainsKey(name))
				continue;

			var entry = info.Entries[i];
			var hasParent = i > 0 && entry.Increment != 0 && entry.ParentIndex != i;
			var pIdx = hasParent ? (int)entry.ParentIndex : -1;
			var parentName = pIdx >= 0 ? nameByHash[info.NameHashes[pIdx]] : null;
			var local = pIdx < 0 ? worlds[i] : worlds[pIdx].Inverse() * worlds[i];
			extraSharedBones[name] = (parentName, ToMat34(local));
		}
	}

	private static (string[] Weighted, string[] Extra) ExtractWeightedAndExtraBones(
		UnitMeshModel model,
		HashSet<uint> stockHashes,
		IReadOnlyDictionary<uint, string> nameByHash)
	{
		var weighted = new HashSet<string>(StringComparer.Ordinal);
		var extras = new HashSet<string>(StringComparer.Ordinal);

		foreach (var bi in model.BoneInfos)
		{
			foreach (var realIdx in bi.RealIndices)
			{
				if (realIdx < model.TransformInfo.NameHashes.Count)
				{
					var h = model.TransformInfo.NameHashes[(int)realIdx];
					var name = nameByHash[h];
					weighted.Add(name);
					if (!stockHashes.Contains(h))
						extras.Add(name);
				}
			}
		}

		// 刚性挂接的 Unit 也要登记至少一根骨。
		if (weighted.Count == 0)
		{
			foreach (var mesh in model.Meshes)
			{
				if (mesh.TransformIndex < model.TransformInfo.NameHashes.Count)
				{
					var h = model.TransformInfo.NameHashes[(int)mesh.TransformIndex];
					weighted.Add(nameByHash[h]);
				}
			}
		}

		return (
			weighted.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
			extras.OrderBy(s => s, StringComparer.Ordinal).ToArray());
	}

	private static IEnumerable<string> OrderedPatchFiles(IEnumerable<string> paths, bool flatten = false) =>
		paths.Select(path => (Path: path, Match: PatchFileName.Match(Path.GetFileName(path))))
			.Where(file => file.Match.Success)
			.OrderBy(file => int.Parse(file.Match.Groups[1].Value, CultureInfo.InvariantCulture))
			.ThenBy(file => flatten ? Path.GetFileName(file.Path) : file.Path, StringComparer.OrdinalIgnoreCase)
			.Select(file => file.Path);

	internal static async Task<IReadOnlyList<string>> ReadTargetArchivesAsync(IEnumerable<string> patchFiles, string bindingCatalogPath,
		CancellationToken token)
	{
		var units = new Dictionary<ulong, string>();
		var scanner = new PatchTocScanner();
		// 按管理器平铺补丁后的文件名排序，对齐打包器的覆盖顺序。
		foreach (var path in OrderedPatchFiles(patchFiles, flatten: true))
		{
			var toc = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
			var name = Path.GetFileName(path);
			foreach (var entry in scanner.ScanEntries(toc, name))
				if (entry.AssetKey.TypeId == UnitTypeId) units[entry.AssetKey.FileId] = name[..name.IndexOf('.')];
		}
		var (body, helmet) = SelectTargets(bindingCatalogPath, units);
		return new[] { body, helmet }.OfType<BindingTargetInfo>().Select(target => target.Archive).ToArray();
	}

	private static Dictionary<AssetKey, RawPatchEntry> ReadModPatchEntries(string modDir, PatchTocScanner scanner,
		SearchOption searchOption, CancellationToken cancellationToken)
	{
		var dict = new Dictionary<AssetKey, RawPatchEntry>();
		foreach (var tocPath in OrderedPatchFiles(Directory.EnumerateFiles(modDir, "*.patch_*", searchOption)))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var tocBytes = File.ReadAllBytes(tocPath);
			var gpuPath = tocPath + ".gpu_resources";
			var stmPath = tocPath + ".stream";
			var gpuBytes = File.Exists(gpuPath) ? File.ReadAllBytes(gpuPath) : [];
			var stmBytes = File.Exists(stmPath) ? File.ReadAllBytes(stmPath) : [];

			var fileName = Path.GetFileName(tocPath);
			var archive = fileName[..fileName.IndexOf('.')];
			var entries = scanner.ScanEntries(tocBytes, fileName);
			foreach (var e in entries)
			{
				var tSlice = e.TocDataSize > 0 ? tocBytes.AsSpan(checked((int)e.TocDataOffset), checked((int)e.TocDataSize)).ToArray() : [];
				var gSlice = e.GpuResourceSize > 0 ? gpuBytes.AsSpan(checked((int)e.GpuResourceOffset), checked((int)e.GpuResourceSize)).ToArray() : [];
				var sSlice = e.StreamSize > 0 ? stmBytes.AsSpan(checked((int)e.StreamOffset), checked((int)e.StreamSize)).ToArray() : [];
				dict[e.AssetKey] = new RawPatchEntry(e.AssetKey.TypeId, e.AssetKey.FileId, archive, tSlice, gSlice, sSlice);
			}
		}

		return dict;
	}

	private static async Task DumpRawEntriesAsync(IReadOnlyList<RawPatchEntry> entries, string destDir, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(destDir);
		var indexRows = new List<object>(entries.Count);
		foreach (var e in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var prefix = $"{e.TypeId:x16}_{e.FileId:x16}";
			await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".toc"), e.TocData, cancellationToken).ConfigureAwait(false);
			if (e.GpuData.Length > 0)
				await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".gpu"), e.GpuData, cancellationToken).ConfigureAwait(false);
			if (e.StreamData.Length > 0)
				await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".stream"), e.StreamData, cancellationToken).ConfigureAwait(false);

			indexRows.Add(new
			{
				typeHex = e.TypeId.ToString("x16", CultureInfo.InvariantCulture),
				fileHex = e.FileId.ToString("x16", CultureInfo.InvariantCulture),
				tocSize = e.TocData.Length,
				gpuSize = e.GpuData.Length,
				streamSize = e.StreamData.Length,
			});
		}

		await File.WriteAllTextAsync(
			Path.Combine(destDir, "index.json"),
			JsonSerializer.Serialize(indexRows, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
	}

	private static (BindingTargetInfo? Body, BindingTargetInfo? Helmet) SelectTargets(string bindingCatalogPath,
		IReadOnlyDictionary<ulong, string> modUnits)
	{
		var targets = ReadBindingTargets(bindingCatalogPath).Values;
		return (SelectBestArchive(targets.Where(t => t.Domain == "body"), modUnits, "护甲"),
			SelectBestArchive(targets.Where(t => t.Domain == "helmet"), modUnits, "头盔"));
	}

	private static BindingTargetInfo? SelectBestArchive(IEnumerable<BindingTargetInfo> candidates,
		IReadOnlyDictionary<ulong, string> modUnits, string label)
	{
		var matches = candidates.Select(target => (Target: target, Hits: target.OwnedUnits.Where(modUnits.ContainsKey).ToArray()))
			.Where(match => match.Hits.Length != 0).ToArray();
		if (matches.Length == 0) return null;
		var count = matches.SelectMany(match => match.Hits).Distinct().Count();
		var complete = matches.Where(match => match.Hits.Length == count).ToArray();
		if (complete.Length == 1) return complete[0].Target;
		var named = complete.Where(match => match.Hits.Any(id =>
			modUnits[id].Equals(match.Target.Archive, StringComparison.OrdinalIgnoreCase))).ToArray();
		if (named.Length == 1) return named[0].Target;
		throw new InvalidDataException($"普通 Mod 的{label} Unit 无法唯一匹配 IAS 底子档案，请拆分来源：{string.Join("、",
			matches.Select(match => match.Target.Archive))}。");
	}

	private static object BuildAuthoringProject(
		UnitTransformInfo stockAvatar,
		IReadOnlyDictionary<uint, string> nameByHash,
		IReadOnlyDictionary<string, (string? Parent, double[] RestLocal)> extraSharedBones)
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

		string NameOf(int idx)
		{
			var h = stockAvatar.NameHashes[idx];
			return nameByHash.TryGetValue(h, out var n) ? n : $"custom_0x{h:x8}";
		}

		var sharedBones = new List<object>(stockCount + extraSharedBones.Count);
		for (var i = 0; i < stockCount; i++)
		{
			var entry = stockAvatar.Entries[i];
			var hasParent = i > 0 && entry.Increment != 0 && entry.ParentIndex != i;
			var pIdx = hasParent ? (int)entry.ParentIndex : -1;
			var parentName = pIdx >= 0 ? NameOf(pIdx) : null;
			var stockLocal = pIdx < 0 ? stockWorld[i] : stockWorld[pIdx].Inverse() * stockWorld[i];
			var mat34 = ToMat34(stockLocal);

			sharedBones.Add(new
			{
				name = NameOf(i),
				parent = parentName,
				is_avatar_source = true,
				rest_local = mat34,
				avatar_source_rest_local = mat34,
			});
		}

		foreach (var (extraName, (parentName, localMat34)) in extraSharedBones.OrderBy(kv => kv.Key, StringComparer.Ordinal))
		{
			sharedBones.Add(new
			{
				name = extraName,
				parent = parentName,
				is_avatar_source = false,
				rest_local = localMat34,
				avatar_source_rest_local = localMat34,
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

	private static List<object> BuildSnapshotNodes(UnitTransformInfo info, IReadOnlyDictionary<uint, string> nameByHash)
	{
		var nodes = new List<object>(info.NameHashes.Count);
		for (var i = 0; i < info.NameHashes.Count; i++)
		{
			var hash = info.NameHashes[i];
			var name = nameByHash.TryGetValue(hash, out var known) ? known : $"custom_0x{hash:x8}";
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
}
