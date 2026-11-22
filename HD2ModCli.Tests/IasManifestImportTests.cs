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
		var aliases = IasDp8Routes.Slots.ToDictionary(slot => slot.Source, slot => slot.Source + 1);
		var lines = new List<string> { "manifest_version=6", $"archive={IasDp8Routes.SourceBody}" };
		AddOptions("Torso_Armor", "coat", "外套", "穿外套", "脱外套");
		AddOptions("LeftLeg", "shoes", "鞋子", "穿鞋", "脱鞋");
		var plan = IasDp8Routes.Plan(aliases, lines);

		var root = Path.Combine(Path.GetTempPath(), "hd2clitests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var package = Path.Combine(root, "package");
		Directory.CreateDirectory(appRoot);
		foreach (var folder in new[] { "", "coat/on", "coat/off", "shoes/on", "shoes/off" })
		{
			var dir = Path.Combine(package, folder);
			Directory.CreateDirectory(dir);
			await File.WriteAllTextAsync(Path.Combine(dir, $"{IasDp8Routes.TargetBody}.patch_0"), folder);
		}
		await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), IasDp8Converter.SerializeManifest(plan));

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
			var coatOn = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "穿外套（默认）");
			var coatOff = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "脱外套");
			var shoes = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "鞋子");
			var shoesOn = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "穿鞋（默认）");
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

		void AddOptions(string part, string group, string name, string onName, string offName)
		{
			lines.AddRange([
				$"free_part={group},{part},on",
				$"free_part_display_name={group},{name}",
				$"free_option={group},off,1",
				$"free_option={group},on,0",
				$"free_option_display_name={group},on,{onName}",
				$"free_option_display_name={group},off,{offName}",
			]);
			foreach (var slot in IasDp8Routes.Slots.Where(slot => slot.Part == part))
			{
				lines.Add($"free_route={group},on,{slot.Source:x16},{aliases[slot.Source]:x16},{aliases[slot.Source]:x16}");
				lines.Add($"free_route={group},off,{slot.Source:x16},{aliases[slot.Source]:x16},{aliases[slot.Source] + 1:x16}");
			}
		}
	}
}
