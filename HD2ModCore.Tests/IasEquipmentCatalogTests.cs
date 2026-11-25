#if WINDOWS
using System.IO;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;
using HD2ModCore.Ias;
using Xunit.Abstractions;
using HD2ModManager.Services.Ias;
using HD2SourceMigration.IasPackaging;

namespace HD2ModCore.Tests;

public sealed class IasEquipmentCatalogTests(ITestOutputHelper output)
{
    private const string Dp8 = "59d238094a164c88";
    private static readonly IasVpkTargets Preferred = new("body-1", Dp8);
    private static readonly IasVpkTargetCatalog Bases = new(
        [Choice("body-1", "首选护甲", false), Choice("body-2", "第二套护甲", false)],
        [Choice(Dp8, "DP-8 头盔", true), Choice("helmet-2", "第二顶头盔", true)]);

    [Fact]
    public async Task VpkChangesOnlyTheOccupiedHelmetAndReportsBothNames()
    {
        var reserve = new Dictionary<string, string>();
        IasArchiveOwner[] owners = [new("role", "2606520120", true, [Dp8])];
        var warnings = await IasVpkAllocation.ConvertAsync(Preferred, Bases, owners, reserve, "Airi", targets =>
        {
            Assert.Equal(new IasVpkTargets("body-1", "helmet-2"), targets);
            return Task.FromResult<IReadOnlyList<string>>([]);
        });
        Assert.Equal("DP-8 头盔已被『2606520120』占用，『Airi』改装到 第二顶头盔 上。", Assert.Single(warnings));
    }

    [Fact]
    public async Task BatchReservationsPreventDuplicateTargetsAndExhaustionStopsBeforePacking()
    {
        var reserve = new Dictionary<string, string>();
        var used = new List<IasVpkTargets>();
        IasArchiveOwner[] owners = [new("disabled", "已停用", false, [Dp8, "body-1"])];
        await IasVpkAllocation.ConvertAsync(Preferred, Bases, owners, reserve, "角色一", Convert);
        await IasVpkAllocation.ConvertAsync(Preferred, Bases, owners, reserve, "角色二", Convert);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IasVpkAllocation.ConvertAsync(Preferred, Bases, owners, reserve, "第三个角色", Convert));

        Assert.Equal([Preferred, new("body-2", "helmet-2")], used);
        Assert.Contains("可用底子都被已启用角色占用，请先停用其中一个。占用者：『角色一』、『角色二』", error.Message);
        Assert.DoesNotContain("已停用", error.Message);

        Task<IReadOnlyList<string>> Convert(IasVpkTargets targets)
        {
            used.Add(targets);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private static IasTargetChoice Choice(string archive, string name, bool helmet) =>
        new(name, "轻甲", [new(1, archive, name,
            helmet ? GameDataEquipmentKitKind.Helmet : GameDataEquipmentKitKind.Armor)]);

    [Fact]
    public void SelectionRoundTripsAndMissingArchiveDoesNotSilentlyFallBack()
    {
        var available = new IasPackagingTargets(["first", "selected"], ["helmet"]);
        Assert.Equal(new("first", "helmet"), IasVpkTargets.Resolve(null, available));
        var choice = new IasVpkTargets("selected", "helmet");
        var restored = JsonSerializer.Deserialize<IasVpkTargets>(JsonSerializer.Serialize(choice));
        Assert.Equal(choice, IasVpkTargets.Resolve(restored, available));
        var error = Assert.Throws<InvalidDataException>(() => IasVpkTargets.Resolve(restored, available with { BodyArchives = ["first"] }));
        Assert.Contains("已失效", error.Message);
        Assert.Contains("selected", error.Message);
    }

    [Fact]
    public async Task RealVpkChoicesAreNamedAndPartOfTheOfficialIntersection()
    {
        var game = Environment.GetEnvironmentVariable("HD2_IAS_GAME_DATA");
        var sdk = Environment.GetEnvironmentVariable("HD2_IAS_W28_AQSDK");
        var root = Environment.GetEnvironmentVariable("HD2_IAS_W28_INDEX_ROOT");
        if (game is null || sdk is null || root is null) return;
        var targets = await IasPackagingService.GetVpkTargetsAsync(sdk, game);
        var paths = new StoragePaths(root);
        var indexed = await CoreServices.CreateRuntimeArmorRegistryService(paths).ReadArmorCatalogAsync();
        var catalog = await IasEquipmentCatalog.ReadAsync(paths, default);
        Check(catalog.ByArchive(false, targets.BodyArchives), targets.BodyArchives);
        Check(catalog.ByArchive(true, targets.HelmetArchives), targets.HelmetArchives);

        void Check(IReadOnlyList<IasTargetChoice> choices, IReadOnlyList<string> valid)
        {
            Assert.NotEmpty(choices);
            Assert.Equal(choices.Count, choices.Select(choice => choice.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var members = choices.SelectMany(choice => choice.Members).ToArray();
            Assert.All(members, member =>
            {
                Assert.Contains(member, indexed);
                Assert.Contains(member.ArchiveId, valid, StringComparer.OrdinalIgnoreCase);
                Assert.False(string.IsNullOrWhiteSpace(member.DisplayName));
                Assert.False(member.DisplayName.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
            });
            output.WriteLine($"IAS choices={choices.Count}, displayed group members={members.Length}, official in game={valid.Count}, first={choices[0].Name}");
        }
    }

    [Fact]
    public void UsesMappingPageGroupsAndFiltersByIasAuthority()
    {
        RuntimeArmorCatalogEntry[] entries =
        [
            new(1, "0000000000000001", "DP-01 六号"),
            new(2, "0000000000000002", "DP-01 六号"),
            new(3, "0000000000000003", "0x00000003"),
            new(4, "0000000000000004", ""),
            new(5, "0000000000000005", "DP-05 五号"),
            new(6, "0000000000000006", "DP-01 六号", GameDataEquipmentKitKind.Helmet),
        ];
        var mapping = ArmorMappingPickerPlanner.BuildBaseCatalog(entries, ArmorTwinIndex.Empty);
        var ias = new IasEquipmentCatalog(entries, ArmorTwinIndex.Empty, []);
        var armor = ias.ByItemId(false, [1, 2, 3, 4]);
        var row = Assert.Single(armor);
        Assert.Equal(mapping.Armors.Single(item => item.PrimaryItemId == 1).Name, row.Name);
        Assert.Equal([1u, 2u], row.Members.Select(member => member.ItemId));
        Assert.Contains("00000002", row.SearchText);
        Assert.Equal(2u, Assert.Single(ias.ByArchive(false, ["0000000000000002", "0000000000000006"])).Primary!.ItemId);
        Assert.Equal(6u, Assert.Single(ias.ByArchive(true, ["0000000000000002", "0000000000000006"])).Primary!.ItemId);
    }
}
#endif
