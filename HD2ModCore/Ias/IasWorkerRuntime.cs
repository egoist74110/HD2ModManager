using System.IO.Compression;
using System.Security.Cryptography;

namespace HD2ModCore.Ias;

internal sealed class IasWorkerRuntime(HttpClient http, IasWorkerRuntime.Package[] packages, bool preferMirrors = false)
{
	// {id}/{ver} 为小写包名/版本。2026-11-25 实测三处都返回 200 且大小一致：
	// 华为云的 v3 地址取自其 v3/index.json 的 PackageBaseAddress；腾讯云只有 v2 源，下载地址是 /nuget/{id}/{ver}。
	private const string OfficialSource = "https://api.nuget.org/v3-flatcontainer/{id}/{ver}/{id}.{ver}.nupkg";
	private static readonly string[] MirrorSources =
	[
		"https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote/{id}/{ver}/{id}.{ver}.nupkg",
		"https://mirrors.cloud.tencent.com/nuget/{id}/{ver}"
	];
	private static readonly string[] Sources = [OfficialSource, .. MirrorSources];
	// 30 秒没收到任何数据才放弃当前源（连接本身另有 15 秒超时），慢但在动的下载不会被掐断。
	private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

	/// <summary>用户在更新设置里选了「优先使用镜像」时先走国内镜像，最后才试官方源。</summary>
	internal static IReadOnlyList<string> OrderedSources(bool preferMirrors) =>
		preferMirrors ? [.. MirrorSources, OfficialSource] : Sources;

	// 版本与 HD2IasWorker.csproj 一致。SHA-512 来自 NuGet 缓存的 .nupkg.sha512，
	// SHA-256 来自该缓存包中下列条目的原始字节。升级 worker 依赖时同步更新。
	internal static readonly Package[] RequiredPackages =
	[
		new("microsoft.windows.sdk.net.ref", "10.0.19041.57",
			"TI9FqukRZSFfSZ6fLh0cjO0X0OIAn5vZKGU/iNzINt3IZeK7o9Y1Dmtp+xwEv696XO1cHMB7aUqTjql3/KY9xg==",
			[
				new("lib/net8.0/Microsoft.Windows.SDK.NET.dll", "0EC371D93798852E36461C8ADDDBEADCE0F963A04752F0B64E54FE19C1C834A7"),
				new("lib/net8.0/WinRT.Runtime.dll", "BCF3A14E8712A90837FC5C0D8C8A24696AF2BD7F74E9767597EC81EDEDFB23DB")
			]),
		new("microsoft.windowsappsdk.foundation", "1.8.251104000",
			"a0LrWAS5g12rpk1hMuYB1NrZ0upc5cSTa/DhQ8P5C26DOpzmBL6Zd9+JHbTjinSp8JpulNv6NNNPX7lQ5f0iDQ==",
			[
				new("runtimes-framework/win-x64/native/Microsoft.WindowsAppRuntime.dll",
					"89FEC903532A888AE4EEF250E2D7D0CF0D02574799957F70844C99F28330717A")
			])
	];

	internal sealed record Package(string Id, string Version, string Sha512, RuntimeFile[] Files)
	{
		internal string Url(string source) => source.Replace("{id}", Id).Replace("{ver}", Version);
	}

	internal sealed record RuntimeFile(string Entry, string Sha256)
	{
		internal string Name => Path.GetFileName(Entry);
	}

	internal static bool IsPackageRequest(Uri uri) => RequiredPackages.Any(package =>
		Sources.Any(source => string.Equals(package.Url(source), uri.AbsoluteUri, StringComparison.Ordinal)));

	internal async Task EnsureAsync(string sourceDirectory, string workerDirectory, IProgress<IasProgress>? progress,
		CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		// 开发布局中的文件由原有的 worker 复制步骤提供。
		if (packages.SelectMany(package => package.Files).All(file => File.Exists(Path.Combine(sourceDirectory, file.Name))))
			return;

		var missing = new List<(Package Package, List<RuntimeFile> Files)>();
		foreach (var package in packages)
		{
			var files = new List<RuntimeFile>();
			foreach (var file in package.Files)
			{
				var target = IasFileTree.Under(workerDirectory, file.Name);
				if (!File.Exists(target) || !await MatchesAsync(target, file.Sha256, token).ConfigureAwait(false))
					files.Add(file);
			}
			if (files.Count > 0) missing.Add((package, files));
		}
		if (missing.Count == 0) return;

		var staging = IasFileTree.Under(workerDirectory, $".runtime-{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);
		try
		{
			foreach (var (package, files) in missing)
				await FetchAsync(package, files, staging, progress, token).ConfigureAwait(false);

			// 所有缺失文件通过校验后才发布，每个文件在同卷内原子替换。
			token.ThrowIfCancellationRequested();
			foreach (var (_, files) in missing)
				foreach (var file in files)
					File.Move(Path.Combine(staging, file.Name), IasFileTree.Under(workerDirectory, file.Name), overwrite: true);
		}
		finally { IasFileTree.Delete(staging); }
	}

	private async Task FetchAsync(Package package, List<RuntimeFile> files, string staging, IProgress<IasProgress>? progress,
		CancellationToken token)
	{
		const long maximumPackageBytes = 32 * 1024 * 1024;
		const string message = "下载 IAS 运行库";
		var nupkg = Path.Combine(staging, package.Id + ".nupkg");
		var failures = new List<Exception>();
		var reporting = new IasProgressThrottle<int>();
		foreach (var source in OrderedSources(preferMirrors))
		{
			token.ThrowIfCancellationRequested();
			progress?.Report(new(IasStage.Download, 0, 0, message));
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(StallTimeout);
			try
			{
				using var response = await http.GetAsync(package.Url(source), HttpCompletionOption.ResponseHeadersRead,
					timeout.Token).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				var size = response.Content.Headers.ContentLength ?? 0;
				using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
				await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
				await using (var output = new FileStream(nupkg, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
					FileOptions.Asynchronous))
				{
					await IasComponentStore.CopyBoundedAsync(input, output, maximumPackageBytes, hash, written =>
					{
						timeout.CancelAfter(StallTimeout);
						if (size > 0 && progress is not null && reporting.ShouldReport((int)(written * 100 / size)))
							progress.Report(new(IasStage.Download, written, size, message));
					}, timeout.Token).ConfigureAwait(false);
				}
				if (Convert.ToBase64String(hash.GetHashAndReset()) != package.Sha512)
					throw new InvalidDataException("NuGet 包的 SHA-512 不符。");

				using var zip = ZipFile.OpenRead(nupkg);
				foreach (var file in files)
				{
					var entry = zip.GetEntry(file.Entry) ?? throw new InvalidDataException($"NuGet 包缺少 {file.Entry}。");
					var pending = Path.Combine(staging, file.Name);
					await using (var input = entry.Open())
					await using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
						FileOptions.Asynchronous))
						await input.CopyToAsync(output, timeout.Token).ConfigureAwait(false);
					if (!await MatchesAsync(pending, file.Sha256, timeout.Token).ConfigureAwait(false))
						throw new InvalidDataException($"IAS 运行库 {file.Name} 的 SHA-256 不符。");
				}
				return;
			}
			catch (OperationCanceledException error) when (!token.IsCancellationRequested)
			{
				failures.Add(new TimeoutException($"{new Uri(package.Url(source)).Host} 下载 IAS 运行库超时。", error));
			}
			catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException)
			{
				failures.Add(new IOException($"{new Uri(package.Url(source)).Host} {error.Message}", error));
			}
			finally { File.Delete(nupkg); }
		}
		token.ThrowIfCancellationRequested();
		var details = new AggregateException(failures);
		var messageWithDetails = $"下载 IAS 运行库失败（{package.Id} {package.Version}），请重试。\n" +
			string.Join("\n", failures.Select(error => error.Message));
		if (failures.Any(error => error.InnerException is InvalidDataException))
			throw new InvalidDataException(messageWithDetails, details);
		throw new HttpRequestException(messageWithDetails, details);
	}

	private static async Task<bool> MatchesAsync(string path, string sha256, CancellationToken token)
	{
		await using var input = File.OpenRead(path);
		return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) == sha256;
	}
}
