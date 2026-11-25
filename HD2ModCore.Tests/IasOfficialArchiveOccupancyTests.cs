#if WINDOWS
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Ias;
using Xunit;
using Xunit.Abstractions;

namespace HD2ModCore.Tests;

public sealed class IasOfficialArchiveOccupancyTests(ITestOutputHelper output)
{
	[Fact]
	public async Task RealLibraryCopyMatchesOfficialConflictReport()
	{
		var original = Environment.GetEnvironmentVariable("HD2_IAS_W32_LIBRARY");
		var components = Environment.GetEnvironmentVariable("HD2_IAS_W32_COMPONENTS");
		var worker = Environment.GetEnvironmentVariable("HD2_IAS_W32_WORKER");
		var temp = Environment.GetEnvironmentVariable("HD2_IAS_W32_SANDBOX");
		if (original is null || components is null || worker is null || temp is null) return;
		var root = Directory.CreateDirectory(Path.Combine(temp, Guid.NewGuid().ToString("N"))).FullName;
		try
		{
			var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
			var library = Path.Combine(root, "library");
			var originalIndex = Directory.EnumerateFiles(Path.Combine(original, "games"), "mod_library.json",
				SearchOption.AllDirectories).Single();
			var originalBytes = await File.ReadAllBytesAsync(originalIndex);
			var stateJson = JsonNode.Parse(originalBytes)!.AsObject();
			stateJson["game"] = game;
			var gameKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToLowerInvariant()))).ToLowerInvariant();
			var indexDirectory = Directory.CreateDirectory(Path.Combine(library, "games", gameKey)).FullName;
			await File.WriteAllTextAsync(Path.Combine(indexDirectory, "mod_library.json"), stateJson.ToJsonString());
			IasFileTree.Copy(Path.Combine(original, "blobs"), Path.Combine(library, "blobs"));

			var workerSource = Path.GetDirectoryName(worker)!;
			var processWorker = new IasProcessWorker(workerSource, Path.Combine(root, "worker"));
			var request = new IasWorkerRequest("archive-occupancy", game, components, library, root, "", "", "1.6.4");
			var reply = await processWorker.RunAsync(request, default);
			Assert.True(reply.Success, reply.Error);
			var owners = JsonSerializer.Deserialize<IasArchiveOwner[]>(reply.Value!)!;
			Assert.All(owners, owner => Assert.True(owner.Enabled));
			var expected = new Dictionary<string, string[]>
			{
				["sdkgroup.00daea208cdc440a9abc88b184d809c9"] = ["59d238094a164c88", "d2fde496226be96f"],
				["sdkgroup.59f306f236334a6584e253f443e0b651"] = ["0121fd0e8370a1dc", "5e0dba19a567fb99"],
				["sdkgroup.cb4287f3ac59409db7abff4ed7950153"] = ["fdd3d3e677e5a3f4", "ffcad1f7ff9888d7"],
				["sdkgroup.deca3070d87752dc9f291677f680b2c7"] = ["84fd7aab7cf6f62d", "b186eae3f2b61509"]
			};
			Assert.Equal(expected.Keys.Order(), owners.Select(owner => owner.Id).Order());

			var load = new AssemblyLoadContext("IAS W32 对照", isCollectible: true);
			load.Resolving += (context, name) =>
			{
				var dependency = Path.Combine(workerSource, name.Name + ".dll");
				return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
			};
			var official = load.LoadFromAssemblyPath(Path.Combine(components, "app", "HD2综合护甲系统.dll"));
			var storeType = official.GetType("HD2RuntimeManager.OwnedStore", throwOnError: true)!;
			var store = Activator.CreateInstance(storeType, library, game)!;
			var state = storeType.GetMethod("LibraryStatus")!.Invoke(store, null)!;
			var entriesField = state.GetType().GetField("entries")!;
			var installed = (Array)entriesField.GetValue(state)!;
			var inspect = storeType.GetMethod("InspectEnabledConflicts")!;
			Assert.Empty(Issues(inspect.Invoke(store, [state])!));

			foreach (var owner in owners)
			{
				var entry = installed.Cast<object>().Single(item => (string)Field(item, "id")! == owner.Id);
				// 复制条目而保留真实 Bundle。官方报告会列出这个 Bundle 独占的每个 Archive。
				var duplicate = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
					.Invoke(entry, null)!;
				duplicate.GetType().GetField("id")!.SetValue(duplicate, "comparison");
				var pair = Array.CreateInstance(entry.GetType(), 2);
				pair.SetValue(entry, 0);
				pair.SetValue(duplicate, 1);
				entriesField.SetValue(state, pair);
				var report = inspect.Invoke(store, [state])!;
				var archives = Issues(report).Select(issue =>
				{
					Assert.Equal("archive", Field(issue, "kind"));
					Assert.Equal(new[] { "comparison", owner.Id }, (string[])Field(issue, "owners")!);
					return (string)Field(issue, "key")!;
				}).Order(StringComparer.Ordinal).ToArray();
				Assert.Equal(expected[owner.Id], archives);
				Assert.Equal(archives, owner.Archives);
				Assert.IsType<InvalidDataException>(Assert.Throws<TargetInvocationException>(() =>
					report.GetType().GetMethod("RequireClear")!.Invoke(report, null)).InnerException);

				var companion = Path.Combine(root, owner.Id + ".zip");
				ZipFile.CreateFromDirectory(Path.Combine(original, "sources", owner.Id, "companion"), companion);
				var candidate = await processWorker.RunAsync(request with { Command = "companion-archives", Argument = companion }, default);
				Assert.True(candidate.Success, candidate.Error);
				Assert.Equal(archives, JsonSerializer.Deserialize<string[]>(candidate.Value!));
				output.WriteLine($"{owner.Name} | {string.Join(", ", archives)}");
			}
			load.Unload();
			Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalIndex));
		}
		finally { IasFileTree.Delete(root); }
	}

	private static object? Field(object value, string field) => value.GetType().GetField(field)!.GetValue(value);
	private static object[] Issues(object report) => ((Array)Field(report, "issues")!).Cast<object>().ToArray();

}
#endif
