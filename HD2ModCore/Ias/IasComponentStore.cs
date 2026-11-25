using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

public sealed record IasInstalled(string Version, string ComponentsDirectory);
public sealed record IasActivationResult(IasInstalled Installed, string? CleanupWarning = null);

public sealed class IasComponentStore(StoragePaths paths, HttpClient httpClient, string? gameRootDirectory = null)
{
	private readonly string _root = IasRoot(paths);
	private static string IasRoot(StoragePaths paths) => Path.Combine(Path.GetFullPath(paths.AppRootDirectory), "ias");
	private string CacheDirectory => Path.Combine(_root, "cache");
	private string ComponentsDirectory => ComponentsAt(_root);
	private string CurrentPath => CurrentAt(ComponentsDirectory);
	private static string ComponentsAt(string root) => Path.Combine(root, "components");
	private static string CurrentAt(string components) => Path.Combine(components, "current.json");
	private string StageDirectory(string version) => Path.Combine(ComponentsDirectory, ".staged-" + version);

	private IasProgress? _progress;
	private readonly IasProgressThrottle<(double? Percent, string Message)> _reporting = new();
	public IasProgress? CurrentProgress => Volatile.Read(ref _progress);

	public async Task<string> DownloadAsync(IasRelease release, IProgress<IasProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		using var lease = OpenWriteLease(cancellationToken);
		try
		{
			EnsureDirectory(CacheDirectory);
			var cachePath = Path.Combine(CacheDirectory, release.ZipSha256.ToLowerInvariant() + ".zip");
			var partialPath = cachePath + ".part";
			IasPaths.RejectLink(cachePath);
			IasPaths.RejectLink(partialPath);
			if (File.Exists(cachePath))
			{
				Report(progress, IasStage.Verify, 0, release.ZipBytes, "正在校验 IAS 缓存整包。");
				if (await VerifyPackageAsync(cachePath, release, cancellationToken).ConfigureAwait(false))
				{
					Report(progress, IasStage.Verify, release.ZipBytes, release.ZipBytes, "IAS 整包校验通过。");
					return cachePath;
				}
				File.Delete(cachePath);
			}

			var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
			if (offset > release.ZipBytes)
			{
				File.Delete(partialPath);
				offset = 0;
			}
			if (offset < release.ZipBytes)
			{
				try
				{
					await DownloadPartialAsync(release, partialPath, offset, progress, cancellationToken).ConfigureAwait(false);
				}
				catch (InvalidDataException)
				{
					File.Delete(partialPath);
					throw;
				}
			}

			Report(progress, IasStage.Verify, 0, release.ZipBytes, "正在校验 IAS 整包。");
			if (!await VerifyPackageAsync(partialPath, release, cancellationToken).ConfigureAwait(false))
			{
				File.Delete(partialPath);
				throw new InvalidDataException("IAS 整包大小或 SHA-256 与签名清单不符，已丢弃。");
			}
			cancellationToken.ThrowIfCancellationRequested();
			Report(progress, IasStage.Verify, release.ZipBytes, release.ZipBytes, "IAS 整包校验通过。");
			File.Move(partialPath, cachePath);
			return cachePath;
		}
		finally
		{
			Volatile.Write(ref _progress, null);
		}
	}

	private async Task DownloadPartialAsync(IasRelease release, string partialPath, long offset,
		IProgress<IasProgress>? progress, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromMinutes(30));
		var token = timeout.Token;
		Report(progress, IasStage.Download, offset, release.ZipBytes, "正在下载 IAS 官方整包。");
		using var request = new HttpRequestMessage(HttpMethod.Get, release.ZipUrl);
		if (offset > 0)
			request.Headers.Range = new RangeHeaderValue(offset, null);

		try
		{
			using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
				.ConfigureAwait(false);
			if (response.RequestMessage?.RequestUri is { } finalUri && !IasFeedClient.IsOfficialUrl(finalUri))
				throw new HttpRequestException("IAS 整包响应来自非官方网址。");
			if (response.StatusCode == HttpStatusCode.OK)
				offset = 0; // 服务端忽略 Range，从头覆写。
			else if (response.StatusCode != HttpStatusCode.PartialContent)
				throw new HttpRequestException($"IAS 整包下载返回 HTTP {(int)response.StatusCode}。", null, response.StatusCode);
			else if (offset == 0 || response.Content.Headers.ContentRange is not { } range ||
				!string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
				range.From != offset || range.To != release.ZipBytes - 1 || range.Length != release.ZipBytes)
				throw new HttpRequestException("IAS 续传响应的 Content-Range 与请求不符。");

			if (response.Content.Headers.ContentLength is long length && length != release.ZipBytes - offset)
				throw new InvalidDataException("IAS 整包响应大小与签名清单不符。");
			using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			using var output = new FileStream(partialPath, offset == 0 ? FileMode.Create : FileMode.Append,
				FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
			var written = await CopyBoundedAsync(input, output, release.ZipBytes - offset, null,
				written => Report(progress, IasStage.Download, offset + written, release.ZipBytes, "正在下载 IAS 官方整包。"),
				token).ConfigureAwait(false);
			if (written != release.ZipBytes - offset)
				throw new EndOfStreamException("IAS 整包下载提前中断，已下载的数据可用于续传。");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException("下载 IAS 官方整包超时，已下载的数据可用于续传。");
		}
	}

	public Task<string> ExtractCoreAsync(IasRelease release, string packagePath,
		IProgress<IasProgress>? progress = null, CancellationToken cancellationToken = default) =>
		ExtractAsync(release, packagePath, StageDirectory(release.Version), staging: true, progress, cancellationToken);

	public Task<string> ExportCoreAsync(IasRelease release, string packagePath, string outputDirectory,
		IProgress<IasProgress>? progress = null, CancellationToken cancellationToken = default) =>
		ExtractAsync(release, packagePath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)),
			staging: false, progress, cancellationToken);

	// 调用方持有 IAS 部署租约，并已备份当前部件目录。
	internal async Task RepairGraphicsAsync(IasRelease release, string package, string work,
		IProgress<IasProgress>? progress, CancellationToken token)
	{
		var verified = await ExportCoreAsync(release, package, work, progress, token).ConfigureAwait(false);
		using var lease = OpenWriteLease(token);
		var installed = GetInstalled();
		if (installed?.Version != release.Version)
			throw new InvalidOperationException("修复期间 IAS 版本已变化，请重试。");
		foreach (var archive in IasPaths.GraphicsArchives)
		{
			token.ThrowIfCancellationRequested();
			var source = Path.Combine(verified, archive);
			if (File.Exists(source))
				File.Move(source, IasFileTree.Under(installed.ComponentsDirectory, archive), overwrite: true);
		}
	}

	private async Task<string> ExtractAsync(IasRelease release, string packagePath, string target, bool staging,
		IProgress<IasProgress>? progress, CancellationToken cancellationToken)
	{
		using var lease = OpenWriteLease(cancellationToken);
		var parent = Path.GetDirectoryName(target) ?? throw new IOException("IAS 输出目录不能是卷根目录。");
		EnsureDirectory(parent);
		var pending = Path.Combine(parent, $".ias-extract-{Guid.NewGuid():N}");
		try
		{
			if (staging)
				DeleteDirectory(target);
			else if (Path.Exists(target))
				throw new IOException($"IAS 输出目录已存在：{target}");
			Report(progress, IasStage.Verify, 0, release.ZipBytes, "正在校验待解压的 IAS 整包。");
			using var package = OpenRead(packagePath);
			if (!await VerifyPackageAsync(package, release, cancellationToken).ConfigureAwait(false))
				throw new InvalidDataException("待解压的 IAS 整包大小或 SHA-256 与签名清单不符。");
			package.Position = 0;
			Report(progress, IasStage.Verify, release.ZipBytes, release.ZipBytes, "IAS 整包校验通过。");
			using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
			var entries = SelectCoreEntries(archive, release);
			var total = entries.Sum(item => item.File.Bytes);
			var completed = 0L;
			Directory.CreateDirectory(pending);
			foreach (var (entry, file) in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var destination = Path.Combine(pending, file.Path.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				using var input = entry.Open();
				using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
					81920, FileOptions.Asynchronous);
				using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				var message = $"正在解压并校验 {file.Path}。";
				Report(progress, IasStage.Extract, completed, total, message);
				var written = await CopyBoundedAsync(input, output, file.Bytes, hash,
					bytes => Report(progress, IasStage.Extract, completed + bytes, total, message), cancellationToken)
					.ConfigureAwait(false);
				if (written != file.Bytes ||
					!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException($"IAS 部件大小或 SHA-256 与签名清单不符：{file.Path}");
				completed += written;
			}
			cancellationToken.ThrowIfCancellationRequested();
			Report(progress, IasStage.Extract, completed, total, "IAS 核心部件校验通过。");
			Directory.Move(pending, target);
			return target;
		}
		finally
		{
			Volatile.Write(ref _progress, null);
			DeleteDirectory(pending);
		}
	}

	private static List<(ZipArchiveEntry Entry, IasReleaseFile File)> SelectCoreEntries(ZipArchive archive, IasRelease release)
	{
		var expected = release.Files.Where(file => IasPaths.IsRetainedFile(file.Path))
			.ToDictionary(file => file.Path, StringComparer.Ordinal);
		var missing = IasPaths.RuntimeFiles.Where(path => !expected.ContainsKey(path)).ToList();
		if (!expected.Keys.Any(path => path.StartsWith("app/", StringComparison.Ordinal))) missing.Add("app/");
		if (missing.Count != 0)
			throw new InvalidDataException($"IAS 版本 {release.Version} 的签名清单缺少 {string.Join("、", missing)}。");

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var selected = new List<(ZipArchiveEntry, IasReleaseFile)>();
		foreach (var entry in archive.Entries)
		{
			var isDirectory = entry.FullName.EndsWith('/');
			var path = isDirectory ? entry.FullName[..^1] : entry.FullName;
			if (!IasPaths.IsRelativePath(path) || !seen.Add(path) ||
				((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
				throw new InvalidDataException($"IAS 整包含危险路径、链接或重复条目：{entry.FullName}");
			if (isDirectory || !IasPaths.IsRetainedFile(path))
				continue;
			if (!expected.Remove(path, out var file) || entry.Length != file.Bytes)
				throw new InvalidDataException($"IAS 部件未列入签名清单或大小不符：{path}");
			selected.Add((entry, file));
		}
		if (expected.Count != 0)
			throw new InvalidDataException($"IAS 整包缺少清单中的部件：{string.Join("、", expected.Keys)}");
		return selected;
	}

	public async Task<IasActivationResult> ActivateAsync(string version, IProgress<IasProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (!IasPaths.IsVersion(version))
			throw new ArgumentException("IAS 版本号无效。", nameof(version));
		using var lease = OpenWriteLease(cancellationToken);
		var stage = StageDirectory(version);
		var target = Path.Combine(ComponentsDirectory, version);
		var pointer = Path.Combine(ComponentsDirectory, $".current-{Guid.NewGuid():N}.json");
		try
		{
			Report(progress, IasStage.Activate, 0, 0, "正在检查游戏版本并激活 IAS 部件。");
			var previous = GetInstalled();
			IasPaths.RejectLink(stage);
			IasPaths.RejectLink(target);
			var staged = Directory.Exists(stage);
			if (!staged && previous?.Version != version)
				throw new InvalidOperationException($"IAS {version} 没有通过校验的暂存部件。");
			var compatibility = await IasGameCompatibility.CheckAsync(gameRootDirectory, staged ? stage : target, cancellationToken)
				.ConfigureAwait(false);
			if (compatibility.Status != IasGameCompatibilityStatus.Compatible)
				throw new IasGameCompatibilityException(compatibility);
			if (!staged)
				return new(previous!);
			if (previous?.Version == version)
				throw new InvalidOperationException($"IAS {version} 已激活，需使用不同版本号更新部件。");

			// 先准备同卷新指针。目录移动成功后，替换 current.json 是唯一提交点。
			await File.WriteAllTextAsync(pointer, JsonSerializer.Serialize(new { version }), cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			// 上次进程可能在移动目录之后、提交指针之前结束。
			DeleteDirectory(target);
			Directory.Move(stage, target);
			try
			{
				File.Move(pointer, CurrentPath, overwrite: true);
			}
			catch
			{
				Directory.Move(target, stage);
				throw;
			}

			var installed = new IasInstalled(version, target);
			try
			{
				if (previous is not null)
					DeleteDirectory(previous.ComponentsDirectory);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return new(installed, $"IAS 已激活，旧部件暂未清除：{exception.Message}");
			}
			return new(installed);
		}
		finally
		{
			Volatile.Write(ref _progress, null);
			if (File.Exists(pointer))
				File.Delete(pointer);
		}
	}

	public IasInstalled? GetInstalled() => ReadInstalled(_root);

	public static IasInstalled? GetInstalled(StoragePaths paths) => ReadInstalled(IasRoot(paths));

	private static IasInstalled? ReadInstalled(string root)
	{
		var components = ComponentsAt(root);
		var current = CurrentAt(components);
		IasPaths.RejectLink(root);
		IasPaths.RejectLink(components);
		IasPaths.RejectLink(current);
		// 允许激活在状态读取期间原子替换指针。
		FileStream input;
		try
		{
			input = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		}
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
		using (input)
		using (var document = JsonDocument.Parse(input))
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!document.RootElement.TryGetProperty("version", out var property) ||
				property.ValueKind != JsonValueKind.String || property.GetString() is not { } version ||
				!IasPaths.IsVersion(version))
				throw new InvalidDataException("IAS 当前版本记录无效。");
			var directory = Path.Combine(components, version);
			IasPaths.RejectLink(directory);
			return new(version, directory);
		}
	}

	public Task RemoveAsync(CancellationToken cancellationToken = default) =>
		Task.Run(() =>
		{
			using var lease = OpenWriteLease(cancellationToken);
			IasPaths.RejectLink(ComponentsDirectory);
			IasPaths.RejectLink(CacheDirectory);
			IasPaths.RejectLink(CurrentPath);
			if (Directory.Exists(ComponentsDirectory))
				File.Delete(CurrentPath);
			DeleteDirectory(ComponentsDirectory);
			DeleteDirectory(CacheDirectory);
		}, cancellationToken);

	private FileStream OpenWriteLease(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureDirectory(_root);
		var path = Path.Combine(_root, ".components.lock");
		IasPaths.RejectLink(path);
		return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
	}

	private void Report(IProgress<IasProgress>? progress, IasStage stage, long done, long total, string message)
	{
		var percent = total > 0 ? Math.Round((double)done / total * 100) : (double?)null;
		if (!_reporting.ShouldReport((percent, message))) return;
		var value = new IasProgress(stage, done, total, message);
		Volatile.Write(ref _progress, value);
		progress?.Report(value);
	}

	private static void EnsureDirectory(string path)
	{
		IasPaths.RejectLink(path);
		Directory.CreateDirectory(path);
	}

	private static void DeleteDirectory(string path)
	{
		IasPaths.RejectLink(path);
		if (Directory.Exists(path))
			Directory.Delete(path, recursive: true);
	}

	private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
		81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

	private static async Task<bool> VerifyPackageAsync(string path, IasRelease release, CancellationToken token)
	{
		using var input = OpenRead(path);
		return await VerifyPackageAsync(input, release, token).ConfigureAwait(false);
	}

	private static async Task<bool> VerifyPackageAsync(FileStream input, IasRelease release, CancellationToken token) =>
		input.Length == release.ZipBytes &&
		Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))
			.Equals(release.ZipSha256, StringComparison.OrdinalIgnoreCase);

	internal static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, IncrementalHash? hash,
		Action<long>? report, CancellationToken token)
	{
		var buffer = new byte[81920];
		var written = 0L;
		int read;
		while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
		{
			if (read > limit - written)
				throw new InvalidDataException("IAS 数据超过签名清单中的大小。");
			await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
			hash?.AppendData(buffer, 0, read);
			written += read;
			report?.Invoke(written);
		}
		return written;
	}
}
