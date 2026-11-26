using HD2ModCore.Ias;
using HD2ModCore.Updates;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class IasUpdateHttpTests
{
	private const string Lfs =
		"https://media.githubusercontent.com/media/Estecsky/HD2-Integrated-Armor-System/7f9ec1208699a0db64764b8f0f4bc6be35d668ea/updates/1.6.5/HD2IAS-1.6.5-update.zip";

	[Theory]
	[InlineData(DownloadSourceMode.Auto)]
	[InlineData(DownloadSourceMode.Mirror)]
	public void LfsZipUsesGithubAddressForMirrors(DownloadSourceMode mode)
	{
		// IAS 1.6.5 的整包放在 LFS 上；镜像只认 github.com，换成同一文件的 raw 地址。
		Assert.Equal(
			"https://github.com/Estecsky/HD2-Integrated-Armor-System/raw/7f9ec1208699a0db64764b8f0f4bc6be35d668ea/updates/1.6.5/HD2IAS-1.6.5-update.zip",
			IasUpdateHttp.MirrorableUrl(new Uri(Lfs), mode));
	}

	[Fact]
	public void DirectModeKeepsOriginalAddress()
	{
		Assert.Equal(Lfs, IasUpdateHttp.MirrorableUrl(new Uri(Lfs), DownloadSourceMode.Ask));
		Assert.Equal(IasFeedClient.FeedUri.AbsoluteUri, IasUpdateHttp.MirrorableUrl(IasFeedClient.FeedUri, DownloadSourceMode.Ask));
	}

	[Fact]
	public void FeedUsesGithubAddressForMirrors() =>
		Assert.Equal("https://github.com/Estecsky/HD2-Integrated-Armor-System/raw/refs/heads/main/feed.json",
			IasUpdateHttp.MirrorableUrl(IasFeedClient.FeedUri, DownloadSourceMode.Auto));

	[Theory]
	[InlineData("https://media.githubusercontent.com/media/someone/other-repo/abc/file.zip")]
	[InlineData("https://github.com/Estecsky/HD2-Integrated-Armor-System/releases/download/V1.6.4/HD2IAS.zip")]
	public void OtherAddressesStayUnchanged(string url) =>
		Assert.Equal(url, IasUpdateHttp.MirrorableUrl(new Uri(url), DownloadSourceMode.Mirror));
}
