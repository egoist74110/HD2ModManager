using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 读取 Mod 根目录的关节修正数据。部署时将启用的护甲件、静止姿态及旧版关节目标
/// 转成物理运行时 <c>install</c> 的选项。
/// </summary>
public static class JointPoseData
{
	/// <summary>Mod 根目录里的文件名（与 IAS 转换器写出的保持一致）。</summary>
	public const string FileName = "joint-pose.json";

	private static readonly string[] LegBones = ["thigh", "knee", "foot", "ball"];

	/// <summary>
	/// 验证 JSON 并生成 Lua 表构造片段。给定启用选项时，只注入该配置里实际可驱动的资源。
	/// </summary>
	public static string ToLuaOptions(string json, IReadOnlySet<string>? activeOptions = null)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		var parts = new List<string>();
		if (root.TryGetProperty("armShoulderTarget", out var arm))
			parts.Add("armShoulderTarget = { l = " + Vector(arm, "l") + ", r = " + Vector(arm, "r") + " }");
		if (root.TryGetProperty("legJointTarget", out var legs) && legs.ValueKind == JsonValueKind.Object)
		{
			var entries = new List<string>();
			foreach (var side in new[] { "l", "r" })
				foreach (var bone in LegBones)
					entries.Add(side + "_" + bone + " = " + Vector(legs, side + "_" + bone));
			parts.Add("legJointTarget = { " + string.Join(", ", entries) + " }");
		}
		if (root.TryGetProperty("independentBones", out var independent))
		{
			parts.Add("independentBones = " + IndependentBones(independent));
			if (root.TryGetProperty("independentRest", out var rest) && rest.ValueKind != JsonValueKind.Null)
			{
				if (rest.ValueKind != JsonValueKind.Array || rest.GetArrayLength() != independent.GetArrayLength())
					throw new InvalidDataException("independentRest must match independentBones.");
				parts.Add("independentRest = { " + string.Join(", ", rest.EnumerateArray().Select(item =>
					"{ \"" + BoneName(item, "parent") + "\", " + Vector(item, "avatar", 4) + ", "
					+ Vector(item, "model", 4) + ", " + Vector(item, "position") + " }")) + " }");
			}
			if (root.TryGetProperty("independentPieces", out var pieces) && pieces.ValueKind != JsonValueKind.Null)
			{
				var names = independent.EnumerateArray().Select(item => BoneName(item, "bone")).ToHashSet(StringComparer.Ordinal);
				parts.AddRange(Pieces(root, pieces, names, activeOptions));
			}
		}
		if (parts.Count == 0)
			throw new InvalidDataException($"{FileName} 里没有 armShoulderTarget、legJointTarget 或 independentBones。");
		return string.Join(", ", parts);
	}

	/// <summary>
	/// 独立骨（实验）：<c>[{ "bone": "ias_l_shoulder", "source": "l_shoulder" }, ...]</c>，父骨在前。
	/// 骨名只允许小写字母、数字和下划线（直接拼进 Lua 源码，不能让任何引号、换行混进去）。
	/// </summary>
	private static string IndependentBones(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 || value.GetArrayLength() > 64)
			throw new InvalidDataException($"{FileName} 的 independentBones 必须是 1–64 项的数组。");
		var entries = new List<string>();
		foreach (var item in value.EnumerateArray())
			entries.Add("{ \"" + BoneName(item, "bone") + "\", \"" + BoneName(item, "source") + "\" }");
		return "{ " + string.Join(", ", entries) + " }";
	}

	private static IEnumerable<string> Pieces(JsonElement root, JsonElement pieces, HashSet<string> names,
		IReadOnlySet<string>? activeOptions)
	{
		var entries = new List<string>();
		var slots = new List<string>();
		root.TryGetProperty("independentPieceRoutes", out var routes);
		foreach (var piece in pieces.EnumerateObject())
		{
			var id = piece.Name;
			if (id.Length != 21 || !id.StartsWith("#ID[", StringComparison.Ordinal) || id[^1] != ']'
				|| !id.AsSpan(4, 16).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
				throw new InvalidDataException($"Invalid independent unit ID: {id}");
			var slot = id;
			if (routes.ValueKind == JsonValueKind.Object && routes.TryGetProperty(id, out var route))
			{
				if (activeOptions is not null && route.TryGetProperty("option", out var option)
					&& option.ValueKind != JsonValueKind.Null && !activeOptions.Contains(option.GetString()!))
					continue;
				if (activeOptions is not null && route.TryGetProperty("disabledBy", out var disabledBy)
					&& disabledBy.ValueKind != JsonValueKind.Null && activeOptions.Contains(disabledBy.GetString()!))
					continue;
				slot = BoneName(route, "slot", allowUppercase: true);
			}
			var bones = piece.Value.EnumerateArray().Select(bone => bone.GetString()).ToArray();
			if (bones.Length == 0 || bones.Any(bone => bone is null || !names.Contains(bone)) || bones.Distinct().Count() != bones.Length)
				throw new InvalidDataException($"Invalid independent bones for {id}.");
			entries.Add($"[\"{id}\"] = {{ " + string.Join(", ", bones.Select(bone => $"\"{bone}\"")) + " }");
			slots.Add($"[\"{id}\"] = \"{slot}\"");
		}
		return ["independentPieces = { " + string.Join(", ", entries) + " }",
			"independentPieceSlots = { " + string.Join(", ", slots) + " }"];
	}

	private static string BoneName(JsonElement item, string property, bool allowUppercase = false)
	{
		var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
			&& value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'
			|| allowUppercase && c is >= 'A' and <= 'Z'))
			throw new InvalidDataException($"{FileName} 的 independentBones.{property} 不合法：{name}");
		return name;
	}

	/// <summary>
	/// 在启用中的 Mod 里按顺序找 <see cref="FileName"/>：先看 Mod 自己的目录，再往上找到 Mod 库根为止
	/// （带选项的 Mod，启用的节点可能是选项子目录）。返回找到的全部文件路径，按传入顺序、去重。
	/// </summary>
	public static IReadOnlyList<string> Find(IEnumerable<string> modDirectories, string modsRootDirectory)
	{
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsRootDirectory));
		var found = new List<string>();
		foreach (var directory in modDirectories)
		{
			var current = Path.GetFullPath(directory);
			while (current.Length > root.Length
				&& current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				var candidate = Path.Combine(current, FileName);
				if (File.Exists(candidate))
				{
					if (!found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
						found.Add(candidate);
					break;
				}
				current = Path.GetDirectoryName(current)!;
			}
		}
		return found;
	}

	private static string Vector(JsonElement owner, string name, int length = 3)
	{
		if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
			throw new InvalidDataException($"{FileName} 缺少 {name}，或它不是 {length} 个数。");
		var builder = new StringBuilder("{ ");
		var index = 0;
		var norm = 0.0;
		foreach (var item in value.EnumerateArray())
		{
			var number = item.GetDouble();
			if (!double.IsFinite(number) || Math.Abs(number) > (length == 4 ? 1.000001 : 1.0))
				throw new InvalidDataException($"{FileName} 的 {name} 超出范围：{number}");
			norm += number * number;
			if (index++ > 0)
				builder.Append(", ");
			builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
		}
		if (length == 4 && Math.Abs(norm - 1) > 0.001)
			throw new InvalidDataException($"{FileName} 的 {name} 不是单位四元数。");
		return builder.Append(" }").ToString();
	}
}
