namespace HD2ModCore.Ias;

public sealed record IasWorkerRequest(
	string Command, string GameRoot, string Components, string Library, string Backup,
	string Export, string Icon, string Version, string? Argument = null, bool Enabled = false)
{
	public bool IsolatedRead { get; init; }
}

public enum IasWorkerErrorKind { ReShadeConflict }

public sealed record IasWorkerResponse(bool Success, string? Value = null, string? Error = null,
	IasWorkerErrorKind? ErrorKind = null);

public sealed record IasArchiveOwner(string Id, string Name, bool Enabled, string[] Archives);

public enum IasDlssMode { Off, FrameGeneration, NeuralRendering }

public sealed record IasGraphicsState(bool ReShadeEnabled, IasDlssMode Dlss);

public interface IIasWorker
{
	Task<IasWorkerResponse> RunAsync(IasWorkerRequest request, CancellationToken cancellationToken);
}

