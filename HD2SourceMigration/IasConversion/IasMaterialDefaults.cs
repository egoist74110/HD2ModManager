using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction;

namespace HD2SourceMigration.IasConversion;

internal sealed class IasMaterialDefaults
{
	private const int ParentOffset = 24;
	private const int SlotKeysOffset = 136;
	private static readonly StingrayMaterialReferenceReader References = new();
	private readonly Dictionary<ulong, (Dictionary<uint, ulong> Template, Dictionary<uint, ulong> Current)> defaults = [];

	internal static ulong ReadParent(ReadOnlySpan<byte> material) =>
		material.Length >= ParentOffset + sizeof(ulong) ? BinaryPrimitives.ReadUInt64LittleEndian(material[ParentOffset..]) : 0;

	internal void Add(ulong parent, byte[] template, byte[] current) =>
		defaults.Add(parent, (ReadSlots(template), ReadSlots(current)));

	internal byte[] Migrate(byte[] material)
	{
		if (!defaults.TryGetValue(ReadParent(material), out var slots))
			return material;

		var actual = ReadSlots(material);
		byte[]? copy = null;
		var valueOffset = SlotKeysOffset + actual.Count * sizeof(uint);
		for (var index = 0; index < actual.Count; index++)
		{
			var key = ReadKey(material, index);
			// Official Generator visual_assets.py: these slots always belong to the author.
			if (key is 0x4c567810 or 0x90b84a53 or 0xe67ac0c7)
				continue;
			if (slots.Template.TryGetValue(key, out var previous) && actual[key] == previous &&
				slots.Current.TryGetValue(key, out var current) && current != previous)
			{
				copy ??= (byte[])material.Clone();
				BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(valueOffset + index * sizeof(ulong)), current);
			}
		}
		return copy ?? material;
	}

	internal static Dictionary<uint, ulong> ReadSlots(byte[] material)
	{
		var textures = References.ReadTextureIds(material);
		var slots = new Dictionary<uint, ulong>(textures.Count);
		for (var index = 0; index < textures.Count; index++)
			if (!slots.TryAdd(ReadKey(material, index), textures[index]))
				throw new InvalidDataException("IAS 材质包含重复贴图槽。");
		return slots;
	}

	private static uint ReadKey(ReadOnlySpan<byte> material, int index) =>
		BinaryPrimitives.ReadUInt32LittleEndian(material[(SlotKeysOffset + index * sizeof(uint))..]);
}
