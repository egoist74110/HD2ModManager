using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HD2ModCore.Ias;

public static class IasMaterialTemplates
{
	private const int MaximumTemplateBytes = 1024 * 1024; // Generator/runtime/companion/runtime_generation.py

	public static async Task<IReadOnlyDictionary<ulong, byte[]>> ReadAsync(IasInstalled installed,
		CancellationToken cancellationToken = default)
	{
		var assets = IasPaths.Expanded(installed, "ArmorAssets");
		var manifest = IasFileTree.Under(assets, "assets.json");
		if (!File.Exists(manifest))
			throw new InvalidDataException($"IAS 组件缺少 ArmorAssets/assets.json，请重新安装 IAS 组件：{manifest}。");

		await using var stream = File.OpenRead(manifest);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
		if (document.RootElement.ValueKind != JsonValueKind.Object ||
			!document.RootElement.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException("IAS ArmorAssets/assets.json 缺少有效的 request。");
		var templates = new Dictionary<ulong, byte[]>();
		if (!request.TryGetProperty("material_defaults", out var defaults))
			return templates; // 官方 Generator 对缺省字段使用空列表。
		if (defaults.ValueKind != JsonValueKind.Array)
			throw new InvalidDataException("IAS material_defaults 必须是数组。");

		foreach (var row in defaults.EnumerateArray())
		{
			if (row.ValueKind != JsonValueKind.Object ||
				!row.TryGetProperty("parent", out var parent) || parent.ValueKind != JsonValueKind.String ||
				!ulong.TryParse(parent.GetString(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ||
				id == 0 || templates.ContainsKey(id) ||
				!row.TryGetProperty("template", out var template) || template.ValueKind != JsonValueKind.Object ||
				!template.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
				!template.TryGetProperty("bytes", out var size) || size.ValueKind != JsonValueKind.Number ||
				!size.TryGetInt32(out var bytes) || bytes is < 0 or > MaximumTemplateBytes ||
				!template.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
				!IasPaths.IsSha256(hash.GetString()))
				throw new InvalidDataException("IAS material_defaults 包含无效模板记录或重复父材质。");

			var file = IasFileTree.Under(assets, path.GetString()!);
			await using var input = File.OpenRead(file);
			if (input.Length != bytes)
				throw new InvalidDataException($"IAS 父材质 {id:x16} 的模板大小与组件清单不符。");
			var data = new byte[bytes];
			await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
			if (!Convert.ToHexString(SHA256.HashData(data)).Equals(hash.GetString(), StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException($"IAS 父材质 {id:x16} 的模板 SHA-256 与组件清单不符。");
			templates.Add(id, data);
		}
		return templates;
	}
}
