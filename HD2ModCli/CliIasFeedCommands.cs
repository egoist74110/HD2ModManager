using HD2ModCore.Ias;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services.Ias;

namespace HD2ModCli;

internal static class CliIasFeedCommands
{
	internal static async Task<int> RunAsync(string command, CliGlobalOptions options)
	{
		var output = options.Output;
		if (command == "download")
		{
			if (string.IsNullOrWhiteSpace(output))
				throw new CliUserException("IasOutputRequired", "请传入 --output <部件输出目录>。");
			output = CliOutput.NormalizeDirectoryPath(output, "IasOutputInvalid");
			if (Path.Exists(output))
				throw new CliUserException("IasOutputExists", $"IAS 部件输出目录已存在：{output}");
		}
		else if (!string.IsNullOrWhiteSpace(output))
		{
			throw new CliUserException("IasOutputNotSupported", "IAS 只读命令不接受 --output。");
		}

		using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
		using var cancellation = new CancellationTokenSource();
		ConsoleCancelEventHandler cancel = (_, args) =>
		{
			args.Cancel = true;
			cancellation.Cancel();
		};
		Console.CancelKeyPress += cancel;
		try
		{
			var feed = new IasFeedClient(http);
			if (command == "status")
				return await StatusAsync(new IasComponentStore(ResolvePaths(options), http), feed, options, cancellation.Token)
					.ConfigureAwait(false);
			if (command == "download")
			{
				if (HD2ModManager.Services.GameProcessProbe.IsGameRunning()) throw new IasGameRunningException();
				var paths = ResolvePaths(options);
				if (Directory.Exists(Path.Combine(paths.AppRootDirectory, "ias", "snapshots", "previous")))
				{
					var workspace = CliWorkspace.Open(options);
					var game = workspace.GameDataRoot is { } data ? Directory.GetParent(data)?.FullName : null;
					var service = new IasModuleService(() => paths with { ModsRootDirectory = workspace.LibraryRoot }, () => game, http);
					await service.RecoverPendingAsync(cancellation.Token).ConfigureAwait(false);
				}
			}

			var result = await feed.GetLatestAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
			if (result.Latest is not { } release)
				throw new CliUserException($"IasFeed{result.Failure}", result.Message);
			if (command == "feed")
			{
				Write(options, new { release.Version, release.Sequence, release.ReleaseUrl, verified = true },
					$"IAS 最新版本 {release.Version}（序号 {release.Sequence}）\n{result.Message}\n{release.ReleaseUrl}");
				return 0;
			}

			if (HD2ModManager.Services.GameProcessProbe.IsGameRunning()) throw new IasGameRunningException();
			var store = new IasComponentStore(ResolvePaths(options), http);
			var package = await store.DownloadAsync(release, cancellationToken: cancellation.Token).ConfigureAwait(false);
			var directory = await store.ExportCoreAsync(release, package, output!, cancellationToken: cancellation.Token)
				.ConfigureAwait(false);
			Write(options, new { release.Version, directory, verified = true },
				$"IAS {release.Version} 部件校验通过，已解压到 {directory}");
			return 0;
		}
		catch (IasGameRunningException exception)
		{
			throw new CliUserException("GameProcessRunning", exception.Message);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
			or HttpRequestException or TimeoutException or InvalidOperationException)
		{
			throw new CliUserException("IasComponentOperationFailed", exception.Message);
		}
		finally
		{
			Console.CancelKeyPress -= cancel;
		}
	}

	private static StoragePaths ResolvePaths(CliGlobalOptions options) =>
		new(string.IsNullOrWhiteSpace(options.ManagerRoot) ? Hd2ModCliLocator.ResolveManagerRoot() : options.ManagerRoot);

	private static async Task<int> StatusAsync(IasComponentStore store, IasFeedClient feed, CliGlobalOptions options,
		CancellationToken token)
	{
		var gameData = CliWorkspace.Open(options).GameDataRoot;
		var gameRoot = gameData is null ? null : Directory.GetParent(Path.GetFullPath(gameData))?.FullName;
		var state = await new IasStatusService(store, feed).GetStatusAsync(gameRoot, token).ConfigureAwait(false);
		var text = state switch
		{
			IasModuleState.NotInstalled => "IAS 未安装。",
			IasModuleState.Installed installed => $"已安装 IAS {installed.Version}。",
			IasModuleState.UpdateAvailable update => $"已安装 IAS {update.InstalledVersion}，可更新至 {update.LatestVersion}。",
			IasModuleState.GameMismatch mismatch => FormatMismatch(mismatch),
			IasModuleState.Busy busy => busy.Progress.Message,
			IasModuleState.Error error => throw new CliUserException("IasStatusError", error.Message),
			_ => throw new InvalidOperationException("未知的 IAS 状态。")
		};
		Write(options, new { state = state.GetType().Name, details = (object)state }, text);
		return 0;
	}

	private static string FormatMismatch(IasModuleState.GameMismatch mismatch) =>
		$"已安装 IAS {mismatch.InstalledVersion}。{mismatch.Details.Message}\n" +
		string.Join("\n", mismatch.Details.Details.Select(detail =>
			$"{detail.Path}：期望 {detail.ExpectedSha256}，实际 {detail.ActualSha256 ?? "未找到"}"));

	private static void Write(CliGlobalOptions options, object value, string text)
	{
		if (options.Json)
			CliOutput.WriteJson(value);
		else
			Console.WriteLine(text);
	}
}
