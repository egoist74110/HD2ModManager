using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction;
using HD2SourceMigration.IasConversion;

namespace HD2SourceMigration.Tests;

public sealed class IasMaterialDefaultsTests
{
	private const ulong Parent = 123;
	private const uint Slot = 10;
	private static readonly StingrayMaterialReferenceReader References = new();

	[Fact]
	public void ReplacesUnchangedTemplateSlotsByKey()
	{
		var defaults = new IasMaterialDefaults();
		defaults.Add(Parent, Material(0, (Slot, 100), (11, 200), (12, 300), (13, 400)),
			Material(0, (13, 0), (12, 300), (Slot, 101), (14, 501)));
		var source = Material(Parent, (14, 500), (13, 400), (12, 300), (11, 200), (Slot, 100));

		var output = defaults.Migrate(source);

		Assert.NotSame(source, output);
		Assert.Equal(Parent, IasMaterialDefaults.ReadParent(output));
		Assert.Equal(new ulong[] { 500, 0, 300, 200, 101 }, References.ReadTextureIds(output));
		Assert.Equal(new ulong[] { 500, 400, 300, 200, 100 }, References.ReadTextureIds(source));
	}

	[Theory]
	[InlineData(Slot, 999UL, Parent)]
	[InlineData(0x4c567810u, 100UL, Parent)]
	[InlineData(0x90b84a53u, 100UL, Parent)]
	[InlineData(0xe67ac0c7u, 100UL, Parent)]
	[InlineData(Slot, 100UL, 456UL)]
	public void KeepsAuthorOverridesProtectedSlotsAndUnknownParents(uint slot, ulong texture, ulong parent)
	{
		var defaults = new IasMaterialDefaults();
		defaults.Add(Parent, Material(0, (slot, 100)), Material(0, (slot, 101)));
		var source = Material(parent, (slot, texture));

		Assert.Same(source, defaults.Migrate(source));
		Assert.Equal(texture, Assert.Single(References.ReadTextureIds(source)));
	}

	[Fact]
	public void MigratesUnparentedMaterialWhenTemplateSlotMatchesUnambiguously()
	{
		var defaults = new IasMaterialDefaults();
		defaults.Add(Parent, Material(0, (Slot, 100), (0xe67ac0c7u, 200)), Material(0, (Slot, 101), (0xe67ac0c7u, 201)));
		var source = Material(0, (0xe67ac0c7u, 200), (Slot, 100), (99, 777));

		var output = defaults.Migrate(source);

		Assert.NotSame(source, output);
		Assert.Equal(0UL, IasMaterialDefaults.ReadParent(output));
		Assert.Equal(new ulong[] { 200, 101, 777 }, References.ReadTextureIds(output));
	}

	[Fact]
	public void SkipsUnparentedMaterialSlotWhenTemplatesDisagree()
	{
		var defaults = new IasMaterialDefaults();
		defaults.Add(Parent, Material(0, (Slot, 100)), Material(0, (Slot, 101)));
		defaults.Add(Parent + 1, Material(0, (Slot, 100)), Material(0, (Slot, 102)));
		var source = Material(0, (Slot, 100));

		Assert.Same(source, defaults.Migrate(source));
		Assert.Equal(100UL, Assert.Single(References.ReadTextureIds(source)));
	}

	[Fact]
	public void MigratesMissingNonAuthoredSlotsFromCurrentParentOrTemplate()
	{
		var defaults = new IasMaterialDefaults();
		defaults.Add(Parent, Material(0, (Slot, 100)), Material(0, (Slot, 101)));
		var source = Material(456, (0xe67ac0c7u, 900), (20, 800), (Slot, 100), (30, 700));
		var currentParentSlots = new Dictionary<uint, ulong>
		{
			[0xe67ac0c7u] = 901,
			[20] = 801,
			[30] = 701,
		};
		var missing = new HashSet<ulong> { 900, 800, 100 };

		var output = defaults.MigrateMissing(source, missing.Contains, currentParentSlots);

		Assert.NotSame(source, output);
		Assert.Equal(new ulong[] { 900, 801, 101, 700 }, References.ReadTextureIds(output));
	}

	[Fact]
	public void MigratesMissingKnownSlotWithOlderUnlistedTextureId()
	{
		var defaults = new IasMaterialDefaults();
		defaults.Add(Parent, Material(0, (Slot, 100)), Material(0, (Slot, 101)));
		var source = Material(0, (Slot, 555), (0xe67ac0c7u, 900));
		var missing = new HashSet<ulong> { 555, 900 };

		var output = defaults.MigrateMissing(source, missing.Contains);

		Assert.NotSame(source, output);
		Assert.Equal(new ulong[] { 101, 900 }, References.ReadTextureIds(output));
	}

	private static byte[] Material(ulong parent, params (uint Slot, ulong Texture)[] slots)
	{
		const int keysOffset = 136;
		var valuesOffset = keysOffset + slots.Length * sizeof(uint);
		var bytes = new byte[valuesOffset + slots.Length * sizeof(ulong)];
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), parent);
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), slots.Length);
		for (var index = 0; index < slots.Length; index++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(keysOffset + index * sizeof(uint)), slots[index].Slot);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(valuesOffset + index * sizeof(ulong)), slots[index].Texture);
		}
		return bytes;
	}
}
