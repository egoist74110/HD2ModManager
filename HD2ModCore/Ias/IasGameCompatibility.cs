using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HD2ModCore.Ias;

public enum IasGameCompatibilityStatus
{
	Compatible,
	Incompatible,
	GameFilesMissing
}

public sealed record IasGameFileDifference(string Path, string? ExpectedSha256, string? ActualSha256);

public sealed record IasGameCompatibilityResult(IasGameCompatibilityStatus Status,
	IReadOnlyList<IasGameFileDifference> Details)
{
	public string Message => Status switch
	{
		IasGameCompatibilityStatus.Compatible => "游戏文件与 IAS ArmorBase 兼容。",
		IasGameCompatibilityStatus.Incompatible => "游戏文件与 IAS ArmorBase 不兼容。",
		_ => "找不到 IAS 所需的游戏文件。"
	};
}

public sealed class IasGameCompatibilityException(IasGameCompatibilityResult result) : InvalidOperationException(result.Message)
{
	public IasGameCompatibilityResult Result { get; } = result;
}

public static class IasGameCompatibility
{
	private static readonly string[] GameFiles = ["bin/helldivers2.exe", "data/game/game.dll"];

	public static IasGameCompatibilityResult Check(string? gameRootDirectory, string componentsDirectory) =>
		CheckAsync(gameRootDirectory, componentsDirectory).GetAwaiter().GetResult();

	public static async Task<IasGameCompatibilityResult> CheckAsync(string? gameRootDirectory, string componentsDirectory,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(gameRootDirectory))
			return new(IasGameCompatibilityStatus.GameFilesMissing,
				GameFiles.Select(path => new IasGameFileDifference(path, null, null)).ToArray());

		var armorBasePath = Path.Combine(componentsDirectory, "ExternalRuntime", "ArmorBase.zip");
		IasPaths.RejectLink(componentsDirectory);
		IasPaths.RejectLink(Path.GetDirectoryName(armorBasePath)!);
		IasPaths.RejectLink(armorBasePath);
		using var archive = ZipFile.OpenRead(armorBasePath);
		var manifest = archive.Entries.SingleOrDefault(entry => entry.FullName == "base.json");
		if (manifest is null || manifest.Length > 4 * 1024 * 1024)
			throw new InvalidDataException("IAS ArmorBase 缺少有效的 base.json。");
		using var manifestStream = manifest.Open();
		using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
			schema.GetString() != "HD2ArmorBase1" ||
			!root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException("IAS ArmorBase 的 base.json schema 或 host 字段无效。");

		var details = new List<IasGameFileDifference>();
		foreach (var path in GameFiles)
		{
			if (!host.TryGetProperty(path, out var hash) || hash.ValueKind != JsonValueKind.String ||
				hash.GetString() is not { } expected || !IasPaths.IsSha256(expected))
				throw new InvalidDataException($"IAS ArmorBase 缺少有效的游戏文件 SHA-256：{path}");
			var gameFile = Path.Combine(gameRootDirectory, path.Replace('/', Path.DirectorySeparatorChar));
			var actual = await ReadHashAsync(gameFile, cancellationToken).ConfigureAwait(false);
			if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
				details.Add(new(path, expected, actual));
		}
		var status = details.Any(detail => detail.ActualSha256 is null)
			? IasGameCompatibilityStatus.GameFilesMissing
			: details.Count > 0 ? IasGameCompatibilityStatus.Incompatible : IasGameCompatibilityStatus.Compatible;
		return new(status, details.AsReadOnly());
	}

	private static async Task<string?> ReadHashAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
		}
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
	}
}
