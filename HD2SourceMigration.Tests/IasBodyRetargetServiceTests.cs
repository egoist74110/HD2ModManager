using HD2SourceMigration.IasPackaging.BodyRetarget;
using Xunit;

namespace HD2SourceMigration.Tests;

public sealed class IasBodyRetargetServiceTests
{
	[Theory]
	[InlineData("1.6.4", true)]
	[InlineData("1.6.5", true)]
	[InlineData("1.6.3", false)]
	[InlineData("1.6.6", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void IsSupportedVersionMatchesVerifiedReleases(string? version, bool expected)
	{
		Assert.Equal(expected, IasBodyRetargetService.IsSupportedVersion(version));
	}

	[Fact]
	public async Task AnalyzeAsyncRejectsUnverifiedVersion()
	{
		var work = Directory.CreateTempSubdirectory("hd2-retarget-test-").FullName;
		try
		{
			var service = new IasBodyRetargetService("1.6.6", work, work, work, work);
			var result = await service.AnalyzeAsync(work, ["e4e41c5f9c3848f0"], work, CancellationToken.None);
			Assert.Equal("IAS 1.6.6 的配套格式尚未验证。", result.Error);
		}
		finally
		{
			Directory.Delete(work, recursive: true);
		}
	}

	[Fact]
	public async Task AnalyzeAsyncAcceptsIas165PastVersionGate()
	{
		var work = Directory.CreateTempSubdirectory("hd2-retarget-test-").FullName;
		try
		{
			var service = new IasBodyRetargetService("1.6.5", work, work, work, work);
			var result = await service.AnalyzeAsync(work, ["e4e41c5f9c3848f0"], work, CancellationToken.None);
			Assert.Equal("缺少 IAS Generator Python 或换护甲脚本。", result.Error);
		}
		finally
		{
			Directory.Delete(work, recursive: true);
		}
	}
}
