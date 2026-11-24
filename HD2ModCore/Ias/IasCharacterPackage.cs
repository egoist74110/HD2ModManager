using System.IO.Compression;
using System.Text.Json;

namespace HD2ModCore.Ias;

internal sealed record IasCharacterPackage(string Name, string Main, string CompanionZip)
{
	internal static async Task<IasCharacterPackage> Read(IReadOnlyList<string> input, string work, CancellationToken token)
	{
		if (input.Count == 0) throw new InvalidDataException("请提供 IAS 主包与配套包。");
		using var archives = new IasDropWorkspace();
		var contents = new List<IasInputContents>();
		foreach (var path in input)
			contents.Add(await archives.InspectAsync(path, token).ConfigureAwait(false));
		return ReadPrepared(contents, work, token);
	}

	internal static IasCharacterPackage ReadPrepared(IReadOnlyList<IasInputContents> input, string work, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		var main = input.SelectMany(item => item.Main).ToArray();
		var companions = input.SelectMany(item => item.Companions).ToArray();
		if (main.Length != 1 || companions.Length != 1)
			throw new InvalidDataException($"每次请添加一组 IAS 主包和配套包（找到主包 {main.Length} 个、配套包 {companions.Length} 个）。");

		var source = Path.Combine(work, "sources");
		var mainSource = Path.Combine(source, "main");
		var companionSource = Path.Combine(source, "companion");
		IasFileTree.Copy(main[0], mainSource);
		IasFileTree.Copy(companions[0], companionSource);
		var zipPath = Path.Combine(work, "companion.zip");
		ZipFile.CreateFromDirectory(companionSource, zipPath);
		var name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(input[0].OriginalPath));
		var marker = Directory.EnumerateFiles(companionSource).Single(IasPackageDetector.IsMarker);
		using var info = JsonDocument.Parse(File.ReadAllBytes(marker));
		if (info.RootElement.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String &&
			label.GetString() is { Length: > 0 } title)
			name = title;
		return new(name, mainSource, zipPath);
	}
}
