using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction;
using HD2SourceMigration.IasPackaging.BodyRetarget;
using Xunit;

namespace HD2SourceMigration.Tests;

public sealed class IasBodyRetargetServiceTests
{
	[Fact]
	public void AppendPackageDependenciesRetainsNativeAndAppendsSortedUniqueKeys()
	{
		var stock = new byte[16 + 16];
		BinaryPrimitives.WriteUInt32LittleEndian(stock.AsSpan(0, 4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(stock.AsSpan(8, 4), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(stock.AsSpan(16, 8), 0xcd4238c6a0c69e32UL);
		BinaryPrimitives.WriteUInt64LittleEndian(stock.AsSpan(24, 8), 0x1111111111111111UL);

		var unitType = 0xe0a48d0be9a7453fUL;
		var updated = IasBodyRetargetService.AppendPackageDependencies(stock,
		[
			new AssetKey(0xcd4238c6a0c69e32UL, 0x1111111111111111UL),
			new AssetKey(unitType, 0x3333333333333333UL),
			new AssetKey(unitType, 0x2222222222222222UL),
			new AssetKey(unitType, 0x2222222222222222UL)
		]);

		Assert.Equal(16 + 3 * 16, updated.Length);
		Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(8, 4)));
		Assert.Equal(stock[16..], updated[16..32]);
		Assert.Equal(unitType, BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(32, 8)));
		Assert.Equal(0x2222222222222222UL, BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(40, 8)));
		Assert.Equal(unitType, BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(48, 8)));
		Assert.Equal(0x3333333333333333UL, BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(56, 8)));
	}

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
