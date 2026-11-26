using System.IO;
using System.Text.Json;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2SourceMigration.IasPackaging.BodyRetarget;
using Microsoft.Data.Sqlite;

namespace HD2ModManager.Services.Ias;

internal sealed class IasBodyRetargeter(StoragePaths storage, string game) : IIasBodyRetargeter
{
    public async Task<IasRetargetAnalysis> AnalyzeAsync(IasInstalled installed, string source, string work, CancellationToken ct)
    {
        var metadata = Metadata(installed);
        var stock = await ReadStockAsync(metadata, ct).ConfigureAwait(false);
        string? body = null;
        string? author = null;
        var group = Path.Combine(source, "companion", "mod_group.json");
        if (File.Exists(group))
        {
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(group, ct).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
            {
                var keys = members.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("archive", out var value) && value.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("archive").GetString()!)
                    .Where(archive => stock.ContainsKey(archive)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (keys.Length == 1) body = keys[0].ToLowerInvariant();
            }
        }
        var manifest = Path.Combine(source, "main", "manifest.json");
        if (File.Exists(manifest))
        {
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest, ct).ConfigureAwait(false));
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Author", out var value) &&
                value.ValueKind == JsonValueKind.String)
                author = value.GetString();
        }

        IasRetargetChoice[] choices;
        try
        {
            var catalog = await IasEquipmentCatalog.ReadAsync(storage, ct).ConfigureAwait(false);
            choices = catalog.ByArchive(false, [.. stock.Keys]).SelectMany(group => group.Members.Select(member =>
                new IasRetargetChoice(member.ArchiveId.ToLowerInvariant(), member.DisplayName, group.SearchText))).ToArray();
        }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException)
        {
            // 系统索引还没生成时使用已安装 IAS 的原版身体目录。
            choices = stock.Select(pair => new IasRetargetChoice(pair.Key.ToLowerInvariant(), pair.Value,
                $"{pair.Value} {pair.Key}")).ToArray();
        }
        if (body is not null && choices.All(choice => choice.Archive != body))
            choices = [.. choices, new(body, stock[body], $"{stock[body]} {body}")];
        try
        {
            var engine = await OpenAsync(installed, work, ct).ConfigureAwait(false);
            var fit = await engine.AnalyzeAsync(source, choices.Select(choice => choice.Archive).ToArray(),
                Path.Combine(work, "plan"), ct).ConfigureAwait(false);
            return new(fit.OriginalArchive ?? body, fit.Author ?? author, choices.Select(choice =>
                choice with { Reason = fit.Error ?? (fit.Fits.TryGetValue(choice.Archive, out var reason)
                    ? reason : "没有对应的身体换绑计划") }).ToArray(), fit.Error);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException)
        {
            return new(body, author, choices.Select(choice => choice with { Reason = error.Message }).ToArray(), error.Message);
        }
    }

    public async Task<IasRetargetPair> RetargetAsync(IasInstalled installed, string source, string archive, string work,
        CancellationToken ct)
    {
        var engine = await OpenAsync(installed, work, ct).ConfigureAwait(false);
        var result = await engine.RetargetAsync(source, archive, Path.Combine(work, "plan"), ct).ConfigureAwait(false);
        return new(result.MainZip, result.CompanionZip, result.Archives, result.Author, result.BodyPatch, result.BodyKeys);
    }

    private async Task<IasBodyRetargetService> OpenAsync(IasInstalled installed, string work, CancellationToken ct)
    {
        var sdk = IasBodyRetargetService.IsSupportedVersion(installed.Version)
            ? await IasAqSdk.GetAsync(storage, Path.Combine(work, "sdk"), ct).ConfigureAwait(false) : "";
        return new(installed.Version, Path.Combine(installed.ComponentsDirectory, "expanded", "Generator"),
            sdk, Metadata(installed), Path.Combine(game, "data"));
    }

    private static string Metadata(IasInstalled installed) =>
        Path.Combine(installed.ComponentsDirectory, "expanded", "ArmorAssets", "metadata");

    private static async Task<Dictionary<string, string>> ReadStockAsync(string path, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(path, "equipment.json"), ct).ConfigureAwait(false));
        return json.RootElement.GetProperty("body").EnumerateArray()
            .Select(item => (Archive: item.GetProperty("archive").GetString()!, Label: item.GetProperty("label").GetString()!))
            .OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .DistinctBy(item => item.Archive, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Archive, item => item.Label, StringComparer.OrdinalIgnoreCase);
    }
}
