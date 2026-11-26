using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

internal sealed class IasModTransaction : IDisposable
{
	private readonly StoragePaths _paths;
	private readonly OptionActivationStore? _optionActivations;
	private readonly string _previous;
	private readonly IasSnapshot _snapshot;
	private IasSnapshot.Manifest _manifest;
	private readonly Dictionary<string, string?> _writes = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, IReadOnlyDictionary<string, OptionHostState>> _optionChanges = new(StringComparer.OrdinalIgnoreCase);
	private bool _runtimeCaptured;
	private bool _finished;
	internal string Work { get; }
	internal string Backup { get; }
	internal string Export { get; }

	internal IasModTransaction(StoragePaths paths, string root, string game, bool captureRuntime, bool captureComponents,
		OptionActivationStore? optionActivations)
	{
		_paths = paths;
		_optionActivations = optionActivations;
		_previous = IasFileTree.Under(root, "snapshots/previous");
		if (IasSnapshot.Exists(_previous)) throw new InvalidOperationException($"IAS 快照尚未清理：{_previous}");
		_snapshot = new(_previous, root, paths, game);
		_manifest = _snapshot.CreateManifest();
		Work = IasFileTree.Under(root, "temp/" + Guid.NewGuid().ToString("N"));
		var operation = Path.GetFileName(Work);
		Backup = IasFileTree.Under(root, "backup/" + operation);
		Export = IasFileTree.Under(root, "export/" + operation);
		try
		{
			// 先写好空清单，再让恢复入口可见。中断时不会留下一个无法识别的空快照。
			var preparing = Path.Combine(Work, "snapshot");
			Directory.CreateDirectory(preparing);
			new IasSnapshot(preparing, root, paths, game).Save(_manifest);
			Directory.CreateDirectory(Path.GetDirectoryName(_previous)!);
			Directory.Move(preparing, _previous);
			if (captureRuntime) EnsureRuntimeCaptured();
			if (captureComponents) Capture("components");
			Directory.CreateDirectory(Backup);
			Directory.CreateDirectory(Export);
		}
		catch
		{
			IasFileTree.Delete(Work);
			IasFileTree.Delete(Backup);
			IasFileTree.Delete(Export);
			IasFileTree.Delete(_previous);
			throw;
		}
	}

	internal async Task<(LibrarySnapshot Snapshot, ModNodeId[] Roots, ModNodeId[] All)> ImportAsync(
		LibrarySnapshot snapshot, string input, string? name, string? label, Guid? replacement,
		CancellationToken token, IasRole? roleReplacement = null, string? companion = null,
		IReadOnlySet<Guid>? registeredIds = null)
	{
		if (File.Exists(input))
		{
			var directory = Path.Combine(Work, Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(input));
			IasFileTree.Extract(input, directory, token);
			input = directory;
		}
		var planService = CoreServices.CreateModImportPlanner();
		var plan = await planService.PlanFolderAsync(input, token).ConfigureAwait(false);
		var errors = plan.Diagnostics.Where(d => d.IsBlocking).Select(d => d.Message).ToArray();
		if (errors.Length != 0 || !plan.Items.Any(item => item.Role is ImportPlanItemRole.Host or ImportPlanItemRole.PlaceholderHost))
			throw new InvalidDataException($"IAS 包导入预检失败：{string.Join("；", errors.DefaultIfEmpty("没有可导入的主体"))}");

		var stage = Path.Combine(Work, Guid.NewGuid().ToString("N"));
		var stagePaths = new StoragePaths(stage);
		var importer = CoreServices.CreateModImportExecutionService(stagePaths);
		var imported = await importer.ImportFolderAsync(input, token).ConfigureAwait(false);
		var nodes = snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
		var staged = imported.Snapshot.Nodes;
		var roots = imported.ImportedNodeIds.Where(id => staged.TryGetValue(id, out var node) && node.Metadata.Kind == ModNodeKind.Standard).Distinct().ToArray();
		if (roots.Length == 0) throw new InvalidDataException("IAS 包没有可部署的主体。");
		if (label == "IAS 系统" && (roots.Length != 1 || staged.Count != 1))
			throw new InvalidDataException("IAS data 包必须只含一个主体。");
		if (replacement is { } existing && nodes.TryGetValue(new(existing), out var previous))
		{
			var fresh = staged[roots.Single()];
			var next = fresh with
			{
				Id = previous.Id,
				RelativePath = previous.RelativePath,
				Metadata = fresh.Metadata with { Name = name!, Notes = Label(fresh.Metadata.Notes, label), CreatedUtc = previous.Metadata.CreatedUtc }
			};
			nodes[previous.Id] = next;
			var source = IasFileTree.Under(stagePaths.ModsDirectory, fresh.RelativePath);
			WriteMetadata(source, next);
			_writes[ModPath(previous.RelativePath)] = source;
			return (snapshot with { Nodes = nodes }, [previous.Id], [previous.Id]);
		}

		var previousRoots = roleReplacement?.ModIds.Select(id => new ModNodeId(id)).ToArray();
		var owned = (roleReplacement?.OwnedModIds ?? roleReplacement?.ModIds ?? []).Select(id => new ModNodeId(id)).ToHashSet();
		var reclaimable = new HashSet<ModNodeId>();
		if (roleReplacement is null && companion is not null)
			foreach (var id in roots)
			{
				if (registeredIds?.Contains(id.Value) == true || !nodes.TryGetValue(id, out var node) ||
					(!IasLibraryScan.IsMarked(node) && IasLibraryScan.ReadCandidate(_paths, node, token) is null)) continue;
				reclaimable.Add(id);
				reclaimable.UnionWith(IasLibraryScan.PackagedOptions(snapshot, node).Where(option =>
					registeredIds?.Contains(option.Value) != true));
			}
		var mapping = staged.Keys.ToDictionary(id => id);
		if (previousRoots is { Length: 1 } && roots.Length == 1)
			mapping[roots[0]] = previousRoots[0];
		else if (previousRoots is not null && !previousRoots.ToHashSet().SetEquals(roots))
			throw new InvalidDataException("新版 IAS 主包的主体结构发生变化，无法保留原角色的设置。");
		if (roleReplacement is not null)
		{
			var previousOptions = owned.Where(snapshot.Nodes.ContainsKey).Select(id => snapshot.Nodes[id])
				.Where(node => node.Metadata.Kind == ModNodeKind.Option && node.Metadata.SourcePackagePath is not null)
				.ToLookup(node => node.Metadata.SourcePackagePath!, StringComparer.OrdinalIgnoreCase);
			foreach (var node in staged.Values.Where(node => node.Metadata.Kind == ModNodeKind.Option && !owned.Contains(node.Id)))
			{
				if (node.Metadata.SourcePackagePath is not { } path) continue;
				var candidates = previousOptions[path].ToArray();
				if (candidates.Length == 1 && !staged.ContainsKey(candidates[0].Id)) mapping[node.Id] = candidates[0].Id;
			}
		}
		if (mapping.Values.Distinct().Count() != mapping.Count)
			throw new InvalidDataException("新版 IAS 主包的主体身份和已有选项重叠。");

		foreach (var (id, node) in staged)
		{
			var isRoot = roots.Contains(id);
			var targetId = mapping[id];
			nodes.TryGetValue(targetId, out var existingNode);
			if (existingNode is not null && !owned.Contains(targetId) && !reclaimable.Contains(targetId))
				throw new InvalidOperationException($"IAS 主包和库里的「{existingNode.Metadata.Name}」使用了同一个 Mod 编号，无法安全覆盖。");
			var existingPath = existingNode is null ? null : ModPath(existingNode.RelativePath);
			var installed = node with
			{
				Id = targetId,
				RelativePath = existingNode?.RelativePath ?? node.RelativePath,
				Children = node.Children.Select(child => mapping.GetValueOrDefault(child, child))
					.Concat(existingNode?.Children.Where(child => !owned.Contains(child)) ?? []).Distinct().ToArray(),
				Metadata = node.Metadata with
				{
					Name = existingNode?.Metadata.Name ?? (isRoot && roots.Length == 1 && name is not null ? name : node.Metadata.Name),
					Notes = isRoot ? EnsureLabel(existingNode?.Metadata.Notes ?? node.Metadata.Notes, label) :
						existingNode?.Metadata.Notes ?? node.Metadata.Notes,
					CreatedUtc = existingNode?.Metadata.CreatedUtc ?? node.Metadata.CreatedUtc,
					HostModGuids = node.Metadata.HostModGuids?.Select(MapId).ToArray(),
					SuppressDefaultDirectOptionActivation = existingNode?.Metadata.SuppressDefaultDirectOptionActivation ??
						node.Metadata.SuppressDefaultDirectOptionActivation
				}
			};
			while (existingPath is null && (Path.Exists(ModPath(installed.RelativePath)) || _writes.ContainsKey(ModPath(installed.RelativePath))))
				installed = installed with { RelativePath = $"ias-{Guid.NewGuid():N}" };
			var source = IasFileTree.Under(stagePaths.ModsDirectory, node.RelativePath);
			WriteMetadata(source, installed, existingPath);
			if (isRoot && companion is not null)
				File.Copy(companion, Path.Combine(source, IasLibraryScan.CompanionFile), overwrite: true);
			_writes[ModPath(installed.RelativePath)] = source;
			nodes[targetId] = installed;
		}
		var options = CoreServices.CreateOptionActivationStore(stagePaths).CreateSnapshot();
		foreach (var (key, value) in options.Options)
		{
			if (Guid.TryParse(key, out var guid) && mapping.TryGetValue(new(guid), out var target) && nodes.ContainsKey(target) &&
				!snapshot.Nodes.ContainsKey(target))
				_optionChanges.Add(target.Value.ToString("N"), value.ToDictionary(pair => MapId(pair.Key), pair => pair.Value,
					StringComparer.OrdinalIgnoreCase));
		}
		var nextSnapshot = RemoveMods(snapshot with { Nodes = nodes },
			owned.Except(mapping.Values).Select(id => id.Value));
		return (nextSnapshot, roots.Select(id => mapping[id]).ToArray(), mapping.Values.ToArray());

		string MapId(string id) => Guid.TryParse(id, out var guid) && mapping.TryGetValue(new(guid), out var mapped)
			? mapped.Value.ToString("N") : id;
	}

	internal LibrarySnapshot RemoveMods(LibrarySnapshot snapshot, IEnumerable<Guid> ids)
	{
		var removed = ids.Select(id => new ModNodeId(id)).ToHashSet();
		if (removed.Count == 0) return snapshot;
		var nodes = snapshot.Nodes.Where(pair => !removed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
		var options = OptionRelationshipGraph.Create(snapshot);
		foreach (var reference in nodes.Values)
			if (reference.Children.Any(removed.Contains) || options.GetParents(reference.Id).Any(removed.Contains) ||
				options.GetDirectOptions(reference.Id).Any(option => removed.Contains(option.Id)))
				throw new InvalidOperationException($"其他 Mod 引用了要卸载的 IAS 内容：{reference.Metadata.Name}");
		foreach (var id in removed)
		{
			if (!snapshot.Nodes.TryGetValue(id, out var node)) continue;
			_writes[ModPath(node.RelativePath)] = null;
			_optionChanges[id.Value.ToString("N")] = new Dictionary<string, OptionHostState>();
		}
		var profiles = new ProfileMutationService();
		var current = snapshot;
		foreach (var profile in snapshot.Profiles)
		{
			var entries = profile.Entries.Where(entry => removed.Contains(entry.NodeId)).Select(entry => entry.NodeId).ToArray();
			if (entries.Length == 0) continue;
			var result = profiles.RemoveEntries(current, profile.Id, entries);
			if (!result.Succeeded) throw new InvalidOperationException($"无法从配置移除 IAS：{result.Message}");
			current = result.Snapshot;
		}
		return current with { Nodes = nodes };
	}

	internal string Revision(LibrarySnapshot snapshot, IEnumerable<ModNodeId> ids, CancellationToken token) =>
		string.Join("/", ids.Select(id =>
		{
			var target = ModPath(snapshot.Nodes[id].RelativePath);
			var directory = _writes[target]!;
			return IasLibraryScan.Revision(directory, IasLibraryScan.ReadFiles(directory, token), token);
		}));

	internal async Task PublishAsync(LibrarySnapshot next, JsonModLibraryStore store, CancellationToken token)
	{
		Capture("manager/library.json");
		Capture("manager/profiles.json");
		var optionsPath = Path.Combine(_paths.ModsDirectory, "option-activations.json");
		if (_optionChanges.Count != 0) Capture("manager/option-activations.json");

		foreach (var (target, source) in _writes)
		{
			token.ThrowIfCancellationRequested();
			Capture("manager/nodes/" + _manifest.Entries.Count, Path.GetRelativePath(_paths.ModsDirectory, target));
			IasFileTree.Delete(target);
			if (source is not null) IasFileTree.Copy(source, target);
		}
		if (_optionChanges.Count != 0)
		{
			var previous = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(optionsPath, token).ConfigureAwait(false);
			var options = previous.Options.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
			foreach (var (key, value) in _optionChanges)
			{
				if (value.Count == 0) options.Remove(key);
				else options[key] = value;
			}
			var optionStore = _optionActivations ?? CoreServices.CreateOptionActivationStore(_paths);
			await optionStore.RestoreSnapshotAsync(new(options), token).ConfigureAwait(false);
		}
		await store.SaveAsync(next, token).ConfigureAwait(false);
	}

	private void Capture(string relative, string? node = null)
	{
		var entry = new IasSnapshot.Entry(relative, false, node);
		var target = _snapshot.Target(entry);
		var copy = IasFileTree.Under(_previous, relative);
		var exists = IasSnapshot.Exists(target);
		IasFileTree.Copy(target, copy, preserveAttributes: true);
		if (exists && !IasSnapshot.Exists(copy)) throw new IOException($"无法保存 IAS 快照：{target}");
		_manifest.Entries.Add(entry with { Exists = exists });
		_snapshot.Save(_manifest);
	}

	internal void EnsureRuntimeCaptured()
	{
		if (_runtimeCaptured) return;
		Capture("Runtime");
		Capture("library");
		_runtimeCaptured = true;
	}

	internal void CaptureShaderToggler()
	{
		const string relative = "game/ShaderToggler.ini";
		var path = _snapshot.Target(new(relative, false));
		if (Directory.Exists(path)) throw new IOException("ShaderToggler.ini 的位置被目录占用。");
		Capture(relative);
	}

	internal void RecordDeployment(IasSnapshot.Deployment deployment)
	{
		var manifest = _manifest with { PriorDeployment = deployment };
		_snapshot.Save(manifest);
		_manifest = manifest;
	}

	internal IasSnapshot.Deployment? PriorDeployment => _manifest.PriorDeployment;
	internal Task RollbackAsync() => _snapshot.RestoreFilesAsync(_manifest, _optionActivations);

	internal void Finish(string result)
	{
		_snapshot.Finish(result);
		_finished = true;
	}

	public void Dispose()
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		try { DisposeCore(); }
		finally { IasTiming.Record("清理暂存与快照", clock.Elapsed); }
	}

	private void DisposeCore()
	{
		IasSnapshot.Clean(Work);
		if (_finished)
		{
			try { IasSnapshot.RemoveFinished(_previous); }
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				System.Diagnostics.Trace.TraceWarning($"无法移走已完成的 IAS 快照：{_previous}，{error.Message}");
			}
		}
		if (!Directory.EnumerateFileSystemEntries(Export).Any()) IasSnapshot.Clean(Export);
		if (!Directory.EnumerateFileSystemEntries(Backup).Any()) IasSnapshot.Clean(Backup);
	}

	private string ModPath(string relative) => IasFileTree.Under(_paths.ModsDirectory, relative);
	private static void WriteMetadata(string directory, ModNode node, string? previousDirectory = null)
	{
		var previous = ModSettingsFile.TryRead(previousDirectory ?? directory);
		var error = ModSettingsFile.TryWrite(directory, new(ModSettingsFile.CurrentSchema, previous?.EnabledMaterialPatches,
			node.Id.Value.ToString("N"), node.Metadata.Name, node.Metadata.Notes));
		if (error is not null) throw new IOException($"无法写入 IAS Mod 标识：{error}");
	}
	private static string? Label(string? notes, string? label) =>
		label is null ? notes : string.IsNullOrEmpty(notes) ? $"[{label}]" : $"[{label}] {notes}";
	private static string? EnsureLabel(string? notes, string? label) =>
		label is not null && notes?.StartsWith($"[{label}]", StringComparison.Ordinal) == true ? notes : Label(notes, label);
}

