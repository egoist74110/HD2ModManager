using System.Diagnostics;

namespace HD2SourceMigration.IasPackaging;

internal sealed class PackagingTiming(Action<string, TimeSpan>? record)
{
	public void Record(string stage, TimeSpan elapsed) => record?.Invoke(stage, elapsed);

	public T Measure<T>(string stage, Func<T> run)
	{
		using var scope = Start(stage);
		return run();
	}

	public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> run)
	{
		using var scope = Start(stage);
		return await run().ConfigureAwait(false);
	}

	public IDisposable Start(string stage) => new Scope(this, stage);

	private sealed class Scope(PackagingTiming timing, string stage) : IDisposable
	{
		private readonly long start = Stopwatch.GetTimestamp();
		public void Dispose() => timing.Record(stage, Stopwatch.GetElapsedTime(start));
	}
}
