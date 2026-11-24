#if WINDOWS
using System.Diagnostics;
using System.IO.Compression;
using HD2ModCore.Ias;

namespace HD2ModCore.Tests;

public sealed class IasCharacterPackageTests : IDisposable
{
	private const string Patch = "a7c4c9d700000000.patch_0";
	private const string Name = "Milfy 发光外套";
	private readonly string _root = Directory.CreateTempSubdirectory("w11-").FullName;

	[Fact]
	public async Task ZipWithRarExtensionFindsNestedPairDespiteWrapperAndReadme()
	{
		var main = MakeZip("armor.zip", (Patch, "patch"));
		var companion = MakeZip("armor.hd2irpack.zip", ("assets/mod_group.json", $"{{\"name\":\"{Name}\"}}"));
		var outer = Path.Combine(_root, "share.rar");
		using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
		{
			zip.CreateEntryFromFile(main, "发光外套/armor.zip");
			zip.CreateEntryFromFile(companion, "发光外套/armor.hd2irpack.zip");
			Write(zip, "发光外套/说明.txt", "说明");
			Write(zip, "发光外套/预览.png", "preview");
		}

		Assert.True(IasPackageDetector.MayAccept(outer));
		Assert.Equal(IasInputKind.IasPackage, IasPackageDetector.Classify(outer));
		await AssertPairAsync(outer);
	}

	[Fact]
	public async Task ActualSevenZipFindsLooseMainAndCompanionInOneArchive()
	{
		var input = Path.Combine(_root, "source");
		var main = Directory.CreateDirectory(Path.Combine(input, "wrapper", "main")).FullName;
		var companion = Directory.CreateDirectory(Path.Combine(input, "wrapper", "companion")).FullName;
		File.WriteAllText(Path.Combine(main, Patch), "patch");
		File.WriteAllText(Path.Combine(companion, "mod_group.json"), $"{{\"name\":\"{Name}\"}}");
		File.WriteAllText(Path.Combine(input, "wrapper", "readme.txt"), "说明");
		var outer = Path.Combine(_root, "share.7z");
		var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "third_party", "7zip", "7z.exe"))
		{
			WorkingDirectory = input,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var arg in new[] { "a", "-t7z", "-bd", outer, "wrapper" }) start.ArgumentList.Add(arg);
		using var process = Process.Start(start)!;
		await process.WaitForExitAsync();
		Assert.Equal(0, process.ExitCode);

		Assert.Equal(IasInputKind.IasPackage, IasPackageDetector.Classify(outer));
		await AssertPairAsync(outer);
	}

	[Fact]
	public async Task GenericNestedNamesAreResolvedFromContents()
	{
		var main = MakeZip("a.zip", (Patch, "patch"));
		var companion = MakeZip("b.zip", ("mod_group.json", $"{{\"name\":\"{Name}\"}}"));
		var middle = Path.Combine(_root, "middle.zip");
		using (var zip = ZipFile.Open(middle, ZipArchiveMode.Create))
		{
			zip.CreateEntryFromFile(main, "arbitrary/a.bin");
			zip.CreateEntryFromFile(companion, "arbitrary/b.bin");
		}
		var outer = Path.Combine(_root, "outer.zip");
		using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
			zip.CreateEntryFromFile(middle, "middle.zip");

		Assert.Equal(IasInputKind.Archive, IasPackageDetector.Classify(outer));
		await AssertPairAsync(outer);
	}

	[Fact]
	public async Task OnlyMainReportsMissingCompanion()
	{
		var main = MakeZip("IAS_MainMod.zip", (Patch, "patch"));
		using var workspace = new IasDropWorkspace();
		Assert.True((await workspace.InspectAsync(main, default)).HasNamedMain);
		var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
			IasCharacterPackage.Read([main], Path.Combine(_root, "work"), default));
		Assert.Equal("每次请添加一组 IAS 主包和配套包（找到主包 1 个、配套包 0 个）。", error.Message);
	}

	[Fact]
	public async Task ArchivesWithoutCompanionGiveSeparatePatchAndVpkConversionInputs()
	{
		var patch = MakeZip("armor.rar", ($"armor/{Patch}", "patch"));
		var vpk = MakeZip("source.7z", ("source/model.vpk", "vpk"));
		using var workspace = new IasDropWorkspace();

		var patchContents = await workspace.InspectAsync(patch, default);
		Assert.Single(patchContents.Main);
		Assert.Empty(patchContents.Companions);
		Assert.False(patchContents.HasNamedMain);
		Assert.Equal(IasInputKind.PatchMod, IasPackageDetector.Classify(patch));

		var vpkContents = await workspace.InspectAsync(vpk, default);
		Assert.EndsWith("model.vpk", Assert.Single(vpkContents.Vpks));
		Assert.Empty(vpkContents.Main);
		Assert.Equal(IasInputKind.L4d2Vpk, IasPackageDetector.Classify(vpk));
	}

	[Fact]
	public async Task ZipWithParentTraversalIsRejectedBeforeExtraction()
	{
		var archive = MakeZip("bad.zip", ("../outside.txt", "bad"));
		using var workspace = new IasDropWorkspace();
		var error = await Assert.ThrowsAsync<InvalidDataException>(() => workspace.InspectAsync(archive, default));
		Assert.Contains("不安全", error.Message);
	}

	[Fact]
	public async Task MultiplePairsReportActualCounts()
	{
		var paths = new[] { MakeZip("a.zip", (Patch, "patch")), MakeZip("b.zip", (Patch, "patch")),
			MakeZip("c.zip", ("mod_group.json", "{}")), MakeZip("d.zip", ("mod_group.json", "{}")) };
		var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
			IasCharacterPackage.Read(paths, Path.Combine(_root, "work"), default));
		Assert.Equal("每次请添加一组 IAS 主包和配套包（找到主包 2 个、配套包 2 个）。", error.Message);
	}

	[Fact]
	public async Task MoreThanThreeArchiveLevelsIsRejected()
	{
		var path = MakeZip("base.zip", (Patch, "patch"));
		for (var level = 1; level <= 3; level++)
		{
			var outer = Path.Combine(_root, $"level{level}.zip");
			using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
				zip.CreateEntryFromFile(path, "nested.zip");
			path = outer;
		}
		var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
			IasCharacterPackage.Read([path], Path.Combine(_root, "work"), default));
		Assert.Equal("IAS 包嵌套层数过多。", error.Message);
	}

	private async Task AssertPairAsync(string input)
	{
		var package = await IasCharacterPackage.Read([input], Path.Combine(_root, "work"), default);
		Assert.Equal(Name, package.Name);
		var patch = Assert.Single(Directory.EnumerateFiles(package.Main, Patch, SearchOption.AllDirectories));
		Assert.Equal("patch", File.ReadAllText(patch));
		using var companion = ZipFile.OpenRead(package.CompanionZip);
		Assert.NotNull(companion.GetEntry("mod_group.json"));
	}

	private string MakeZip(string fileName, params (string Path, string Text)[] entries)
	{
		var path = Path.Combine(_root, fileName);
		using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
		foreach (var entry in entries) Write(zip, entry.Path, entry.Text);
		return path;
	}

	private static void Write(ZipArchive zip, string path, string text)
	{
		using var writer = new StreamWriter(zip.CreateEntry(path).Open());
		writer.Write(text);
	}

	public void Dispose() => IasFileTree.Delete(_root);
}
#endif
