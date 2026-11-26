using System.IO.Compression;
using System.Text.Json;

namespace HD2ModCore.Ias;

public sealed partial class IasDeploymentService
{
	private const string RetargetOriginal = "retarget-original";
	private const string OfficialCompanion = "official-companion.hd2irpack.zip";

	private sealed record RetargetPreparation(IasInstalled Installed, string? PreviousId, string? PreviousRevision,
		IasCharacterPackage? Derived, IasRoleArmor? Armor, string[] Archives, IasRetargetChange? Change, string? Hint,
		bool Manual, string[] ExpectedOwners);

	public async Task<IasRetargetAnalysis> GetRetargetChoicesAsync(string id, CancellationToken ct)
	{
		var role = RequireRetargetRole(id);
		var work = At("temp/armor-picker-" + Guid.NewGuid().ToString("N"));
		try
		{
			var installed = GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
			var source = OriginalSources(role);
			var inspection = await AnalyzeRetargetAsync(installed, source, work, ct).ConfigureAwait(false);
			var archives = IasLibraryScan.ReadCompanionDirectory(Path.Combine(source, "companion"))?.Archives ?? [];
			var owners = await GetArchiveOccupancyAsync(ct).ConfigureAwait(false);
			return DescribeChoices(inspection, role, archives, owners);
		}
		finally { IasFileTree.Delete(work); }
	}

	public async Task<IasAddResult> RetargetAsync(string id, string archive, CancellationToken ct,
		IReadOnlyCollection<string>? displayedOccupants = null)
	{
		EnsureGameStopped();
		var role = RequireRetargetRole(id);
		var installed = GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
		var work = At("temp/change-armor-" + Guid.NewGuid().ToString("N"));
		try
		{
			var source = OriginalSources(role);
			var stamp = await IasSourceStamp.CaptureAsync(source, "IAS 原始两件套", ct).ConfigureAwait(false);
			var copy = Path.Combine(work, "original");
			IasFileTree.Copy(source, copy);
			var package = ReadStoredPair(copy, Path.Combine(work, "original.hd2irpack.zip"), role.Name);
			var inspection = await AnalyzeRetargetAsync(installed, copy, Path.Combine(work, "analysis"), ct)
				.ConfigureAwait(false);
			var original = role.Armor?.OriginalArchive ?? inspection.BodyArchive ?? throw new InvalidDataException(inspection.Reason ?? "无法识别原护甲。");
			var originalArchives = await ReadArchiveDataAsync<string[]>(installed, work, "companion-archives", package.CompanionZip, ct, isolatedRead: true)
				.ConfigureAwait(false);
			var owners = await GetArchiveOccupancyAsync(ct).ConfigureAwait(false);
			var choices = DescribeChoices(inspection, role, originalArchives, owners);
			var choice = choices.Choices.SingleOrDefault(item => item.Archive == archive)
				?? throw new InvalidDataException("目标护甲已失效或已是当前护甲。");
			if (choice.Reason is not null) throw new InvalidDataException(choice.Reason);
			if (displayedOccupants is not null && !displayedOccupants.ToHashSet(StringComparer.Ordinal).SetEquals(choice.OccupantIds ?? []))
				throw new InvalidOperationException("护甲占用已变更，请重新选择。");
			var originalName = role.Armor?.OriginalName ?? NameOf(inspection, original);
			var from = role.Armor?.CurrentName ?? originalName;
			var configured = new IasRoleArmor(original, archive, originalName, choice.Name, inspection.Author ?? role.Armor?.Author);
			var archives = originalArchives.Where(item => !SameArchive(item, original)).Append(archive).ToArray();
			var conflicts = ConflictsFor(id, archives, owners);
			var derived = choice.Original ? null : await BuildVerifiedRetargetAsync(installed, package, archive, archives,
				Path.Combine(work, "build"), ct).ConfigureAwait(false);
			var change = new IasRetargetChange(configured, from, choice.Original, conflicts.Select(owner => owner.Name).Distinct().ToArray());
			var prepared = new RetargetPreparation(installed, id, role.LibraryRevision, derived, configured, archives, change, null,
				true, conflicts.Select(owner => owner.Id).ToArray());
			return await CommitAddAsync(package, IasRoleSource.IasPackage, ct, (_, token) => stamp.VerifyAsync(token), retarget: prepared)
				.ConfigureAwait(false);
		}
		finally { IasFileTree.Delete(work); }
	}

	private async Task<RetargetPreparation?> PrepareDroppedRetargetAsync(IasCharacterPackage package, string work, CancellationToken ct)
	{
		var previous = FindPackageRole(GetRoles(), package.Identity);
		if (previous is not null && await package.HasSameContentAsync(OriginalSources(previous), ct).ConfigureAwait(false))
			return null;
		if (previous is not null && previous.Armor is null) return null;
		var installed = GetInstalled() ?? throw new InvalidOperationException("请先安装 IAS。");
		var archives = await ReadArchiveDataAsync<string[]>(installed, work, "companion-archives", package.CompanionZip, ct, isolatedRead: true)
			.ConfigureAwait(false);
		var owners = await GetArchiveOccupancyAsync(ct).ConfigureAwait(false);
		var conflicts = ConflictsFor(previous?.Id ?? "", archives, owners);
		if (previous is null && conflicts.Length == 0) return null;
		var inspection = await AnalyzeRetargetAsync(installed, package.Source, Path.Combine(work, "analysis"), ct)
			.ConfigureAwait(false);
		var original = inspection.BodyArchive;
		var originalName = original is null ? null : NameOf(inspection, original);
		var initial = original is null ? null : new IasRoleArmor(original, original, originalName!, originalName!, inspection.Author);
		var basePlan = new RetargetPreparation(installed, previous?.Id, previous?.LibraryRevision, null, initial, archives,
			null, null, false, []);
		if (previous is null && original is not null && conflicts.Any(owner => owner.Archives.Any(archive => !SameArchive(archive, original))))
			return basePlan with { Hint = "原头盔也被占用，只换身体无法解决" };

		var previousArmor = previous?.Armor;
		if (previousArmor is not null && original is not null && SameArchive(previousArmor.CurrentArchive, previousArmor.OriginalArchive))
			return basePlan;
		var preferred = previousArmor?.CurrentArchive;
		if (preferred is not null && SameArchive(preferred, original)) return basePlan;
		var attempts = new List<string>();
		if (original is not null && inspection.Reason is null)
		{
			foreach (var choice in inspection.Choices)
			{
				if (SameArchive(choice.Archive, original) || preferred is not null && !SameArchive(preferred, choice.Archive)) continue;
				if (choice.Reason is not null) { attempts.Add(choice.Reason); continue; }
				if (previous is null && OwnersOf(owners, choice.Archive).Length != 0) continue;
				var movedArchives = archives.Where(archive => !SameArchive(archive, original)).Append(choice.Archive).ToArray();
				try
				{
					var derived = await BuildVerifiedRetargetAsync(installed, package, choice.Archive, movedArchives,
						Path.Combine(work, "build-" + choice.Archive), ct).ConfigureAwait(false);
					var armor = initial! with { CurrentArchive = choice.Archive, CurrentName = choice.Name };
					var change = previous is null ? new IasRetargetChange(armor, originalName!, false,
						conflicts.Select(owner => owner.Name).Distinct().ToArray()) : null;
					return basePlan with { Derived = derived, Armor = armor, Archives = movedArchives, Change = change };
				}
				catch (Exception error) when (RetargetUnavailable(error)) { attempts.Add(error.Message); }
			}
		}
		var detail = inspection.Reason ?? attempts.FirstOrDefault();
		var hint = previous is null ? "没有能换的空闲护甲" : "新版无法套在当前目标上，已退回原护甲";
		return basePlan with { Hint = detail is null ? hint : $"{hint}（{detail}）" };
	}

	private IasRetargetAnalysis DescribeChoices(IasRetargetAnalysis analysis, IasRole role, string[] archives,
		IEnumerable<IasArchiveOwner> owners)
	{
		var original = role.Armor?.OriginalArchive ?? analysis.BodyArchive;
		var current = role.Armor?.CurrentArchive ?? original;
		if (original is null) return analysis with { Choices = [] };
		var choices = analysis.Choices.ToList();
		if (choices.All(choice => !SameArchive(choice.Archive, original)))
		{
			var label = role.Armor?.OriginalName ?? original;
			choices.Insert(0, new(original, label, label + " " + original));
		}
		var helmets = archives.Where(archive => !SameArchive(archive, original)).ToArray();
		var headOwners = ConflictsFor(role.Id, helmets, owners);
		return analysis with
		{
			BodyArchive = original,
			Choices = choices.Where(choice => !SameArchive(choice.Archive, current)).Select(choice =>
			{
				var occupied = ConflictsFor(role.Id, [choice.Archive], owners);
				var isOriginal = SameArchive(choice.Archive, original);
				var reason = isOriginal ? null : choice.Reason ?? analysis.Reason;
				if (reason is null && headOwners.Any(head => occupied.All(body => body.Id != head.Id)))
					reason = $"原头盔被{string.Join("、", headOwners.Select(owner => $"『{owner.Name}』").Distinct())}占用";
				return choice with
				{
					Original = isOriginal, Reason = reason, Occupants = occupied.Select(owner => owner.Name).Distinct().ToArray(),
					OccupantIds = occupied.Select(owner => owner.Id).ToArray()
				};
			}).ToArray()
		};
	}

	private async Task<IasRetargetAnalysis> AnalyzeRetargetAsync(IasInstalled installed, string source, string work, CancellationToken ct)
	{
		try { return await _retargeter!.AnalyzeAsync(installed, source, work, ct).ConfigureAwait(false); }
		catch (Exception error) when (RetargetUnavailable(error)) { return new(null, null, [], error.Message); }
	}

	private async Task<IasCharacterPackage> BuildVerifiedRetargetAsync(IasInstalled installed, IasCharacterPackage original,
		string archive, string[] expectedArchives, string work, CancellationToken ct)
	{
		var result = await _retargeter!.RetargetAsync(installed, original.Source, archive, work, ct).ConfigureAwait(false);
		var official = await ReadArchiveDataAsync<string[]>(installed, work, "companion-archives", result.CompanionZip, ct, isolatedRead: true)
			.ConfigureAwait(false);
		var keys = await ReadArchiveDataAsync<string[]>(installed, work, "retarget-patch-keys", result.BodyPatch, ct, isolatedRead: true)
			.ConfigureAwait(false);
		if (!expectedArchives.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(result.Archives) ||
			!expectedArchives.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(official) ||
			keys.Length != result.BodyKeys.Length || !keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(result.BodyKeys))
			throw new InvalidDataException("官方 Archive 或 Unit 键回读与换绑计划不符。");
		var destination = Path.Combine(work, "sources");
		var main = Path.Combine(destination, "main");
		IasFileTree.Extract(result.MainZip, main, ct);
		IasFileTree.Extract(result.CompanionZip, Path.Combine(destination, "companion"), ct);
		return new(original.Name, main, result.CompanionZip);
	}

	private static IasCharacterPackage ReadStoredPair(string source, string zip, string name)
	{
		var main = Path.Combine(source, "main");
		var companion = Path.Combine(source, "companion");
		if (!Directory.Exists(main) || !Directory.Exists(companion))
			throw new InvalidDataException("原 IAS 两件套缺失，请重拖原包。");
		var originalZip = Path.Combine(source, OfficialCompanion);
		if (File.Exists(originalZip)) zip = originalZip;
		else ZipFile.CreateFromDirectory(companion, zip);
		return new(name, main, zip);
	}

	private IasRole RequireRetargetRole(string id) => _retargeter is not null &&
		GetRoles().SingleOrDefault(role => role.Id == id && role.Source == IasRoleSource.IasPackage) is { } role
			? role : throw new InvalidOperationException("仅支持给已登记的 IAS Mod 换护甲。");

	private string OriginalSources(IasRole role)
	{
		var source = Sources(role.Id);
		var original = Path.Combine(source, RetargetOriginal);
		return Directory.Exists(original) ? original : source;
	}

	private IasRole? FindPackageRole(IEnumerable<IasRole> roles, string? identity) => identity is null ? null :
		roles.SingleOrDefault(role => string.Equals(identity, role.PackageIdentity ??
			IasLibraryScan.ReadCompanionDirectory(Path.Combine(OriginalSources(role), "companion"))?.Identity,
			StringComparison.OrdinalIgnoreCase));

	private static string ReadMainName(IasCharacterPackage package)
	{
		var manifest = Path.Combine(package.Main, "manifest.json");
		if (!File.Exists(manifest)) return package.Name;
		using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
		return doc.RootElement.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } text ? text : package.Name;
	}

	private static bool SameArchive(string? one, string? two) => string.Equals(one, two, StringComparison.OrdinalIgnoreCase);
	private static string NameOf(IasRetargetAnalysis analysis, string archive) =>
		analysis.Choices.FirstOrDefault(choice => SameArchive(choice.Archive, archive))?.Name ?? archive;

	private static bool RetargetUnavailable(Exception error) => error is IOException or InvalidDataException or InvalidOperationException
		or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException;
}
