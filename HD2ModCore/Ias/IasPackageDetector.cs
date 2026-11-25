using System.IO.Compression;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

public enum IasInputKind { IasPackage, PatchMod, L4d2Vpk, Unsupported, Archive }

public static class IasPackageDetector
{
	public const string MissingMainHint = "这是 IAS 配套包（hd2irpack），需要和同一个 Mod 的主包一起拖进来。";
	public const string MissingCompanionHint = "缺少 IAS 配套包（.hd2irpack.zip），请和主包一起拖进来。";

	private static readonly SevenZipArchiveExtractor Extractor = new();
	private static readonly Application.IPatchFileNameParser PatchParser = CoreServices.CreatePatchFileNameParser();

	// 拖动期间只检查入口，目录和压缩包留到放下后识别。
	public static bool MayAccept(string path)
	{
		try { return Directory.Exists(path) || File.Exists(path) && (IsVpk(path) || IsArchive(path, out _)); }
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	// 只读外层目录。遇到无法从目录判断的嵌套包，返回 Archive。
	public static IasInputKind Classify(string path)
	{
		try
		{
			if (Directory.Exists(path)) return ClassifyNames(Directory.EnumerateFiles(path, "*", SafeEnumeration));
			if (!File.Exists(path)) return IasInputKind.Unsupported;
			if (IsArchive(path, out _))
				return ClassifyNames(Extractor.ListAsync(path).GetAwaiter().GetResult());
			if (IsVpk(path)) return IasInputKind.L4d2Vpk;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
		return IasInputKind.Unsupported;
	}

	// 普通导入先读目录区，常见的普通 Patch/VPK 压缩包无需为 IAS 再解压一次。
	internal static async Task<bool> MayContainCompanionAsync(string path, CancellationToken token)
	{
		if (Directory.Exists(path))
			return Directory.EnumerateFiles(path, "*", SafeEnumeration).Any(IsCandidate);
		if (!File.Exists(path) || IsVpk(path) || !IsArchive(path, out var isZip)) return false;
		if (isZip)
		{
			using var zip = ZipFile.OpenRead(path);
			return zip.Entries.Any(entry => IsCandidate(entry.FullName));
		}
		var entries = await Extractor.ListAsync(path, token).ConfigureAwait(false);
		return entries.Any(IsCandidate);

		bool IsCandidate(string entry)
		{
			token.ThrowIfCancellationRequested();
			return IsMarker(entry) || Path.GetExtension(entry).ToLowerInvariant() is ".zip" or ".rar" or ".7z";
		}
	}

	internal static EnumerationOptions SafeEnumeration => new()
	{
		RecurseSubdirectories = true,
		AttributesToSkip = FileAttributes.ReparsePoint,
		IgnoreInaccessible = false
	};

	internal static bool IsMarker(string path) =>
		Path.GetFileName(path).Equals("mod_group.json", StringComparison.OrdinalIgnoreCase);

	public static bool IsVpk(string path) => path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase);

	public static bool IsNamedMain(string path)
	{
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
		return name.Equals("main", StringComparison.OrdinalIgnoreCase) ||
			Path.GetFileNameWithoutExtension(name).EndsWith("IAS_MainMod", StringComparison.OrdinalIgnoreCase);
	}

	// main 是通用目录名，只能在已有配套包时作为配对线索。
	internal static bool HasExplicitMainName(string path)
	{
		var stem = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));
		return stem.Equals("IAS_MainMod", StringComparison.OrdinalIgnoreCase) ||
			stem.EndsWith("_IAS_MainMod", StringComparison.OrdinalIgnoreCase);
	}

	internal static bool IsArchive(string path, out bool isZip)
	{
		using var stream = File.OpenRead(path);
		Span<byte> header = stackalloc byte[8];
		var length = stream.ReadAtLeast(header, header.Length, false);
		var bytes = header[..length];
		isZip = bytes.StartsWith("PK\u0003\u0004"u8) || bytes.StartsWith("PK\u0005\u0006"u8) ||
			bytes.StartsWith("PK\u0007\u0008"u8);
		ReadOnlySpan<byte> sevenZip = [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c];
		return isZip || bytes.StartsWith("Rar!\u001a\u0007"u8) || bytes.StartsWith(sevenZip);
	}

	public static bool IsPatch(string path) => PatchParser.TryParse(Path.GetFileName(path), out _);

	internal static bool IsPatchPayload(string path) => IsPatch(path) &&
		!path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
		!path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase);

	private static IasInputKind ClassifyNames(IEnumerable<string> entries)
	{
		var kind = IasInputKind.Unsupported;
		foreach (var entry in entries)
		{
			if (IsMarker(entry) || entry.EndsWith(".hd2irpack.zip", StringComparison.OrdinalIgnoreCase) ||
				Path.GetFileName(entry).Contains("_IAS_Companion.", StringComparison.OrdinalIgnoreCase))
				return IasInputKind.IasPackage;
			if (IsPatchPayload(entry)) kind = IasInputKind.PatchMod;
			else if (kind != IasInputKind.PatchMod && IsVpk(entry)) kind = IasInputKind.L4d2Vpk;
			else if (kind == IasInputKind.Unsupported &&
				Path.GetExtension(entry).ToLowerInvariant() is ".zip" or ".rar" or ".7z") kind = IasInputKind.Archive;
		}
		return kind;
	}
}
