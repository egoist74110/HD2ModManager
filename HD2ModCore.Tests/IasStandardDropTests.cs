#if WINDOWS
using System.IO.Compression;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services.Ias;

namespace HD2ModCore.Tests;

public sealed class IasStandardDropTests : IDisposable
{
	private const string Patch = "a7c4c9d700000000.patch_0";
	private readonly string _root = Directory.CreateTempSubdirectory("w13-").FullName;

	[Fact]
	public async Task OrdinarySourcesKeepTheirOriginalPathsAndOrder()
	{
		var patch = Path.Combine(_root, Patch);
		File.WriteAllText(patch, "patch");
		var vpk = Path.Combine(_root, "model.vpk");
		File.WriteAllBytes(vpk, [0x34, 0x12, 0xaa, 0x55]);
		var ordinary = MakeZip("ordinary.zip", Patch);
		var nested = Path.Combine(_root, "nested.zip");
		using (var zip = ZipFile.Open(nested, ZipArchiveMode.Create)) zip.CreateEntryFromFile(ordinary, "ordinary.zip");
		var mainAlone = MakeZip("armor_IAS_MainMod.zip", Patch);
		var paths = new[] { patch, ordinary, vpk, MakeZip("vpk.7z", "model.vpk"), nested, mainAlone,
			Path.Combine(_root, "missing.zip") };
		using var drop = await IasStandardDrop.InspectAsync(paths, default);

		Assert.Equal(paths, drop.Items.Select(item => item.Path));
		Assert.All(drop.Items, item => Assert.Null(item.Package));
	}

	[Fact]
	public async Task SingleGenericMainPairsWithLooseCompanionDirectory()
	{
		var main = MakeZip("armor.zip", Patch);
		var companion = Directory.CreateDirectory(Path.Combine(_root, "companion")).FullName;
		File.WriteAllText(Path.Combine(companion, "mod_group.json"), "{}");
		using var drop = await IasStandardDrop.InspectAsync([main, companion], default);

		var route = Assert.Single(drop.Items);
		Assert.Equal(main, route.Path);
		Assert.NotNull(route.Package);
	}

	[Fact]
	public async Task NestedIasPairIsRoutedWithoutConsumingOtherPaths()
	{
		var main = MakeZip("armor.zip", Patch);
		var companion = MakeZip("character.hd2irpack.zip", "mod_group.json");
		var pair = Path.Combine(_root, "share.rar");
		using (var zip = ZipFile.Open(pair, ZipArchiveMode.Create))
		{
			zip.CreateEntryFromFile(main, "外套/armor.zip");
			zip.CreateEntryFromFile(companion, "外套/character.hd2irpack.zip");
		}
		var paths = new[] { MakeZip("ordinary.zip", Patch), pair, MakeZip("vpk.zip", "model.vpk") };
		string stage;
		using (var drop = await IasStandardDrop.InspectAsync(paths, default))
		{
			Assert.Equal(paths, drop.Items.Select(item => item.Path));
			Assert.Null(drop.Items[0].Package);
			Assert.Null(drop.Items[2].Package);
			var package = Assert.IsType<IasStandardDropPackage>(drop.Items[1].Package);
			var (source, output) = package.Stage();
			stage = Path.GetDirectoryName(output)!;
			Assert.True(File.Exists(Path.Combine(source, "main", Patch)));
			Assert.True(File.Exists(Path.Combine(source, "companion", "mod_group.json")));
		}
		Assert.False(Directory.Exists(stage));
	}

	[Fact]
	public async Task NamedIasMainIsPairedAndOrdinaryArchiveContinuesThroughItsOwnRoute()
	{
		var main = MakeZip("armor_IAS_MainMod.zip", Patch);
		var companion = MakeZip("companion.zip", "mod_group.json");
		var ordinary = MakeZip("ordinary.zip", Patch);
		using var drop = await IasStandardDrop.InspectAsync([companion, ordinary, main], default);

		Assert.Equal(new[] { main, ordinary }, drop.Items.Select(item => item.Path));
		var package = Assert.IsType<IasStandardDropPackage>(drop.Items[0].Package);
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			IasStandardDropImport.PrepareAsync(package, null, new StoragePaths(_root), default));
		Assert.Contains("请先在设置中选择有效的游戏数据目录", error.Message);
		Assert.Null(drop.Items[1].Package);
	}

	[Fact]
	public async Task AmbiguousLoosePairLeavesEverySourceOnItsOriginalRoute()
	{
		var paths = new[] { MakeZip("a.zip", Patch), MakeZip("b.zip", Patch), MakeZip("c.zip", "mod_group.json") };
		using var drop = await IasStandardDrop.InspectAsync(paths, default);

		Assert.Equal(paths, drop.Items.Select(item => item.Path));
		Assert.All(drop.Items, item => Assert.Null(item.Package));
	}

	private string MakeZip(string name, string entry)
	{
		var path = Path.Combine(_root, name);
		using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
		using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
		writer.Write("{}");
		return path;
	}

	public void Dispose() => IasFileTree.Delete(_root);
}
#endif
