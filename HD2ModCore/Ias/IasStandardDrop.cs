namespace HD2ModCore.Ias;

public sealed record IasStandardImportItem(int Index, string Path, IasStandardDropPackage? Package = null)
{
	public string? Error { get; init; }
}

public sealed class IasStandardDropPackage
{
	private readonly IasDropWorkspace _workspace;
	private readonly string _main;
	private readonly string _companion;

	internal IasStandardDropPackage(IasDropWorkspace workspace, string main, string companion)
	{
		_workspace = workspace;
		_main = main;
		_companion = companion;
	}

	public (string Source, string Output) Stage()
	{
		var stage = _workspace.CreateStandardStage(_main, _companion);
		return (Path.Combine(stage, "in"), Path.Combine(stage, "out"));
	}
}

// 一次拖入共享配对范围和暂存生命周期。队列完成后释放解包及转换目录。
public sealed class IasStandardDrop : IDisposable
{
	private IasDropWorkspace? _workspace;

	private IasStandardDrop() { }

	public IReadOnlyList<IasStandardImportItem> Items { get; private set; } = [];

	public static async Task<IasStandardDrop> InspectAsync(IReadOnlyList<string> paths, CancellationToken token)
	{
		var drop = new IasStandardDrop();
		try
		{
			drop.Items = await drop.RouteAsync(paths, token).ConfigureAwait(false);
			return drop;
		}
		catch
		{
			drop.Dispose();
			throw;
		}
	}

	private async Task<IReadOnlyList<IasStandardImportItem>> RouteAsync(IReadOnlyList<string> paths, CancellationToken token)
	{
		var inputs = new IasInputContents?[paths.Count];
		var routes = paths.Select((path, index) => (IasStandardImportItem?)new IasStandardImportItem(index, path)).ToArray();
		for (var i = 0; i < paths.Count; i++)
		{
			token.ThrowIfCancellationRequested();
			var path = paths[i];
			if (!IasPackageDetector.HasExplicitMainName(path) &&
				!await MayContainCompanionAsync(path, token).ConfigureAwait(false)) continue;
			var input = inputs[i] = await InspectCandidateAsync(path, token).ConfigureAwait(false);
			if (input is { Main.Count: 1, Companions.Count: 1, Vpks.Count: 0 })
				routes[i] = CreateRoute(i, paths[i], input.Main[0], input.Companions[0]);
		}

		var companions = Enumerable.Range(0, paths.Count)
			.Where(i => inputs[i] is { Main.Count: 0, Companions.Count: 1, Vpks.Count: 0 }).ToArray();
		if (companions.Length == 1)
		{
			var mains = new List<int>();
			for (var i = 0; i < paths.Count; i++)
			{
				inputs[i] ??= await InspectCandidateAsync(paths[i], token).ConfigureAwait(false);
				if (inputs[i] is { Main.Count: 1, Companions.Count: 0, Vpks.Count: 0 }) mains.Add(i);
			}
			var named = mains.Where(i => inputs[i]!.HasNamedMain).ToArray();
			var candidates = named.Length == 0 ? mains.ToArray() : named;
			if (candidates.Length == 1)
			{
				var main = candidates[0];
				var companion = companions[0];
				var first = Math.Min(main, companion);
				routes[first] = CreateRoute(first, paths[main], inputs[main]!.Main[0], inputs[companion]!.Companions[0]);
				routes[Math.Max(main, companion)] = null;
			}
		}

		for (var i = 0; i < paths.Count; i++)
		{
			if (routes[i] is not { Package: null } route) continue;
			var error = inputs[i] switch
			{
				{ Main.Count: 0, Companions.Count: > 0, Vpks.Count: 0 } => IasPackageDetector.MissingMainHint,
				{ Main.Count: 1, Companions.Count: 0, Vpks.Count: 0, HasExplicitMain: true } => IasPackageDetector.MissingCompanionHint,
				_ => null
			};
			if (error is not null) routes[i] = route with { Error = error };
		}
		return routes.OfType<IasStandardImportItem>().ToArray();

		IasStandardImportItem CreateRoute(int index, string path, string main, string companion) =>
			new(index, path, new(_workspace!, main, companion));
	}

	private async Task<IasInputContents?> InspectCandidateAsync(string path, CancellationToken token)
	{
		try
		{
			if (!IasPackageDetector.MayAccept(path) || IasPackageDetector.IsVpk(path)) return null;
			_workspace ??= new IasDropWorkspace();
			return await _workspace.InspectAsync(path, token).ConfigureAwait(false);
		}
		catch (Exception error) when (IsProbeFailure(error))
		{
			System.Diagnostics.Trace.TraceWarning($"无法检查 IAS 拖入来源：{path}，{error.Message}");
			return null;
		}
	}

	private static async Task<bool> MayContainCompanionAsync(string path, CancellationToken token)
	{
		try { return await IasPackageDetector.MayContainCompanionAsync(path, token).ConfigureAwait(false); }
		catch (Exception error) when (IsProbeFailure(error)) { return false; }
	}

	private static bool IsProbeFailure(Exception error) =>
		error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException;

	public void Dispose() => _workspace?.Dispose();
}
