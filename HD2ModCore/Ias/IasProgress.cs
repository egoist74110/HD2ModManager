namespace HD2ModCore.Ias;

public enum IasStage
{
	FetchFeed,
	Download,
	Verify,
	Extract,
	Activate
}

public sealed record IasProgress(IasStage Stage, long CompletedBytes, long TotalBytes, string Message);
