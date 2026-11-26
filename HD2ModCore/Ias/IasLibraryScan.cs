using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Ias;

public sealed record IasLibraryRole(Guid ModId, string Name, string Directory, DateTimeOffset AddedAt, bool Legacy,
	string? CompanionPath, string[] Archives, string Revision);

public sealed record IasScannedRole(IasRole Role, bool? Enabled, bool MissingFromLibrary, string[] Archives, string Revision);

public sealed record IasRoleScan(IReadOnlyList<IasScannedRole> Registered, IReadOnlyList<IasLibraryRole> Library,
	IReadOnlyList<IasArchiveOwner> Occupants);

internal static class IasLibraryScan
{
	internal const string RoleLabel = "IAS 角色";
	internal const string CompanionFile = "IAS_Companion.hd2irpack.zip";
	internal sealed record Companion(string? Identity, string[] Archives);

	internal static bool IsMarked(ModNode node) =>
		node.Metadata.Notes?.StartsWith($"[{RoleLabel}]", StringComparison.Ordinal) == true;

	internal static ModNodeId[] PackagedOptions(LibrarySnapshot snapshot, ModNode root)
	{
		if (string.IsNullOrWhiteSpace(root.Metadata.SourcePackageGuid)) return [];
		var graph = OptionRelationshipGraph.Create(snapshot);
		var options = graph.GetDescendantOptions(root.Id).Where(option =>
			string.Equals(option.Metadata.SourcePackageGuid, root.Metadata.SourcePackageGuid, StringComparison.OrdinalIgnoreCase))
			.Select(option => option.Id).ToHashSet();
		// 多宿主选项继续归库管理，避免登记或覆盖 IAS 主包时改动其他 Mod。
		ModNodeId[] shared;
		do
		{
			shared = options.Where(id => graph.GetParents(id).Any(parent => parent != root.Id && !options.Contains(parent))).ToArray();
			options.ExceptWith(shared);
		} while (shared.Length > 0);
		return options.OrderBy(id => id.Value).ToArray();
	}

	internal static IasLibraryRole? ReadCandidate(StoragePaths paths, ModNode node, CancellationToken token)
	{
		if (node.Metadata.Kind != ModNodeKind.Standard) return null;
		var directory = IasFileTree.Under(paths.ModsDirectory, node.RelativePath);
		if (!Directory.Exists(directory)) return null;
		var marked = IsMarked(node);
		var files = ReadFiles(directory, token);
		if (!marked && !node.Metadata.IsLogicalContainer && node.PatchGroups.Count == 0 && !files.Any(IasPackageDetector.IsPatchPayload))
			return null;
		var companions = files.Where(IsCompanionFile).Select(path =>
		{
			if (IasPackageDetector.IsMarker(path))
			{
				var parent = Path.GetDirectoryName(path)!;
				return (Path: parent, Data: ReadCompanionDirectory(parent));
			}
			return (Path: path, Data: ReadCompanionZip(path));
		}).Where(item => item.Data is not null).ToArray();
		var companion = companions.Length == 1 ? companions[0] : default;
		if (!marked && companion.Data is null) return null;
		return new(node.Id.Value, node.Metadata.Name, directory, node.Metadata.CreatedUtc, marked, companion.Path,
			companion.Data?.Archives ?? [], Revision(directory, files, token));
	}

	internal static string[] ReadFiles(string directory, CancellationToken token)
	{
		if (!Directory.Exists(directory)) return [];
		IasPaths.RejectLink(directory);
		var files = new List<string>();
		foreach (var path in Directory.EnumerateFiles(directory, "*", IasPackageDetector.SafeEnumeration))
		{
			token.ThrowIfCancellationRequested();
			files.Add(path);
		}
		return files.ToArray();
	}

	internal static string Revision(string directory, IEnumerable<string> files, CancellationToken token)
	{
		using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Span<byte> header = stackalloc byte[256];
		foreach (var file in files.Where(IsRevisionFile).Order(StringComparer.OrdinalIgnoreCase))
		{
			token.ThrowIfCancellationRequested();
			using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var item = $"{Path.GetRelativePath(directory, file)}\0{stream.Length}\0{File.GetLastWriteTimeUtc(file).Ticks}\0";
			fingerprint.AppendData(System.Text.Encoding.UTF8.GetBytes(item));
			fingerprint.AppendData(header[..stream.ReadAtLeast(header, header.Length, false)]);
		}
		return Convert.ToHexString(fingerprint.GetHashAndReset());
	}

	private static bool IsRevisionFile(string file) => IasPackageDetector.IsPatch(file) || IsCompanionFile(file) ||
		Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
		Path.GetExtension(file) is ".rigbin" or ".hd2phys" or ".hd2shared";

	private static bool IsCompanionFile(string path) => IasPackageDetector.IsMarker(path) ||
		path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

	internal static bool HasMainIdentity(string manifest, Guid id)
	{
		if (!File.Exists(manifest)) return false;
		using var stream = File.OpenRead(manifest);
		if (stream.Length > 4 * 1024 * 1024) return false;
		try
		{
			using var document = JsonDocument.Parse(stream);
			return document.RootElement.ValueKind == JsonValueKind.Object &&
				Guid.TryParse(Text(document.RootElement, "Guid"), out var parsed) && parsed == id;
		}
		catch (JsonException) { return false; }
	}

	internal static Companion? ReadCompanionDirectory(string directory)
	{
		var marker = Path.Combine(directory, "mod_group.json");
		if (!File.Exists(marker)) return null;
		IasPaths.RejectLink(marker);
		using var stream = File.OpenRead(marker);
		return ReadCompanion(stream, stream.Length);
	}

	private static Companion? ReadCompanionZip(string path)
	{
		try
		{
			using var zip = ZipFile.OpenRead(path);
			var marker = zip.Entries.SingleOrDefault(entry => IasPackageDetector.IsMarker(entry.FullName));
			if (marker is null) return null;
			using var stream = marker.Open();
			return ReadCompanion(stream, marker.Length);
		}
		catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return null; }
	}

	private static Companion? ReadCompanion(Stream stream, long length)
	{
		if (length > 4 * 1024 * 1024) return null;
		try
		{
			using var document = JsonDocument.Parse(stream);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return null;
			var identity = Text(root, "id");
			var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (root.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
				foreach (var member in members.EnumerateArray()) AddArchive(member);
			AddArchive(root);
			return new(string.IsNullOrWhiteSpace(identity) ? null : identity, archives.Order(StringComparer.OrdinalIgnoreCase).ToArray());

			void AddArchive(JsonElement entry)
			{
				if (entry.ValueKind == JsonValueKind.Object && Text(entry, "archive") is { Length: 16 } archive &&
					archive.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
					archives.Add(archive);
			}
		}
		catch (JsonException) { return null; }
	}

	private static string? Text(JsonElement element, string key) =>
		element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
