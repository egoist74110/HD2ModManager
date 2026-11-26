using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

// 自动下载使用可复现的 SDK 快照，用户指定的 SDK 由打包器按接口与表结构检查。
public static class IasAqSdk
{
	private const string Revision = "86b7252";
	internal static readonly Uri ArchiveUri = new($"https://codeload.github.com/Estecsky/HD2SDK-AQ-Edition/zip/{Revision}");
	internal static readonly Uri MirrorArchiveUri = new($"https://github.com/Estecsky/HD2SDK-AQ-Edition/archive/{Revision}.zip");
	private const long MaxDownload = 128 * 1024 * 1024;

	public static async Task<string> GetAsync(StoragePaths paths, string work, CancellationToken token, HttpClient? http = null)
	{
		var configured = Environment.GetEnvironmentVariable("HD2_AQSDK_DIR");
		if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

		var root = Path.Combine(Path.GetFullPath(paths.AppRootDirectory), "ias");
		var cached = IasFileTree.Under(root, $"dependencies/aqsdk-{Revision}");
		if (Directory.Exists(cached)) return cached;
		Directory.CreateDirectory(work);
		var package = Path.Combine(work, "aqsdk.zip");
		using var fallback = http is null
			? IasUpdateHttp.Create(() => IasUpdateHttp.ReadSettings(Path.GetFullPath(paths.AppRootDirectory)))
			: null;
		var client = http ?? fallback!;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
		timeout.CancelAfter(TimeSpan.FromMinutes(3));
		try
		{
			using var response = await client.GetAsync(ArchiveUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength > MaxDownload) throw new InvalidDataException("AQ SDK 下载包过大。");
			await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
			await using (var output = File.Create(package))
			{
				var buffer = new byte[81920];
				while (true)
				{
					var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
					if (read == 0) break;
					if (output.Length + read > MaxDownload) throw new InvalidDataException("AQ SDK 下载包过大。");
					await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
				}
			}
			var extraction = Path.Combine(work, "aqsdk");
			IasFileTree.Extract(package, extraction, timeout.Token, 4 * MaxDownload);
			var folder = Directory.GetDirectories(extraction).SingleOrDefault()
				?? throw new InvalidDataException("AQ SDK 下载包目录结构无效。");
			if (!Path.GetFileName(folder).StartsWith($"HD2SDK-AQ-Edition-{Revision}", StringComparison.OrdinalIgnoreCase) ||
				!File.Exists(Path.Combine(folder, "hd2_system", "resource_binding_targets.json")))
				throw new InvalidDataException($"AQ SDK 下载包与提交 {Revision} 不符。");
			Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
			try { Directory.Move(folder, cached); }
			catch (IOException) when (Directory.Exists(cached)) { /* 另一进程已放入同一版本。 */ }
			return cached;
		}
		catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
		{
			var cause = error is OperationCanceledException ? new TimeoutException($"下载 AQ SDK 快照 {Revision} 超时。", error) : error;
			throw new IOException($"无法取得 AQ SDK 快照 {Revision}。可用 HD2_AQSDK_DIR 指定兼容的本地 HD2SDK-AQ-Edition 根目录。{error.Message}", cause);
		}
	}
}

