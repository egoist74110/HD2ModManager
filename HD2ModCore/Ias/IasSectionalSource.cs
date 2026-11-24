using System.Buffers.Binary;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using PatchEntryPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using PatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using StingrayMaterialReferenceReader = HD2ModAdaptation.PatchReconstruction.StingrayMaterialReferenceReader;

namespace HD2ModCore.Ias;

internal static class IasSectionalSource
{
	internal static bool IsMarkedPackage(string directory)
	{
		var path = Path.Combine(directory, "manifest.json");
		if (!File.Exists(path)) return false;
		try
		{
			using var stream = File.OpenRead(path);
			using var manifest = JsonDocument.Parse(stream);
			return manifest.RootElement.ValueKind == JsonValueKind.Object
				&& manifest.RootElement.TryGetProperty("sectionalPatchNumbering", out var numbering)
				&& numbering.ValueKind == JsonValueKind.String
				&& numbering.GetString() == "ias-per-archive-v1";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return false;
		}
	}

	// 普通白名单只追踪 Unit → 材质 → 贴图。IAS 自带父材质也要随可达的子材质一起保留。
	internal static async ValueTask<SectionalSourceAssetKeys> ExtendMaterialClosureAsync(
		SectionalSourceAssetKeys keys, IReadOnlyList<PatchTocEntry> entries, CancellationToken cancellationToken)
	{
		var byId = entries.GroupBy(entry => entry.AssetKey.FileId)
			.ToDictionary(group => group.Key, group => group.Last());
		var materials = keys.Materials.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var textures = keys.Textures.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var pending = new Queue<ulong>();
		foreach (var raw in keys.Materials)
			if (SectionalConversionPlanner.TryParseKey(raw, out var key))
				pending.Enqueue(key.FileId);
		var visited = new HashSet<ulong>();
		var payloadReader = new PatchEntryPayloadReader();
		var references = new StingrayMaterialReferenceReader();
		while (pending.TryDequeue(out var id))
		{
			if (!visited.Add(id) || !byId.TryGetValue(id, out var entry)) continue;
			var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			foreach (var texture in references.ReadTextureIds(payload.TocData))
				if (texture != 0)
					textures.Add(SectionalConversionPlanner.FormatKey(new(MaterialDependencyResolver.TextureTypeId, texture)));
			var parent = BinaryPrimitives.ReadUInt64LittleEndian(payload.TocData.AsSpan(24));
			if (parent != 0 && byId.ContainsKey(parent))
			{
				materials.Add(SectionalConversionPlanner.FormatKey(new(MaterialDependencyResolver.MaterialTypeId, parent)));
				pending.Enqueue(parent);
			}
		}
		return keys with { Materials = materials.Order(StringComparer.Ordinal).ToArray(), Textures = textures.Order(StringComparer.Ordinal).ToArray() };
	}
}
