using System.IO.Compression;
using System.Security.Cryptography;

namespace HD2ModCore.Ias;

public static class IasFileTree
{
	internal static FileAttributes? Attributes(string path)
	{
		try { return File.GetAttributes(path); }
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
	}

	internal static void Restore(string? source, string target, List<Exception> failures)
	{
		try
		{
			IasPaths.RejectLink(target);
			if (source is not null) IasPaths.RejectLink(source);
			FileAttributes? from = source is null ? null : Attributes(source) ?? throw new IOException($"快照内容缺失：{source}");
			var to = Attributes(target);
			var fromDirectory = from?.HasFlag(FileAttributes.Directory) == true;
			var toDirectory = to?.HasFlag(FileAttributes.Directory) == true;
			var savedChildren = fromDirectory ? Directory.GetFileSystemEntries(source!) : [];
			if (toDirectory)
			{
				var savedNames = savedChildren.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
				foreach (var child in Directory.GetFileSystemEntries(target))
					if (!savedNames.Contains(Path.GetFileName(child))) Restore(null, child, failures);
				if (!fromDirectory) Directory.Delete(target);
			}
			if (fromDirectory)
			{
				if (to is not null && !toDirectory) Delete(target);
				Directory.CreateDirectory(target);
				foreach (var child in savedChildren)
					Restore(child, Path.Combine(target, Path.GetFileName(child)), failures);
			}
			else if (source is not null)
			{
				if (to is not null && !toDirectory && SameContents(source, target)) return;
				RestoreFile(source, target, toDirectory ? null : to);
			}
			else if (to is not null && !toDirectory)
			{
				Delete(target);
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			failures.Add(new IOException($"恢复 {target} 失败：{error.Message}", error));
		}
	}

	private static bool SameContents(string source, string target)
	{
		if (new FileInfo(source).Length != new FileInfo(target).Length) return false;
		using var expected = File.OpenRead(source);
		// 游戏映射的 DLL 通常仍可读取。比对成功时也无需设置文件属性。
		using var actual = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		return SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual));
	}

	private static void RestoreFile(string source, string target, FileAttributes? existing)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		var pending = Path.Combine(Path.GetDirectoryName(target)!, $".ias-restore-{Guid.NewGuid():N}.tmp");
		try
		{
			File.Copy(source, pending);
			if (existing?.HasFlag(FileAttributes.ReadOnly) == true)
				File.SetAttributes(target, existing.Value & ~FileAttributes.ReadOnly);
			File.Move(pending, target, overwrite: true);
		}
		finally
		{
			if (File.Exists(pending)) Delete(pending);
		}
	}

	internal static void Copy(string source, string target, bool preserveAttributes = false)
	{
		IasPaths.RejectLink(source);
		if (File.Exists(source))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(source, target);
			if (!preserveAttributes) File.SetAttributes(target, FileAttributes.Normal);
			return;
		}
		if (!Directory.Exists(source)) return;
		Directory.CreateDirectory(target);
		foreach (var path in Directory.EnumerateFileSystemEntries(source))
			Copy(path, Path.Combine(target, Path.GetFileName(path)), preserveAttributes);
	}

	public static void Delete(string path)
	{
		IasPaths.RejectLink(path);
		if (File.Exists(path))
		{
			File.SetAttributes(path, FileAttributes.Normal);
			File.Delete(path);
		}
		else if (Directory.Exists(path))
		{
			foreach (var child in Directory.EnumerateFileSystemEntries(path)) Delete(child);
			Directory.Delete(path);
		}
	}

	public static void Extract(string source, string target, CancellationToken token, long maximumBytes = 16L * 1024 * 1024 * 1024)
	{
		using var zip = ZipFile.OpenRead(source);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var remaining = maximumBytes;
		var buffer = new byte[128 * 1024];
		foreach (var entry in zip.Entries)
		{
			token.ThrowIfCancellationRequested();
			var path = entry.FullName.TrimEnd('/');
			if (!IasPaths.IsRelativePath(path) || !seen.Add(path) ||
				((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
				throw new InvalidDataException($"IAS ZIP 中存在不安全或重复的条目：{entry.FullName}");
			var destination = Path.Combine(target, path);
			remaining -= entry.Length;
			if (remaining < 0) throw new InvalidDataException("IAS ZIP 解压大小超出限制。");
			if (entry.Name.Length == 0)
				Directory.CreateDirectory(destination);
			else
			{
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				using var input = entry.Open();
				using var output = new FileStream(destination, FileMode.CreateNew);
				long written = 0;
				while (true)
				{
					token.ThrowIfCancellationRequested();
					var read = input.Read(buffer);
					if (read == 0) break;
					written += read;
					if (written > entry.Length) throw new InvalidDataException($"IAS ZIP 条目大小无效：{entry.FullName}");
					output.Write(buffer, 0, read);
				}
				if (written != entry.Length) throw new InvalidDataException($"IAS ZIP 条目大小无效：{entry.FullName}");
			}
		}
	}

	public static string Under(string root, string name)
	{
		if (!IasPaths.IsRelativePath(name.Replace('\\', '/')))
			throw new InvalidDataException($"IAS 路径无效：{name}");
		var path = Path.GetFullPath(Path.Combine(root, name));
		if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"IAS 路径超出指定目录：{name}");
		for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
			IasPaths.RejectLink(parent);
		return path;
	}
}

