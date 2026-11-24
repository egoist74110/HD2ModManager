namespace HD2ModCore.Ias;

/// <summary>Samples changed progress at most once per 100 ms, before notifying observers.</summary>
public sealed class IasProgressThrottle<T>(TimeProvider? clock = null)
{
	private readonly TimeProvider _clock = clock ?? TimeProvider.System;
	private readonly object _gate = new();
	private (long At, T Key)? _last;

	public bool ShouldReport(T displayKey)
	{
		lock (_gate)
		{
			var now = _clock.GetTimestamp();
			if (_last is { } last && (EqualityComparer<T>.Default.Equals(last.Key, displayKey) ||
				_clock.GetElapsedTime(last.At, now) < TimeSpan.FromMilliseconds(100)))
				return false;
			_last = (now, displayKey);
			return true;
		}
	}
}
