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

	internal bool ContainsParent(ulong parent) => defaults.ContainsKey(parent);

	internal byte[] Migrate(byte[] material)
	{
		var parent = ReadParent(material);
		var hasParent = defaults.TryGetValue(parent, out var slots);
		if (!hasParent && parent != 0)
			return material;

		var actual = ReadSlots(material);
		byte[]? copy = null;
		var valueOffset = SlotKeysOffset + actual.Count * sizeof(uint);
		for (var index = 0; index < actual.Count; index++)
		{
			var key = ReadKey(material, index);
			// Official Generator visual_assets.py: these slots always belong to the author.
			if (IsAuthoredSlot(key))
				continue;
			var previous = actual[key];
			var current = 0UL;
			var resolved = hasParent
				? slots.Template.TryGetValue(key, out var expected) && previous == expected &&
					slots.Current.TryGetValue(key, out current) && current != previous
				: TryResolveFallback(key, previous, out current);
			if (resolved)
			{
				copy ??= (byte[])material.Clone();
				BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(valueOffset + index * sizeof(ulong)), current);
			}
		}
		return copy ?? material;
	}

	internal byte[] MigrateMissing(byte[] material, Func<ulong, bool> isMissingTexture,
		IReadOnlyDictionary<uint, ulong>? currentGameParentSlots = null)
	{
		var actual = ReadSlots(material);
		byte[]? copy = null;
		var valueOffset = SlotKeysOffset + actual.Count * sizeof(uint);
		for (var index = 0; index < actual.Count; index++)
		{
			var key = ReadKey(material, index);
			if (IsAuthoredSlot(key))
				continue;
			var previous = actual[key];
			if (previous == 0 || !isMissingTexture(previous))
				continue;
			var current = 0UL;
			var resolved = (currentGameParentSlots is not null && currentGameParentSlots.TryGetValue(key, out current) &&
					current != 0 && current != previous)
				|| TryResolveFallback(key, previous, out current)
				|| TryResolveCurrentSlotDefault(key, out current);
			if (resolved && current != 0 && current != previous)
			{
				copy ??= (byte[])material.Clone();
				BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(valueOffset + index * sizeof(ulong)), current);
			}
		}
		return copy ?? material;
	}

	private static bool IsAuthoredSlot(uint key) => key is 0x4c567810 or 0x90b84a53 or 0xe67ac0c7;

	private bool TryResolveCurrentSlotDefault(uint key, out ulong current)
	{
		current = 0;
		var matched = false;
		foreach (var slots in defaults.Values)
		{
			if (!slots.Current.TryGetValue(key, out var candidate) || candidate == 0)
				continue;
			if (matched && candidate != current)
			{
				current = 0;
				return false;
			}
			current = candidate;
			matched = true;
		}
		return matched;
	}

	private bool TryResolveFallback(uint key, ulong previous, out ulong current)
	{
		current = 0;
		var matched = false;
		foreach (var slots in defaults.Values)
		{
			if (!slots.Template.TryGetValue(key, out var expected) || expected != previous ||
				!slots.Current.TryGetValue(key, out var candidate) || candidate == previous)
				continue;
			if (matched && candidate != current)
			{
				current = 0;
				return false;
			}
			current = candidate;
			matched = true;
		}
		return matched;
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
