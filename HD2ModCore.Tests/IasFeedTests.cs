using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

public sealed class IasFeedTests
{
	[Fact]
	public async Task GetsLatestFromAuthenticSignedFeed()
	{
		using var http = Client(request =>
		{
			Assert.Equal(IasFeedClient.FeedUri, request.RequestUri);
			return new(HttpStatusCode.OK) { Content = new ByteArrayContent(IasFeedFixture.SignedFeed) };
		});
		var result = await new IasFeedClient(http).GetLatestAsync();
		Assert.Equal(IasFeedFailure.None, result.Failure);
		var release = Assert.IsType<IasRelease>(result.Latest);
		Assert.Equal("1.6.4", release.Version);
		Assert.Equal(20261122012510L, release.Sequence);
		Assert.Equal(20261102204529L, release.MinimumSequence);
		Assert.Equal(248618011L, release.ZipBytes);
		Assert.Equal("541dc4341945b048651d16551e6eeebc052e70cd63162758c6b52c07dcd25192", release.ZipSha256);
		Assert.Equal("media.githubusercontent.com", release.ZipUrl.Host);
		Assert.Equal("https://github.com/Estecsky/HD2-Integrated-Armor-System/releases/tag/V1.6.4", release.ReleaseUrl.AbsoluteUri);
		Assert.Contains("1.6.4", release.Notes);
		Assert.Equal(13, release.Files.Count);
	}

	[Theory]
	[InlineData("payload")]
	[InlineData("signature")]
	public async Task RejectsChangedSignedBytes(string field)
	{
		var envelope = JsonNode.Parse(IasFeedFixture.SignedFeed)!.AsObject();
		var changed = Convert.FromBase64String(envelope[field]!.GetValue<string>());
		changed[changed.Length / 2] ^= 1;
		envelope[field] = Convert.ToBase64String(changed);
		using var http = Client(_ => new(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString()) });
		var result = await new IasFeedClient(http).GetLatestAsync();
		Assert.Null(result.Latest);
		Assert.Equal(IasFeedFailure.InvalidSignature, result.Failure);
	}

	[Fact]
	public void RejectsUnknownSchemaAfterAuthenticationBoundary()
	{
		// 只有官方持有签名私钥，单独覆盖验签后的 schema 分支。
		var result = IasFeedClient.ParsePayload("""{"schema":"HD2ManagerFeed2"}"""u8.ToArray());
		Assert.Null(result.Latest);
		Assert.Equal(IasFeedFailure.InvalidSchema, result.Failure);
	}

	[Fact]
	public async Task ReturnsNetworkStatusAndTimeoutSeparately()
	{
		using var unavailable = Client(_ => new(HttpStatusCode.ServiceUnavailable));
		using var timeout = Client(_ => throw new TaskCanceledException());
		var status = await new IasFeedClient(unavailable).GetLatestAsync();
		Assert.Equal(IasFeedFailure.HttpStatus, status.Failure);
		Assert.Contains("503", status.Message);
		Assert.Equal(IasFeedFailure.Timeout, (await new IasFeedClient(timeout).GetLatestAsync()).Failure);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ResumesDownloadOrRestartsWhenRangeIgnored(bool supportsRange)
	{
		using var sandbox = new Sandbox();
		var data = "verified-official-content"u8.ToArray();
		var release = CreateRelease(data);
		var attempts = 0;
		using var http = Client(request =>
		{
			if (++attempts == 1)
			{
				Assert.Null(request.Headers.Range);
				// 模拟连接只交付前 8 字节就结束，下一次从这 8 字节续传。
				return new(HttpStatusCode.OK) { Content = new StreamContent(new ShortNonSeekableStream(data[..8])) };
			}
			Assert.Equal("bytes=8-", request.Headers.Range?.ToString());
			if (!supportsRange)
				return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
			var content = new ByteArrayContent(data[8..]);
			content.Headers.ContentRange = new ContentRangeHeaderValue(8, data.Length - 1, data.Length);
			return new(HttpStatusCode.PartialContent) { Content = content };
		});
		var store = sandbox.Store(http);
		await Assert.ThrowsAsync<EndOfStreamException>(() => store.DownloadAsync(release));
		Assert.Equal(8, new FileInfo(Assert.Single(Directory.GetFiles(sandbox.Cache, "*.part"))).Length);

		var package = await store.DownloadAsync(release);
		Assert.Equal(data, await File.ReadAllBytesAsync(package));
		Assert.Empty(Directory.GetFiles(sandbox.Cache, "*.part"));
		Assert.Equal(package, await store.DownloadAsync(release));
		Assert.Equal(2, attempts);
	}

	[Fact]
	public async Task DiscardsCorruptWholeArchive()
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent("fails"u8.ToArray()) });
		await Assert.ThrowsAsync<InvalidDataException>(() => sandbox.Store(http).DownloadAsync(CreateRelease("valid"u8.ToArray())));
		Assert.Empty(Directory.GetFiles(sandbox.Cache));
	}

	[Fact]
	public async Task RejectsCorruptComponentAndPreservesInstalledVersion()
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => new(HttpStatusCode.ServiceUnavailable));
		var store = sandbox.Store(http);
		var first = sandbox.CreatePackage("1.6.3");
		await store.ExtractCoreAsync(first.Release, first.Package);
		var previous = (await store.ActivateAsync(first.Release.Version)).Installed;
		var second = sandbox.CreatePackage("1.6.4", corruptFile: true);
		var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ExtractCoreAsync(second.Release, second.Package));
		Assert.Contains("app/client.dll", error.Message);
		await Assert.ThrowsAsync<InvalidOperationException>(() => store.ActivateAsync(second.Release.Version));
		Assert.Equal(previous, store.GetInstalled());
		Assert.Equal(new[] { previous.ComponentsDirectory }, Directory.GetDirectories(sandbox.Components));
	}

	[Fact]
	public async Task RestoresStageIfCurrentPointerCannotBeReplaced()
	{
		// Windows 文件共享模式可以精确阻断指针替换，同时允许读取旧指针。
		if (!OperatingSystem.IsWindows())
			return;
		using var sandbox = new Sandbox();
		using var http = Client(_ => throw new InvalidOperationException());
		var store = sandbox.Store(http);
		var first = sandbox.CreatePackage("1.6.3");
		await store.ExtractCoreAsync(first.Release, first.Package);
		var previous = (await store.ActivateAsync(first.Release.Version)).Installed;
		var second = sandbox.CreatePackage("1.6.4");
		var stage = await store.ExtractCoreAsync(second.Release, second.Package);
		using (new FileStream(Path.Combine(sandbox.Components, "current.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var error = await Record.ExceptionAsync(() => store.ActivateAsync(second.Release.Version));
			Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
			Assert.True(Directory.Exists(stage));
			Assert.False(Directory.Exists(Path.Combine(sandbox.Components, second.Release.Version)));
			Assert.Equal(previous, store.GetInstalled());
			Assert.Empty(Directory.GetFiles(sandbox.Components, ".current-*.json"));
		}
		Assert.Equal(second.Release.Version, (await store.ActivateAsync(second.Release.Version)).Installed.Version);
		Assert.False(Directory.Exists(previous.ComponentsDirectory));
	}

	[Theory]
	[InlineData("../escape.txt")]
	[InlineData("app/../escape.txt")]
	[InlineData("ignored/../../escape.txt")]
	[InlineData("app\\escape.txt")]
	[InlineData("C:/escape.txt")]
	[InlineData("app/client.dll:alt")]
	public async Task RejectsUnsafeArchivePaths(string unsafePath)
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => throw new InvalidOperationException());
		var store = sandbox.Store(http);
		var sample = sandbox.CreatePackage(excessEntry: unsafePath);
		await Assert.ThrowsAsync<InvalidDataException>(() => store.ExtractCoreAsync(sample.Release, sample.Package));
		Assert.Empty(Directory.GetDirectories(sandbox.Components));
		Assert.False(File.Exists(Path.Combine(sandbox.Root, "escape.txt")));
	}

	[Fact]
	public async Task ChecksBothHostHashesAndBlocksIncompatibleActivation()
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => throw new InvalidOperationException());
		var store = sandbox.Store(http);
		var sample = sandbox.CreatePackage();
		var stage = await store.ExtractCoreAsync(sample.Release, sample.Package);
		Assert.Equal(IasGameCompatibilityStatus.Compatible, IasGameCompatibility.Check(sandbox.Game, stage).Status);

		var dll = Path.Combine(sandbox.Game, "data", "game", "game.dll");
		await File.WriteAllTextAsync(dll, "changed");
		var mismatch = IasGameCompatibility.Check(sandbox.Game, stage);
		Assert.Equal(IasGameCompatibilityStatus.Incompatible, mismatch.Status);
		var difference = Assert.Single(mismatch.Details);
		Assert.Equal("data/game/game.dll", difference.Path);
		Assert.Equal(Sha("game"u8.ToArray()), difference.ExpectedSha256);
		Assert.Equal(Sha("changed"u8.ToArray()), difference.ActualSha256);
		var exception = await Assert.ThrowsAsync<IasGameCompatibilityException>(() => store.ActivateAsync(sample.Release.Version));
		Assert.Equal(mismatch.Status, exception.Result.Status);
		Assert.Null(store.GetInstalled());

		File.Delete(dll);
		var missing = IasGameCompatibility.Check(sandbox.Game, stage);
		Assert.Equal(IasGameCompatibilityStatus.GameFilesMissing, missing.Status);
		Assert.Null(Assert.Single(missing.Details).ActualSha256);
	}

	[Fact]
	public async Task ActivatesAndCleansOldVersionAndRemovalKeepsOtherIasData()
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => new(HttpStatusCode.ServiceUnavailable));
		var store = sandbox.Store(http);
		var first = sandbox.CreatePackage("1.6.3");
		await store.ExtractCoreAsync(first.Release, first.Package);
		var previous = (await store.ActivateAsync(first.Release.Version)).Installed;
		using (var official = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(IasFeedFixture.SignedFeed) }))
		{
			var onlineState = await new IasStatusService(store, new IasFeedClient(official)).GetStatusAsync(sandbox.Game);
			Assert.Equal(new IasModuleState.UpdateAvailable("1.6.3", "1.6.4"), onlineState);
		}
		var second = sandbox.CreatePackage("1.6.4", excessEntry: "ExternalRuntime/Unrelated.zip", includeGraphics: true);
		await store.ExtractCoreAsync(second.Release, second.Package);
		var current = (await store.ActivateAsync(second.Release.Version)).Installed;
		Assert.False(Directory.Exists(previous.ComponentsDirectory));
		Assert.Equal(current, store.GetInstalled());
		Assert.False(File.Exists(Path.Combine(current.ComponentsDirectory, "ExternalRuntime", "Unrelated.zip")));
		foreach (var name in new[] { "ReShade", "DLSSFrameGeneration", "DLSSNeuralRendering" })
			Assert.Equal(name, File.ReadAllText(Path.Combine(current.ComponentsDirectory, "ExternalRuntime", name + ".zip")));
		Assert.Equal(8, Directory.GetFiles(current.ComponentsDirectory, "*", SearchOption.AllDirectories).Length);
		var state = await new IasStatusService(store, new IasFeedClient(http)).GetStatusAsync(sandbox.Game);
		Assert.Equal(new IasModuleState.Installed("1.6.4"), state);

		var library = Directory.CreateDirectory(Path.Combine(sandbox.Root, "ias", "library")).FullName;
		var runtime = Directory.CreateDirectory(Path.Combine(sandbox.Root, "ias", "runtime")).FullName;
		Directory.CreateDirectory(sandbox.Cache);
		await store.RemoveAsync();
		await store.RemoveAsync();
		Assert.Null(store.GetInstalled());
		Assert.False(Directory.Exists(sandbox.Components));
		Assert.False(Directory.Exists(sandbox.Cache));
		Assert.True(Directory.Exists(library));
		Assert.True(Directory.Exists(runtime));
	}

	[Fact]
	public async Task SameVersionRepairFillsMissingGraphicsArchivesWithoutChangingActivation()
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => throw new InvalidOperationException());
		var store = sandbox.Store(http);
		var legacy = sandbox.CreatePackage();
		await store.ExtractCoreAsync(legacy.Release, legacy.Package);
		var active = (await store.ActivateAsync(legacy.Release.Version)).Installed;
		var repair = sandbox.CreatePackage(includeGraphics: true);
		var pending = Path.Combine(sandbox.Root, "repair");

		await store.RepairGraphicsAsync(repair.Release, repair.Package, pending, null, default);

		Assert.Equal(active, store.GetInstalled());
		Assert.Equal("signed", File.ReadAllText(Path.Combine(active.ComponentsDirectory, "app/client.dll")));
		foreach (var name in new[] { "ReShade", "DLSSFrameGeneration", "DLSSNeuralRendering" })
			Assert.Equal(name, File.ReadAllText(Path.Combine(active.ComponentsDirectory, "ExternalRuntime", name + ".zip")));
	}

	[Fact]
	public async Task RejectsGraphicsArchiveAbsentFromSignedManifest()
	{
		using var sandbox = new Sandbox();
		using var http = Client(_ => throw new InvalidOperationException());
		var sample = sandbox.CreatePackage(excessEntry: "ExternalRuntime/ReShade.zip");
		var error = await Assert.ThrowsAsync<InvalidDataException>(() => sandbox.Store(http).ExtractCoreAsync(sample.Release, sample.Package));
		Assert.Contains("ExternalRuntime/ReShade.zip", error.Message);
	}

	private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new FakeHandler(send));

	private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static IasRelease CreateRelease(byte[] data, string version = "1.6.4", IReadOnlyList<IasReleaseFile>? files = null)
	{
		const string repo = "Estecsky/HD2-Integrated-Armor-System";
		return new(version, 2, 1, "", new($"https://media.githubusercontent.com/media/{repo}/main/update.zip"),
			Sha(data), data.Length, files ?? [], new($"https://github.com/{repo}/releases/tag/V{version}"));
	}

	private static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> files)
	{
		using var stream = new MemoryStream();
		using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (path, data) in files)
			{
				using var entry = zip.CreateEntry(path).Open();
				entry.Write(data);
			}
		}
		return stream.ToArray();
	}

	private sealed class Sandbox : IDisposable
	{
		public string Root { get; } = Path.Combine(Path.GetTempPath(), $"hd2-ias-feed-tests-{Guid.NewGuid():N}");
		public string Cache => Path.Combine(Root, "ias", "cache");
		public string Components => Path.Combine(Root, "ias", "components");
		public string Game => Path.Combine(Root, "game");

		public Sandbox()
		{
			Directory.CreateDirectory(Path.Combine(Game, "bin"));
			Directory.CreateDirectory(Path.Combine(Game, "data", "game"));
			File.WriteAllBytes(Path.Combine(Game, "bin", "helldivers2.exe"), "exe"u8.ToArray());
			File.WriteAllBytes(Path.Combine(Game, "data", "game", "game.dll"), "game"u8.ToArray());
		}

		public IasComponentStore Store(HttpClient http) => new(new StoragePaths(Root), http, Game);

		public (IasRelease Release, string Package) CreatePackage(string version = "1.6.4", bool corruptFile = false,
			string? excessEntry = null, bool includeGraphics = false)
		{
			var host = new Dictionary<string, string>
			{
				["bin/helldivers2.exe"] = Sha("exe"u8.ToArray()),
				["data/game/game.dll"] = Sha("game"u8.ToArray())
			};
			var baseJson = JsonSerializer.SerializeToUtf8Bytes(new { schema = "HD2ArmorBase1", host });
			var files = new Dictionary<string, byte[]>
			{
				["app/client.dll"] = "signed"u8.ToArray(),
				["ExternalRuntime/ArmorBase.zip"] = Zip(new Dictionary<string, byte[]> { ["base.json"] = baseJson }),
				["ExternalRuntime/ArmorAssets.zip"] = "assets"u8.ToArray(),
				["ExternalRuntime/Generator.zip"] = "generator"u8.ToArray(),
				["ExternalRuntime/bundle.json"] = "{}"u8.ToArray()
			};
			if (includeGraphics)
				foreach (var name in new[] { "ReShade", "DLSSFrameGeneration", "DLSSNeuralRendering" })
					files.Add($"ExternalRuntime/{name}.zip", Encoding.UTF8.GetBytes(name));
			var manifest = files.Select(pair => new IasReleaseFile(pair.Key, pair.Value.Length, Sha(pair.Value))).ToArray();
			if (corruptFile)
				files["app/client.dll"] = "edited"u8.ToArray();
			if (excessEntry is not null)
				files.Add(excessEntry, "ignored"u8.ToArray());
			var data = Zip(files);
			var path = Path.Combine(Root, version + ".zip");
			File.WriteAllBytes(path, data);
			return (CreateRelease(data, version, manifest), path);
		}

		public void Dispose() => Directory.Delete(Root, recursive: true);
	}

	private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(send(request));
		}
	}

	private sealed class ShortNonSeekableStream(byte[] bytes) : MemoryStream(bytes)
	{
		public override bool CanSeek => false;
	}
}
