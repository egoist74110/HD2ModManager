using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services.Ias;

/// <summary>从磁盘上识别一个库内 Mod 的来源。</summary>
/// <param name="Source">Standard 表示既没有来源 VPK 也不是素体，是否可转由资产内容决定。</param>
/// <param name="SourceVpks">Mod 根上的来源 VPK，只有 L4d2Vpk 才非空。</param>
/// <param name="HasConversionReport">根上有 vpk2patch.json：说明这个 Mod 是 VPK 转出来的。</param>
/// <param name="HasRootPatches">Mod 根上直接放着来源补丁（原始 Mod 的 patch）；独立素体只有转换产物目录，没有。</param>
internal sealed record IasLibraryModFacts(IasLibrarySource Source, IReadOnlyList<string> SourceVpks,
    bool HasConversionReport, int MaterialPackCount, uint? WardrobeTargetItemId, bool HasRootPatches = false);

/// <summary>
/// 「从库里选 Mod 转 IAS」的识别规则：全部按现有存储判定与结构文件读，不看 Mod 名字。
/// </summary>
internal static partial class IasLibraryCandidateReader
{
    public const string AlreadyRoleReason = "已经是 IAS 角色";
    public const string ManagedModReason = "IAS 管理 Mod";
    public const string CustomModelReason = "素体没有对应的原版装备，暂不能转";
    public const string MissingVpkReason = "原 VPK 已不在，无法转";

    public static async Task<IasLibraryModFacts> InspectAsync(string modDirectory, CancellationToken ct)
    {
        // 来源 VPK 在根上 = 存储形态 Vpk2Patch：与重构建、详情页用的是同一个判定。
        var vpks = VpkRebuildService.FindSourceVpkPaths(modDirectory);
        var source = vpks.Count != 0 ? IasLibrarySource.L4d2Vpk
            : VpkCustomModelFile.TryReadModel(modDirectory) is not null ? IasLibrarySource.CustomModel
            : IasLibrarySource.Standard;
        var report = File.Exists(VpkConversionReport.GetPath(modDirectory));
        var materialPacks = 0;
        uint? target = null;
        if (source != IasLibrarySource.Standard)
        {
            var manifest = await SectionalPatchManifestFile.TryReadAsync(
                Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName), ct).ConfigureAwait(false);
            materialPacks = manifest?.Patches.Count(patch => patch.Role == SectionalPatchRole.Material &&
                !string.Equals(patch.OptionName, SectionalPatchManifest.BuiltInMaterialOptionName, StringComparison.Ordinal)) ?? 0;
            target = ReadWardrobeTargetItemId(modDirectory);
        }
        return new(source, vpks, report, materialPacks, target, HasRootPatches(modDirectory));
    }

    /// <summary>
    /// 素体 Mod 只有转换产物（自定义 unit）时没有对应的原版装备，实测 IAS 打包报「无法匹配到受支持的 IAS 装备归档」，不可转；
    /// 根上留着原始补丁的（例如「替换 TG-8」转出来的素体）可以用那些原始补丁转，实测打包成功。
    /// </summary>
    public static string? UnavailableReason(IasLibraryModFacts facts) => facts.Source switch
    {
        IasLibrarySource.CustomModel when !facts.HasRootPatches => facts.HasConversionReport ? MissingVpkReason : CustomModelReason,
        _ => null,
    };

    /// <summary>根上有没有来源补丁：与存储形态判定用同一个文件名解析器。</summary>
    private static bool HasRootPatches(string modDirectory)
    {
        try
        {
            var parser = CoreServices.CreatePatchFileNameParser();
            return Directory.EnumerateFiles(modDirectory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>衣柜里记录的目标护甲条目；没有（或目标是素体）时为 null。</summary>
    private static uint? ReadWardrobeTargetItemId(string modDirectory)
    {
        try
        {
            var path = Path.Combine(modDirectory, "wardrobe.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("targetItemId", out var id) &&
                id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out var value) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 库目录名里记录的编号：纯数字目录（创意工坊 ID），或以 _AR 加数字结尾（站点编号）。
    /// 只认这两种固定形态的目录名，不拆显示名。
    /// </summary>
    public static string? NumberOf(string relativePath)
    {
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(relativePath ?? string.Empty));
        if (string.IsNullOrEmpty(leaf)) return null;
        if (leaf.All(char.IsAsciiDigit)) return leaf;
        var match = SiteNumber().Match(leaf);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"_(AR\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteNumber();
}
