using HD2SourceMigration.IasPackaging;
using Xunit;

namespace HD2SourceMigration.Tests;

public sealed class IasPackagingServiceTests : IDisposable
{
	private readonly string root = Directory.CreateTempSubdirectory("ias-pack-tests-").FullName;

	[Theory]
	[InlineData("extension", "必须是存在的 .vpk")]
	[InlineData("name", "模型名称")]
	[InlineData("id", "全零")]
	[InlineData("gender", "骨架性别")]
	public async Task InvalidArgumentsReturnAnError(string invalid, string expected)
	{
		var source = Path.Combine(root, invalid == "extension" ? "input.zip" : "input.vpk");
		File.WriteAllBytes(source, []);
		var options = Options();
		options = invalid switch
		{
			"name" => options with { Name = " " },
			"id" => options with { ModelId = Guid.Empty },
			"gender" => options with { RigGender = (IasRigGender)99 },
			_ => options,
		};

		var result = await new IasPackagingService().ConvertL4d2VpkAsync(source, options);

		Assert.False(result.IsSuccess);
		Assert.Contains(expected, Assert.Single(result.Errors));
		Assert.False(Directory.Exists(options.OutputDirectory));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MissingDependenciesIncludeDownloadLocations(bool isVpk)
	{
		var source = Path.Combine(root, "input.vpk");
		File.WriteAllBytes(source, []);
		var service = new IasPackagingService();
		var result = isVpk
			? await service.ConvertL4d2VpkAsync(source, Options())
			: await service.ConvertPatchModAsync(root, Options());

		Assert.False(result.IsSuccess);
		Assert.Equal(isVpk ? 3 : 2, result.Errors.Count);
		Assert.Contains(result.Errors, error => error.Contains("IAS Generator") && error.Contains("archive_payloads.py") && error.Contains("github.com/Estecsky"));
		Assert.Contains(result.Errors, error => error.Contains("HD2SDK-AQ-Edition") && error.Contains("resource_binding_targets.json"));
		if (isVpk)
			Assert.Contains(result.Errors, error => error.Contains("texconv.exe") && error.Contains("github.com/microsoft/DirectXTex"));
	}

	[Fact]
	public void GeneratorVersionUsesInstallationBundleWithoutMatchingItsFingerprint()
	{
		var components = Path.Combine(root, "components");
		var generator = Path.Combine(components, "expanded", "Generator");
		var sdk = Path.Combine(root, "sdk");
		Write(generator, "python/python.exe", "");
		Write(generator, "runtime/companion/archive_payloads.py", "# fixture");
		Write(generator, "runtime/companion/library_catalog.py", "# fixture");
		Write(components, "ExternalRuntime/bundle.json", """{"versions":{"armor":"9.8.7"}}""");
		Write(sdk, "hd2_system/__init__.py", "");
		Write(sdk, "hd2_system/armor_table/armor_part_unit_ids.json", "{}");
		Write(sdk, "hd2_system/resource_binding_targets.json", "{}");
		var options = Options() with { GeneratorDirectory = generator, AqSdkDirectory = sdk };

		var located = new IasDirectoryDependencyLocator().Locate(options, requiresTexconv: false);

		Assert.Empty(located.Errors);
		Assert.Equal("9.8.7", located.Dependencies?.GeneratorVersion);

		File.Delete(Path.Combine(generator, "runtime", "companion", "archive_payloads.py"));
		var error = Assert.Single(new IasDirectoryDependencyLocator().Locate(options, requiresTexconv: false).Errors);
		Assert.Contains("9.8.7", error);
		Assert.Contains("runtime/companion/archive_payloads.py", error);
	}

	[Fact]
	public async Task VpkTargetsUseCatalogAndAvailableGameArchives()
	{
		var sdk = Path.Combine(root, "sdk");
		Write(sdk, "hd2_system/resource_binding_targets.json", """
			{"targets":{
			  "0000000000000001":{"domain":"BODY","equipment":[{"unit_ids":["a","b"]},{"unit_ids":["b"]}]},
			  "0000000000000002":{"domain":"HELMET","equipment":[{"unit_ids":["c"]}]},
			  "0000000000000003":{"domain":"BODY","equipment":[{"unit_ids":["d"]}]},
			  "0000000000000004":{"domain":"BODY","equipment":[{"unit_ids":["e"]},{"unit_ids":["f"]}]}
			}}
			""");
		var game = Path.Combine(root, "game");
		foreach (var archive in new[] { 1, 2, 4, 5 }) Write(game, archive.ToString("x16"), "fixture");

		var targets = await IasPackagingService.GetVpkTargetsAsync(sdk, game);

		Assert.Equal("0000000000000001", Assert.Single(targets.BodyArchives));
		Assert.Equal("0000000000000002", Assert.Single(targets.HelmetArchives));
	}

	[Fact]
	public async Task ExistingOutputDirectoryIsPreserved()
	{
		var options = Options();
		var existing = Directory.CreateDirectory(options.OutputDirectory);
		var marker = Path.Combine(existing.FullName, "keep.txt");
		await File.WriteAllTextAsync(marker, "existing");

		var result = await new IasPackagingService().ConvertPatchModAsync(root, options);

		Assert.False(result.IsSuccess);
		Assert.Contains("输出路径已存在", Assert.Single(result.Errors));
		Assert.Equal("existing", await File.ReadAllTextAsync(marker));
	}

	[Fact]
	public async Task ConversionFailureClearsStagingAndLeavesNoPartialPackage()
	{
		var patch = Directory.CreateDirectory(Path.Combine(root, "patch")).FullName;
		var gameData = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
		var options = Options() with { GameDataDirectory = gameData };
		var service = new IasPackagingService(new StubLocator(patch));

		var result = await service.ConvertPatchModAsync(patch, options);

		Assert.False(result.IsSuccess);
		Assert.Equal("No Unit entries found in mod patches.", Assert.Single(result.Errors));
		Assert.Null(result.MainModZip);
		Assert.Null(result.CompanionZip);
		Assert.False(Directory.Exists(options.OutputDirectory));
		Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".ias-pack-*"));
	}

	private IasPackagingOptions Options() => new(Path.Combine(root, "output"), root);

	private static void Write(string directory, string relativePath, string content)
	{
		var path = Path.Combine(directory, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	public void Dispose() => Directory.Delete(root, recursive: true);

	private sealed class StubLocator(string path) : IasDependencyLocator
	{
		public IasDependencyResolution Locate(IasPackagingOptions options, bool requiresTexconv) =>
			new(new(path, path, null), []);
	}
}
