using System.IO;
using System.Windows;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;
using HD2ModManager.Views;
using IasModelCli;
using Microsoft.Win32;

namespace HD2ModManager.ViewModels;

public partial class ModDetailsPageViewModel
{
    private static IasPatchConversionTools? _lastIasTools;
    private RelayCommand? _generateIasCommand;
    private bool _generatingIas;
    private bool _hasRetainedIasPatch;

    internal bool ShowGenerateIas
    {
        get
        {
            if (_disposed || IsDecoration || !TryGetCurrentNode(out var node)) return false;
            if (HasSourceVpk) return true;
            if (HasSectionalSource) return _hasRetainedIasPatch;
            return node.PatchGroups.Count > 0
                && _library.GetDerivedData(Mod!.Guid)?.AssetSummary?.Assets.Any(asset =>
                    asset.Key.TypeId == PatchUnitMeshReader.UnitTypeId
                    && (asset.ArchiveCategory.Equals("Armor", StringComparison.OrdinalIgnoreCase)
                        || asset.ArchiveCategory.Equals("Helmet", StringComparison.OrdinalIgnoreCase))
                    && asset.SemanticTargetArchiveIds.Count > 0) == true;
        }
    }

    private static bool HasRootIasPatch(string directory)
    {
        try
        {
            var parser = new PatchFileNameParser();
            return SectionalSplitter.EnumeratePatchFiles(directory).Any(path => parser.TryParse(path, out _));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private RelayCommand GenerateIasCommand => _generateIasCommand ??= new RelayCommand(
        async _ => await GenerateIasAsync(), _ => ShowGenerateIas && !_generatingIas);

    private async Task GenerateIasAsync()
    {
        if (!ShowGenerateIas || _generatingIas || !TryGetCurrentNode(out var source)
            || System.Windows.Application.Current?.MainWindow is not { } owner) return;

        BackgroundTaskItem? task = null;
        _generatingIas = true;
        GenerateIasCommand.RaiseCanExecuteChanged();
        try
        {
            var gameData = SettingsService.GetGameDataFolder();
            if (!Directory.Exists(gameData))
                throw new InvalidOperationException("请先在设置页选择游戏 data 目录。");
            var directory = Path.Combine(_library.ModsRootDirectory, source.RelativePath);
            string? vpkSource = null;
            IasEquipmentSelection? equipment = null;
            if (HasSourceVpk)
            {
                var vpks = VpkRebuildService.FindSourceVpks(directory);
                if (vpks.Count == 0)
                    throw new InvalidOperationException("找不到这个 Mod 的原始 VPK 文件。");
                vpkSource = vpks.Count == 1 ? vpks[0] : SelectIasVpk(owner, directory, vpks);
                if (vpkSource is null) return;
            }
            else if (HasSectionalSource)
            {
                var savedSource = await SectionalSourceFile.TryReadAsync(directory);
                if (_disposed) return;
                if (savedSource?.HasSelection != true || !HasRootIasPatch(directory))
                    throw new InvalidOperationException("找不到可用的 Sectional 原始 Patch 或来源装备记录。");
                equipment = new IasEquipmentSelection(
                    savedSource.Armor?.HasValue == true ? savedSource.Armor.ArchiveId : null,
                    savedSource.Helmet?.HasValue == true ? savedSource.Helmet.ArchiveId : null);
            }
            var tools = SelectIasTools(owner);
            if (tools is null) return;

            var name = string.IsNullOrWhiteSpace(source.Metadata.Name) ? "模型" : source.Metadata.Name;
            var companionPath = SaveIasCompanion(owner, name);
            if (companionPath is null) return;
            var outputRoot = Path.GetDirectoryName(companionPath)!;
            var gameRoot = Directory.GetParent(Path.GetFullPath(gameData))?.FullName ?? gameData;
            if (IsNormalizationPathInside(outputRoot, _library.ModsRootDirectory)
                || IsNormalizationPathInside(outputRoot, gameRoot)
                || IsNormalizationPathInside(outputRoot, tools.IasRoot)
                || IsNormalizationPathInside(outputRoot, tools.AqSdkRoot))
                throw new InvalidOperationException("请选择 Mod 库、游戏与工具目录以外的输出位置。");

            var boneHashes = File.Exists(_paths.BoneHashesPath) ? _paths.BoneHashesPath
                : Path.Combine(AppContext.BaseDirectory, "data", "resources", "bonehash.txt");

            task = _backgroundTasks?.Enqueue(BackgroundTaskKind.Other, "生成 IAS 版", name,
                origin: "Mod 详情", userVisibleReason: "配套 ZIP 导入 IAS，模型主包 ZIP 导入 Mod 管理器。",
                canCancel: true, presentation: BackgroundTaskPresentation.Foreground);
            task?.MarkRunning("正在检查模型");
            var token = task?.CancellationToken ?? CancellationToken.None;
            var progress = new Progress<string>(stage => task?.UpdateStage(stage));
            await Task.Run(() => IasPatchConversionService.GenerateAsync(tools, directory, gameData,
                boneHashes, companionPath, name, progress, token, equipment, vpkSource), token);

            task?.SetOutputArtifacts(outputRoot, null);
            task?.MarkCompleted();
            _notifications?.Show($"配套 ZIP 导入 IAS：{Path.GetFileName(companionPath)}\n"
                + $"模型主包导入 Mod 管理器：{Path.GetFileName(IasPatchConversionService.GetModelZipPath(companionPath))}",
                NotificationLevel.Info, TimeSpan.FromSeconds(12));
        }
        catch (OperationCanceledException) { task?.MarkCanceled(); }
        catch (Exception error)
        {
            task?.MarkFailed(error.Message);
            LogService.Error($"生成 IAS 版失败：{error}");
            AppDialog.Show(owner, error.Message, "生成 IAS 版失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _generatingIas = false;
            GenerateIasCommand.RaiseCanExecuteChanged();
        }
    }

    private static string? SaveIasCompanion(Window owner, string name)
    {
        var extension = IasPatchConversionService.CompanionExtension;
        var dialog = new SaveFileDialog
        {
            Title = "保存 IAS 配套 ZIP（模型主包也保存在同一位置）",
            Filter = "IAS 配套 ZIP (*.hd2irpack.zip)|*.hd2irpack.zip",
            DefaultExt = extension,
            FileName = $"{SanitizeFileName(name)}-IAS-{DateTime.Now:yyyyMMdd-HHmmssfff}{extension}",
            InitialDirectory = SettingsService.GetResolvedDefaultExportPath(),
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(owner) != true) return null;

        var path = dialog.FileName;
        if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return path;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path + extension;
    }

    private static string? SelectIasVpk(Window owner, string directory, IReadOnlyList<string> vpks)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择此 Mod 的模型 VPK",
            Filter = "Left 4 Dead 2 模型包 (*.vpk)|*.vpk",
            InitialDirectory = directory,
        };
        if (dialog.ShowDialog(owner) != true) return null;
        if (!vpks.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择这个 Mod 文件夹中包含模型的原始 VPK。");
        return dialog.FileName;
    }

    private static IasPatchConversionTools? SelectIasTools(Window owner)
    {
        if (_lastIasTools is { } previous && IasPatchConversionTools.ContainsGenerator(previous.IasRoot)
            && IasPatchConversionTools.ContainsAqSdk(previous.AqSdkRoot))
            return previous;

        var local = IasPatchConversionTools.FindLocal(AppContext.BaseDirectory);
        var ias = local.IasRoot ?? SelectIasFolder(owner, "找不到 IAS 1.6.3，请选择解包后的 IAS 文件夹", local.BrowseRoot);
        if (ias is null) return null;
        ias = ResolveDependencyRoot(ias, "Generator");
        if (!IasPatchConversionTools.ContainsGenerator(ias))
            throw new InvalidOperationException("这个文件夹里找不到 IAS Generator。请先解包 IAS 1.6.3，再选择包含 Generator 的文件夹，也可以直接选择 Generator 文件夹。");

        var sdk = local.AqSdkRoot ?? SelectIasFolder(owner, "找不到 AQSDK，请选择 HD2SDK-AQ-Edition 文件夹", local.BrowseRoot);
        if (sdk is null) return null;
        sdk = ResolveDependencyRoot(sdk, "hd2_system");
        if (!IasPatchConversionTools.ContainsAqSdk(sdk))
            throw new InvalidOperationException("这个文件夹里找不到 AQSDK 的护甲目录或绑定表。请选择包含 hd2_system 的 HD2SDK-AQ-Edition 文件夹，也可以直接选择 hd2_system 文件夹。");
        return _lastIasTools = new IasPatchConversionTools(ias, sdk);
    }

    private static string ResolveDependencyRoot(string folder, string childName)
    {
        var directory = new DirectoryInfo(folder);
        return directory.Name.Equals(childName, StringComparison.OrdinalIgnoreCase)
            ? directory.Parent?.FullName ?? folder : folder;
    }

    private static string? SelectIasFolder(Window owner, string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory! : string.Empty };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}
