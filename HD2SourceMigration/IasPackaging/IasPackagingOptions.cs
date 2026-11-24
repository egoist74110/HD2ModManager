namespace HD2SourceMigration.IasPackaging;

public enum IasRigGender { Female, Male }

public sealed record IasPackagingOptions(string OutputDirectory, string GameDataDirectory)
{
	public string? GeneratorDirectory { get; init; }
	public string? AqSdkDirectory { get; init; }
	public string? TexconvPath { get; init; }
	public string? Name { get; init; }
	public IasRigGender RigGender { get; init; } = IasRigGender.Female;
	public bool WithIasPhys { get; init; } = true;
	public Guid? ModelId { get; init; }
	public string? BoneHashPath { get; init; }
	public string? L4d2Directory { get; init; }
	/// <summary>附加材质来源。按文件名升序叠加，同文件名取列表中的最后一份。</summary>
	public IReadOnlyList<string> MaterialVpkPaths { get; init; } = [];
	public string? ModelStem { get; init; }
	public string? BodyArchive { get; init; }
	public string? HelmetArchive { get; init; }
	public Action<string, TimeSpan>? RecordTiming { get; init; }
}

public sealed record IasPackagingTargets(IReadOnlyList<string> BodyArchives, IReadOnlyList<string> HelmetArchives);

public sealed record IasPackagingResult(
	string? MainModZip, string? CompanionZip, Guid? ModelId, string? Name,
	IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors)
{
	public bool IsSuccess => MainModZip is not null && CompanionZip is not null && Errors.Count == 0;
}
