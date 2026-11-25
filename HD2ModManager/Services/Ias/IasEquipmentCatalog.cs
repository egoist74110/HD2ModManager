using System.IO;
using HD2GameItemAssets;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;
using HD2ModCore.Ias;
using HD2SourceMigration.IasPackaging;

namespace HD2ModManager.Services.Ias;

public sealed record IasTargetChoice(string Name, string Group, IReadOnlyList<RuntimeArmorCatalogEntry> Members)
{
    public string? IconPath { get; init; }
    public RuntimeArmorCatalogEntry? Primary => Members.FirstOrDefault();
    public string SearchText => string.Join(" ", Members.Select(member =>
        $"{member.DisplayName} {member.ArchiveId} {member.ItemId} {member.ItemId:x8}"));
}

public sealed record IasVpkTargetCatalog(IReadOnlyList<IasTargetChoice> Armor, IReadOnlyList<IasTargetChoice> Helmet);
public sealed record IasStandardTargetDomain(string? OriginalName, uint? OriginalId, IReadOnlyList<IasTargetChoice> Choices);
public sealed record IasStandardTargetCatalog(IasStandardTargetDomain? Armor, IasStandardTargetDomain? Helmet);

public sealed record IasVpkTargets(string? Body, string? Helmet)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Error { get; init; }

    public static IasVpkTargets Resolve(IasVpkTargets? selection, IasPackagingTargets targets)
    {
        if (selection?.Error is { } error) throw new InvalidDataException(error);
        return new(ResolveArchive(selection?.Body, targets.BodyArchives, "护甲"),
            ResolveArchive(selection?.Helmet, targets.HelmetArchives, "头盔"));
    }

    private static string ResolveArchive(string? selected, IReadOnlyList<string> available, string label)
    {
        if (selected is null && available.Count > 0) return available[0];
        if (selected is not null && available.Contains(selected, StringComparer.OrdinalIgnoreCase)) return selected;
        throw new InvalidDataException(selected is null ? $"IAS 绑定目录与当前游戏没有可选的底子{label}。"
            : $"底子{label} {selected} 在当前 IAS 绑定目录或游戏中已失效，请在 IAS 页重选后再拖入。");
    }
}

internal sealed class IasEquipmentCatalog(
    IReadOnlyList<RuntimeArmorCatalogEntry> entries, ArmorTwinIndex twins, IReadOnlyList<GameDataAuthorityKit> kits)
{
    public static async Task<IasEquipmentCatalog> ReadAsync(StoragePaths storage, CancellationToken ct)
    {
        var entries = await CoreServices.CreateRuntimeArmorRegistryService(storage).ReadArmorCatalogAsync(ct).ConfigureAwait(false);
        if (entries.Count == 0)
            throw new InvalidDataException("护甲目录尚未建立，请先在管理器更新游戏数据索引。");
        var authority = await ArmorAuthorityIndexReader.TryReadAsync(storage, ct).ConfigureAwait(false);
        return new(entries, authority is null ? ArmorTwinIndex.Empty : ArmorTwinBundling.BuildTwinIndex(authority.Kits),
            authority?.Kits ?? []);
    }

    public string? NameOf(uint id) => entries.FirstOrDefault(entry => entry.ItemId == id)?.DisplayName;

    public IReadOnlyList<IasTargetChoice> ByArchive(bool helmet, IReadOnlyList<string> archives)
    {
        var allowed = archives.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Build(helmet, entry => allowed.Contains(entry.ArchiveId));
    }

    public IReadOnlyList<IasTargetChoice> ByItemId(bool helmet, IReadOnlyList<uint> ids)
    {
        var allowed = ids.ToHashSet();
        return Build(helmet, entry => allowed.Contains(entry.ItemId));
    }

    private IReadOnlyList<IasTargetChoice> Build(bool helmet, Func<RuntimeArmorCatalogEntry, bool> allowed)
    {
        var kind = helmet ? GameDataEquipmentKitKind.Helmet : GameDataEquipmentKitKind.Armor;
        var filtered = entries.Where(entry => entry.KitKind == kind && allowed(entry))
            .DistinctBy(entry => entry.ItemId).ToDictionary(entry => entry.ItemId);
        var seenArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(filtered.Values.ToArray(), twins);
        return (helmet ? baseCatalog.Helmets : baseCatalog.Armors)
            .Select(row => (row.Name, Members: row.MemberItemIds.Where(filtered.ContainsKey).Select(id => filtered[id])
                .Where(entry => seenArchives.Add(entry.ArchiveId)).ToArray()))
            .Where(row => row.Members.Length != 0)
            .Select(row => new IasTargetChoice(row.Name, GroupOf(row.Members[0]), row.Members))
            .OrderBy(row => row.Group switch { "轻甲" => 0, "中甲" => 1, "重甲" => 2, _ => 3 })
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private string GroupOf(RuntimeArmorCatalogEntry entry)
    {
        var kit = kits.FirstOrDefault(kit => kit.ItemId == entry.ItemId);
        var slot = entry.KitKind == GameDataEquipmentKitKind.Helmet ? CustomizationKitSlot.Helmet : CustomizationKitSlot.Torso;
        var weight = kit?.Pieces.Where(piece => piece.Slot == slot).GroupBy(piece => piece.Weight)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).FirstOrDefault()?.Key;
        return weight switch { 0 => "轻甲", 1 => "中甲", 2 => "重甲", _ => "未分类" };
    }
}

internal sealed record IasVpkAllocation(IasVpkTargets Targets, IReadOnlyList<string> Warnings)
{
    internal static async Task<IReadOnlyList<string>> ConvertAsync(IasVpkTargets preferred, IasVpkTargetCatalog catalog,
        IReadOnlyList<IasArchiveOwner> occupancy, Dictionary<string, string> reserved, string role,
        Func<IasVpkTargets, Task<IReadOnlyList<string>>> convert)
    {
        var allocation = Select(preferred, catalog, occupancy, reserved, role);
        var warnings = await convert(allocation.Targets).ConfigureAwait(false);
        reserved[allocation.Targets.Body!] = role;
        reserved[allocation.Targets.Helmet!] = role;
        return [.. allocation.Warnings, .. warnings];
    }

    internal static IasVpkAllocation Select(IasVpkTargets preferred, IasVpkTargetCatalog catalog,
        IReadOnlyList<IasArchiveOwner> occupancy, IReadOnlyDictionary<string, string> reserved, string role)
    {
        var body = catalog.Armor.SelectMany(choice => choice.Members).ToArray();
        var helmet = catalog.Helmet.SelectMany(choice => choice.Members).ToArray();
        var owners = occupancy.Where(owner => owner.Enabled)
            .SelectMany(owner => owner.Archives.Select(archive => (Archive: archive, owner.Name)))
            .Concat(reserved.Select(pair => (Archive: pair.Key, Name: pair.Value)))
            .ToLookup(pair => pair.Archive, pair => pair.Name, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var bodyId = SelectSlot(body, preferred.Body!);
        var helmetId = SelectSlot(helmet, preferred.Helmet!);
        return new(new(bodyId, helmetId), warnings);

        string SelectSlot(RuntimeArmorCatalogEntry[] choices, string preferredId)
        {
            var first = choices.FirstOrDefault(entry => entry.ArchiveId.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"底子装备 {preferredId} 在当前游戏护甲目录中已失效，请在 IAS 页重选后再拖入。");
            if (!owners.Contains(first.ArchiveId)) return first.ArchiveId;
            var available = choices.FirstOrDefault(entry => !owners.Contains(entry.ArchiveId));
            if (available is null)
            {
                var blockers = string.Join("、", body.Concat(helmet).SelectMany(entry => owners[entry.ArchiveId])
                    .Distinct().Select(name => $"『{name}』"));
                throw new InvalidDataException($"可用底子都被已启用角色占用，请先停用其中一个。占用者：{blockers}。");
            }
            var occupiedBy = string.Join("、", owners[first.ArchiveId].Distinct().Select(name => $"『{name}』"));
            warnings.Add($"{first.DisplayName}已被{occupiedBy}占用，『{role}』改装到 {available.DisplayName} 上。");
            return available.ArchiveId;
        }
    }
}
