using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Ias;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class IasWorkerRuntimeTests
{
	[Fact]
	public async Task ValidExistingFilesDoNotDownload()
	{
		using var fixture = new Fixture();
		fixture.WriteFiles(fixture.Worker);

		await fixture.EnsureAsync();

		Assert.Empty(fixture.Requests);
		fixture.AssertInstalled();
	}

	[Fact]
	public async Task DevelopmentFilesAreLeftForWorkerCopyWithoutDownloading()
	{
		using var fixture = new Fixture();
		fixture.WriteFiles(fixture.Source);

		await fixture.EnsureAsync();

		Assert.Empty(fixture.Requests);
		Assert.False(Directory.Exists(fixture.Worker));
	}

	[Fact]
	public async Task DownloadsBothPackagesAndReplacesCorruptFiles()
	{
		using var fixture = new Fixture();
		Directory.CreateDirectory(fixture.Worker);
		await File.WriteAllTextAsync(Path.Combine(fixture.Worker, Fixture.Files[0].Name), "stale");

		await fixture.EnsureAsync();
		await fixture.EnsureAsync();

		Assert.Equal(new[] { "api.nuget.org", "api.nuget.org" }, fixture.Requests.Select(uri => uri.Host));
		Assert.Equal("/v3-flatcontainer/sdk/1.0.0/sdk.1.0.0.nupkg", fixture.Requests[0].AbsolutePath);
		Assert.Contains(fixture.Progress, value => value.Stage == IasStage.Download && value.Message == "下载 IAS 运行库");
		fixture.AssertInstalled();
	}

	[Fact]
	public async Task OnlyDownloadsPackageWithMissingFiles()
	{
		using var fixture = new Fixture();
		fixture.WriteFiles(fixture.Worker);
		File.Delete(Path.Combine(fixture.Worker, Fixture.Files[2].Name));

		await fixture.EnsureAsync();

		Assert.Equal("/v3-flatcontainer/foundation/1.0.0/foundation.1.0.0.nupkg",
			Assert.Single(fixture.Requests).AbsolutePath);
		fixture.AssertInstalled();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FallsBackInOrderAfterHttpOrPackageHashFailure(bool badHash)
	{
		using var fixture = new Fixture();
		fixture.Send = (uri, bytes, _) => Task.FromResult(uri.Host switch
		{
			"api.nuget.org" when badHash => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0]) },
			"api.nuget.org" or "repo.huaweicloud.com" => new(HttpStatusCode.ServiceUnavailable),
			_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
		});

		await fixture.EnsureAsync();

		string[] hosts = ["api.nuget.org", "repo.huaweicloud.com", "mirrors.cloud.tencent.com"];
		Assert.Equal(hosts.Concat(hosts), fixture.Requests.Select(uri => uri.Host));
		fixture.AssertInstalled();
	}

	[Theory]
	[InlineData(false, "SHA-512")]
	[InlineData(true, "SHA-256")]
	public async Task FailedVerificationDoesNotPublishAnyFiles(bool badEntry, string expectedError)
	{
		using var fixture = new Fixture(badEntry);
		if (!badEntry)
			fixture.Send = (uri, bytes, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(uri.AbsolutePath.Contains("/foundation/", StringComparison.Ordinal) ? [0] : bytes)
			});

		var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.EnsureAsync());

		Assert.Contains(expectedError, error.Message);
		Assert.Equal(4, fixture.Requests.Count);
		Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Worker));
	}

	[Theory]
	[InlineData(false, "api.nuget.org", "repo.huaweicloud.com", "mirrors.cloud.tencent.com")]
	[InlineData(true, "repo.huaweicloud.com", "mirrors.cloud.tencent.com", "api.nuget.org")]
	public void SourceOrderFollowsMirrorPreference(bool preferMirrors, params string[] hosts)
	{
		var package = IasWorkerRuntime.RequiredPackages[0];
		Assert.Equal(hosts, IasWorkerRuntime.OrderedSources(preferMirrors).Select(source => new Uri(package.Url(source)).Host));
		Assert.All(IasWorkerRuntime.OrderedSources(preferMirrors),
			source => Assert.True(IasWorkerRuntime.IsPackageRequest(new Uri(package.Url(source)))));
	}

	[Fact]
	public async Task CancellationCleansStagingWithoutTryingMirrors()
	{
		using var fixture = new Fixture();
		using var cancellation = new CancellationTokenSource();
		fixture.Send = (_, _, token) =>
		{
			cancellation.Cancel();
			return Task.FromCanceled<HttpResponseMessage>(token);
		};

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.EnsureAsync(cancellation.Token));

		Assert.Single(fixture.Requests);
		Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Worker));
	}

	private sealed class Fixture : IDisposable
	{
		internal static readonly IasWorkerRuntime.RuntimeFile[] Files =
			IasWorkerRuntime.RequiredPackages.SelectMany(package => package.Files).ToArray();
		private readonly string _root = Directory.CreateTempSubdirectory("ias-runtime-test-").FullName;
		private readonly HttpClient _http;
		private readonly IasWorkerRuntime _runtime;
		internal string Source => Path.Combine(_root, "bin", "ias-worker");
		internal string Worker => Path.Combine(_root, "ias", "worker");
		internal List<Uri> Requests { get; } = [];
		internal List<IasProgress> Progress { get; } = [];
		internal Func<Uri, byte[], CancellationToken, Task<HttpResponseMessage>> Send { get; set; } =
			(_, bytes, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

		internal Fixture(bool badEntry = false)
		{
			var archives = new Dictionary<string, byte[]>();
			_http = new(new Handler((uri, token) =>
			{
				Requests.Add(uri);
				// 官方/华为是 …/{id}.{ver}.nupkg，腾讯是 …/{id}/{ver}，按路径里的包名找。
				return Send(uri, archives.Single(pair => uri.AbsolutePath.Contains("/" + pair.Key + "/")).Value, token);
			}));
			_runtime = new(_http, [CreatePackage("sdk", Files[..2]), CreatePackage("foundation", Files[2..])]);

			IasWorkerRuntime.Package CreatePackage(string id, IasWorkerRuntime.RuntimeFile[] files)
			{
				using var stream = new MemoryStream();
				using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
				{
					foreach (var file in files)
					{
						using var entry = zip.CreateEntry(file.Entry).Open();
						entry.Write(Content(file));
					}
				}
				var bytes = stream.ToArray();
				archives.Add(id, bytes);
				return new(id, "1.0.0", Convert.ToBase64String(SHA512.HashData(bytes)), files.Select(file =>
					file with { Sha256 = Convert.ToHexString(SHA256.HashData(badEntry && id == "foundation" ? [0] : Content(file))) })
					.ToArray());
			}
		}

		internal Task EnsureAsync(CancellationToken token = default) =>
			_runtime.EnsureAsync(Source, Worker, new ProgressRecorder(Progress), token);

		internal void WriteFiles(string directory)
		{
			Directory.CreateDirectory(directory);
			foreach (var file in Files)
				File.WriteAllBytes(Path.Combine(directory, file.Name), Content(file));
		}

		internal void AssertInstalled()
		{
			Assert.Equal(Files.Select(file => file.Name).Order(),
				Directory.EnumerateFileSystemEntries(Worker).Select(Path.GetFileName).Order());
			foreach (var file in Files)
				Assert.Equal(Content(file), File.ReadAllBytes(Path.Combine(Worker, file.Name)));
		}

		private static byte[] Content(IasWorkerRuntime.RuntimeFile file) => Encoding.UTF8.GetBytes(file.Entry);

		public void Dispose()
		{
			_http.Dispose();
			IasFileTree.Delete(_root);
		}

		private sealed class Handler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
		{
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
				send(request.RequestUri!, cancellationToken);
		}

		private sealed class ProgressRecorder(List<IasProgress> values) : IProgress<IasProgress>
		{
			public void Report(IasProgress value) => values.Add(value);
		}
	}
}
