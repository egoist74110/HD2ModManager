using System.Text;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2SourceMigration.IasConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class IasConversionTests
{
	[Fact]
	public void RigReaderFoldsCustomChainIntoMappedNativeAncestorAndChecksCrc()
	{
		using var payload = new MemoryStream();
		using var body = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
		WriteName(body, "head");
		body.Write(-1);
		body.Write(0u);
		WriteMatrix(body, 1);
		body.Write(42ul);
		body.Write(3);
		body.Write(0);
		body.Write(0);
		body.Write(3);
		body.Write(8u);
		WriteTarget("head", -1, 0, 0, 1);
		WriteTarget("hair", 0, 1, 256, 0.1f);
		WriteTarget("hair_tip", 1, 2, 256, 0.2f);

		var bytes = payload.ToArray();
		using var rig = new MemoryStream();
		using var header = new BinaryWriter(rig, Encoding.UTF8, leaveOpen: true);
		header.Write("HD2IRG1\0"u8);
		header.Write(1);
		header.Write(32);
		header.Write(1);
		header.Write(1);
		header.Write(IasRigReader.Crc32(bytes));
		header.Write(2u);
		header.Write(bytes);
		rig.Position = 0;
		var profile = IasRigReader.Read(rig)[42];
		Assert.Equal(0, profile.ResolveSemanticNode(2));
		Assert.Equal(1.3, profile.Nodes[2].World.Origin.Z, 6);
		rig.Position = rig.Length - 1;
		rig.WriteByte(1);
		rig.Position = 0;
		Assert.Throws<InvalidDataException>(() => IasRigReader.Read(rig));
		return;

		void WriteTarget(string name, int parent, int slot, uint flags, float z)
		{
			WriteName(body, name);
			body.Write(parent);
			body.Write(0);
			body.Write(slot);
			body.Write(flags);
			body.Write(1f);
			WriteMatrix(body, z);
		}
	}

	[Fact]
	public void FoldedWeightsMergeBeforeTakingFourAndNormalize()
	{
		var merged = IasNativeMesh.MergeInfluences(
			[new(1, 0.24f), new(2, 0.25f), new(3, 0.16f), new(1, 0.05f), new(4, 0.16f), new(5, 0.09f)]);
		Assert.Equal(new uint[] { 1, 2, 3, 4 }, merged.Select(item => item.BoneHash));
		Assert.Equal(1f, merged.Sum(item => item.Weight), 6);
		Assert.Equal(0.29f / 0.86f, merged[0].Weight, 6);
	}

	[Fact]
	public void CustomWeightsFoldIntoTheOriginalGameBone()
	{
		var profile = new IasRigProfile(["head"], [
			new("head", -1, 0, 1, 1, Mat4.Identity),
			new("hair", 0, -1, 4, 256, Mat4.Identity),
			new("hair_tip", 1, -1, 7, 256, Mat4.Identity),
		]);
		var folded = IasNativeMesh.FoldVertexWeights([(1, .25f), (4, .25f), (7, .5f)],
			new Dictionary<int, int> { [1] = 0, [4] = 1, [7] = 2 }, new Dictionary<int, int>(), profile);
		Assert.Equal(new SourceVertexWeight(0, 1), Assert.Single(folded));
	}

	[Fact]
	public async Task StandardMilfyOutputUsesOnlyGameAndSpringSkinBonesWhenFixtureIsProvided()
	{
		var output = Environment.GetEnvironmentVariable("HD2_IAS_STANDARD_OUTPUT");
		if (string.IsNullOrWhiteSpace(output))
			return;
		var companion = Environment.GetEnvironmentVariable("HD2_IAS_PHYSICS_FIXTURES")
			?? throw new InvalidOperationException("HD2_IAS_PHYSICS_FIXTURES is required for the Milfy output check.");
		var game = Environment.GetEnvironmentVariable("HD2_IAS_GAME_DATA")
			?? throw new InvalidOperationException("HD2_IAS_GAME_DATA is required for the Milfy output check.");
		var formerNames = new HashSet<uint>();
		var rigs = Directory.EnumerateFiles(companion, "*.consumers.rigbin")
			.Concat(Directory.EnumerateFiles(companion, "*.canonical.rigbin"))
			.Concat(Directory.EnumerateFiles(companion, "*.84fd7aab7cf6f62d.rigbin"));
		foreach (var path in rigs)
		{
			using var rig = File.OpenRead(path);
			foreach (var profile in IasRigReader.Read(rig).Values)
				foreach (var node in profile.SourceNames.Concat(profile.Nodes.Select(node => node.Name)))
					formerNames.Add(Hd2NameHash.Of("ias_" + node));
		}
		output = Path.GetFullPath(output);
		var root = Path.Combine(output, "mod");
		var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToArray();
		Assert.DoesNotContain(files, path => Path.GetFileName(path) is "arm-pose.json" or "joint-pose.json");
		Assert.Contains(Path.Combine(root, "manifest.json"), files);
		var scanner = new PatchTocScanner();
		var reader = new PatchUnitMeshReader();
		var resolver = new GameDataPackageResolver(game);
		var avatar = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync();
		var vanilla = new GameDataUnitMeshReader(resolver);
		var baseUnits = 0;
		var optionUnits = 0;
		var weightedVertices = 0;
		foreach (var patch in files.Where(path => Path.GetFileName(path).EndsWith(".patch_0", StringComparison.Ordinal)))
		{
			var entries = await scanner.ScanEntriesAsync(patch);
			var archive = Path.GetFileNameWithoutExtension(patch);
			var springHashes = await IasPhysicsTests.ReadSpringHashesAsync(patch);
			var weightedSprings = new HashSet<uint>();
			foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
			{
				if (Path.GetDirectoryName(patch) == root)
					baseUnits++;
				else
					optionUnits++;
				var unit = await reader.ReadCanonicalSourceAsync(entry, entries);
				var donor = await vanilla.ReadAsync(archive, entry.AssetKey);
				var model = unit.Model;
				var allowed = avatar.NameHashes.Concat(donor.Model.TransformNameHashes).Concat(springHashes).ToHashSet();
				Assert.All(model.TransformNameHashes, hash =>
				{
					Assert.DoesNotContain(hash, formerNames);
					Assert.Contains(hash, allowed);
				});
				foreach (var boneInfo in model.BoneInfos)
				foreach (var index in boneInfo.RealIndices)
				{
					var hash = model.TransformNameHashes[checked((int)index)];
					Assert.DoesNotContain(hash, formerNames);
					if (springHashes.Contains(hash))
						weightedSprings.Add(hash);
				}
				weightedVertices += model.RawMeshData.Where(mesh => mesh.LodIndex >= 0).Sum(mesh => mesh.Vertices.Count);
			}
			if (springHashes.Count > 0)
				Assert.NotEmpty(weightedSprings);
		}
		Assert.Equal(20, baseUnits);
		Assert.Equal(6, optionUnits);
		Assert.True(weightedVertices > 0);
	}

	[Fact]
	public void 读取同域多份清单以及头盔三选一()
	{
		var directory = Directory.CreateTempSubdirectory("ias-routes-");
		try
		{
			Write("body.isolation.ini", ["isolation_version=1", "domain=body", "archive=abc", "equipment=10",
				"unit=1,101", "unit=2,201", "unit=3,301"]);
			WriteOptions("body-one", "body", "abc", "10", "tail", "尾巴", 1, "有");
			WriteOptions("body-two", "body", "abc", "10", "under", "底衣", 2, "内衣", "打底裤");
			WriteOptions("head", "helmet", "def", "20", "face", "表情", 8, "默认", "看到鸡蛋", "拿到鸡蛋");

			var plans = IasPackageRoutes.Read(directory.FullName);
			Assert.Equal(2, plans.Count);
			Assert.Equal(new IasEquipment(false, 0xabc, 0x10), plans[0].Equipment);
			Assert.Equal(new IasRoute(3, 0x301), Assert.Single(plans[0].Base));
			Assert.Equal(new IasEquipment(true, 0xdef, 0x20), plans[1].Equipment);
			Assert.Empty(plans[1].Base);
			var groups = plans.SelectMany(plan => plan.Groups).ToArray();
			Assert.Equal(["尾巴", "底衣", "表情"], groups.Select(group => group.Name));
			Assert.Equal([1, 2, 3], groups.Select(group => group.Options.Count));
			Assert.Equal(["默认", "看到鸡蛋", "拿到鸡蛋"], groups[2].Options.Select(option => option.Name));
			Assert.Equal([0x801UL, 0x802UL, 0x803UL],
				groups[2].Options.Select(option => Assert.Single(option.Routes).Published));
			var folders = groups.SelectMany(group => group.Options).Select(option => option.Folder).ToArray();
			Assert.Equal(folders.Length, folders.Distinct().Count());
			Assert.Equal(folders, IasPackageRoutes.Read(directory.FullName).SelectMany(plan => plan.Groups)
				.SelectMany(group => group.Options).Select(option => option.Folder));

			using var manifest = JsonDocument.Parse(IasStandardConverter.SerializeManifest("夹具 · 目标名称", groups));
			Assert.Equal("ias-per-archive-v1", manifest.RootElement.GetProperty("sectionalPatchNumbering").GetString());
			var face = manifest.RootElement.GetProperty("options")[2];
			Assert.Equal(folders[3], face.GetProperty("include")[0].GetString());
			Assert.Equal(folders[5], face.GetProperty("subOptions")[2].GetProperty("include")[0].GetString());
		}
		finally
		{
			directory.Delete(recursive: true);
		}

		void Write(string file, IEnumerable<string> rows) =>
			File.WriteAllLines(Path.Combine(directory.FullName, file), rows);

		void WriteOptions(string file, string domain, string archive, string item, string part, string name,
			int unit, params string[] choices)
		{
			var rows = new List<string>
			{
				"manifest_version=6", $"domain={domain}", $"archive={archive}", $"equipment={item}",
				$"free_part={part},slot,choice1", $"free_part_display_name={part},{name}",
			};
			foreach (var (displayName, index) in choices.Select((value, index) => (value, index)).Reverse())
			{
				var choice = $"choice{index + 1}";
				rows.Add($"free_option={part},{choice},{index}");
				rows.Add($"free_option_display_name={part},{choice},{displayName}");
				rows.Add($"free_route={part},{choice},{unit:x},{unit:x}01,{unit:x}0{index + 1}");
			}
			Write(file + ".difference.ini", rows);
		}
	}

	[Theory]
	[InlineData(null, "缺少 isolation.ini 或 difference.ini")]
	[InlineData("5", "不支持 manifest_version=5")]
	[InlineData("6", "缺少有效的 domain")]
	public void 不支持的清单报告中文原因(string? version, string expected)
	{
		var directory = Directory.CreateTempSubdirectory("ias-invalid-");
		try
		{
			if (version is not null)
				File.WriteAllText(Path.Combine(directory.FullName, "unsupported.difference.ini"), $"manifest_version={version}");
			var error = Assert.Throws<InvalidDataException>(() => IasPackageRoutes.Read(directory.FullName));
			Assert.Contains(expected, error.Message);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void HiddenUnitGeneratorProducesOnlyASubMillimetrePlaceholder()
	{
		var stream = new UnitStreamInfo(0, 0, 0, 1, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0,
			[new(0, "position", 2, "vec3_float", 0, 0, 12)]);
		var mesh = new UnitRawMeshData(0, 0, 0, 0, [new(0, 123, [new(0, 1, 2)])], [new(0, 1, 2)], []);
		var result = new HiddenUnitGenerator().Generate(mesh, stream);
		Assert.True(result.IsValid);
		Assert.Single(result.Mesh!.Triangles);
		Assert.Equal(3, result.Mesh.Vertices.Count);
		Assert.All(result.Mesh.Vertices, vertex =>
			Assert.All(vertex.Components[0].FloatValues, coordinate => Assert.InRange(coordinate, 0f, .0001f)));
	}

	private static void WriteName(BinaryWriter writer, string name)
	{
		var bytes = Encoding.UTF8.GetBytes(name);
		writer.Write(bytes);
		writer.Write(new byte[64 - bytes.Length]);
	}

	private static void WriteMatrix(BinaryWriter writer, float z)
	{
		foreach (var value in new[] { 1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, z })
			writer.Write(value);
	}
}
