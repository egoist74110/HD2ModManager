using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 运行时关节修正数据（实验，C1 阶段）：转换器把 <see cref="FileName"/> 放在 Mod 根目录，
/// 部署时开了「手臂姿态修正」就读出来，拼成物理运行时 <c>install</c> 的选项。
/// <para>
/// C1 只是先把数据送进游戏做实机验证：运行时还不区分玩家穿的是哪件护甲，
/// 启用中的 Mod 里只取第一份（按加载顺序）。按护甲绑定是下一步的事。
/// </para>
/// </summary>
public static class JointPoseData
{
	/// <summary>Mod 根目录里的文件名（与 IAS 转换器写出的保持一致）。</summary>
	public const string FileName = "joint-pose.json";

	private static readonly string[] LegBones = ["thigh", "knee", "foot", "ball"];

	/// <summary>
	/// 把 JSON 转成 Lua 表构造片段：<c>armShoulderTarget = {...}, legJointTarget = {...}</c>。
	/// 只认这两个键，其余忽略；坐标必须有限且 |c| ≤ 1 米，否则抛 <see cref="InvalidDataException"/>
	/// （运行时也会再校验一次，这里先挡住，免得部署出去才发现）。
	/// </summary>
	public static string ToLuaOptions(string json)
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
			parts.Add("independentBones = " + IndependentBones(independent));
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

	private static string BoneName(JsonElement item, string property)
	{
		var name = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
			&& value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
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

	private static string Vector(JsonElement owner, string name)
	{
		if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
			throw new InvalidDataException($"{FileName} 缺少 {name}，或它不是 3 个数。");
		var builder = new StringBuilder("{ ");
		var index = 0;
		foreach (var item in value.EnumerateArray())
		{
			var number = item.GetDouble();
			if (!double.IsFinite(number) || Math.Abs(number) > 1.0)
				throw new InvalidDataException($"{FileName} 的 {name} 超出范围：{number}");
			if (index++ > 0)
				builder.Append(", ");
			builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
		}
		return builder.Append(" }").ToString();
	}
}
