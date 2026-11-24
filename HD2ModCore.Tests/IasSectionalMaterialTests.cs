using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Ias;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using GameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using PatchAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using PatchEntryPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using PatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using PatchTocScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using StingrayMaterialReferenceReader = HD2ModAdaptation.PatchReconstruction.StingrayMaterialReferenceReader;

namespace HD2ModCore.Tests;

public sealed class IasSectionalMaterialTests(ITestOutputHelper output)
{
	[Fact]
	public async Task 仅补齐可达父材质及其贴图()
	{
		var root = Path.Combine(Path.GetTempPath(), "ias-material-closure-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			const ulong material = MaterialDependencyResolver.MaterialTypeId;
			const ulong texture = MaterialDependencyResolver.TextureTypeId;
			var keys = new SectionalSourceAssetKeys([], [Format(material, 1)], [Format(texture, 10)]);
			var closed = await IasSectionalSource.ExtendMaterialClosureAsync(keys,
				[MakeMaterial(1, 2, 10), MakeMaterial(2, 3, 20), MakeMaterial(3, 2, 30), MakeMaterial(4, 0, 40)], default);

			Assert.Equal([Format(material, 1), Format(material, 2), Format(material, 3)], closed.Materials);
			Assert.Equal([Format(texture, 10), Format(texture, 20), Format(texture, 30)], closed.Textures);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}

		PatchTocEntry MakeMaterial(ulong id, ulong parent, ulong texture)
		{
			var data = new byte[148];
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), parent);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(140), texture);
			var name = $"{id:x16}.material";
			var path = Path.Combine(root, name);
			File.WriteAllBytes(path, data);
			return new(new(MaterialDependencyResolver.MaterialTypeId, id), path, name, 0, 0, 0, 0, 0, (uint)data.Length, 0, 0, 0, 0, 0);
		}

		static string Format(ulong type, ulong id) => SectionalConversionPlanner.FormatKey(new(type, id));
	}

	[Fact]
	public async Task Milfy普通Mod转素体后生效层保留身体和材质闭包()
	{
		// 真数据夹具由 IAS CLI 转出。游戏目录与索引只读，两个入库步骤在临时库执行。
		var package = Environment.GetEnvironmentVariable("HD2_TEST_IAS_SECTIONAL_PACKAGE");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_IAS_SECTIONAL_GAME");
		var index = Environment.GetEnvironmentVariable("HD2_TEST_IAS_SECTIONAL_INDEX");
		if (package is null || game is null || index is null) return;

		var root = Path.Combine(Path.GetTempPath(), "hd2-ias-sectional", Guid.NewGuid().ToString("N"));
		var paths = new StoragePaths(root);
		Directory.CreateDirectory(paths.IndexDirectory);
		try
		{
			File.Copy(index, paths.DbPath);
			var importer = CoreServices.CreateModImportExecutionService(paths);
			var imported = await importer.ImportFolderAsync(package);
			var host = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard);
			var graph = OptionRelationshipGraph.Create(imported.Snapshot);
			var sources = graph.GetDescendantOptions(host.Id).Prepend(host)
				.Select(node => new SectionalSourceCandidate(node.Id.Value.ToString("N"), node.Metadata.Name,
					Path.Combine(paths.ModsDirectory, node.RelativePath)))
				.Where(source => SectionalSplitter.EnumeratePatchFiles(source.Directory).Count != 0)
				.ToArray();
			Assert.Equal(5, sources.Length);
			var patches = sources.SelectMany(source => SectionalSplitter.EnumeratePatchFiles(source.Directory)).ToArray();
			var scanner = new PatchTocScanner();
			var original = new List<PatchTocEntry>();
			foreach (var patch in patches)
				original.AddRange(await scanner.ScanEntriesAsync(patch));
			var units = original.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
				.Select(entry => entry.AssetKey).Distinct().ToArray();
			var whitelist = await SectionalWhitelistBuilder.BuildFromPatchesAsync(units, patches);
			Assert.Null(SectionalWhitelistBuilder.Describe(whitelist));

			var staging = Path.Combine(root, "Milfy-sectional");
			Directory.CreateDirectory(staging);
			var assembly = await SectionalSourceAssembler.AssembleAsync(staging, sources,
				new("d2fde496226be96f", "DP-8"), new("59d238094a164c88", "DP-8"), whitelist, paths: paths);
			Assert.True(assembly.Ok, assembly.Error);
			var assembled = await importer.ImportFolderAsync(staging);
			var modelNode = assembled.Snapshot.Nodes[Assert.Single(assembled.ImportedNodeIds)];
			var model = Path.Combine(paths.ModsDirectory, modelNode.RelativePath);
			var context = new VpkHostContext(game, paths);
			var result = await Legacy2SectionalConverter.RunAsync(modelNode.Id.Value.ToString("N"), model, context);
			Assert.True(result.Ok, result.Error);
			output.WriteLine($"材质组 {result.MaterialGroups}");
			var gameAssets = new Dictionary<ulong, IReadOnlyList<PatchTocEntry>>();
			var regular = await VerifyOverwriteAsync(model, game, original, gameAssets, lowPollution: false);

			var lowPollution = await ModCompileService.CompileAsync(modelNode.Id.Value.ToString("N"), model,
				context with { LowPollutionMode = true });
			Assert.True(lowPollution.Ok, lowPollution.Error);
			output.WriteLine(string.Join(Environment.NewLine, lowPollution.Notes));
			var compressed = await VerifyOverwriteAsync(model, game, original, gameAssets, lowPollution: true);
			Assert.Equal(regular, compressed);
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				VpkPayloadSwap.ClearReadOnlyForWrite(file);
			Directory.Delete(root, recursive: true);
		}
	}

	private async Task<ulong[]> VerifyOverwriteAsync(string model, string game, IReadOnlyList<PatchTocEntry> original,
		Dictionary<ulong, IReadOnlyList<PatchTocEntry>> gameAssets, bool lowPollution)
	{
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(model);
		var map = await WardrobeSectionMapFile.ReadAsync(Path.Combine(overwrite, WardrobeSectionMapFile.FileName));
		var scanner = new PatchTocScanner();
		var entries = new List<PatchTocEntry>();
		foreach (var patch in SectionalSplitter.EnumeratePatchFiles(overwrite))
			entries.AddRange(await scanner.ScanEntriesAsync(patch));
		var byKey = entries.ToLookup(entry => entry.AssetKey);
		var sourceResources = original.Select(entry => entry.AssetKey).ToHashSet();
		var materials = new Queue<ulong>();
		var mappedSections = 0;
		var sections = 0;
		var payloadReader = new PatchEntryPayloadReader();
		var meshReader = new PatchUnitMeshReader();
		var units = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).ToArray();
		var identities = map.Units.ToDictionary(unit => unit.FileId, StringComparer.Ordinal);
		foreach (var entry in units)
		{
			var id = entry.AssetKey.FileId.ToString("x16");
			var unit = await meshReader.ReadStructureOnlyAsync(entry, entries);
			var bindings = unit.Model.Materials.ToLookup(binding => binding.SectionId, binding => binding.MaterialId);
			var lod0 = 0;
			var used = new HashSet<ulong>();
			foreach (var mesh in unit.Model.Meshes.Where(mesh => mesh.LodIndex >= 0))
			foreach (var section in mesh.Sections.Where(section => section.NumIndices > 0))
			{
				var material = Assert.Single(bindings[section.MaterialSlotId].Distinct());
				Assert.NotEqual(0UL, material);
				used.Add(material);
				if (mesh.LodIndex == 0) lod0++;
			}
			Assert.True(lod0 > 0, id);
			sections += lod0;
			if (identities.ContainsKey(id)) mappedSections += lod0;
			var slot = identities.TryGetValue(id, out var identity) ? $"{identity.Slot}/{identity.BodyType}" : "合体候选";
			output.WriteLine($"{slot} {id} · LOD0 {lod0} sections · "
				+ string.Join(" ", used.Order().Select(material => material.ToString("x16"))));
			foreach (var material in used)
				materials.Enqueue(material);
		}
		var direct = materials.Distinct().Order().ToArray();
		output.WriteLine($"可见 Unit {units.Length} · LOD0 section {sections}（结构表 {mappedSections}）· {direct.Length} 个材质 "
			+ string.Join(" ", direct.Select(id => id.ToString("x16"))));

		var resolver = new GameDataPackageResolver(game);
		var references = new StingrayMaterialReferenceReader();
		var visited = new HashSet<ulong>();
		var textures = new HashSet<ulong>();
		var missing = new List<string>();
		var gameCount = 0;
		var localCount = 0;
		while (materials.TryDequeue(out var id))
		{
			if (!visited.Add(id)) continue;
			var toc = await ReadAsync(new(MaterialDependencyResolver.MaterialTypeId, id));
			if (toc is null) continue;
			var parent = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(24));
			if (parent != 0) materials.Enqueue(parent);
			textures.UnionWith(references.ReadTextureIds(toc).Where(texture => texture != 0));
		}
		foreach (var texture in textures.Order())
			await ReadAsync(new(MaterialDependencyResolver.TextureTypeId, texture));

		output.WriteLine($"闭包 · 材质 {visited.Count} · 贴图 {textures.Count} · 产物 {localCount} · 游戏 {gameCount} · 缺失 {missing.Count}");
		Assert.Empty(missing);
		Assert.Equal(11, direct.Length);
		Assert.Equal(lowPollution ? 4 : 7, units.Length);
		Assert.Equal(30, mappedSections);
		Assert.Equal(14, visited.Count);
		Assert.Equal(13, textures.Count);
		return direct;

		async Task<byte[]?> ReadAsync(PatchAssetKey key)
		{
			var entry = byKey[key].LastOrDefault();
			if (entry is not null)
			{
				localCount++;
				output.WriteLine($"{key.TypeId:x16}:{key.FileId:x16} {entry.SourceFileName}");
				return (await payloadReader.ReadPayloadAsync(entry)).TocData;
			}
			// 自带资源若有同键原版条目，也必须保留 IAS 自己的版本。
			if (!sourceResources.Contains(key))
			{
				if (!gameAssets.TryGetValue(key.TypeId, out var candidates))
				{
					var indexed = new List<PatchTocEntry>();
					foreach (var archive in await resolver.GetPackageNamesAsync())
					{
						var toc = await resolver.GetPackageTocAsync(archive);
						if (toc is not null)
							indexed.AddRange(scanner.ScanEntries(toc.Data, archive, toc.UsesSlimEntryOffset)
								.Where(asset => asset.AssetKey.TypeId == key.TypeId));
					}
					gameAssets[key.TypeId] = candidates = indexed;
				}
				foreach (var candidate in candidates.Where(asset => asset.AssetKey == key))
				{
					var toc = await resolver.GetPackageResourceAsync(candidate.SourceFileName, candidate.TocDataOffset, candidate.TocDataSize);
					if (toc is not { Length: > 0 }) continue;
					gameCount++;
					output.WriteLine($"{key.TypeId:x16}:{key.FileId:x16} 游戏 {candidate.SourceFileName}");
					return toc;
				}
			}
			var formatted = $"{key.TypeId:x16}:{key.FileId:x16}";
			missing.Add(formatted);
			output.WriteLine($"{formatted} 缺失");
			return null;
		}
	}
}
