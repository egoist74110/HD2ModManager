using HD2ModCore.Ias;

namespace HD2ModCore.Tests;

public sealed class IasProgressThrottleTests
{
	[Fact]
	public void LimitsToTenReportsPerSecondAndSkipsUnchangedDisplayValues()
	{
		var clock = new Clock();
		var received = new List<int>();
		var progress = new IasProgressThrottle<(int Percent, string Label)>(clock);
		for (var i = 0; i < 10_000; i++)
		{
			clock.ElapsedTicks = TimeSpan.TicksPerSecond * i / 10_000;
			if (progress.ShouldReport((i / 100, "下载"))) received.Add(i);
		}
		Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 1000), received);

		clock.ElapsedTicks = TimeSpan.TicksPerSecond;
		Assert.False(progress.ShouldReport((90, "下载")));
		Assert.True(progress.ShouldReport((90, "解压")));
	}

	private sealed class Clock : TimeProvider
	{
		public long ElapsedTicks { get; set; }
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => ElapsedTicks;
	}
}
