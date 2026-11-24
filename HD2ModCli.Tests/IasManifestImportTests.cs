using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2SourceMigration.IasConversion;
using Xunit;

namespace HD2ModCli.Tests;

public sealed class IasManifestImportTests
{
	[Fact]
	public async Task SerializeManifest_ImportsIntoVanillaManagerWithOnlyPrimaryOptionsEnabled()
	{
		IasOptionGroup[] groups =
		[
			new("外套", [new("穿外套", "coat/on", []), new("脱外套", "coat/off", [])]),
			new("鞋子", [new("穿鞋", "shoes/on", []), new("脱鞋", "shoes/off", [])]),
		];

		var root = Path.Combine(Path.GetTempPath(), "hd2clitests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var package = Path.Combine(root, "package");
		Directory.CreateDirectory(appRoot);
		foreach (var folder in new[] { "", "coat/on", "coat/off", "shoes/on", "shoes/off" })
		{
			var dir = Path.Combine(package, folder);
			Directory.CreateDirectory(dir);
			await File.WriteAllTextAsync(Path.Combine(dir, "0000000000000001.patch_0"), folder);
		}
		await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), IasStandardConverter.SerializeManifest("IAS 夹具", groups));

		try
		{
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));
			var imported = await importer.ImportFolderAsync(package);

			Assert.False(imported.HasOptionPathConflicts);
			Assert.Empty(imported.UnownedPayloadPaths);

			var states = new OptionActivationStore(Path.Combine(appRoot, "option-activations.json"));
			var rootNode = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard);
			await new OptionActivationDefaultInitializer().InitializeMissingStatesAsync(imported.Snapshot, states, [rootNode.Id]);
			var snapshot = states.CreateSnapshot();

			var coat = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "外套");
			var coatOn = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "穿外套");
			var coatOff = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "脱外套");
			var shoes = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "鞋子");
			var shoesOn = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "穿鞋");
			var shoesOff = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "脱鞋");

			var rootKey = rootNode.Id.Value.ToString("N");
			Assert.True(snapshot.IsEnabled(coat.Id.Value.ToString("N"), rootKey));
			Assert.True(snapshot.IsEnabled(coatOn.Id.Value.ToString("N"), coat.Id.Value.ToString("N")));
			Assert.False(snapshot.IsEnabled(coatOff.Id.Value.ToString("N"), coat.Id.Value.ToString("N")));
			Assert.True(snapshot.IsEnabled(shoes.Id.Value.ToString("N"), rootKey));
			Assert.True(snapshot.IsEnabled(shoesOn.Id.Value.ToString("N"), shoes.Id.Value.ToString("N")));
			Assert.False(snapshot.IsEnabled(shoesOff.Id.Value.ToString("N"), shoes.Id.Value.ToString("N")));
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}

	}
}
