using System.Text.Json;

namespace HD2SourceMigration.IasPackaging;

public sealed record IasPackagingDependencies(string GeneratorDirectory, string AqSdkDirectory, string? TexconvPath)
{
	public string GeneratorVersion { get; init; } = "未知";
	internal string PythonPath => Path.Combine(GeneratorDirectory, "python", "python.exe");
	internal string PartCatalog => Path.Combine(AqSdkDirectory, "hd2_system", "armor_table", "armor_part_unit_ids.json");
	internal string BindingCatalog => Path.Combine(AqSdkDirectory, "hd2_system", "resource_binding_targets.json");
}

public sealed record IasDependencyResolution(IasPackagingDependencies? Dependencies, IReadOnlyList<string> Errors);

public interface IasDependencyLocator
{
	IasDependencyResolution Locate(IasPackagingOptions options, bool requiresTexconv);
}

public sealed class IasDirectoryDependencyLocator : IasDependencyLocator
{
	private const string IasDownload = "https://github.com/Estecsky/HD2-Integrated-Armor-System/releases";
	private const string AqSdkDownload = "https://github.com/Estecsky/HD2SDK-AQ-Edition";
	private const string TexconvDownload = "https://github.com/microsoft/DirectXTex/releases";

	public IasDependencyResolution Locate(IasPackagingOptions options, bool requiresTexconv)
	{
		var errors = new List<string>();
		var generator = FullPath(options.GeneratorDirectory);
		if (generator is not null && Directory.Exists(Path.Combine(generator, "Generator")))
			generator = Path.Combine(generator, "Generator");
		var version = generator is null ? "未知" : ReadGeneratorVersion(generator);
		var generatorFiles = new[] { "python/python.exe", "runtime/companion/archive_payloads.py", "runtime/companion/library_catalog.py" };
		var missingGenerator = generatorFiles.Where(file => generator is null || !File.Exists(Path.Combine(generator, file))).ToArray();
		if (missingGenerator.Length != 0)
			errors.Add($"IAS Generator（版本 {version}）缺少 {string.Join("、", missingGenerator)}。请从 {IasDownload} 下载并解压官方 Generator。");

		var sdk = FullPath(options.AqSdkDirectory);
		var sdkFiles = new[] { "hd2_system/__init__.py", "hd2_system/armor_table/armor_part_unit_ids.json", "hd2_system/resource_binding_targets.json" };
		var missingSdk = sdkFiles.Where(file => sdk is null || !File.Exists(Path.Combine(sdk, file))).ToArray();
		if (missingSdk.Length != 0)
			errors.Add($"HD2SDK-AQ-Edition 缺少 {string.Join("、", missingSdk)}。请从 {AqSdkDownload} 下载、解压并指定根目录。");

		var texconv = FullPath(options.TexconvPath);
		if (texconv is null && sdk is not null)
			texconv = Path.Combine(sdk, "deps", "texconv.exe");
		if (!File.Exists(texconv))
		{
			texconv = null;
			if (requiresTexconv)
				errors.Add($"求生 VPK 转换需要 texconv.exe。可以使用 AQSDK 附带的 deps/texconv.exe，或从 {TexconvDownload} 下载并指定路径。");
		}

		return new(errors.Count == 0 ? new(generator!, sdk!, texconv) { GeneratorVersion = version } : null, errors.ToArray());
	}

	private static string? FullPath(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

	private static string ReadGeneratorVersion(string directory)
	{
		var parent = Directory.GetParent(directory)?.FullName ?? directory;
		return ReadVersion(Path.Combine(directory, "generator.json"), "version")
			?? ReadVersion(Path.Combine(parent, "bundle.json"), "versions", "armor")
			?? ReadVersion(Path.Combine(parent, "ExternalRuntime", "bundle.json"), "versions", "armor")
			?? ReadVersion(Path.Combine(parent, "..", "ExternalRuntime", "bundle.json"), "versions", "armor")
			?? "未知";
	}

	private static string? ReadVersion(string manifest, params string[] keys)
	{
		if (!File.Exists(manifest)) return null;
		try
		{
			using var stream = File.OpenRead(manifest);
			using var document = JsonDocument.Parse(stream);
			var value = document.RootElement;
			foreach (var key in keys)
				if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value))
					return null;
			return value.ValueKind == JsonValueKind.String && Version.TryParse(value.GetString(), out _)
				? value.GetString() : null;
		}
		catch (JsonException) { return null; }
	}
}
