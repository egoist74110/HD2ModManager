using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HD2ModCore.Ias;

public sealed class IasProcessWorker(string sourceDirectory, string workerDirectory) : IIasWorker
{
	private const string TempPrefix = "h2i-";
	private const int MaxWindowsPath = 259;
	private const int ReservedStagePath = 140;

	public Task<IasWorkerResponse> RunAsync(IasWorkerRequest request, CancellationToken cancellationToken) =>
		RunInTemporaryDirectoryAsync(start => RunProcessAsync(start, request, cancellationToken), cancellationToken);

	internal async Task<IasWorkerResponse> RunInTemporaryDirectoryAsync(
		Func<ProcessStartInfo, Task<IasWorkerResponse>> run, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		CheckTempPathLength(Path.Combine(Path.GetTempPath(), TempPrefix));
		var temporary = Directory.CreateTempSubdirectory(TempPrefix);
		try
		{
			var temp = temporary.FullName;
			CheckTempPathLength(temp);
			return await run(CreateStartInfo(temp)).ConfigureAwait(false);
		}
		finally
		{
			IasFileTree.Delete(temporary.FullName);
		}
	}

	internal static void CheckTempPathLength(string path)
	{
		var limit = MaxWindowsPath - ReservedStagePath;
		if (path.Length > limit)
			throw new PathTooLongException($"IAS worker 暂存路径过长（{path.Length} 个字符，最多 {limit} 个字符）。请将 TEMP/TMP 指向较短的可写目录后重试：{path}");
	}

	private async Task<IasWorkerResponse> RunProcessAsync(ProcessStartInfo start, IasWorkerRequest request,
		CancellationToken cancellationToken)
	{
		var sourceExe = Path.Combine(sourceDirectory, "HD2IasWorker.exe");
		if (!File.Exists(sourceExe)) throw new FileNotFoundException("未找到 IAS 部署 worker。", sourceExe);
		var copy = Stopwatch.StartNew();
		Directory.CreateDirectory(workerDirectory);
		foreach (var source in Directory.EnumerateFiles(sourceDirectory))
		{
			var target = IasFileTree.Under(workerDirectory, Path.GetFileName(source));
			File.Copy(source, target, overwrite: true);
		}
		IasTiming.Record($"官方 IAS：{request.Command}：复制 worker", copy.Elapsed);

		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(TimeSpan.FromMinutes(15));
		using var process = Process.Start(start) ?? throw new IOException("无法启动 IAS 部署 worker。");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		try
		{
			await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), limit.Token).ConfigureAwait(false);
			process.StandardInput.Close();
			await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
		}
		catch (Exception error)
		{
			if (!process.HasExited)
			{
				try { process.Kill(entireProcessTree: true); }
				catch (InvalidOperationException) when (process.HasExited) { }
				await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			}
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
			if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
				throw new TimeoutException($"IAS worker 操作超时：{request.Command}");
			throw;
		}
		var output = await stdout.ConfigureAwait(false);
		var diagnostics = (await stderr.ConfigureAwait(false)).Trim();
		foreach (var line in diagnostics.Split('\n'))
		{
			// worker 用 "IAS计时\t阶段\t毫秒" 报告内部各步耗时。
			var parts = line.Trim().Split('\t');
			if (parts is ["IAS计时", var stage, var ms] && double.TryParse(ms, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var value))
				IasTiming.Record($"官方 IAS：{request.Command}：{stage}", TimeSpan.FromMilliseconds(value));
		}
		if (diagnostics.Length > 0)
		{
			var log = IasFileTree.Under(request.Backup, $"worker-{request.Command}.log");
			await File.WriteAllTextAsync(log, diagnostics, CancellationToken.None).ConfigureAwait(false);
		}
		IasWorkerResponse? result = null;
		try { result = JsonSerializer.Deserialize<IasWorkerResponse>(output); }
		catch (JsonException) { /* 进程退出但没有返回协议记录。 */ }
		if (result is { Success: true } && process.ExitCode == 0) return result;
		if (request.Command == "reshade-install" && process.ExitCode == 1 &&
			result is { Success: false, ErrorKind: IasWorkerErrorKind.ReShadeConflict, Error.Length: > 0 })
			return result;
		return new(false, Error: $"IAS worker {request.Command} 失败（退出码 {process.ExitCode}）：{result?.Error ?? output}\n诊断日志：{request.Backup}");
	}

	private ProcessStartInfo CreateStartInfo(string temp)
	{
		var start = new ProcessStartInfo(Path.Combine(workerDirectory, "HD2IasWorker.exe"))
		{
			WorkingDirectory = workerDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = new UTF8Encoding(false),
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		start.Environment["TMP"] = temp;
		start.Environment["TEMP"] = temp;
		var privateRuntime = Path.Combine(Path.GetDirectoryName(sourceDirectory)!, "dotnet");
		if (Directory.Exists(Path.Combine(privateRuntime, "host", "fxr")))
			start.Environment["DOTNET_ROOT_X64"] = privateRuntime;
		return start;
	}

	public static string Locate(string baseDirectory)
	{
		var adjacent = Path.Combine(baseDirectory, "ias-worker");
		return Directory.Exists(adjacent) ? adjacent : Path.Combine(baseDirectory, "bin", "ias-worker");
	}
}

