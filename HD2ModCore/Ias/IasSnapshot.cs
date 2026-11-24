using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

internal sealed class IasSnapshot(string previous, string root, StoragePaths paths, string game)
{
	private string ManifestPath => Path.Combine(previous, "manifest.json");
	private string ModsRoot => Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ModsDirectory));
	internal sealed record Entry(string Copy, bool Exists, string? Node = null);
	internal sealed record Deployment(bool CanRestore, Guid? ProfileId);
	internal sealed record Manifest(string ModsRoot, string GameRoot, List<Entry> Entries, Deployment? PriorDeployment = null);

	internal Manifest CreateManifest() => new(ModsRoot, game, []);

	internal void Save(Manifest manifest)
	{
		var pending = ManifestPath + ".tmp";
		File.WriteAllText(pending, JsonSerializer.Serialize(manifest));
		File.Move(pending, ManifestPath, overwrite: true);
	}

	internal string Target(Entry entry) => (entry.Copy, entry.Node) switch
	{
		("Runtime" or "library" or "components", null) => IasFileTree.Under(root, entry.Copy),
		("manager/library.json" or "manager/profiles.json" or "manager/option-activations.json", null) =>
			IasFileTree.Under(ModsRoot, Path.GetFileName(entry.Copy)),
		("game/ShaderToggler.ini", null) => IasFileTree.Under(game, "bin/ShaderToggler.ini"),
		(var copy, { } node) when copy.StartsWith("manager/nodes/", StringComparison.Ordinal) &&
			int.TryParse(copy.AsSpan("manager/nodes/".Length), out var index) && index >= 0 =>
			IasFileTree.Under(ModsRoot, node),
		_ => throw new InvalidDataException($"IAS 快照目标无效：{entry.Copy}")
	};

	internal static bool Exists(string previous) => IasFileTree.Attributes(previous) is not null;

	internal Manifest Load(out List<Exception> failures)
	{
		failures = [];
		IasPaths.RejectLink(ManifestPath);
		if (IasFileTree.Attributes(ManifestPath) is not null)
		{
			var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath))
				?? throw new InvalidDataException("IAS 快照清单为空。");
			if (!string.Equals(manifest.ModsRoot, ModsRoot, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(manifest.GameRoot, game, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("IAS 快照的 Mod 库或游戏目录与当前配置不一致。");
			return manifest;
		}

		// 旧版没有记录缺失目标、游戏根目录，也没有写出 Mod 节点编号与原路径的对应关系。
		if (Exists(Path.Combine(previous, "manager")))
			failures.Add(new InvalidDataException("旧版 IAS 快照缺少管理器 Mod 节点原路径，无法安全恢复管理器库。"));
		if (Exists(Path.Combine(previous, "game")))
			failures.Add(new InvalidDataException("旧版 IAS 快照缺少原游戏目录，无法安全恢复 ShaderToggler.ini。"));
		var entries = new[] { "Runtime", "library", "components" }
			.Where(copy => Exists(IasFileTree.Under(previous, copy))).Select(copy => new Entry(copy, true)).ToList();
		if (entries.Count == 0 && failures.Count == 0)
			throw new InvalidDataException("IAS 快照缺少恢复清单和可识别的旧版内容。");
		return CreateManifest() with { Entries = entries };
	}

	internal async Task RestoreFilesAsync(Manifest manifest, OptionActivationStore? options, List<Exception>? failures = null)
	{
		failures ??= [];
		foreach (var entry in manifest.Entries.AsEnumerable().Reverse())
		{
			try
			{
				var target = Target(entry);
				var copy = entry.Exists ? IasFileTree.Under(previous, entry.Copy) : null;
				IasFileTree.Restore(copy, target, failures);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				failures.Add(new IOException($"恢复快照项 {entry.Copy} 失败：{error.Message}", error));
			}
		}

		if (options is not null && manifest.Entries.Any(entry => entry.Copy == "manager/option-activations.json"))
		{
			try { await options.ReloadAsync().ConfigureAwait(false); }
			catch (Exception error) { failures.Add(new IOException("重新加载 Mod 选项激活状态失败。", error)); }
		}
		if (failures.Count != 0)
			throw new AggregateException($"有 {failures.Count} 项 IAS 快照内容恢复失败", failures);
	}

	internal void Finish(string result)
	{
		var finished = IasFileTree.Under(previous, "finished");
		File.WriteAllText(finished, result);
	}

	internal static void RemoveFinished(string previous)
	{
		// 先移走事务入口。清理中断时，已完成的快照不会被误当成待恢复的快照。
		var retired = previous + "-finished-" + Guid.NewGuid().ToString("N");
		Directory.Move(previous, retired);
		Clean(retired);
	}

	internal static void Clean(string path)
	{
		try { IasFileTree.Delete(path); }
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			System.Diagnostics.Trace.TraceWarning($"无法清理 IAS 暂存：{path}，{error.Message}");
		}
	}
}
