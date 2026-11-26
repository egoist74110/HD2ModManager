using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Domain;
using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services.Ias;

public static class IasStandardDropImport
{
    public static Task<string> PrepareAsync(IasStandardDropPackage package, string? gameData, StoragePaths paths, CancellationToken ct) =>
        Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
                throw new InvalidOperationException("转换 IAS 包需要游戏数据目录，请先在设置中选择有效的游戏数据目录。");

            ct.ThrowIfCancellationRequested();
            var (source, output) = package.Stage();
            var folder = await IasModuleService.ConvertStandardPackageAsync(source, gameData, output,
                IasComponentStore.GetInstalled(paths), ct).ConfigureAwait(false);
            var manifestPath = Path.Combine(folder, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false))!.AsObject();
            var name = manifest["name"]!.GetValue<string>();
            if (!name.EndsWith("（由 IAS 转换）", StringComparison.Ordinal))
            {
                manifest["name"] = name + "（由 IAS 转换）";
                await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct)
                    .ConfigureAwait(false);
            }

            var plan = await CoreServices.CreateModImportPlanner().PlanFolderAsync(folder, ct).ConfigureAwait(false);
            var errors = plan.Diagnostics.Where(diagnostic => diagnostic.IsBlocking).Select(diagnostic => diagnostic.Message).ToArray();
            if (errors.Length != 0 || !plan.Items.Any(item => item.Role is ImportPlanItemRole.Host or ImportPlanItemRole.PlaceholderHost))
                throw new InvalidDataException($"IAS 包导入预检失败：{string.Join("；", errors.DefaultIfEmpty("没有可导入的主体"))}");
            return folder;
        }, ct);
}
