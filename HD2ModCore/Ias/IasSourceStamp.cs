using System.Security.Cryptography;

namespace HD2ModCore.Ias;

/// <summary>Checks that inputs still match the files read during unlocked preparation.</summary>
public sealed class IasSourceStamp
{
	private readonly string _path;
	private readonly string _description;
	private readonly SortedDictionary<string, string?> _entries;

	private IasSourceStamp(string path, string description, SortedDictionary<string, string?> entries)
	{
		_path = path;
		_description = description;
		_entries = entries;
	}

	public static async Task<IasSourceStamp> CaptureAsync(string path, string description, CancellationToken token) =>
		new(path, description, await ReadAsync(path, description, token).ConfigureAwait(false));

	public async Task VerifyAsync(CancellationToken token)
	{
		var current = await ReadAsync(_path, _description, token).ConfigureAwait(false);
		if (!_entries.SequenceEqual(current))
			throw new InvalidOperationException($"{_description}在处理期间已改动，请重新添加或转换。");
	}

	private static async Task<SortedDictionary<string, string?>> ReadAsync(string path, string description, CancellationToken token)
	{
		var entries = new SortedDictionary<string, string?>(StringComparer.Ordinal);
		try { await ReadEntryAsync(path).ConfigureAwait(false); }
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new InvalidOperationException($"{description}已删除或无法读取，请检查源文件。", error);
		}
		return entries;

		async Task ReadEntryAsync(string entry)
		{
			token.ThrowIfCancellationRequested();
			IasPaths.RejectLink(entry);
			var relative = Path.GetRelativePath(path, entry);
			if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
			{
				entries.Add(relative, null);
				foreach (var child in Directory.EnumerateFileSystemEntries(entry))
					await ReadEntryAsync(child).ConfigureAwait(false);
				return;
			}

			await using var stream = File.OpenRead(entry);
			entries.Add(relative, Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)));
		}
	}
}
