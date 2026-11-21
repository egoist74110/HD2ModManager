using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration;
using HD2SourceMigration.Retarget;
using HD2SourceModel;

using static IasModelCli.IasAssets;

namespace IasModelCli;

public sealed record IasEquipmentSelection(string? BodyArchive, string? HelmetArchive);

/// <summary>
/// Extracts authored HD2 equipment meshes and skin weights for IAS.
/// Matches the source Unit IDs to body and helmet archives and fills unused slots with point units.
/// </summary>
public static class PatchExtractor
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453fUL;
	private const ulong PackageTypeId = 0xad9c6d9ed1e5e77aUL;
	private static readonly Regex PatchFileName = new(
		@"^[0-9a-f]{16}\.patch_([0-9]+)$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private sealed record RawPatchEntry(ulong TypeId, ulong FileId, byte[] TocData, byte[] GpuData, byte[] StreamData);

	internal static Task RunAsync(IReadOnlyDictionary<string, string> opts) =>
		ExtractAsync(opts["--mod-dir"], opts["--game-data"], opts["--bone-hash"],
			opts["--part-catalog"], opts["--binding-catalog"], opts["--out"],
			SearchOption.AllDirectories, CancellationToken.None);

	public static async Task ExtractAsync(string modDir, string gameDataDir, string boneHashPath,
		string partCatalogPath, string bindingCatalogPath, string outDir, SearchOption searchOption,
		CancellationToken cancellationToken, IasEquipmentSelection? equipment = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(outDir);
		Console.WriteLine($"[IasModelCli] Scanning third-party mod (read-only): {modDir}");

		var tocScanner = new PatchTocScanner();
		var resolver = new GameDataPackageResolver(gameDataDir);
		var unitMeshReader = new GameDataUnitMeshReader(resolver, tocScanner);
		var avatarReader = new CanonicalAvatarRigReader(resolver, tocScanner);

		var stockAvatarTransform = await avatarReader.ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		var boneNames = SourceRetargetPipeline.ReadBoneHash(boneHashPath);
		var nameByHash = BuildHashLookup(boneNames);

		// 1. Read all entries from all .patch_* files in modDir (later patch overrides earlier patch on same AssetKey)
		var modEntries = ReadModPatchEntries(modDir, tocScanner, searchOption, cancellationToken);
		var modUnits = modEntries.Values.Where(e => e.TypeId == UnitTypeId).ToDictionary(e => e.FileId, e => e);
		var modNonUnits = modEntries.Values
			.Where(e => e.TypeId != UnitTypeId && e.TypeId != PackageTypeId)
			.OrderBy(e => e.TypeId)
			.ThenBy(e => e.FileId)
			.ToArray();

		Console.WriteLine($"[IasModelCli] Found {modUnits.Count} Unit entries and {modNonUnits.Length} Material/Texture/Aux entries.");
		if (modUnits.Count == 0)
			throw new InvalidOperationException("No Unit entries found in mod patches.");

		// 2. Auto-detect target BODY and HELMET stock archives
		var partSlots = ReadPartCatalog(partCatalogPath);
		var bindingTargets = ReadBindingTargets(bindingCatalogPath);

		var bodyTarget = equipment is null
			? SelectBestArchive(bindingTargets.Values.Where(t => t.Domain == "body"), modUnits.Keys)
			: ResolveSelectedArchive(bindingTargets, "body", equipment.BodyArchive, modUnits.Keys);
		var helmetTarget = equipment is null
			? SelectBestArchive(bindingTargets.Values.Where(t => t.Domain == "helmet"), modUnits.Keys)
			: ResolveSelectedArchive(bindingTargets, "helmet", equipment.HelmetArchive, modUnits.Keys);
		if (bodyTarget is null && helmetTarget is null)
			throw new InvalidOperationException("Could not match mod Unit IDs to any supported IAS equipment archive.");

		if (bodyTarget is not null)
			Console.WriteLine($"[IasModelCli] Matched BODY archive: {bodyTarget.Archive} ({modUnits.Keys.Intersect(bodyTarget.OwnedUnits).Count()}/{bodyTarget.OwnedUnits.Count} units)");
		if (helmetTarget is not null)
			Console.WriteLine($"[IasModelCli] Matched HELMET archive: {helmetTarget.Archive} ({modUnits.Keys.Intersect(helmetTarget.OwnedUnits).Count()}/{helmetTarget.OwnedUnits.Count} units)");

		// 3. Dump raw_materials for package_model.py
		var rawMatDir = Path.Combine(outDir, "raw_materials");
		await DumpRawEntriesAsync(modNonUnits, rawMatDir, cancellationToken).ConfigureAwait(false);

		var authoredUnitsDir = Path.Combine(outDir, "units_authored");
		var pointUnitsDir = Path.Combine(outDir, "units_point");
		Directory.CreateDirectory(authoredUnitsDir);
		Directory.CreateDirectory(pointUnitsDir);

		// Track custom bones across all authored units for authoring_project.json
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

		// 4. Write authoring_project.json (stock avatar source bones + any custom bones found in third-party Units)
		var authoringProject = BuildAuthoringProject(stockAvatarTransform, nameByHash, extraSharedBones);
		await File.WriteAllTextAsync(
			Path.Combine(outDir, "authoring_project.json"),
			JsonSerializer.Serialize(authoringProject, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

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
			JsonSerializer.Serialize(summaryManifest, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

		Console.WriteLine($"[IasModelCli] Done! Wrote IAS conversion inputs to {outDir}");
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
		var candidateSlots = domain == "HELMET"
			? new[] { "Head" }
			: new[] { "Hips", "LeftArm", "RightArm", "Torso", "Torso_Armor", "LeftLeg", "RightLeg" };

		var stockHashes = new HashSet<uint>(stockAvatar.NameHashes);
		var authoredRecords = new List<object>();
		var coveredNativeIds = new HashSet<ulong>();

		foreach (var slotName in candidateSlots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!partSlots.TryGetValue(slotName, out var knownSlotIds))
				continue;

			var matchingNatives = stockUnitIds.Intersect(knownSlotIds).OrderBy(x => x).ToArray();
			if (matchingNatives.Length == 0)
				continue;

			// Identify which matching native units in the mod carry real visible geometry (not collapsed dummy hiders)
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

			// AQSDK build_save_jobs expands a PartSlot to all matching native variants in the archive.
			// If a third-party mod authored only one body-type variant of the slot, clone its geometry onto the missing variant with stock CustomizationInfo!
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
				await File.WriteAllBytesAsync(tocFile, finalToc).ConfigureAwait(false);
				await File.WriteAllBytesAsync(gpuFile, finalGpu).ConfigureAwait(false);

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

		// Convert all remaining equipment-owned native units (including collapsed hiders in the mod!) into strict IAS 1-vertex Point Units
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
			await File.WriteAllBytesAsync(tocFile, ptToc).ConfigureAwait(false);
			await File.WriteAllBytesAsync(gpuFile, ptGpu).ConfigureAwait(false);

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

		// IAS validates every mesh record. Preserve indexed geometry and turn an
		// intentionally empty mesh/LOD into one degenerate triangle.
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

		// Ensure at least one bone is recorded if unit uses rigid transform attachment instead of skinning BoneInfo
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

	private static Dictionary<AssetKey, RawPatchEntry> ReadModPatchEntries(string modDir, PatchTocScanner scanner,
		SearchOption searchOption, CancellationToken cancellationToken)
	{
		var patchFiles = Directory.EnumerateFiles(modDir, "*.patch_*", searchOption)
			.Select(path => (Path: path, Match: PatchFileName.Match(Path.GetFileName(path))))
			.Where(file => file.Match.Success)
			.OrderBy(file => int.Parse(file.Match.Groups[1].Value, CultureInfo.InvariantCulture))
			.ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
			.Select(file => file.Path);

		var dict = new Dictionary<AssetKey, RawPatchEntry>();
		foreach (var tocPath in patchFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var tocBytes = File.ReadAllBytes(tocPath);
			var gpuPath = tocPath + ".gpu_resources";
			var stmPath = tocPath + ".stream";
			var gpuBytes = File.Exists(gpuPath) ? File.ReadAllBytes(gpuPath) : [];
			var stmBytes = File.Exists(stmPath) ? File.ReadAllBytes(stmPath) : [];

			var entries = scanner.ScanEntries(tocBytes, Path.GetFileName(tocPath));
			foreach (var e in entries)
			{
				var tSlice = e.TocDataSize > 0 ? tocBytes.AsSpan(checked((int)e.TocDataOffset), checked((int)e.TocDataSize)).ToArray() : [];
				var gSlice = e.GpuResourceSize > 0 ? gpuBytes.AsSpan(checked((int)e.GpuResourceOffset), checked((int)e.GpuResourceSize)).ToArray() : [];
				var sSlice = e.StreamSize > 0 ? stmBytes.AsSpan(checked((int)e.StreamOffset), checked((int)e.StreamSize)).ToArray() : [];
				dict[e.AssetKey] = new RawPatchEntry(e.AssetKey.TypeId, e.AssetKey.FileId, tSlice, gSlice, sSlice);
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
			await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".toc"), e.TocData).ConfigureAwait(false);
			if (e.GpuData.Length > 0)
				await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".gpu"), e.GpuData).ConfigureAwait(false);
			if (e.StreamData.Length > 0)
				await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".stream"), e.StreamData).ConfigureAwait(false);

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
			JsonSerializer.Serialize(indexRows, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
	}

	private static BindingTargetInfo? SelectBestArchive(IEnumerable<BindingTargetInfo> candidates, IEnumerable<ulong> modUnitIds)
	{
		var unitSet = modUnitIds.ToHashSet();
		return candidates
			.Select(c => (Target: c, Hits: c.OwnedUnits.Intersect(unitSet).Count()))
			.Where(x => x.Hits > 0)
			.OrderByDescending(x => x.Hits)
			.ThenBy(x => x.Target.Archive, StringComparer.Ordinal)
			.Select(x => x.Target)
			.FirstOrDefault();
	}

	private static BindingTargetInfo? ResolveSelectedArchive(
		IReadOnlyDictionary<string, BindingTargetInfo> targets, string domain, string? archive, IEnumerable<ulong> unitIds)
	{
		if (archive is null)
			return null;
		if (!targets.TryGetValue(archive, out var target) || target.Domain != domain)
			throw new InvalidOperationException($"IAS 不支持来源记录中的装备归档 {archive}（{domain}）。");
		if (!unitIds.Any(target.OwnedUnits.Contains))
			throw new InvalidOperationException($"原始 Patch 中找不到来源归档 {archive} 的模型 Unit。");
		return target;
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
