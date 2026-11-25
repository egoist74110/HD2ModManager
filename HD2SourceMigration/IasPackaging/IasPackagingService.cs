using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;

namespace HD2SourceMigration.IasPackaging;

public sealed class IasPackagingService
{
	private const string MainFileName = "IAS_MainMod.zip";
	private const string CompanionFileName = "IAS_Companion.hd2irpack.zip";
	private readonly IasDependencyLocator dependencyLocator;

	public IasPackagingService(IasDependencyLocator? dependencyLocator = null) =>
		this.dependencyLocator = dependencyLocator ?? new IasDirectoryDependencyLocator();

	public static async Task<IasPackagingTargets> GetVpkTargetsAsync(string sdkDirectory, string gameDataDirectory,
		CancellationToken ct = default)
	{
		var catalog = new IasPackagingDependencies("", sdkDirectory, null).BindingCatalog;
		var archives = (await new GameDataPackageResolver(gameDataDirectory).GetPackageNamesAsync(ct)
			.ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var targets = IasAssets.ReadBindingTargets(catalog).Values
			.Where(target => target.OwnedUnits.Count != 0 && archives.Contains(target.Archive))
			.OrderBy(target => target.Archive, StringComparer.Ordinal).ToArray();
		return new(targets.Where(target => target.Domain == "body").Select(target => target.Archive).ToArray(),
			targets.Where(target => target.Domain == "helmet").Select(target => target.Archive).ToArray());
	}

	public Task<IasPackagingResult> ConvertPatchModAsync(string patchDirectory, IasPackagingOptions options,
		IProgress<string>? progress = null, CancellationToken ct = default) =>
		ConvertAsync(patchDirectory, options, isVpk: false, progress, ct);

	public static Task<IReadOnlyList<string>> ReadPatchTargetArchivesAsync(IEnumerable<string> patchFiles, string sdkDirectory,
		CancellationToken token) =>
		PatchExtractor.ReadTargetArchivesAsync(patchFiles,
			new IasPackagingDependencies("", sdkDirectory, null).BindingCatalog, token);

	public Task<IasPackagingResult> ConvertL4d2VpkAsync(string vpkPath, IasPackagingOptions options,
		IProgress<string>? progress = null, CancellationToken ct = default) =>
		ConvertAsync(vpkPath, options, isVpk: true, progress, ct);

	private async Task<IasPackagingResult> ConvertAsync(string input, IasPackagingOptions options, bool isVpk,
		IProgress<string>? progress, CancellationToken ct)
	{
		var warnings = new List<string>();
		string? staging = null;
		string? output = null;
		string? name = null;
		Guid? id = null;
		IReadOnlyList<string> errors = [];
		var timing = new PackagingTiming(options?.RecordTiming);
		using var total = timing.Start("IAS 打包总计");
		try
		{
			ct.ThrowIfCancellationRequested();
			ArgumentNullException.ThrowIfNull(options);
			input = NormalizePath(input, "输入路径");
			if (isVpk ? !File.Exists(input) || !Path.GetExtension(input).Equals(".vpk", StringComparison.OrdinalIgnoreCase)
				: !Directory.Exists(input))
				throw new ArgumentException(isVpk ? "输入必须是存在的 .vpk 文件。" : "输入必须是存在的 patch 目录。");
			var gameData = NormalizePath(options.GameDataDirectory, "游戏 data 目录");
			if (!Directory.Exists(gameData))
				throw new DirectoryNotFoundException($"找不到游戏 data 目录：{gameData}");
			name = options.Name ?? (isVpk ? Path.GetFileNameWithoutExtension(input) : Path.GetFileName(input));
			if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
				throw new ArgumentException("模型名称不能为空或包含控制字符。");
			if (!Enum.IsDefined(options.RigGender))
				throw new ArgumentException("骨架性别必须是 Female 或 Male。");
			if (options.ModelId == Guid.Empty)
				throw new ArgumentException("复用的模型 UUID 不能是全零。");

			var destination = NormalizePath(options.OutputDirectory, "输出目录");
			if (Path.Exists(destination))
				throw new IOException($"输出路径已存在：{destination}");
			var l4d2Directory = isVpk && options.L4d2Directory is not null
				? NormalizePath(options.L4d2Directory, "Left 4 Dead 2 目录") : null;
			var materialVpks = isVpk ? options.MaterialVpkPaths
				.Select(path => NormalizePath(path, "材质 VPK 路径")).ToArray() : [];
			foreach (var path in materialVpks)
			{
				if (!File.Exists(path) || !Path.GetExtension(path).Equals(".vpk", StringComparison.OrdinalIgnoreCase))
					throw new ArgumentException($"材质来源必须是存在的 .vpk 文件：{path}");
			}
			if (l4d2Directory is not null && !File.Exists(Path.Combine(l4d2Directory, "pak01_dir.vpk")))
				throw new FileNotFoundException($"在指定的 Left 4 Dead 2 目录中找不到 pak01_dir.vpk：{l4d2Directory}");

			var resolution = dependencyLocator.Locate(options, isVpk);
			if (resolution.Dependencies is not { } tools || resolution.Errors.Count > 0)
				return new(null, null, null, name, [], resolution.Errors);

			foreach (var directory in new[] { gameData, tools.GeneratorDirectory, tools.AqSdkDirectory,
				isVpk ? null : input, l4d2Directory, options.GeneratorDirectory })
			{
				if (!string.IsNullOrWhiteSpace(directory) && IsWithin(directory, destination))
					throw new ArgumentException($"输出目录必须位于输入或依赖目录之外：{directory}");
			}

			// 随程序分发在 data\resources\ias：应用内更新只认 bin、third_party、hd2mod-docs、data\resources，别的目录整包会被拒。
			var scripts = Path.Combine(AppContext.BaseDirectory, "data", "resources", "ias");
			var script = Path.Combine(scripts, "ias_package.py");
			var bundledBoneHash = Path.Combine(scripts, "bonehash.txt");
			var boneHash = options.BoneHashPath is not null ? NormalizePath(options.BoneHashPath, "骨名哈希表")
				: File.Exists(bundledBoneHash) ? bundledBoneHash
				: Path.Combine(AppContext.BaseDirectory, "data", "resources", "bonehash.txt");
			if (!File.Exists(script) || !File.Exists(Path.Combine(scripts, "package_model.py")))
				throw new FileNotFoundException($"找不到随程序分发的 IAS 打包脚本：{scripts}");
			if (!File.Exists(boneHash))
				throw new FileNotFoundException($"找不到 IAS 转换所需的 bonehash.txt：{boneHash}");

			ct.ThrowIfCancellationRequested();
			// 同卷暂存，整目录发布。并发创建同名目标时，Directory.Move 会保留已有目录。
			var parent = Path.GetDirectoryName(destination)
				?? throw new ArgumentException("输出目录必须有父目录。");
			staging = Directory.CreateDirectory(Path.Combine(parent, ".ias-pack-" + Guid.NewGuid().ToString("N"))).FullName;
			var raw = Path.Combine(staging, "raw");
			var packaged = Path.Combine(staging, "output");
			if (isVpk)
			{
				var targets = await timing.MeasureAsync("核对 IAS 底子",
					() => GetVpkTargetsAsync(tools.AqSdkDirectory, gameData, ct)).ConfigureAwait(false);
				progress?.Report("正在提取求生模型、重定向骨架并转换材质。");
				var extraction = new Dictionary<string, string>
				{
					["--vpk"] = input,
					["--game-data"] = gameData,
					["--bone-hash"] = boneHash,
					["--part-catalog"] = tools.PartCatalog,
					["--binding-catalog"] = tools.BindingCatalog,
					["--body-archive"] = RequireTarget(options.BodyArchive, targets.BodyArchives, "body", "护甲"),
					["--helmet-archive"] = RequireTarget(options.HelmetArchive, targets.HelmetArchives, "helmet", "头盔"),
					["--texconv"] = tools.TexconvPath ?? throw new InvalidOperationException("依赖定位结果缺少 texconv.exe。"),
					["--out"] = raw,
				};
				if (l4d2Directory is not null)
					extraction.Add("--l4d2-root", l4d2Directory);
				if (options.ModelStem is not null)
					extraction.Add("--model-stem", options.ModelStem);
				warnings.AddRange(await VpkExtractor.RunAsync(extraction, materialVpks, options.WithIasPhys, timing, ct).ConfigureAwait(false));
			}
			else
			{
				progress?.Report("正在提取护甲与头盔资源。");
				using var extractTiming = timing.Start("提取普通 Mod 补丁");
				await PatchExtractor.ExtractAsync(input, gameData, boneHash, tools.PartCatalog, tools.BindingCatalog,
					raw, SearchOption.TopDirectoryOnly, ct).ConfigureAwait(false);
			}

			id = options.ModelId ?? Guid.NewGuid();
			progress?.Report("正在生成 IAS 主包与配套包。");
			warnings.AddRange(await timing.MeasureAsync("Python 打包",
				() => PackageAsync(tools, script, staging, raw, packaged, id.Value, name, options, timing, ct))
				.ConfigureAwait(false));
			if (!File.Exists(Path.Combine(packaged, MainFileName)) || !File.Exists(Path.Combine(packaged, CompanionFileName)))
				throw new InvalidDataException("IAS 打包未生成完整的两份 ZIP。");
			ct.ThrowIfCancellationRequested();
			Directory.Move(packaged, destination);
			output = destination;
		}
		catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
			or InvalidOperationException or JsonException or KeyNotFoundException or FormatException
			or OverflowException or Win32Exception or NotSupportedException)
		{
			errors = [error.Message];
		}
		finally
		{
			if (staging is not null)
			{
				try { Directory.Delete(staging, recursive: true); }
				catch (Exception error) when (error is IOException or UnauthorizedAccessException)
				{
					warnings.Add($"清理 IAS 暂存目录失败：{staging}。{error.Message}");
				}
			}
		}

		return new(output is null ? null : Path.Combine(output, MainFileName),
			output is null ? null : Path.Combine(output, CompanionFileName),
			output is null ? null : id, name, warnings.ToArray(), errors);
	}

	private static async Task<IReadOnlyList<string>> PackageAsync(IasPackagingDependencies tools, string script,
		string staging, string raw, string output, Guid id, string name, IasPackagingOptions options,
		PackagingTiming timing, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var warningsPath = Path.Combine(staging, "warnings.json");
		var start = new ProcessStartInfo(tools.PythonPath)
		{
			WorkingDirectory = staging,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		start.Environment["TEMP"] = staging;
		start.Environment["TMP"] = staging;
		foreach (var argument in new[]
		{
			"-B", "-X", "utf8", script, "--raw", raw, "--out", output,
			"--generator", tools.GeneratorDirectory, "--aqsdk", tools.AqSdkDirectory,
			"--generator-version", tools.GeneratorVersion,
			$"--name={name}", "--id", id.ToString("D"), "--rig-gender", options.RigGender.ToString().ToUpperInvariant(),
			"--warnings", warningsPath,
		})
			start.ArgumentList.Add(argument);
		if (options.WithIasPhys)
			start.ArgumentList.Add("--with-physics");

		using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 IAS Generator 的 Python。");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		try
		{
			await process.WaitForExitAsync(ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			try { process.Kill(entireProcessTree: true); }
			catch (InvalidOperationException) when (process.HasExited) { }
			await process.WaitForExitAsync().ConfigureAwait(false);
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
			throw;
		}
		var logs = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
		foreach (var line in logs[0].Split('\n'))
		{
			var fields = line.Split('\t');
			if (fields.Length == 3 && fields[0] == "IAS_TIMING" &&
				long.TryParse(fields[2].Trim(), out var milliseconds) && milliseconds >= 0)
				timing.Record(fields[1], TimeSpan.FromMilliseconds(milliseconds));
		}
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"IAS 打包失败（退出码 {process.ExitCode}）。{Environment.NewLine}{string.Join(Environment.NewLine, logs).Trim()}");
		return JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(warningsPath, ct).ConfigureAwait(false))
			?? throw new InvalidDataException("IAS 打包警告文件格式无效。");
	}

	private static string NormalizePath(string path, string label) =>
		string.IsNullOrWhiteSpace(path) ? throw new ArgumentException($"{label}不能为空。")
			: Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	private static string RequireTarget(string? selected, IReadOnlyList<string> candidates, string domain, string label)
	{
		if (selected is null && candidates.Count == 1) return candidates[0];
		if (selected is null)
			throw new ArgumentException($"请选择 IAS 底子{label}（--{domain}-archive）。兼容归档：{string.Join("、", candidates)}");
		return candidates.FirstOrDefault(candidate => candidate.Equals(selected.Trim(), StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"IAS 底子{label}归档 {selected} 不在绑定目录与当前游戏数据的交集中。");
	}

	private static bool IsWithin(string directory, string destination)
	{
		var relative = Path.GetRelativePath(directory, destination);
		return !Path.IsPathRooted(relative) && relative != ".."
			&& !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}
}
