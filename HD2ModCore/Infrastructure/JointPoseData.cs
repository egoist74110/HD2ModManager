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
				try
				{
					parts.AddRange(Pieces(root, pieces, names, activeOptions));
				}
				catch (Exception error) when (error is KeyNotFoundException or FormatException or InvalidOperationException)
				{
					throw new InvalidDataException($"{FileName} contains malformed independent piece data.", error);
				}
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
		var positions = new List<string>();
		var clothing = new List<string>();
		var legs = new List<string>();
		var grips = new List<string>();
		var selectedUnits = new HashSet<string>(StringComparer.Ordinal);
		root.TryGetProperty("independentPieceRoutes", out var routes);
		root.TryGetProperty("independentTranslations", out var translations);
		root.TryGetProperty("independentPoseRules", out var poseRules);
		root.TryGetProperty("independentLegs", out var legCalibration);
		root.TryGetProperty("independentGrips", out var gripCalibration);
		foreach (var piece in pieces.EnumerateObject())
		{
			var separator = piece.Name.IndexOf('@');
			var id = separator < 0 ? piece.Name : piece.Name[..separator];
			if (separator >= 0 && (activeOptions is null || !activeOptions.Contains(piece.Name[(separator + 1)..])))
				continue;
			if (id.Length != 21 || !id.StartsWith("#ID[", StringComparison.Ordinal) || id[^1] != ']'
				|| !id.AsSpan(4, 16).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
				throw new InvalidDataException($"Invalid independent unit ID: {id}");
			var slot = id;
			if (routes.ValueKind == JsonValueKind.Object && routes.TryGetProperty(piece.Name, out var route))
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
			if (!selectedUnits.Add(id))
				throw new InvalidDataException($"Multiple active independent variants for {id}.");
			if (bones.Length == 0 || bones.Any(bone => bone is null || !names.Contains(bone)) || bones.Distinct().Count() != bones.Length)
				throw new InvalidDataException($"Invalid independent bones for {id}.");
			entries.Add($"[\"{id}\"] = {{ " + string.Join(", ", bones.Select(bone => $"\"{bone}\"")) + " }");
			slots.Add($"[\"{id}\"] = \"{slot}\"");
			if (translations.ValueKind == JsonValueKind.Object && translations.TryGetProperty(piece.Name, out var translated))
			{
				var translatedBones = new List<string>();
				foreach (var bone in translated.EnumerateObject())
				{
					if (!bones.Contains(bone.Name))
						throw new InvalidDataException($"Invalid independent translation bone {bone.Name} for {id}.");
					translatedBones.Add($"[\"{bone.Name}\"] = {{ " + Vector(bone.Value, "avatar") + ", "
						+ Vector(bone.Value, "model") + " }");
				}
				positions.Add($"[\"{id}\"] = {{ " + string.Join(", ", translatedBones) + " }");
			}
			if (poseRules.ValueKind == JsonValueKind.Object && poseRules.TryGetProperty(piece.Name, out var rules))
				clothing.Add($"[\"{id}\"] = " + PoseRules(rules));
			if (gripCalibration.ValueKind == JsonValueKind.Object && gripCalibration.TryGetProperty(piece.Name, out var calibratedGrips))
			{
				var sides = calibratedGrips.EnumerateObject().Select(grip =>
				{
					if (grip.Name is not ("l" or "r") || grip.Value.GetArrayLength() != 12)
						throw new InvalidDataException("Invalid IAS hand-to-grip matrix.");
					return grip.Name + " = { " + string.Join(", ", grip.Value.EnumerateArray()
						.Select((value, index) =>
						{
							var number = value.GetDouble();
							var bound = index % 4 == 3 ? 1e7 : 1.001;
							if (!double.IsFinite(number) || Math.Abs(number) > bound)
								throw new InvalidDataException("Invalid IAS hand-to-grip matrix element.");
							return number.ToString("R", CultureInfo.InvariantCulture);
						})) + " }";
				});
				grips.Add($"[\"{id}\"] = {{ " + string.Join(", ", sides) + " }");
			}
			if (legCalibration.ValueKind == JsonValueKind.Object && legCalibration.TryGetProperty(piece.Name, out var calibrated))
			{
				var sides = calibrated.EnumerateObject().Select(leg =>
				{
					var scale = leg.Value.GetProperty("lateralScale").GetDouble();
					if (leg.Name is not ("l" or "r") || !double.IsFinite(scale))
						throw new InvalidDataException("Invalid IAS leg calibration.");
					return $"{leg.Name} = {{ {scale.ToString("R", CultureInfo.InvariantCulture)}, " + Vector(leg.Value, "bendNormal") + " }";
				});
				legs.Add($"[\"{id}\"] = {{ " + string.Join(", ", sides) + " }");
			}
		}
		yield return "independentPieces = { " + string.Join(", ", entries) + " }";
		yield return "independentPieceSlots = { " + string.Join(", ", slots) + " }";
		if (positions.Count > 0)
			yield return "independentTranslations = { " + string.Join(", ", positions) + " }";
		if (clothing.Count > 0)
			yield return "independentPoseRules = { " + string.Join(", ", clothing) + " }";
		if (legs.Count > 0)
			yield return "independentLegs = { " + string.Join(", ", legs) + " }";
		if (grips.Count > 0)
			yield return "independentGrips = { " + string.Join(", ", grips) + " }";
	}

	private static string PoseRules(JsonElement rules)
	{
		if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() is < 1 or > 64)
			throw new InvalidDataException("Invalid independent clothing rule count.");
		return "{ " + string.Join(", ", rules.EnumerateArray().Select(rule =>
		{
			var mode = rule.GetProperty("mode").GetInt32();
			if (mode is < 0 or > 3)
				throw new InvalidDataException("Invalid clothing combination mode.");
			var axes = rule.GetProperty("axes");
			if (axes.GetArrayLength() != 3)
				throw new InvalidDataException("A clothing rule must have three curves.");
			var curves = axes.EnumerateArray().Select(axis =>
			{
				var flags = axis.GetProperty("flags").GetInt32();
				var secondary = axis.GetProperty("secondaryAxis").GetInt32();
				var points = axis.GetProperty("points");
				if (flags is < 0 or > 3 || secondary is < 0 or > 2 || points.GetArrayLength() is < 2 or > 256)
					throw new InvalidDataException("Invalid clothing curve.");
				var previous = double.NegativeInfinity;
				var rows = points.EnumerateArray().Select(point =>
				{
					if (point.GetArrayLength() != 4 || point[0].GetDouble() <= previous)
						throw new InvalidDataException("Clothing curve points must have increasing inputs.");
					previous = point[0].GetDouble();
					return "{ " + string.Join(", ", point.EnumerateArray().Select(value => PoseNumber(value, -180, 180))) + " }";
				});
				return $"{{ flags = {flags}, secondaryAxis = {secondary}, points = {{ " + string.Join(", ", rows) + " } }";
			});
			return "{ target = \"" + BoneName(rule, "target", allowUppercase: true) + "\", primary = " + PoseDriver(rule.GetProperty("primary"))
				+ (mode == 0 ? "" : ", secondary = " + PoseDriver(rule.GetProperty("secondary")))
				+ $", mode = {mode}, influence = " + PoseNumber(rule.GetProperty("influence"), 0, 1)
				+ ", basis = " + Vector(rule, "basis", 4) + ", axes = { " + string.Join(", ", curves) + " } }";
		})) + " }";
	}

	private static string PoseDriver(JsonElement driver) => "{ bone = \"" + BoneName(driver, "bone") + "\""
		+ (driver.GetProperty("reference").ValueKind == JsonValueKind.Null ? ""
			: ", reference = \"" + BoneName(driver, "reference") + "\"")
		+ ", neutral = " + Vector(driver, "neutral", 4) + ", offset = " + Vector(driver, "offset", 4)
		+ ", referenceOffset = " + Vector(driver, "referenceOffset", 4) + " }";

	private static string PoseNumber(JsonElement value, double min, double max)
	{
		var number = value.GetDouble();
		if (!double.IsFinite(number) || number < min || number > max)
			throw new InvalidDataException("Clothing pose value is outside its authored range.");
		return number.ToString("R", CultureInfo.InvariantCulture);
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
