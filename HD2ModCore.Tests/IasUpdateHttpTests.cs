using System.IO.Compression;
using System.Net;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2ModCore.Updates;
using HD2ModManager.Services.Ias;
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

	[Theory]
	[InlineData(DownloadSourceMode.Auto)]
	[InlineData(DownloadSourceMode.Mirror)]
	public void AqSdkZipUsesGithubArchiveAddressForMirrors(DownloadSourceMode mode)
	{
		Assert.Equal(IasAqSdk.MirrorArchiveUri.AbsoluteUri, IasUpdateHttp.MirrorableUrl(IasAqSdk.ArchiveUri, mode));
	}

	[Fact]
	public void DirectModeKeepsOriginalAddress()
	{
		Assert.Equal(Lfs, IasUpdateHttp.MirrorableUrl(new Uri(Lfs), DownloadSourceMode.Ask));
		Assert.Equal(IasFeedClient.FeedUri.AbsoluteUri, IasUpdateHttp.MirrorableUrl(IasFeedClient.FeedUri, DownloadSourceMode.Ask));
		Assert.Equal(IasAqSdk.ArchiveUri.AbsoluteUri, IasUpdateHttp.MirrorableUrl(IasAqSdk.ArchiveUri, DownloadSourceMode.Ask));
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

	[Fact]
	public async Task DirectFollowAllowsRedirectToCodeload()
	{
		var seen = new List<string>();
		FetchFn raw = (url, _, _) =>
		{
			seen.Add(url);
			if (seen.Count == 1)
			{
				var redirect = new HttpResponseMessage(HttpStatusCode.Found);
				redirect.Headers.Location = IasAqSdk.ArchiveUri;
				return Task.FromResult(redirect);
			}
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
		};
		using var response = await UpdateNetwork.FollowAsync(raw, IasAqSdk.MirrorArchiveUri.AbsoluteUri, null, CancellationToken.None, loopback: false);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal([IasAqSdk.MirrorArchiveUri.AbsoluteUri, IasAqSdk.ArchiveUri.AbsoluteUri], seen);
	}

	[Fact]
	public async Task MirrorWrapAllowsRedirectToCodeload()
	{
		var seen = new List<string>();
		FetchFn raw = (url, _, _) =>
		{
			seen.Add(url);
			if (seen.Count == 1)
			{
				var redirect = new HttpResponseMessage(HttpStatusCode.Found);
				redirect.Headers.Location = IasAqSdk.ArchiveUri;
				return Task.FromResult(redirect);
			}
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
		};
		var wrapped = UpdateMirrors.Wrap(UpdateMirrors.All[0], raw);
		using var response = await wrapped(IasAqSdk.MirrorArchiveUri.AbsoluteUri, null, CancellationToken.None);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal([UpdateMirrors.All[0].Base + IasAqSdk.MirrorArchiveUri.AbsoluteUri, IasAqSdk.ArchiveUri.AbsoluteUri], seen);
	}

	[Fact]
	public async Task AqSdkUsesInjectedHttpClientAndMarksDownloadFailure()
	{
		var previousEnv = Environment.GetEnvironmentVariable("HD2_AQSDK_DIR");
		var tempRoot = Directory.CreateTempSubdirectory("ias-aqsdk-test-").FullName;
		try
		{
			Environment.SetEnvironmentVariable("HD2_AQSDK_DIR", null);
			var paths = new StoragePaths(tempRoot);

			// 1) 失败时（网络异常 / 超时）应被识别为可换源的下载失败，并在拖入转换汇总里置 DownloadFailed。
			using var failingHttp = new HttpClient(new StubHandler(_ => throw new TaskCanceledException("simulated timeout")));
			var timeoutError = await Assert.ThrowsAsync<IOException>(() =>
				IasAqSdk.GetAsync(paths, Path.Combine(tempRoot, "work-timeout"), CancellationToken.None, failingHttp));
			Assert.True(IasDownloadFailure.Is(timeoutError));

			var dropResult = await IasModuleService.AddDropConversionsAsync(
				[new IasModuleService.DropConversion(Path.Combine(tempRoot, "a.vpk"), IasDropItemKind.L4d2Vpk, "a", [], [])],
				0, [], null, (_, _) => throw new InvalidDataException(timeoutError.Message, timeoutError), CancellationToken.None);
			Assert.False(dropResult.Success);
			Assert.True(dropResult.DownloadFailed);

			// 2) 正常通过注入的 HttpClient 下载并解包到缓存目录。
			var zipBytes = CreateFakeAqSdkZip();
			Uri? requested = null;
			using var okHttp = new HttpClient(new StubHandler(req =>
			{
				requested = req.RequestUri;
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
			}));
			var extracted = await IasAqSdk.GetAsync(paths, Path.Combine(tempRoot, "work-ok"), CancellationToken.None, okHttp);
			Assert.Equal(IasAqSdk.ArchiveUri, requested);
			Assert.True(File.Exists(Path.Combine(extracted, "hd2_system", "resource_binding_targets.json")));
		}
		finally
		{
			Environment.SetEnvironmentVariable("HD2_AQSDK_DIR", previousEnv);
			IasFileTree.Delete(tempRoot);
		}
	}

	private static byte[] CreateFakeAqSdkZip()
	{
		using var ms = new MemoryStream();
		using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
		{
			var entry = zip.CreateEntry("HD2SDK-AQ-Edition-86b7252abcdef/hd2_system/resource_binding_targets.json");
			using var writer = new StreamWriter(entry.Open());
			writer.Write("{}");
		}
		return ms.ToArray();
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}
}
