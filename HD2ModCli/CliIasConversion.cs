using System.Globalization;
using HD2GameItemAssets;
using HD2SourceMigration.IasConversion;

namespace HD2ModCli;

internal static class CliIasConversion
{
	internal static async Task<int> ConvertAsync(string sample, string? gameData, string? output)
	{
		if (string.IsNullOrWhiteSpace(gameData) || string.IsNullOrWhiteSpace(output))
			throw new CliUserException("MissingIasPath", "ias convert <sample> 需要 --game-data <目录> 和 --output <目录>。");
		var matchSlot = await CliIasSlotAuthority.ReadAsync(gameData).ConfigureAwait(false);
		var result = await new IasDp8Converter(HashBone, matchSlot).ConvertAsync(sample, gameData, output).ConfigureAwait(false);
		Console.WriteLine(result.ModDirectory);
		Console.WriteLine($"本体 {result.BaseUnits} 个 Unit，四个选项合计 {result.OptionUnits} 个 Unit。");
		foreach (var texture in result.ExternalTextures)
			Console.WriteLine($"外部贴图 {texture.Id:x16} {texture.GameArchive ?? "未找到"}");
		foreach (var material in result.ExternalMaterials)
			Console.WriteLine($"外部父材质 {material.Id:x16} {material.GameArchive ?? "未找到"}");
		var targets = result.ShoulderTargets;
		Console.WriteLine(FormattableString.Invariant(
			$"肩关节目标（锁骨局部，米）左 ({targets.Left.X:F4}, {targets.Left.Y:F4}, {targets.Left.Z:F4}) 右 ({targets.Right.X:F4}, {targets.Right.Y:F4}, {targets.Right.Z:F4})；左肩需挪动 {targets.LeftShift.Length * 100:F1} cm"));
		return 0;
	}

	internal static async Task<int> PreviewAsync(string mod, string? gameData, string? output)
	{
		if (string.IsNullOrWhiteSpace(gameData) || string.IsNullOrWhiteSpace(output))
			throw new CliUserException("MissingIasPath", "ias preview <mod> 需要 --game-data <目录> 和 --output <目录>。");
		await IasDp8Preview.ExportAsync(mod, gameData, output).ConfigureAwait(false);
		return 0;
	}

	internal static uint HashBone(string name) =>
		uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var hash)
			? hash : (uint)(StingrayHash.Murmur64A(name) >> 32);
}
