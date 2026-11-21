using System.Diagnostics;
using System.IO;
using IasModelCli;

namespace HD2ModManager.Services;

internal sealed record IasPatchConversionTools(string IasRoot, string AqSdkRoot)
{
    public string Python => Path.Combine(IasRoot, "Generator", "python", "python.exe");
    public string PartCatalog => Path.Combine(AqSdkRoot, "hd2_system", "armor_table", "armor_part_unit_ids.json");
    public string Bindings => Path.Combine(AqSdkRoot, "hd2_system", "resource_binding_targets.json");

    public static bool ContainsGenerator(string root) =>
        File.Exists(Path.Combine(root, "Generator", "python", "python.exe"))
        && File.Exists(Path.Combine(root, "Generator", "runtime", "companion", "archive_payloads.py"));

    public static bool ContainsAqSdk(string root)
    {
        var system = Path.Combine(root, "hd2_system");
        return File.Exists(Path.Combine(system, "armor_table", "armor_part_unit_ids.json"))
            && File.Exists(Path.Combine(system, "resource_binding_targets.json"))
            && File.Exists(Path.Combine(system, "__init__.py"));
    }

    public static (string? IasRoot, string? AqSdkRoot, string BrowseRoot) FindLocal(string appDirectory)
    {
        var toolsDirectory = Path.Combine(appDirectory, "tools", "ias");
        var upstream = FindRepositoryUpstream(appDirectory);
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "HD2IntegratedArmorSystem");

        var iasRoot = Candidates("ias-1.6.3").Append(installed).FirstOrDefault(ContainsGenerator);
        var aqSdkRoot = Candidates("HD2SDK-AQ-Edition").FirstOrDefault(ContainsAqSdk);
        return (iasRoot, aqSdkRoot, upstream ?? toolsDirectory);

        IEnumerable<string> Candidates(string name)
        {
            yield return Path.Combine(toolsDirectory, name);
            if (upstream is not null) yield return Path.Combine(upstream, name);
        }
    }

    private static string? FindRepositoryUpstream(string appDirectory)
    {
        for (var parent = new DirectoryInfo(appDirectory); parent is not null; parent = parent.Parent)
        {
            if (!File.Exists(Path.Combine(parent.FullName, "tools", "IasModelCli", "IasModelCli.csproj"))) continue;
            var upstream = Path.Combine(parent.FullName, "tmp", "ias-lab", "upstream");
            return Directory.Exists(upstream) ? upstream : null;
        }
        return null;
    }
}

internal static class IasPatchConversionService
{
    internal const string CompanionExtension = ".hd2irpack.zip";

    internal static string GetModelZipPath(string companionPath) =>
        companionPath[..^CompanionExtension.Length] + "-模型主包.zip";

    public static async Task GenerateAsync(IasPatchConversionTools tools, string source, string gameData,
        string boneHashes, string companionPath, string name, IProgress<string> progress, CancellationToken token,
        IasEquipmentSelection? equipment = null, string? vpkSource = null)
    {
        if (!companionPath.EndsWith(CompanionExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("IAS 配套 ZIP 的文件名须以 .hd2irpack.zip 结尾。", nameof(companionPath));
        var modelPath = GetModelZipPath(companionPath);
        if (File.Exists(companionPath) || File.Exists(modelPath))
            throw new IOException("已有同名的配套或模型 ZIP，请换一个文件名。");

        if (!IasPatchConversionTools.ContainsAqSdk(tools.AqSdkRoot))
            throw new InvalidOperationException("所选目录不是可用的 HD2SDK-AQ-Edition。");

        var script = Path.Combine(AppContext.BaseDirectory, "tools", "ias", "ias_package.py");
        if (!File.Exists(script))
            throw new FileNotFoundException("找不到随管理器分发的 IAS 打包脚本。", script);

        var staging = Directory.CreateDirectory(Path.Combine(
            Path.GetDirectoryName(companionPath)!, ".ias-model-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var raw = Path.Combine(staging, "raw");
            var packaged = Path.Combine(staging, "output");
            if (vpkSource is null)
            {
                progress.Report("正在提取护甲与头盔资源");
                await PatchExtractor.ExtractAsync(source, gameData, boneHashes, tools.PartCatalog, tools.Bindings,
                    raw, SearchOption.TopDirectoryOnly, token, equipment).ConfigureAwait(false);
            }
            else
            {
                progress.Report("正在转换 Left 4 Dead 2 模型与材质");
                await VpkExtractor.RunAsync(new Dictionary<string, string>
                {
                    ["--vpk"] = vpkSource,
                    ["--game-data"] = gameData,
                    ["--bone-hash"] = boneHashes,
                    ["--part-catalog"] = tools.PartCatalog,
                    ["--binding-catalog"] = tools.Bindings,
                    ["--body-archive"] = "58e4bd4b2278d15c",
                    ["--helmet-archive"] = "a856edff49cfdd95",
                    ["--texconv"] = Environment.GetEnvironmentVariable("HD2_TEXCONV")
                        ?? Path.Combine(tools.AqSdkRoot, "deps", "texconv.exe"),
                    ["--out"] = raw,
                }, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            progress.Report("正在生成 IAS 主包与 Companion");
            await PackageAsync(tools, script, raw, packaged, name, token).ConfigureAwait(false);
            var stagedModel = Path.Combine(packaged, "IAS_MainMod.zip");
            var stagedCompanion = Path.Combine(packaged, "IAS_Companion" + CompanionExtension);
            if (!File.Exists(stagedModel) || !File.Exists(stagedCompanion))
                throw new InvalidOperationException("IAS 打包未生成完整的两份 ZIP。");
            token.ThrowIfCancellationRequested();
            File.Move(stagedModel, modelPath);
            try
            {
                File.Move(stagedCompanion, companionPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                File.Move(modelPath, stagedModel);
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException error) { LogService.Error($"清理 IAS 暂存目录失败：{error}"); }
            catch (UnauthorizedAccessException error) { LogService.Error($"清理 IAS 暂存目录失败：{error}"); }
        }
    }

    private static async Task PackageAsync(IasPatchConversionTools tools, string script, string raw,
        string output, string name, CancellationToken token)
    {
        var start = new ProcessStartInfo(tools.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in new[]
        {
            "-X", "utf8", script, "--raw", raw, "--out", output,
            "--ias", tools.IasRoot, "--aqsdk", tools.AqSdkRoot, "--name", name,
        })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 IAS Python。");
        var errorOutput = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await errorOutput.ConfigureAwait(false);
            throw;
        }

        var error = await errorOutput.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"IAS 打包失败（{process.ExitCode}）。{Environment.NewLine}{error.Trim()}");
    }
}
