using FileDecryptor;
using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCli;

internal static class CliIasSlotAuthority
{
	internal static async Task<Func<ulong, bool, ulong>> ReadAsync(string gameData)
	{
		var path = Path.Combine(gameData, "game", "generated_customization_armor_sets.dl_bin");
		var encrypted = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
#pragma warning disable CS0618
		var decoded = Decryptor.CreateWithEmbeddedKeys().DecodeData(encrypted);
#pragma warning restore CS0618
		var catalog = CustomizationArmorSetsReader.Read(decoded);
		var sources = new[] { FindKit(0x5096bce9), FindKit(0x735a0c0b) };
		var targets = new[] { BuildTable(FindKit(0xa9a71fe7)), BuildTable(FindKit(0xb19d6c0a)) };
		return (unit, helmet) =>
		{
			var index = helmet ? 1 : 0;
			var source = sources[index].Pieces.Single(piece => piece.UnitAsset.NameHash == unit);
			var cell = Cell(source);
			var body = ArmorStructure.ToBodyVariant(source.BodyType);
			var match = targets[index].FindExact(cell, body)
				?? throw new InvalidDataException($"No DP-8 cell matching SR-64 asset {unit:x16} ({cell.Display}, {body}).");
			return match.Part.UnitAssetKey.FileId;
		};

		GameItemAssetRecord FindKit(uint item) => catalog.FindById(item)
			?? throw new InvalidDataException($"Missing original game customization kit {item:x8}.");
	}

	private static ArmorCellTable BuildTable(GameItemAssetRecord kit) => ArmorCellTable.Build(kit.Pieces.Select(piece =>
		new EquipmentUnitPart(new AssetKey(piece.UnitAsset.TypeHash, piece.UnitAsset.NameHash), 0, 0,
			UnitMeshPartKind.Unknown, UnitMeshPartLayer.Unknown, ArmorStructure.ToBodyVariant(piece.BodyType), "", 0, [])
		{
			Cell = Cell(piece),
		}));

	private static ArmorCell Cell(GameItemPiece piece) => ArmorCell.FromRegistryCoordinates((int)piece.Slot, (int)piece.PieceType);
}
