using System.Globalization;
using FileDecryptor;
using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Ias;

public sealed class IasSlotAuthority
{
	private readonly GameItemAssetCatalog catalog;

	internal IasSlotAuthority(GameItemAssetCatalog catalog) => this.catalog = catalog;

	public static async Task<IasSlotAuthority> ReadAsync(string gameData, CancellationToken token = default)
	{
		var path = Path.Combine(gameData, "game", "generated_customization_armor_sets.dl_bin");
		try
		{
			var encrypted = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
#pragma warning disable CS0618
			var decoded = Decryptor.CreateWithEmbeddedKeys().DecodeData(encrypted);
#pragma warning restore CS0618
			var names = await ReadNamesAsync(gameData, token).ConfigureAwait(false);
			return new(CustomizationArmorSetsReader.Read(decoded, names));
		}
		catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			throw new InvalidDataException($"无法读取游戏护甲注册数据 {path}：{error.Message}", error);
		}
	}

	public GameItemAssetRecord? FindOriginal(bool helmet, ulong archive, uint equipment, IReadOnlyCollection<ulong> units) =>
		FindOriginal(GetKits(helmet), archive, equipment, units);

	public IReadOnlyList<uint> GetCompatibleTargets(bool helmet, ulong archive, uint equipment,
		IReadOnlyDictionary<ulong, bool> hiddenByUnit)
	{
		var kits = GetKits(helmet);
		var units = hiddenByUnit.Keys.ToArray();
		var original = FindOriginal(kits, archive, equipment, units);
		var sources = original is null ? kits.Where(item => ContainsUnits(item, units)).ToArray() : [original];
		return kits.Where(target =>
		{
			var source = sources.FirstOrDefault(item => item.Id == target.Id) ?? (sources.Length == 1 ? sources[0] : null);
			if (source is null) return false;
			var match = MatchSlots(source, target);
			try
			{
				var mapped = new HashSet<ulong>();
				return hiddenByUnit.All(pair => match(pair.Key, pair.Value).All(slot => mapped.Add(slot.Unit)));
			}
			catch (InvalidDataException) { return false; }
		}).Select(item => item.Id).ToArray();
	}

	public (GameItemAssetRecord Source, GameItemAssetRecord Target) Resolve(bool helmet, ulong archive,
		uint equipment, IReadOnlyCollection<ulong> units, string? requested)
	{
		var label = helmet ? "头盔" : "护甲";
		var kits = GetKits(helmet);
		var original = FindOriginal(kits, archive, equipment, units);
		GameItemAssetRecord target;
		if (requested is not null)
		{
			var matches = kits.Where(item => MatchesSelector(item, requested)).Take(2).ToArray();
			target = matches.Length == 1 ? matches[0]
				: throw new InvalidDataException($"目标{label}“{requested}”不存在或对应多件装备，请填写唯一的归档 ID 或装备 ID。");
		}
		else
		{
			target = original ?? throw new InvalidDataException(
				$"无法确定 IAS 原套{label}（归档 {archive:x16}，装备 {equipment:x8}）。请前往 IAS 页添加角色，在角色行选择后转换，或在 CLI 指定 --target-{(helmet ? "helmet" : "armor")}。");
		}

		var candidates = original is null ? kits.Where(item => ContainsUnits(item, units)).ToArray() : [original];
		var source = candidates.FirstOrDefault(item => item.Id == target.Id) ?? (candidates.Length == 1 ? candidates[0] : null);
		if (source is null)
			throw new InvalidDataException($"无法从源{label}的归档、装备和 Unit 对应表确认原版格子（{archive:x16}/{equipment:x8}）。");
		return (source, target);
	}

	private GameItemAssetRecord[] GetKits(bool helmet) =>
		catalog.Items.Where(item => item.KitType == (helmet ? CustomizationKitType.Helmet : CustomizationKitType.Armor) &&
			item.ArchiveHash != 0).ToArray();

	private static GameItemAssetRecord? FindOriginal(GameItemAssetRecord[] kits, ulong archive, uint equipment,
		IReadOnlyCollection<ulong> units)
	{
		if (archive == 0 && equipment == 0) return null;
		var declared = kits.Where(item => (archive == 0 || item.ArchiveHash == archive) && (equipment == 0 || item.Id == equipment))
			.Take(2).ToArray();
		return declared.Length == 1 && ContainsUnits(declared[0], units) ? declared[0] : null;
	}

	private static bool ContainsUnits(GameItemAssetRecord kit, IReadOnlyCollection<ulong> units)
	{
		var known = kit.Pieces.Select(piece => piece.UnitAsset.NameHash).ToHashSet();
		return units.All(known.Contains);
	}

	private static bool MatchesSelector(GameItemAssetRecord item, string value)
	{
		var raw = value.Trim();
		var explicitHex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
		if (explicitHex) raw = raw[2..];
		if (ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
		{
			if (item.ArchiveHash == hex) return true;
			if ((explicitHex || raw.Length <= 8) && item.Id == hex) return true;
		}
		return !explicitHex && uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && item.Id == id;
	}

	public static Func<ulong, bool, IReadOnlyList<(ulong Unit, string Cell, string Body, int WriteOrder, bool IsTorso)>> MatchSlots(
		GameItemAssetRecord source, GameItemAssetRecord target)
	{
		var targetTable = BuildTable(target);
		var sourcePieces = source.Pieces.ToLookup(piece => piece.UnitAsset.NameHash);
		return (unit, hidden) =>
		{
			var matches = new Dictionary<ulong, (ulong Unit, string Cell, string Body, int WriteOrder, bool IsTorso)>();
			foreach (var piece in sourcePieces[unit])
			{
				var cell = Cell(piece);
				var body = ArmorStructure.ToBodyVariant(piece.BodyType);
				if (source.Id == target.Id)
				{
					matches.TryAdd(unit, (unit, cell.Display, body.ToString(), WriteOrder(cell, body, hidden), cell.Part == ArmorPart.Torso));
					continue;
				}
				var slots = body == UnitMeshBodyVariant.Any
					? targetTable.SlotsOf(cell)
					: new[] { targetTable.FindExact(cell, body) ?? targetTable.FindAny(cell) }.OfType<ArmorCellSlot>().ToArray();
				if (slots.Count == 0 && !hidden)
					throw new InvalidDataException($"目标装备 {target.Name} 缺少 {cell.Display}（{body}），无法放置 IAS Unit {unit:x16}。");
				if (targetTable.Duplicates.Any(slot => slot.Cell == cell && slots.Any(match => match.Body == slot.Body)))
					throw new InvalidDataException($"目标装备 {target.Name} 的 {cell.Display} 存在重复的体型格子。");
				foreach (var slot in slots)
					matches.TryAdd(slot.Part.UnitAssetKey.FileId, (slot.Part.UnitAssetKey.FileId, cell.Display,
						slot.Body.ToString(), WriteOrder(cell, slot.Body, hidden), cell.Part == ArmorPart.Torso));
			}
			if (!sourcePieces.Contains(unit))
				throw new InvalidDataException($"源装备 {source.Name} 缺少游戏 Unit {unit:x16} 的格子记录。");
			return matches.Values.ToArray();
		};
	}

	// 保持已发布产物的可见件、隐藏件与物理链顺序。排序只用格子和体型。
	private static int WriteOrder(ArmorCell cell, UnitMeshBodyVariant body, bool hidden)
	{
		var rank = (hidden, cell.Part) switch
		{
			(false, ArmorPart.Hips) => 0,
			(false, ArmorPart.LeftArm) => 1,
			(false, ArmorPart.RightArm) => 2,
			(false, ArmorPart.Torso) => 3,
			(false, ArmorPart.LeftLeg) => 4,
			(false, ArmorPart.RightLeg) => 5,
			(true, ArmorPart.LeftLeg) => 0,
			(true, ArmorPart.RightLeg) => 1,
			(true, ArmorPart.LeftArm) => 2,
			(true, ArmorPart.RightArm) => 3,
			(true, ArmorPart.LeftShoulder) => 4,
			(true, ArmorPart.RightShoulder) => 5,
			(true, ArmorPart.Torso) => 6,
			_ => cell.Slot + 16,
		};
		return (hidden ? 1024 : 0) + rank * 16 + cell.Layer * 4 + (body == UnitMeshBodyVariant.Slim ? 1 : 0);
	}

	private static ArmorCellTable BuildTable(GameItemAssetRecord kit) => ArmorCellTable.Build(kit.Pieces.Select(piece =>
		new EquipmentUnitPart(new AssetKey(piece.UnitAsset.TypeHash, piece.UnitAsset.NameHash), 0, 0,
			UnitMeshPartKind.Unknown, UnitMeshPartLayer.Unknown, ArmorStructure.ToBodyVariant(piece.BodyType), "", 0, [])
		{
			Cell = Cell(piece),
		}));

	private static ArmorCell Cell(GameItemPiece piece) => ArmorCell.FromRegistryCoordinates((int)piece.Slot, (int)piece.PieceType);

	private static async Task<IReadOnlyDictionary<uint, string>> ReadNamesAsync(string gameData, CancellationToken token)
	{
		// 游戏的 strings 基础归档。IAS 主包的材质补丁从包内索引发现，与这个归档名无关。
		const string stringsArchive = "9ba626afa44a3aa3";
		var resolver = new GameDataPackageResolver(gameData);
		var toc = await resolver.GetPackageTocAsync(stringsArchive, token).ConfigureAwait(false);
		var names = new Dictionary<uint, string>();
		if (toc is null)
			return names;
		foreach (var entry in new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntries(toc.Data, stringsArchive, toc.UsesSlimEntryOffset)
			.Where(entry => entry.AssetKey.TypeId == GameDataStringsReader.AssetTypeId))
		{
			if (entry.TocDataSize < 16)
				continue;
			var header = await resolver.GetPackageResourceAsync(stringsArchive, entry.TocDataOffset, 16, token).ConfigureAwait(false);
			if (header is null || BitConverter.ToUInt32(header) != GameDataStringsReader.Magic ||
				GameDataLanguages.Resolve(BitConverter.ToUInt32(header, 12)).Locale != "en-US")
				continue;
			var bytes = await resolver.GetPackageResourceAsync(stringsArchive, entry.TocDataOffset, entry.TocDataSize, token).ConfigureAwait(false);
			if (bytes is not null)
				foreach (var (id, name) in GameDataStringsReader.Read(bytes, entry.AssetKey.FileId).Entries)
					names.TryAdd(id, name);
		}
		return names;
	}
}
