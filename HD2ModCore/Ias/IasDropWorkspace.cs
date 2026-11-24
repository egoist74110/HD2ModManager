using System.IO.Compression;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

public sealed record IasInputContents(
	string OriginalPath, IReadOnlyList<string> Main, IReadOnlyList<string> Companions,
	IReadOnlyList<string> Vpks, bool HasNamedMain);

// 解压与识别共用一个短暂存目录。调用方在添加或取消后统一清理。
public sealed class IasDropWorkspace : IDisposable
{
	private const string Prefix = "h2i-";
	private const int MaxArchiveDepth = 3;
	private readonly SevenZipArchiveExtractor _extractor = new();
	private readonly string _root;
	private int _nextArchive;

	public IasDropWorkspace()
	{
		IasProcessWorker.CheckTempPathLength(Path.Combine(Path.GetTempPath(), Prefix));
		_root = Directory.CreateTempSubdirectory(Prefix).FullName;
		try { IasProcessWorker.CheckTempPathLength(_root); }
		catch
		{
			Dispose();
			throw;
		}
	}

	internal string CreateStandardStage(string main, string companion)
	{
		var stage = Path.Combine(_root, $"s{++_nextArchive:x}");
		var source = Path.Combine(stage, "in");
		IasFileTree.Copy(main, Path.Combine(source, "main"));
		IasFileTree.Copy(companion, Path.Combine(source, "companion"));
		return stage;
	}

	public async Task<IasInputContents> InspectAsync(string input, CancellationToken token)
	{
		var main = new List<string>();
		var companions = new List<string>();
		var vpks = new List<string>();
		var namedMain = false;
		await ScanAsync(Path.GetFullPath(input), 0).ConfigureAwait(false);
		return new(input, main, companions, vpks, namedMain);

		async Task ScanAsync(string path, int depth)
		{
			token.ThrowIfCancellationRequested();
			IasPaths.RejectLink(path);
			if (Directory.Exists(path))
			{
				await ScanDirectoryAsync(path, depth, path).ConfigureAwait(false);
				return;
			}
			if (!File.Exists(path)) throw new FileNotFoundException("找不到 IAS 导入文件。", path);
			if (IasPackageDetector.IsArchive(path, out var isZip))
			{
				if (depth == MaxArchiveDepth) throw new InvalidDataException("IAS 包嵌套层数过多。");
				var destination = Path.Combine(_root, (++_nextArchive).ToString("x"));
				if (isZip) ValidateZip(path, token);
				else
				{
					var entries = await _extractor.ListAsync(path, token).ConfigureAwait(false);
					var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (var entry in entries) ValidateEntryName(entry, seen);
				}
				await _extractor.ExtractAsync(path, destination, token).ConfigureAwait(false);
				RejectExtractedLinks(destination, token);
				await ScanDirectoryAsync(destination, depth + 1, path).ConfigureAwait(false);
			}
			else if (IasPackageDetector.IsVpk(path))
				vpks.Add(path);
		}

		async Task ScanDirectoryAsync(string directory, int depth, string originalName)
		{
			token.ThrowIfCancellationRequested();
			var files = Directory.EnumerateFiles(directory, "*", IasPackageDetector.SafeEnumeration).ToArray();
			var markers = files.Where(IasPackageDetector.IsMarker).ToArray();
			if (markers.Length == 0)
			{
				if (files.Any(IasPackageDetector.IsPatchPayload))
				{
					main.Add(directory);
					namedMain |= IasPackageDetector.IsNamedMain(originalName);
				}
				foreach (var file in files.Where(file => !IasPackageDetector.IsPatch(file)))
					await ScanAsync(file, depth).ConfigureAwait(false);
				return;
			}

			var directlyInside = markers.Any(file => string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase));
			if (directlyInside)
			{
				if (files.Any(IasPackageDetector.IsPatchPayload))
					throw new InvalidDataException("请将 IAS 主包与配套包放在独立目录或 ZIP 内。");
				companions.Add(directory);
				return;
			}

			// 有配套目录时按同级目录拆分，避免把配套资产当作主包载荷。
			foreach (var child in Directory.EnumerateFileSystemEntries(directory))
			{
				token.ThrowIfCancellationRequested();
				if (IasPackageDetector.IsPatchPayload(child) && File.Exists(child))
					throw new InvalidDataException("请将 IAS 主包与配套包放在独立目录或 ZIP 内。");
				await ScanAsync(child, depth).ConfigureAwait(false);
			}
		}
	}

	private static void ValidateZip(string path, CancellationToken token)
	{
		// 保留 IAS 原有 ZIP 的路径、链接和大小校验，实际解压统一交给 7-Zip。
		using var archive = ZipFile.OpenRead(path);
		var remaining = 16L * 1024 * 1024 * 1024;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in archive.Entries)
		{
			token.ThrowIfCancellationRequested();
			ValidateEntryName(entry.FullName, seen);
			if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
				(entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
				throw new InvalidDataException($"IAS ZIP 中存在链接：{entry.FullName}");
			remaining -= entry.Length;
			if (remaining < 0) throw new InvalidDataException("IAS ZIP 解压大小超出限制。");
		}
	}

	private static void ValidateEntryName(string entry, HashSet<string> seen)
	{
		var relative = entry.Replace('\\', '/').TrimEnd('/');
		if (!IasPaths.IsRelativePath(relative) || !seen.Add(relative))
			throw new InvalidDataException($"IAS 压缩包中存在不安全或重复的条目：{entry}");
	}

	private static void RejectExtractedLinks(string path, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		IasPaths.RejectLink(path);
		if (Directory.Exists(path))
			foreach (var child in Directory.EnumerateFileSystemEntries(path))
				RejectExtractedLinks(child, token);
	}

	public void Dispose()
	{
		try { DeleteWithoutFollowingLinks(_root); }
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			System.Diagnostics.Trace.TraceWarning($"无法清理 IAS 压缩包暂存：{_root}，{error.Message}");
		}
	}

	private static void DeleteWithoutFollowingLinks(string path)
	{
		var attributes = File.GetAttributes(path);
		var link = attributes.HasFlag(FileAttributes.ReparsePoint);
		if (attributes.HasFlag(FileAttributes.Directory))
		{
			if (!link)
				foreach (var child in Directory.EnumerateFileSystemEntries(path))
					DeleteWithoutFollowingLinks(child);
			Directory.Delete(path);
		}
		else
		{
			if (!link) File.SetAttributes(path, FileAttributes.Normal);
			File.Delete(path);
		}
	}
}
