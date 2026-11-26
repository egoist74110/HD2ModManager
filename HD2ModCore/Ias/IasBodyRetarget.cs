namespace HD2ModCore.Ias;

public sealed record IasRetargetChoice(string Archive, string Name, string SearchText, string? Reason = null,
	string[]? Occupants = null, bool Original = false)
{
	public string[]? OccupantIds { get; init; }
}

public sealed record IasRetargetAnalysis(string? BodyArchive, string? Author,
	IReadOnlyList<IasRetargetChoice> Choices, string? Reason = null);

public sealed record IasRetargetPair(string MainZip, string CompanionZip, string[] Archives, string? Author, string BodyPatch,
	string[] BodyKeys);

public interface IIasBodyRetargeter
{
	Task<IasRetargetAnalysis> AnalyzeAsync(IasInstalled installed, string source, string work, CancellationToken ct);
	Task<IasRetargetPair> RetargetAsync(IasInstalled installed, string source, string archive, string work, CancellationToken ct);
}

public sealed record IasRoleArmor(string OriginalArchive, string CurrentArchive, string OriginalName, string CurrentName,
	string? Author);

public sealed record IasRetargetChange(IasRoleArmor Armor, string From, bool ReturnedToOriginal, string[] Occupants);
