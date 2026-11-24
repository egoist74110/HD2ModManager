using HD2GameItemAssets;
using HD2ModCore.Ias;

namespace HD2ModCore.Tests;

public sealed class IasSlotAuthorityTests
{
	[Fact]
	public void 默认按归档与装备原地替换并允许明确换装()
	{
		var original = Kit(7, 0xab, false, Piece(11, 3), Piece(12, 0, layer: 1));
		var target = Kit(10, 0xcd, false, Piece(21, 0), Piece(22, 1));
		var helmet = Kit(10, 0xef, true, Piece(31, 3, slot: 0));
		var authority = new IasSlotAuthority(new([original, target, helmet]));

		var automatic = authority.Resolve(false, 0xab, 7, [11, 12], null);
		Assert.Same(original, automatic.Target);
		Assert.Equal(11UL, Assert.Single(IasSlotAuthority.MatchSlots(automatic.Source, automatic.Target)(11, false)).Unit);

		var changed = authority.Resolve(false, 0xab, 7, [11, 12], "0x0000000a");
		var mapping = IasSlotAuthority.MatchSlots(changed.Source, changed.Target);
		Assert.Equal([21UL, 22UL], mapping(11, false).Select(slot => slot.Unit).Order());
		Assert.Empty(mapping(12, true));
		var error = Assert.Throws<InvalidDataException>(() => mapping(12, false));
		Assert.Contains("缺少", error.Message);

		Assert.Equal([7u, 10u], authority.GetCompatibleTargets(false, 0xab, 7,
			new Dictionary<ulong, bool> { [11] = false, [12] = true }));
		Assert.Equal([7u], authority.GetCompatibleTargets(false, 0xab, 7,
			new Dictionary<ulong, bool> { [11] = false, [12] = false }));
		var chosen = authority.Resolve(false, 0, 0, [11, 12], "0x0000000a");
		Assert.Same(original, chosen.Source);
		Assert.Same(target, chosen.Target);
	}

	[Fact]
	public void MissingOriginalRequiresExplicitSelectionAndExplainsBothEntryPoints()
	{
		var authority = new IasSlotAuthority(new([Kit(8, 0xfe, true, Piece(11, 3, slot: 0))]));
		Assert.Null(authority.FindOriginal(true, 0, 0, [11]));
		var error = Assert.Throws<InvalidDataException>(() => authority.Resolve(true, 0, 0, [11], null));
		Assert.Contains("--target-helmet", error.Message);
		Assert.Contains("IAS 页", error.Message);
		Assert.Equal(8u, authority.Resolve(true, 0, 0, [11], "0x00000008").Target.Id);
	}

	private static GameItemPiece Piece(ulong id, uint body, uint layer = 0, uint slot = 3)
		=> new(StingrayAssetId.Unit(id), slot, layer, 0, body, 0, []);

	private static GameItemAssetRecord Kit(uint id, ulong archive, bool helmet, params GameItemPiece[] pieces)
		=> new(id, 0, 0, 0, 0, 0, $"装备 {id}", "", 0, 0, archive,
			helmet ? CustomizationKitType.Helmet : CustomizationKitType.Armor, pieces);
}
