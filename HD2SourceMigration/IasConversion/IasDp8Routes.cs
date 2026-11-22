using System.Globalization;

namespace HD2SourceMigration.IasConversion;

internal sealed record IasSlot(string Part, string Body, ulong Source, ulong Target, bool Helmet = false);
internal sealed record IasRoute(IasSlot Slot, ulong Published, bool Hidden = false);
internal sealed record IasOption(string Name, string Folder, IReadOnlyList<IasRoute> Routes);
internal sealed record IasOptionGroup(string Name, IReadOnlyList<IasOption> Options);
internal sealed record IasPackagePlan(IReadOnlyList<IasRoute> Base, IReadOnlyList<IasOptionGroup> Groups);

internal static class IasDp8Routes
{
	internal const string SourceBody = "b186eae3f2b61509";
	internal const string SourceHelmet = "84fd7aab7cf6f62d";
	internal const string TargetBody = "d2fde496226be96f";
	internal const string TargetHelmet = "59d238094a164c88";
	internal const string Materials = "9ba626afa44a3aa3";

	// P0 Q4 matches the SR-64 and DP-8 equipment records by slot, layer and body profile.
	internal static readonly IasSlot[] Slots =
	[
		new("Hips", "Stocky", 0xf93d508ad9fa980d, 0x271136a6336f2327),
		new("Hips", "Slim", 0xb69bc1bf1c0bd5c5, 0xa1a48fb43841de86),
		new("LeftArm", "Stocky", 0xf6d665ef313b5a3c, 0x9b0351f8f12977fa),
		new("LeftArm", "Slim", 0x23a244c877843da3, 0xbc933c56b1d30a48),
		new("RightArm", "Stocky", 0x3b2875408869343e, 0x0aec901d0bfe6abb),
		new("RightArm", "Slim", 0x30204ad0fe720352, 0x3d7f9a0176bfdcf6),
		new("Torso", "Stocky", 0x61b3454c7e18aa01, 0x179e23134c2b0eeb),
		new("Torso", "Slim", 0xbd6394328a162b66, 0x21bcd576830894fa),
		new("Torso_Armor", "Stocky", 0x7ea0088b1192f975, 0xe3c2ff7999bd66ad),
		new("Torso_Armor", "Slim", 0xb01f561a105a6182, 0xd438f72d512e082e),
		new("LeftLeg", "Shared", 0x7352d1dcc74de764, 0xd939d51b56980ee7),
		new("RightLeg", "Shared", 0x72f4fba7b1745a62, 0xa824eaebb12e4234),
		new("Head", "Shared", 0xd93884ea73aaae2b, 0x510291bd9b94c994, true),
	];

	internal static IasPackagePlan Read(string companionDirectory)
	{
		var aliases = new Dictionary<ulong, ulong>();
		foreach (var path in Directory.EnumerateFiles(companionDirectory, "*.isolation.ini"))
		{
			var rows = ReadRows(File.ReadLines(path));
			var archive = ParseId(rows["archive"].Single()[0]);
			if (archive != ParseId(SourceBody) && archive != ParseId(SourceHelmet))
				continue;
			foreach (var row in rows["unit"])
				aliases.Add(ParseId(row[0]), ParseId(row[1]));
		}

		var differences = Directory.EnumerateFiles(companionDirectory, "*.difference.ini").Single();
		return Plan(aliases, File.ReadLines(differences));
	}

	internal static IasPackagePlan Plan(IReadOnlyDictionary<ulong, ulong> aliases, IEnumerable<string> differences)
	{
		var rows = ReadRows(differences);
		if (rows["manifest_version"].Single()[0] != "6" || ParseId(rows["archive"].Single()[0]) != ParseId(SourceBody))
			throw new InvalidDataException("Expected SR-64 free-parts v6 routes.");

		var baseRoutes = Slots.Where(slot => slot.Part is not ("Torso_Armor" or "LeftLeg"))
			.Select(slot => new IasRoute(slot, aliases[slot.Source])).ToArray();
		var groups = new List<IasOptionGroup>();
		foreach (var (partName, folder) in new[] { ("Torso_Armor", "coat"), ("LeftLeg", "shoes") })
		{
			var part = rows["free_part"].Single(row => row[1] == partName);
			var groupName = rows["free_part_display_name"].Single(row => row[0] == part[0])[1];
			var choices = rows["free_option"].Where(row => row[0] == part[0])
				.OrderBy(row => row[1] != part[2]).ToArray();
			if (choices.Length != 2 || choices[0][1] != part[2])
				throw new InvalidDataException($"Expected default and alternate choices for {partName}.");
			var options = new List<IasOption>();
			foreach (var (choice, index) in choices.Select((choice, index) => (choice, index)))
			{
				var name = rows["free_option_display_name"].Single(row => row[0] == part[0] && row[1] == choice[1])[2];
				var routes = rows["free_route"].Where(row => row[0] == part[0] && row[1] == choice[1])
					.ToDictionary(row => ParseId(row[2]));
				var slots = Slots.Where(slot => slot.Part == partName).ToArray();
				if (routes.Count != slots.Length)
					throw new InvalidDataException($"Unexpected route count for {name}.");
				var optionRoutes = slots.Select(slot =>
				{
					var route = routes[slot.Source];
					var published = ParseId(route[4]);
					if (aliases[slot.Source] != ParseId(route[3]) || (index == 0 && published != aliases[slot.Source]))
						throw new InvalidDataException($"Isolation and option aliases differ for {slot.Source:x16}.");
					return new IasRoute(slot, published, partName == "Torso_Armor" && index == 1);
				}).ToArray();
				options.Add(new(name, $"{folder}/{(index == 0 ? "on" : "off")}", optionRoutes));
			}
			groups.Add(new(groupName, options));
		}
		return new(baseRoutes, groups);
	}

	private static ILookup<string, string[]> ReadRows(IEnumerable<string> lines) =>
		lines.Select(line => line.Trim()).Where(line => line.Length > 0 && line[0] is not ('#' or ';'))
			.Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
			.ToLookup(pair => pair[0].Trim(), pair => pair[1].Split(',', StringSplitOptions.TrimEntries), StringComparer.Ordinal);

	private static ulong ParseId(string value) =>
		ulong.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan(),
			NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
