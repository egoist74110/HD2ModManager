using System.Buffers.Binary;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.IasConversion;
using Xunit.Abstractions;

namespace HD2SourceMigration.Tests;

public sealed class IasStandardReadbackTests(ITestOutputHelper output)
{
	[Fact]
	public async Task EkuMergedProductUsesOnlyArmorAndPreservesDefaultGeometry()
	{
		var merged = Environment.GetEnvironmentVariable("HD2_IAS_W28_EKU");
		var regular = Environment.GetEnvironmentVariable("HD2_IAS_W28_EKU_UNMERGED");
		var sample = Environment.GetEnvironmentVariable("HD2_IAS_W28_EKU_SOURCE");
		if (merged is null || regular is null || sample is null) return;

		var source = await IasStandardConverter.ReadOriginalEquipmentAsync(sample, default);
		var armorArchive = source.Single(item => !item.Equipment.Helmet).Equipment.Archive.ToString("x16");
		var helmetArchive = source.Single(item => item.Equipment.Helmet).Equipment.Archive.ToString("x16");
		var head = source.Single(item => item.Equipment.Helmet).HiddenByUnit.Keys;
		var patches = Paths(merged, true).ToArray();
		Assert.NotEmpty(patches);
		foreach (var patch in patches)
		{
			Assert.StartsWith(armorArchive + ".patch_", Path.GetFileName(patch));
			var entries = await new PatchTocScanner().ScanEntriesAsync(patch);
			Assert.DoesNotContain(entries, entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId && head.Contains(entry.AssetKey.FileId));
			Assert.All(entries.Where(entry => entry.GpuResourceSize > 0), entry => Assert.Equal(0UL, entry.GpuResourceOffset % 64));
		}
		Assert.Contains(Paths(regular, true), path => Path.GetFileName(path).StartsWith(helmetArchive, StringComparison.Ordinal));
		var before = await DefaultTriangleCountsAsync(regular);
		var after = await DefaultTriangleCountsAsync(merged);
		var headTriangles = before.Where(unit => unit.Key.Archive == helmetArchive).Sum(unit => unit.Value);
		var armorBefore = before.Where(unit => unit.Key.Archive == armorArchive).ToDictionary();
		Assert.Equal(armorBefore.Keys.Order(), after.Keys.Order());
		Assert.True(headTriangles > 0);
		var chestDeltas = after.Select(unit => unit.Value - armorBefore[unit.Key]).Where(delta => delta != 0).ToArray();
		Assert.InRange(chestDeltas.Length, 1, 2);
		Assert.All(chestDeltas, delta => Assert.Equal(headTriangles, delta));
		output.WriteLine($"Default head LOD0 triangles={headTriangles}; chest deltas={string.Join(", ", chestDeltas)}; merged patches={patches.Length}; archive={armorArchive}");

		static IEnumerable<string> Paths(string directory, bool recursive) => Directory.EnumerateFiles(directory, "*.patch_*",
			recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
			.Where(path => Path.GetFileName(path).Split(".patch_") is [_, var number] && int.TryParse(number, out _));

		static async Task<Dictionary<(string Archive, ulong Unit), int>> DefaultTriangleCountsAsync(string directory)
		{
			using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
			var directories = manifest.RootElement.GetProperty("options").EnumerateArray()
				.SelectMany(option => option.GetProperty("include").EnumerateArray())
				.Select(folder => Path.Combine(directory, folder.GetString()!)).Prepend(directory);
			var triangles = new Dictionary<(string Archive, ulong Unit), int>();
			var scanner = new PatchTocScanner();
			var reader = new PatchUnitMeshReader();
			foreach (var patch in directories.SelectMany(folder => Paths(folder, false)))
			{
				var entries = await scanner.ScanEntriesAsync(patch);
				foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
				{
					var unit = await reader.ReadStructureOnlyAsync(entry, entries);
					triangles.Add((Path.GetFileName(patch).Split(".patch_")[0], entry.AssetKey.FileId),
						unit.Model.Meshes.Where(mesh => mesh.LodIndex == 0)
						.SelectMany(mesh => mesh.Sections).Where(section => section.NumIndices > 3)
						.Sum(section => (int)section.NumIndices / 3));
				}
			}
			return triangles;
		}
	}

	[Theory]
	[InlineData("HD2_IAS_W22_EKU", 37)]
	[InlineData("HD2_IAS_W22_MILFY", 30)]
	[InlineData("HD2_IAS_W22_DP8", 26)]
	[InlineData("HD2_IAS_W22_BREMERTON", 23)]
	[InlineData("HD2_IAS_W24_CE07", 29)]
	[InlineData("HD2_IAS_W24_EKU", 37)]
	[InlineData("HD2_IAS_W24_MILFY", 30)]
	[InlineData("HD2_IAS_W28_EKU", 42)]
	public async Task 回读每个本体和选项Unit的材质闭包(string fixture, int expectedUnits)
	{
		var package = Environment.GetEnvironmentVariable(fixture);
		var game = Environment.GetEnvironmentVariable("HD2_IAS_GAME_DATA");
		if (package is null || game is null)
			return;
		var scanner = new PatchTocScanner();
		var reader = new PatchUnitMeshReader();
		var payloadReader = new PatchEntryPayloadReader();
		var resolver = new GameDataPackageResolver(game);
		var local = new Dictionary<AssetKey, PatchTocEntry>();
		var units = new Queue<(PatchTocEntry Unit, IReadOnlyList<PatchTocEntry> Patch)>();
		foreach (var path in Directory.EnumerateFiles(package, "*.patch_*", SearchOption.AllDirectories)
			.Where(path => !path.EndsWith(".gpu_resources", StringComparison.Ordinal) && !path.EndsWith(".stream", StringComparison.Ordinal)))
		{
			var entries = await scanner.ScanEntriesAsync(path);
			foreach (var entry in entries)
			{
				if (entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
					units.Enqueue((entry, entries));
				else
					Assert.True(local.TryAdd(entry.AssetKey, entry), $"资源重复：{entry.AssetKey}");
			}
		}
		Assert.Equal(expectedUnits, units.Count);

		var direct = new HashSet<ulong>();
		var visibleSections = 0;
		while (units.TryDequeue(out var item))
		{
			var mesh = await reader.ReadStructureOnlyAsync(item.Unit, item.Patch);
			var materialBySlot = mesh.Model.Materials.ToLookup(binding => binding.SectionId, binding => binding.MaterialId);
			var used = mesh.Model.Materials.Select(binding => binding.MaterialId).Where(id => id != 0).ToHashSet();
			foreach (var section in mesh.Model.Meshes.Where(part => part.LodIndex >= 0)
				.SelectMany(part => part.Sections).Where(section => section.NumIndices > 3))
			{
				var material = Assert.Single(materialBySlot[section.MaterialSlotId].Distinct());
				Assert.NotEqual(0UL, material);
				visibleSections++;
			}
			direct.UnionWith(used);
			output.WriteLine($"{Path.GetRelativePath(package, item.Unit.SourceFilePath)} {item.Unit.AssetKey.FileId:x16} 材质 "
				+ string.Join(" ", used.Order().Select(id => id.ToString("x16"))));
		}

		var parent = new Queue<ulong>(direct);
		var visited = new HashSet<ulong>();
		var textures = new HashSet<ulong>();
		var references = new StingrayMaterialReferenceReader();
		var gameEntries = new Dictionary<AssetKey, PatchTocEntry>();
		IEnumerator<string>? archives = null;
		try
		{
			while (parent.TryDequeue(out var id))
			{
				if (!visited.Add(id)) continue;
				var bytes = await ReadAsync(new(MaterialDependencyResolver.MaterialTypeId, id));
				var parentId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24));
				if (parentId != 0) parent.Enqueue(parentId);
				textures.UnionWith(references.ReadTextureIds(bytes).Where(texture => texture != 0));
			}
			foreach (var texture in textures.Order())
				await ReadAsync(new(MaterialDependencyResolver.TextureTypeId, texture));
			if (fixture == "HD2_IAS_W24_CE07")
				await CheckCe07SlotsAsync();
		}
		finally
		{
			archives?.Dispose();
		}
		output.WriteLine($"{fixture} · Unit {expectedUnits} · 可见 section {visibleSections} · 直接材质 {direct.Count} · 闭包材质 {visited.Count} · 贴图 {textures.Count} · 缺失 0");
		Assert.NotEmpty(direct);

		async Task CheckCe07SlotsAsync()
		{
			const ulong material = 0x0714f22d9efaca5a;
			var source = Environment.GetEnvironmentVariable("HD2_IAS_W24_CE07_SOURCE");
			Assert.NotNull(source);
			Assert.Contains(material, direct);
			var key = new AssetKey(MaterialDependencyResolver.MaterialTypeId, material);
			Assert.Contains(key, local.Keys);
			var input = Assert.Single(await scanner.ScanEntriesAsync(source), entry => entry.AssetKey == key);
			var before = IasMaterialDefaults.ReadSlots((await payloadReader.ReadPayloadAsync(input)).TocData);
			var converted = await ReadAsync(key);
			var parentId = IasMaterialDefaults.ReadParent(converted);
			var parentKey = new AssetKey(MaterialDependencyResolver.MaterialTypeId, parentId);
			Assert.DoesNotContain(parentKey, local.Keys);
			var current = IasMaterialDefaults.ReadSlots(await ReadAsync(parentKey));
			var after = IasMaterialDefaults.ReadSlots(converted);
			var changed = before.Where(slot => after[slot.Key] != slot.Value).ToArray();
			Assert.Equal(new ulong[] { 0x38775d670507376c, 0x539be119a86c60da, 0x61c984545df83fdf, 0x6b84e2d5d7d43589,
				0x8ce143956252e5ce, 0x90f13ab202d5c4e9, 0xe2245ea44a9d5bc6, 0xe7caf54c22588e7a },
				changed.Select(slot => slot.Value).Order());
			foreach (var (slot, previous) in changed.OrderBy(slot => slot.Key))
			{
				var texture = after[slot];
				Assert.Equal(current[slot], texture);
				var textureKey = new AssetKey(MaterialDependencyResolver.TextureTypeId, texture);
				Assert.DoesNotContain(textureKey, local.Keys);
				await ReadAsync(textureKey);
				output.WriteLine($"CE-07 槽 {slot:x8} · {previous:x16} → {texture:x16} · {gameEntries[textureKey].SourceFileName}");
			}
		}

		async Task<byte[]> ReadAsync(AssetKey key)
		{
			if (local.TryGetValue(key, out var entry))
				return (await payloadReader.ReadPayloadAsync(entry)).TocData;
			archives ??= (await resolver.GetPackageNamesAsync()).GetEnumerator();
			while (!gameEntries.ContainsKey(key) && archives.MoveNext())
			{
				var name = archives.Current;
				var toc = await resolver.GetPackageTocAsync(name);
				if (toc is null) continue;
				foreach (var resource in scanner.ScanEntries(toc.Data, name, toc.UsesSlimEntryOffset).Where(entry =>
					entry.AssetKey.TypeId is MaterialDependencyResolver.MaterialTypeId or MaterialDependencyResolver.TextureTypeId))
					gameEntries.TryAdd(resource.AssetKey, resource);
			}
			Assert.True(gameEntries.TryGetValue(key, out var vanilla), $"未找到游戏资源 {key.TypeId:x16}:{key.FileId:x16}");
			var payload = await resolver.GetPackageResourceAsync(vanilla.SourceFileName, vanilla.TocDataOffset, vanilla.TocDataSize);
			Assert.NotNull(payload);
			Assert.NotEmpty(payload);
			return payload;
		}
	}

	[Fact]
	public void Eku必需部件的选项与默认位置()
	{
		var package = Environment.GetEnvironmentVariable("HD2_IAS_W22_EKU");
		if (package is null)
			return;
		using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "manifest.json")));
		var groups = manifest.RootElement.GetProperty("options").EnumerateArray()
			.ToDictionary(group => group.GetProperty("name").GetString()!);
		Check("尾巴", "有", "无");
		Check("底衣", "打底裤＋内衣", "内衣", "打底裤", "脱");
		Check("表情", "默认", "看到鸡蛋", "拿到鸡蛋");

		void Check(string part, params string[] names)
		{
			var group = groups[part];
			var choices = group.GetProperty("subOptions").EnumerateArray().ToArray();
			Assert.Equal(names, choices.Select(choice => choice.GetProperty("name").GetString()));
			Assert.False(choices[0].TryGetProperty("include", out _));
			Assert.True(Directory.Exists(Path.Combine(package, group.GetProperty("include")[0].GetString()!)));
			foreach (var choice in choices.Skip(1))
				Assert.True(Directory.Exists(Path.Combine(package, choice.GetProperty("include")[0].GetString()!)));
		}
	}
}
