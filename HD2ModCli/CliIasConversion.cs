using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services.Ias;

namespace HD2ModCli;

internal static class CliIasConversion
{
	internal static async Task<int> ConvertAsync(string sample, string? gameData, string? output,
		string? targetArmor, string? targetHelmet, string? managerRoot, bool mergeHelmet)
	{
		if (string.IsNullOrWhiteSpace(gameData) || string.IsNullOrWhiteSpace(output))
			throw new CliUserException("MissingIasPath", "ias convert <sample> 需要 --game-data <目录> 和 --output <目录>。");
		HD2SourceMigration.IasConversion.IasStandardResult result;
		try
		{
			var root = managerRoot ?? Hd2ModCliLocator.ResolveManagerRoot();
			var installed = IasComponentStore.GetInstalled(new StoragePaths(root));
			if (installed is null)
				throw new CliUserException("MissingIasComponents", "找不到已安装的官方 IAS 组件，无法读取材质默认模板。请先在管理器安装 IAS，并用 --manager-root <管理器目录> 指向该安装。");
			result = await IasModuleService.ConvertStandardAsync(sample, gameData, output, installed, CancellationToken.None,
				targetArmor, targetHelmet, mergeHelmet).ConfigureAwait(false);
		}
		catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException
			or InvalidOperationException or ArgumentException)
		{
			throw new CliUserException("IasConversionFailed", $"IAS 普通 Mod 转换失败：{error.Message}");
		}
		Console.WriteLine(result.ModDirectory);
		Console.WriteLine($"本体 {result.BaseUnits} 个 Unit，选项合计 {result.OptionUnits} 个 Unit。");
		foreach (var texture in result.ExternalTextures)
			Console.WriteLine($"外部贴图 {texture.Id:x16} {texture.GameArchive ?? "未找到"}");
		foreach (var material in result.ExternalMaterials)
			Console.WriteLine($"外部父材质 {material.Id:x16} {material.GameArchive ?? "未找到"}");
		return 0;
	}
}
