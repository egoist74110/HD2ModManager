using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HD2SourceMigration.IasConversion;

public readonly record struct IasEquipment(bool Helmet, ulong Archive, uint Equipment);
public sealed record IasOriginalEquipment(IasEquipment Equipment, IReadOnlyDictionary<ulong, bool> HiddenByUnit);
public sealed record IasTargetSlot(ulong Unit, string Cell, string Body, int WriteOrder)
{
	public bool IsTorso { get; init; }
}
public sealed record IasTargetKit(ulong Archive, string Name, bool Helmet, Func<ulong, bool, IReadOnlyList<IasTargetSlot>> Match);
public delegate Task<IasTargetKit> IasTargetResolver(IasEquipment equipment, IReadOnlyCollection<ulong> units, CancellationToken token);

internal sealed record IasRoute(ulong Source, ulong Published);
internal sealed record IasOption(string Name, string Folder, IReadOnlyList<IasRoute> Routes);
internal sealed record IasOptionGroup(string Name, IReadOnlyList<IasOption> Options);
internal sealed record IasPackagePlan(IasEquipment Equipment, IReadOnlyList<IasRoute> Base, IReadOnlyList<IasOptionGroup> Groups);

internal static class IasPackageRoutes
{
	internal static IReadOnlyList<IasPackagePlan> Read(string companion)
	{
		if (!Directory.Exists(companion))
			throw new InvalidDataException("IAS 包缺少 companion/HD2IndependentRig 目录。");

		var differences = Directory.EnumerateFiles(companion, "*.difference.ini").Order(StringComparer.Ordinal)
			.Select(path => new Manifest(path, "manifest_version", "6")).ToArray();
		var isolations = Directory.EnumerateFiles(companion, "*.isolation.ini").Order(StringComparer.Ordinal)
			.Select(path => new Manifest(path, "isolation_version", "1")).ToArray();
		if (isolations.Length == 0 && differences.Length == 0)
			throw new InvalidDataException("IAS 包缺少 isolation.ini 或 difference.ini。");

		var plans = new List<IasPackagePlan>();
		foreach (var domain in isolations.Concat(differences).GroupBy(file => file.Equipment.Helmet).OrderBy(group => group.Key))
		{
			var identities = domain.Select(file => file.Equipment).ToArray();
			var archives = identities.Select(value => value.Archive).Where(value => value != 0).Distinct().ToArray();
			var equipment = identities.Select(value => value.Equipment).Where(value => value != 0).Distinct().ToArray();
			if (archives.Length > 1 || equipment.Length > 1)
				throw domain.First().Error("同一域声明了多个不同的源套件，无法共用目标。");
			var identity = new IasEquipment(domain.Key, archives.FirstOrDefault(), equipment.FirstOrDefault());
			var aliases = new Dictionary<ulong, ulong>();
			foreach (var file in domain.Where(isolations.Contains))
				foreach (var row in file.Rows("unit", 2))
					AddAlias(file, file.Id(row[0]), file.Id(row[1]));

			var groups = new List<IasOptionGroup>();
			var claimed = new HashSet<ulong>();
			var folders = new HashSet<string>(StringComparer.Ordinal);
			foreach (var file in domain.Where(differences.Contains))
			{
				if (file.Field("difference_logic") is not (null or "free_parts"))
					throw file.Error("仅支持 free_parts 差分。");
				var parts = file.Rows("free_part", 3).ToArray();
				var choices = file.Rows("free_option", 3).ToLookup(row => row[0], StringComparer.Ordinal);
				var routes = file.Rows("free_route", 5).ToLookup(row => (row[0], row[1]));
				foreach (var part in parts)
				{
					var ordered = choices[part[0]].OrderBy(row => row[1] != part[2])
						.ThenBy(row => file.OptionOrder(row[2])).ToArray();
					if (ordered.Length == 0 || ordered[0][1] != part[2])
						throw file.Error($"部件 {part[1]} 缺少默认选项 {part[2]}。");
					var defaults = ReadRoutes(ordered[0]);
					if (defaults.Count == 0)
						throw file.Error($"部件 {part[1]} 的默认选项缺少 Unit 路由。");
					foreach (var (source, published) in defaults)
					{
						AddAlias(file, source, published);
						if (!claimed.Add(source))
							throw file.Error($"同一源 Unit {source:x16} 被多个部件占用。");
					}
					var options = new List<IasOption>();
					foreach (var choice in ordered)
					{
						var selected = ReadRoutes(choice);
						if (selected.Keys.Any(source => !defaults.ContainsKey(source)))
							throw file.Error($"选项 {choice[1]} 包含默认选项没有声明的源 Unit。");
						var name = file.Display("free_option_display_name", choice[1], part[0], choice[1]);
						var folder = $"options/{(identity.Helmet ? "helmet" : "body")}-{StableId(part[0])}/{StableId(choice[1])}";
						if (!folders.Add(folder))
							throw file.Error($"部件或选项 ID 重复：{part[0]}/{choice[1]}。");
						options.Add(new(name, folder, defaults.Select(pair =>
							new IasRoute(pair.Key, selected.GetValueOrDefault(pair.Key, pair.Value))).ToArray()));
					}
					groups.Add(new(file.Display("free_part_display_name", part[1], part[0]), options));

					Dictionary<ulong, ulong> ReadRoutes(string[] choice)
					{
						var result = new Dictionary<ulong, ulong>();
						foreach (var row in routes[(part[0], choice[1])])
						{
							var source = file.Id(row[2]);
							var defaultId = file.Id(row[3]);
							var published = file.Id(row[4]);
							if (choice[1] == part[2] && published != defaultId)
								throw file.Error($"默认选项的 Unit {source:x16} 与基础别名不一致。");
							AddAlias(file, source, defaultId);
							if (!result.TryAdd(source, published))
								throw file.Error($"选项 {choice[1]} 重复声明源 Unit {source:x16}。");
						}
						return result;
					}
				}
			}

			if (aliases.Count == 0)
				throw domain.First().Error("缺少源 Unit 与发布 Unit 的对应表。");
			plans.Add(new(identity, aliases.Where(pair => !claimed.Contains(pair.Key))
				.Select(pair => new IasRoute(pair.Key, pair.Value)).ToArray(), groups));

			void AddAlias(Manifest file, ulong source, ulong published)
			{
				if (!aliases.TryAdd(source, published) && aliases[source] != published)
					throw file.Error($"源 Unit {source:x16} 的默认发布别名不一致。");
			}
		}
		return plans;
	}

	private static string StableId(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

	private sealed class Manifest(string path, string versionKey, string version)
	{
		private readonly ILookup<string, string> rows = File.ReadLines(path).Select(line => line.Trim())
			.Where(line => line.Length > 0 && line[0] is not ('#' or ';'))
			.Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
			.ToLookup(pair => pair[0].Trim(), pair => pair[1].Trim(), StringComparer.Ordinal);

		internal IasEquipment Equipment
		{
			get
			{
				if (Field(versionKey) != version)
					throw Error($"不支持 {versionKey}={Field(versionKey) ?? "缺失"}，要求版本 {version}。");
				var helmet = Field("domain") switch
				{
					"helmet" => true,
					"body" => false,
					_ => throw Error("缺少有效的 domain，要求 body 或 helmet。"),
				};
				return new(helmet, OptionalId(Field("archive"), ulong.MaxValue), (uint)OptionalId(Field("equipment"), uint.MaxValue));
			}
		}

		internal string? Field(string key)
		{
			var values = rows[key].Take(2).ToArray();
			if (values.Length > 1)
				throw Error($"重复声明 {key}。");
			return values.FirstOrDefault();
		}

		internal IEnumerable<string[]> Rows(string key, int columns) => rows[key].Select(value =>
		{
			var parts = value.Split(',', StringSplitOptions.TrimEntries);
			if (parts.Length < columns || parts.Take(columns).Any(string.IsNullOrEmpty))
				throw Error($"{key} 缺少必要字段：{value}。");
			return parts;
		});

		internal string Display(string key, string fallback, params string[] identity)
		{
			var names = rows[key].Select(value => value.Split(',', identity.Length + 1, StringSplitOptions.TrimEntries))
				.Where(row => row.Length > identity.Length && row.Take(identity.Length).SequenceEqual(identity))
				.Select(row => row[^1]).Distinct(StringComparer.Ordinal).Take(2).ToArray();
			if (names.Length > 1)
				throw Error($"{key} 的显示名有冲突：{string.Join("/", identity)}。");
			return names.FirstOrDefault() is { Length: > 0 } name ? name : fallback;
		}

		internal ulong Id(string value) => OptionalId(value, ulong.MaxValue) is var id && id != 0
			? id : throw Error($"无效的 Unit ID：{value}。");

		internal int OptionOrder(string value) =>
			int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var order)
				? order : throw Error($"free_option 的排序号无效：{value}。");

		private static ulong OptionalId(string? value, ulong maximum) =>
			value is not null && ulong.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan(),
				NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && id <= maximum ? id : 0;

		internal InvalidDataException Error(string message) => new($"IAS 清单 {Path.GetFileName(path)}：{message}");
	}
}
