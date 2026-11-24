using System.Diagnostics;

namespace HD2ModCore.Ias;

/// <summary>IAS 操作分段计时：每段一行写到 &lt;管理器&gt;/logs/ias-timing.log，用来查哪一步慢。</summary>
public static class IasTiming
{
	private static readonly object Gate = new();
	private static readonly AsyncLocal<string?> Operation = new();
	private static string? _path;

	internal static void Configure(string appRoot) => _path = Path.Combine(appRoot, "logs", "ias-timing.log");

	/// <summary>标记当前异步流程属于哪个操作，之后的分段都带上这个名字。</summary>
	public static IDisposable Begin(string operation)
	{
		var previous = Operation.Value;
		Operation.Value = operation;
		var clock = Stopwatch.StartNew();
		return new Scope(() =>
		{
			Record("总计", clock.Elapsed);
			Operation.Value = previous;
		});
	}

	public static async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> run)
	{
		var clock = Stopwatch.StartNew();
		try { return await run().ConfigureAwait(false); }
		finally { Record(stage, clock.Elapsed); }
	}

	public static async Task MeasureAsync(string stage, Func<Task> run)
	{
		var clock = Stopwatch.StartNew();
		try { await run().ConfigureAwait(false); }
		finally { Record(stage, clock.Elapsed); }
	}

	public static T Measure<T>(string stage, Func<T> run)
	{
		var clock = Stopwatch.StartNew();
		try { return run(); }
		finally { Record(stage, clock.Elapsed); }
	}

	public static void Record(string stage, TimeSpan elapsed)
	{
		var line = $"{DateTimeOffset.Now:O}\t{Operation.Value ?? "-"}\t{stage}\t{elapsed.TotalMilliseconds:F0}ms";
		Trace.WriteLine("IAS 计时 " + line);
		if (_path is not { } path) return;
		try
		{
			lock (Gate)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.AppendAllText(path, line + Environment.NewLine);
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			// 计时只用于诊断，写不进去不影响操作。
		}
	}

	private sealed class Scope(Action dispose) : IDisposable
	{
		private Action? _dispose = dispose;
		public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
	}
}
