using System.Text.Json;

namespace HD2ModCore.Ias;

public abstract record IasModuleState
{
	public static bool IsVerifiedVersion(string? version) => version is "1.6.4";

	public sealed record NotInstalled : IasModuleState;
	public sealed record Installed(string Version) : IasModuleState;
	public sealed record UpdateAvailable(string InstalledVersion, string LatestVersion) : IasModuleState;
	public sealed record GameMismatch(IasGameCompatibilityResult Details, string? InstalledVersion = null) : IasModuleState;
	public sealed record Busy(IasProgress Progress) : IasModuleState;
	public sealed record Error(string Message) : IasModuleState;
}

public sealed class IasStatusService(IasComponentStore store, IasFeedClient feed)
{
	public async Task<IasModuleState> GetStatusAsync(string? gameRoot, CancellationToken cancellationToken = default) =>
		(await InspectAsync(gameRoot, false, cancellationToken).ConfigureAwait(false)).State;

	public async Task<(IasModuleState State, IasFeedResult? Feed)> InspectAsync(string? gameRoot, bool fetchWhenMissing,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (store.CurrentProgress is { } active)
			return (new IasModuleState.Busy(active), null);

		try
		{
			var installed = store.GetInstalled();
			if (installed is null)
				return (new IasModuleState.NotInstalled(), fetchWhenMissing
					? await feed.GetLatestAsync(cancellationToken: cancellationToken).ConfigureAwait(false) : null);
			if (!string.IsNullOrWhiteSpace(gameRoot))
			{
				var compatibility = await IasGameCompatibility.CheckAsync(gameRoot, installed.ComponentsDirectory,
					cancellationToken).ConfigureAwait(false);
				if (compatibility.Status != IasGameCompatibilityStatus.Compatible)
					return (new IasModuleState.GameMismatch(compatibility, installed.Version), null);
			}
			var result = await feed.GetLatestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
			if (store.CurrentProgress is { } progress)
				return (new IasModuleState.Busy(progress), null);
			if (result.Latest is { } latest && Version.Parse(latest.Version) > Version.Parse(installed.Version))
				return (new IasModuleState.UpdateAvailable(installed.Version, latest.Version), result);
			return (new IasModuleState.Installed(installed.Version), result);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
			or JsonException or InvalidOperationException or ArgumentException)
		{
			return (new IasModuleState.Error($"无法读取 IAS 模块状态：{exception.Message}"), null);
		}
	}
}
