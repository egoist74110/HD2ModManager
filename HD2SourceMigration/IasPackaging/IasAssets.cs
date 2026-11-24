using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2SourceMigration.IasPackaging;

internal static class IasAssets
{
	internal sealed record DumpedArchive(byte[] TocBytes, HashSet<ulong> UnitIds);
	internal sealed record BindingTargetInfo(string Archive, string Domain, HashSet<ulong> OwnedUnits);

	internal static (byte[] TocData, byte[] GpuData) BuildIasPointUnit(UnitMeshModel donorModel, byte[] donorTocData)
	{
		var streamCompiler = new CanonicalStreamContractCompiler();
		var preparation = new CanonicalMeshPreparation();
		var rebuilder = new CanonicalUnitRebuilder();

		var pointMeshes = new List<UnitRawMeshData>(donorModel.Meshes.Count);
		foreach (var meshInfo in donorModel.Meshes)
		{
			var raw = donorModel.RawMeshData.Single(r => r.MeshInfoIndex == meshInfo.Index);
			var stream = donorModel.Streams.Single(s => s.Index == (int)meshInfo.StreamIndex);
			pointMeshes.Add(CreatePointMesh(raw, stream));
		}

		var compiledStreams = streamCompiler.TryCompile(donorModel, pointMeshes);
		if (!compiledStreams.IsValid)
			throw new InvalidOperationException("Point unit stream compile failed.");

		var updatedModel = donorModel with { Streams = compiledStreams.Streams };
		var prepared = new List<UnitRawMeshData>(pointMeshes.Count);
		foreach (var pm in pointMeshes)
		{
			var stream = updatedModel.Streams.Single(s => s.Index == (int)pm.StreamIndex);
			var enc = preparation.TryPrepare(pm, stream);
			if (!enc.IsValid || enc.Mesh is null)
				throw new InvalidOperationException("Point unit mesh preparation failed.");
			prepared.Add(enc.Mesh);
		}

		var bones = updatedModel.BoneInfos
			.Select((b, idx) => new CanonicalBoneInfoRebuild(idx, b))
			.ToArray();

		var rebuilt = rebuilder.TryRebuild(updatedModel, donorTocData, prepared, bones, updatedModel.Materials);
		if (!rebuilt.IsValid || rebuilt.Output is null)
			throw new InvalidOperationException($"Point unit rebuild failed: {string.Join("; ", rebuilt.Diagnostics.Select(d => d.Message))}");

		var toc = FixSelfParentedTransformEntries(rebuilt.Output.TocData);
		if (donorModel.CustomizationInfoOffset == 0)
			toc = AppendEmptyCustomization(toc);
		return (toc, rebuilt.Output.GpuData);
	}

	internal static UnitRawMeshData PreparePointMesh(UnitRawMeshData raw, UnitStreamInfo stream)
	{
		var prepared = new CanonicalMeshPreparation().TryPrepare(CreatePointMesh(raw, stream), stream);
		return prepared.Mesh ?? throw new InvalidOperationException(
			$"Point mesh {raw.MeshInfoIndex} preparation failed: {string.Join("; ", prepared.Diagnostics.Select(d => d.Message))}");
	}

	private static byte[] AppendEmptyCustomization(byte[] toc)
	{
		// 部分原版 Unit 缺少定制区。用四个以 NUL 结尾的空字段表达，供 IAS 严格读取。
		const int headerSize = 24;
		const int stringSize = 5;
		const int gap = 12;
		Span<byte> block = stackalloc byte[headerSize + 4 * stringSize + 3 * gap];
		block.Clear();
		for (var field = 0; field < 4; field++)
			BinaryPrimitives.WriteUInt32LittleEndian(block[(headerSize + field * (stringSize + gap))..], 1);
		return AppendCustomization(toc, block);
	}

	internal static byte[] CopyStockCustomization(byte[] toc, ReadOnlySpan<byte> stockToc, uint stockOffset)
	{
		if (stockOffset == 0)
			return AppendEmptyCustomization(toc);

		var start = checked((int)stockOffset);
		var end = checked(start + 24);
		for (var field = 0; field < 4; field++)
		{
			var length = BinaryPrimitives.ReadUInt32LittleEndian(stockToc[end..]);
			if (length is < 1 or > 512)
				throw new InvalidDataException("Invalid stock Unit customization metadata for IAS.");
			end = checked(end + 4 + (int)length + (field < 3 ? 12 : 0));
		}
		return AppendCustomization(toc, stockToc[start..end]);
	}

	private static byte[] AppendCustomization(byte[] toc, ReadOnlySpan<byte> block)
	{
		// 追加定制区，保留 Unit 内已有表的偏移。
		const int customizationOffsetField = 0x4c;
		var offset = checked((toc.Length + 3) & ~3);
		var output = new byte[checked(offset + block.Length)];
		toc.CopyTo(output, 0);
		block.CopyTo(output.AsSpan(offset));
		BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(customizationOffsetField, 4), (uint)offset);
		return output;
	}

	private static UnitRawMeshData CreatePointMesh(UnitRawMeshData raw, UnitStreamInfo stream)
	{
		var matLayout = CanonicalFinalMaterialLayout.TryCreate(raw);
		if (!matLayout.IsValid)
			throw new InvalidOperationException($"Point unit material layout failed on mesh {raw.MeshInfoIndex}.");

		var zeroVertex = new UnitRawVertexRecord(0u, [], stream.Components.Select(CreateZeroPointComponent).ToArray());
		var point = new UnitTriangleIndices(0, 0, 0);
		return raw with
		{
			Sections = raw.Sections.Select((section, index) => new UnitRawMeshSectionData(
				matLayout.GetMaterialOrdinal(index), section.MaterialSlotId, index == 0 ? [point] : [])).ToArray(),
			Triangles = [point],
			Vertices = [zeroVertex],
		};
	}

	private static UnitVertexComponentValue CreateZeroPointComponent(UnitStreamComponentInfo component)
	{
		var values = component.Type switch
		{
			0 => component.FormatName == "float" ? new[] { 0f } : [0f, 0f, 0f, 0f],
			1 or 3 => [0f, 0f, 1f, 0f],
			2 => [1f, 0f, 0f, 0f],
			4 => [0f, 0f, 0f, 0f],
			5 => [1f, 1f, 1f, 1f],
			7 => [1f, 0f, 0f, 0f],
			_ => [],
		};
		var uintValues = component.Type == 6 ? new[] { 0u, 0u, 0u, 0u } : [];
		return new(component.Type, component.TypeName, component.Format, component.FormatName, component.Index, values, uintValues, []);
	}

	internal static byte[] FixSelfParentedTransformEntries(byte[] tocData)
	{
		var copy = (byte[])tocData.Clone();
		var at = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(0x34, 4)));
		var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(at, 4)));
		var linksOffset = at + 16 + count * 128;
		for (var i = 0; i < count; i++)
		{
			var pos = linksOffset + i * 4;
			var link = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(pos, 4));
			var increment = link & 0xFFFFu;
			var parent = link >> 16;
			if (increment == 1u && parent == (uint)i)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(pos, 4), 0u);
			}
		}

		return copy;
	}

	private const ulong PackageTypeId = 0xad9c6d9ed1e5e77aUL;

	internal static async Task<DumpedArchive> DumpStockArchiveAsync(
		GameDataPackageResolver resolver,
		PatchTocScanner scanner,
		string archiveName,
		string destDir,
		CancellationToken cancellationToken = default,
		bool inventoryOnly = false)
	{
		Directory.CreateDirectory(destDir);
		var toc = await resolver.GetPackageTocAsync(archiveName, cancellationToken).ConfigureAwait(false)
			?? throw new FileNotFoundException($"Stock archive {archiveName} not found.");
		var entries = scanner.ScanEntries(toc.Data, archiveName, toc.UsesSlimEntryOffset);
		var archiveId = ulong.Parse(archiveName, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		var unitIds = new HashSet<ulong>();
		var indexRows = new List<object>(entries.Count);

		foreach (var e in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (e.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
				unitIds.Add(e.AssetKey.FileId);
			// VPK 的原版资源键只用于冲突检查。SDK 需要读取内容的只有底子的 Package。
			var omitPayload = inventoryOnly && e.AssetKey != new AssetKey(PackageTypeId, archiveId);
			if (omitPayload)
			{
				indexRows.Add(new { typeHex = $"{e.AssetKey.TypeId:x16}", fileHex = $"{e.AssetKey.FileId:x16}", payloadOmitted = true });
				continue;
			}
			var mainBytes = e.TocDataSize == 0
				? []
				: (await resolver.GetPackageResourceAsync(archiveName, e.TocDataOffset, e.TocDataSize, cancellationToken).ConfigureAwait(false))!;
			var gpuBytes = e.GpuResourceSize == 0
				? []
				: (await resolver.GetPackageResourceAsync(archiveName + ".gpu_resources", e.GpuResourceOffset, e.GpuResourceSize, cancellationToken).ConfigureAwait(false))!;
			var streamBytes = e.StreamSize == 0
				? []
				: (await resolver.GetPackageResourceAsync(archiveName + ".stream", e.StreamOffset, e.StreamSize, cancellationToken).ConfigureAwait(false))!;

			var prefix = $"{e.AssetKey.TypeId:x16}_{e.AssetKey.FileId:x16}";
			await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".toc"), mainBytes.AsSpan(0, checked((int)e.TocDataSize)).ToArray(), cancellationToken).ConfigureAwait(false);
			if (e.GpuResourceSize > 0)
				await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".gpu"), gpuBytes.AsSpan(0, checked((int)e.GpuResourceSize)).ToArray(), cancellationToken).ConfigureAwait(false);
			if (e.StreamSize > 0)
				await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".stream"), streamBytes.AsSpan(0, checked((int)e.StreamSize)).ToArray(), cancellationToken).ConfigureAwait(false);

			indexRows.Add(new
			{
				typeHex = e.AssetKey.TypeId.ToString("x16", CultureInfo.InvariantCulture),
				fileHex = e.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture),
				tocSize = e.TocDataSize,
				gpuSize = e.GpuResourceSize,
				streamSize = e.StreamSize,
			});
		}

		if (!entries.Any(e => e.AssetKey.TypeId == PackageTypeId && e.AssetKey.FileId == archiveId))
		{
			foreach (var candidatePkg in await resolver.GetPackageNamesAsync(cancellationToken).ConfigureAwait(false))
			{
				var cToc = await resolver.GetPackageTocAsync(candidatePkg, cancellationToken).ConfigureAwait(false);
				if (cToc is null)
					continue;
				var cEntries = scanner.ScanEntries(cToc.Data, candidatePkg, cToc.UsesSlimEntryOffset);
				var hit = cEntries.FirstOrDefault(e => e.AssetKey.TypeId == PackageTypeId && e.AssetKey.FileId == archiveId);
				if (hit is not null && hit.TocDataSize > 0)
				{
					var pkgBytes = (await resolver.GetPackageResourceAsync(candidatePkg, hit.TocDataOffset, hit.TocDataSize, cancellationToken).ConfigureAwait(false))!;
					var prefix = $"{PackageTypeId:x16}_{archiveId:x16}";
					await File.WriteAllBytesAsync(Path.Combine(destDir, prefix + ".toc"), pkgBytes.AsSpan(0, checked((int)hit.TocDataSize)).ToArray(), cancellationToken).ConfigureAwait(false);
					indexRows.Add(new
					{
						typeHex = PackageTypeId.ToString("x16", CultureInfo.InvariantCulture),
						fileHex = archiveId.ToString("x16", CultureInfo.InvariantCulture),
						tocSize = hit.TocDataSize,
						gpuSize = 0u,
						streamSize = 0u,
					});
					break;
				}
			}
		}

		await File.WriteAllTextAsync(
			Path.Combine(destDir, "index.json"),
			JsonSerializer.Serialize(indexRows, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

		return new DumpedArchive(toc.Data, unitIds);
	}

	internal static Dictionary<uint, string> BuildHashLookup(UnitBoneNames names)
	{
		var dict = new Dictionary<uint, string>();
		for (var i = 0; i < Math.Min(names.Hashes.Count, names.Names.Count); i++)
			dict.TryAdd(names.Hashes[i], names.Names[i]);
		return dict;
	}

	internal static Dictionary<string, HashSet<ulong>> ReadPartCatalog(string path)
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var res = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
		foreach (var prop in CatalogObject(doc, path, "part_slots").EnumerateObject())
		{
			var set = new HashSet<ulong>();
			foreach (var item in prop.Value.EnumerateArray())
				set.Add(ulong.Parse(item.GetString()!, CultureInfo.InvariantCulture));
			res[prop.Name] = set;
		}
		return res;
	}

	internal static Dictionary<string, BindingTargetInfo> ReadBindingTargets(string path)
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var res = new Dictionary<string, BindingTargetInfo>(StringComparer.OrdinalIgnoreCase);
		foreach (var prop in CatalogObject(doc, path, "targets").EnumerateObject())
		{
			if (prop.Name.Length != 16 || !prop.Name.All(char.IsAsciiHexDigit) ||
				prop.Value.ValueKind != JsonValueKind.Object ||
				!prop.Value.TryGetProperty("domain", out var domainValue) || domainValue.ValueKind != JsonValueKind.String ||
				!prop.Value.TryGetProperty("equipment", out var equipment) || equipment.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException($"AQ SDK 绑定目录 {path} 的归档 {prop.Name} 缺少有效的 domain 或 equipment。");
			var domain = domainValue.GetString()!.ToLowerInvariant();
			HashSet<ulong>? intersection = null;
			foreach (var eq in equipment.EnumerateArray())
			{
				var eqSet = new HashSet<ulong>();
				if (eq.ValueKind != JsonValueKind.Object || !eq.TryGetProperty("unit_ids", out var units) || units.ValueKind != JsonValueKind.Array)
					throw new InvalidDataException($"AQ SDK 绑定目录 {path} 的归档 {prop.Name} 缺少 unit_ids。");
				foreach (var unit in units.EnumerateArray())
				{
					if (unit.ValueKind != JsonValueKind.String || !ulong.TryParse(unit.GetString(),
						NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
						throw new InvalidDataException($"AQ SDK 绑定目录 {path} 的归档 {prop.Name} 含无效的 Unit ID。");
					eqSet.Add(id);
				}
				if (intersection is null)
					intersection = eqSet;
				else
					intersection.IntersectWith(eqSet);
			}
			if (!res.TryAdd(prop.Name, new BindingTargetInfo(prop.Name.ToLowerInvariant(), domain, intersection ?? [])))
				throw new InvalidDataException($"AQ SDK 绑定目录 {path} 重复声明归档 {prop.Name}。");
		}
		return res;
	}

	private static JsonElement CatalogObject(JsonDocument doc, string path, string name) =>
		doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var rows) &&
			rows.ValueKind == JsonValueKind.Object ? rows
			: throw new InvalidDataException($"AQ SDK 数据表 {path} 缺少有效的 {name}。");
}
