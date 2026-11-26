using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HD2ModCore.Ias;
using HD2ModCore.Domain;
using HD2ModManager.Enums;
using HD2ModManager.Services;
using HD2ModManager.Services.Ias;
using HD2ModManager.ViewModels;

namespace HD2ModCore.Tests;

[Collection("WpfApplication")]
public sealed class IasPageViewModelTests
{
    [Fact]
    public async Task GroupsKeepLibraryCandidatesBelowRegisteredRolesAndManualScanReportsDifferences()
    {
        var shots = RoleSamples();
        var current = shots[..3];
        var notifications = new NotificationService();
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            ReadCharacters = _ => Task.FromResult<IReadOnlyList<IasCharacterInfo>>(current)
        };
        using var page = CreatePage(service, notifications: notifications);
        await page.EnterAsync();
        Assert.Equal(new[] { "已启用（1）", "未启用（2）" }, page.CharacterGroups.Select(group => group.Title));
        Assert.Equal(new[] { "disabled", "library:brem" }, page.CharacterGroups[1].Rows.Select(row => row.Id));

        current = [shots[0] with { Revision = "new" }, shots[1] with { MissingFromLibrary = true }, shots[3]];
        page.RefreshRolesCommand.Execute(null);
        Assert.Contains("发现 1 个库里的 IAS Mod", page.ResultText);
        Assert.Contains("1 个角色的库内文件有变化", page.ResultText);
        Assert.Contains("1 个角色的库内文件已删除", page.ResultText);
        Assert.Contains("库里移除了 1 个未登记的 IAS Mod", page.ResultText);
        Assert.True(page.CharacterGroups[1].Rows[0].MissingFromLibrary);
        Assert.Equal(2, service.CharacterReads);
        Assert.Equal(1, service.StatusReads);
        Assert.Equal(page.ResultText, notifications.History.Last().Message);

        page.RefreshRolesCommand.Execute(null);
        Assert.Equal("刷新完成：Mod 库和 IAS 一致，没有变化。", page.ResultText);
        Assert.Equal(2, notifications.History.Count);
    }

    [Fact]
    public async Task IdenticalDropRefreshesOnceAndKeepsWarningAndFlash()
    {
        var duplicate = new IasOperationResult(true, "『Milfy SR-64』已经装过了，内容一样，没有再装。", [])
            { Warning = true, FlashRoleId = "character", SkipLibraryReload = true };
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
            { Add = (_, _, _) => Task.FromResult(duplicate) };
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        await page.HandleWindowDropAsync([AppContext.BaseDirectory]);

        Assert.Equal(2, service.CharacterReads);
        Assert.Equal(duplicate.Message, page.ResultText);
        Assert.True(page.ResultWarning);
        Assert.True(Assert.Single(page.Characters).IsFlashing);
        Assert.StartsWith("已同步 · ", page.RoleScanText);
        Assert.Equal(0, dialogs.Confirmations);
    }

    [Fact]
    public async Task OpeningLibraryRowQueuesAdoptionAndShowsPendingSwitch()
    {
        var candidates = RoleSamples();
        var current = candidates[..3];
        var adopt = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            ReadCharacters = _ => Task.FromResult<IReadOnlyList<IasCharacterInfo>>(current),
            Adopt = _ => adopt.Task
        };
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters, item => item.IsLibraryOnly);
        var task = page.SetCharacterEnabledAsync(row, true);
        Assert.True(row.IsToggling);
        Assert.False(row.IsChecked);
        Assert.Equal("正在登记…", row.ToggleHint);
        current = [candidates[0], candidates[1], candidates[2] with { Id = "brem", LibraryModId = null, RegisteredModId = "brem", Enabled = true }];
        adopt.SetResult(new(true, "已把『布莱默顿』登记进 IAS 并启用。", []));
        await task;
        Assert.Equal(new[] { "brem" }, service.AdoptCalls);
        Assert.Equal(2, page.CharacterGroups[0].Rows.Count);
        Assert.Empty(service.ToggleCalls);
        Assert.Equal(0, dialogs.Confirmations);
    }

    private static IasCharacterInfo[] RoleSamples() =>
    [
        new("character", "Milfy SR-64", null, IasCharacterSource.IasPackage, DateTimeOffset.UnixEpoch, true)
            { Equipment = "SR-64 煤渣砖", Revision = "original" },
        new("disabled", "少前2 维多利亚", null, IasCharacterSource.IasPackage, DateTimeOffset.UnixEpoch, false)
            { Equipment = "SR-64 煤渣砖", Conflict = "和『Milfy SR-64』占同一件护甲（SR-64 煤渣砖）" },
        new("library:brem", "布莱默顿", null, IasCharacterSource.IasPackage, DateTimeOffset.UnixEpoch, false)
            { LibraryModId = "brem", LibraryOrigin = "以前当普通 Mod 导入进库的 IAS 包", Equipment = "DP-8 民主执政官" },
        new("library:eku", "Eku", null, IasCharacterSource.PatchMod, DateTimeOffset.UnixEpoch, false)
            { LibraryModId = "eku", LibraryOrigin = "上一份管理器转出来的角色", Equipment = "DP-8 民主执政官" }
    ];

    internal static void RenderRoleStates(System.Windows.Application app, string output, string retargetOutput)
    {
        Directory.CreateDirectory(output);
        app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        app.Resources["CardStackMargin"] = new Thickness(0, 0, 0, 12);
        app.Resources["CardPadding"] = new Thickness(16);
        app.Resources["CardCornerRadius"] = new CornerRadius(14);
        app.Resources["LayoutGap"] = 12d;
        app.Resources["CardContainer"] = new Style(typeof(Border), (Style)app.Resources["AppCard"])
        {
            Setters = { new Setter(Border.PaddingProperty, app.Resources["CardPadding"]),
                new Setter(FrameworkElement.MarginProperty, new Thickness(0)) }
        };

        var roles = RoleSamples();
        var current = roles[..2];
        var scan = Task.FromResult<IReadOnlyList<IasCharacterInfo>>(current);
        var duplicate = new IasOperationResult(true, "『Milfy SR-64』已经装过了，内容一样，没有再装。", [])
            { Warning = true, FlashRoleId = roles[0].Id, SkipLibraryReload = true };
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            ReadCharacters = _ => scan,
            Add = (_, _, _) => Task.FromResult(duplicate),
            ArmorChoices = ArmorChoices()
        };
        using var page = CreatePage(service);
        page.EnterAsync().GetAwaiter().GetResult();
        Save("groups-and-conflict", "和『Milfy SR-64』占同一件护甲（SR-64 煤渣砖）");

        page.HandleWindowDropAsync([AppContext.BaseDirectory]).GetAwaiter().GetResult();
        Save("duplicate-drop", duplicate.Message);

        var pending = new TaskCompletionSource<IReadOnlyList<IasCharacterInfo>>();
        scan = pending.Task;
        page.RefreshRolesCommand.Execute(null);
        Assert.True(page.IsScanningRoles);
        Save("refresh-scanning", "正在扫描 Mod 库…");
        pending.SetResult(roles[..3]);
        Assert.False(page.IsScanningRoles);
        Save("refresh-complete", "刷新完成：发现 1 个库里的 IAS Mod。");
        scan = Task.FromResult<IReadOnlyList<IasCharacterInfo>>(roles);
        page.OnLibrarySnapshotChanged();
        Save("library-roles", "库里有，未启用");

        var w53 = retargetOutput;
        Directory.CreateDirectory(w53);
        var moved = roles[0] with
        {
            Armor = new("sr64", "bp20", "SR-64 煤渣砖", "BP-20 思想矫正官", "长啸云天")
        };
        var auto = new IasOperationResult(true,
            "『Milfy SR-64』的原护甲 SR-64 煤渣砖 被『煤渣砖重制』占用，已换到 BP-20 思想矫正官 并启用。", [])
            { RetargetRoleId = moved.Id };
        var blocked = new IasOperationResult(true,
            "已装好，但没启用：和『煤渣砖重制』占同一件护甲（SR-64 煤渣砖头盔），原头盔也被占用，只换身体无法解决。", [])
            { Warning = true, SwapRoleId = moved.Id };
        var occupant = roles[1] with { Name = "煤渣砖重制", Enabled = true, Conflict = null };
        var original = moved with { Armor = moved.Armor! with { CurrentArchive = "sr64", CurrentName = "SR-64 煤渣砖" } };
        var returned = new IasOperationResult(true, "已把『Milfy SR-64』从 BP-20 思想矫正官 换到 SR-64 煤渣砖（换回了原护甲）。", [])
            { RetargetRoleId = moved.Id };
        var next = auto;
        IReadOnlyList<IasCharacterInfo> retargetRows = [moved, occupant];
        var retargetService = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            ReadCharacters = _ => Task.FromResult(retargetRows),
            Add = (_, _, _) => Task.FromResult(next),
            ArmorChoices = ArmorChoices(),
            Retarget = (_, archive) =>
            {
                Assert.Equal("sr64", archive);
                retargetRows = [original, occupant with { Enabled = false, Conflict = "和『Milfy SR-64』占同一件护甲（SR-64 煤渣砖）" }];
                return returned;
            }
        };
        using var retargetPage = CreatePage(retargetService);
        retargetPage.EnterAsync().GetAwaiter().GetResult();
        retargetPage.HandleWindowDropAsync([AppContext.BaseDirectory]).GetAwaiter().GetResult();
        Save("automatic-retarget", auto.Message, retargetPage, w53, "套在 BP-20 思想矫正官（原本 SR-64 煤渣砖） · 原作者 长啸云天");
        retargetPage.RetargetCharacterCommand.Execute(null);
        Save("retarget-panel", "被占用（1）", retargetPage, w53, "原护甲", "换不了（1）", "空闲，能换（1）", "换过去");
        var row = retargetPage.Characters.Single(item => item.Id == moved.Id);
        row.ArmorPanel!.Groups.SelectMany(group => group.Options).Single(option => option.IsOriginal).SelectCommand.Execute(null);
        retargetPage.ApplyArmorCommand.Execute(row);
        Save("returned-to-original", returned.Message, retargetPage, w53);
        next = blocked;
        retargetRows = [original with { Enabled = false, Conflict = "和『煤渣砖重制』占同一件护甲（SR-64 煤渣砖头盔）" }, occupant];
        retargetPage.HandleWindowDropAsync([AppContext.BaseDirectory]).GetAwaiter().GetResult();
        Save("helmet-conflict", blocked.Message, retargetPage, w53, "换成这个");

        void Save(string name, string expectedText, IasPageViewModel? model = null, string? destination = null,
            params string[] additional)
        {
            var view = new HD2ModManager.Views.IasPageView { DataContext = model ?? page };
            var canvas = new Border
            {
                Background = (Brush)app.Resources["SurfaceCanvas"],
                Padding = new Thickness(16),
                Child = view
            };
            var size = new Size(1000, name == "retarget-panel" ? 1140 : 960);
            canvas.Measure(size);
            canvas.Arrange(new Rect(size));
            canvas.UpdateLayout();
            var texts = VisibleText(canvas).ToArray();
            foreach (var text in additional.Prepend(expectedText))
                Assert.Contains(text, texts);
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(destination ?? output, name + ".png");
            using (var file = File.Create(path)) encoder.Save(file);
            Assert.True(new FileInfo(path).Length > 5000, name);
        }
        static IEnumerable<string> VisibleText(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) yield break;
            if (node is TextBlock block) yield return block.Text;
            if (node is Button { Content: string label }) yield return label;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var text in VisibleText(VisualTreeHelper.GetChild(node, i))) yield return text;
        }
    }

    [Fact]
    public void RetargetPanelGroupsAndSearchExcludeDisabledChoicesFromSelection()
    {
        var panel = new IasArmorPanelViewModel();
        panel.Show(ArmorChoices());
        Assert.Equal(new[] { "空闲，能换（1）", "被占用（1）", "换不了（1）" }, panel.Groups.Select(group => group.Header));
        Assert.True(panel.Groups[1].Options[0].IsOriginal);
        Assert.Contains("煤渣砖重制", panel.Groups[1].Options[0].Detail);
        Assert.False(panel.Groups[2].Options[0].CanSelect);
        panel.Groups[1].Options[0].SelectCommand.Execute(null);
        Assert.True(panel.HasSelection);
        panel.Query = "ad-49";
        Assert.False(panel.HasSelection);
        Assert.Equal("AD-49 阿波罗神", Assert.Single(panel.Groups[0].Options).Name);
        Assert.Empty(panel.Groups[1].Options);
        Assert.Empty(panel.Groups[2].Options);
    }

    private static IasRetargetAnalysis ArmorChoices() => new("sr64", "长啸云天",
    [
        new("ad49", "AD-49 阿波罗神", "AD-49"),
        new("sr64", "SR-64 煤渣砖", "SR-64", Occupants: ["煤渣砖重制"], Original: true),
        new("rs40", "RS-40 掠食凶兽", "RS-40", "缺少作者可见部件或差分槽")
    ]);

    [Fact]
    public async Task RunningGameDisablesWritesAndRecoveryRunsOnReentryAfterExit()
    {
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            GameRunning = true,
            Graphics = new(true, IasDlssMode.Off),
            PhysicsOn = true,
            BuiltInLuaOn = true
        };
        using var page = CreatePage(service);
        await page.EnterAsync();

        Assert.True(page.GameRunning);
        Assert.Equal(IasGameRunningException.Notice, page.GameRunningNotice);
        Assert.False(page.PrimaryCommand.CanExecute(null));
        Assert.False(page.UninstallCommand.CanExecute(null));
        Assert.False(page.TurnOffPhysicsCommand.CanExecute(null));
        Assert.False(page.TurnOffLuaCommand.CanExecute(null));
        Assert.False(page.CanToggleIas);
        Assert.False(page.CanToggleCharacter);
        Assert.False(page.CanToggleReShade);
        Assert.False(page.CanToggleDlss);
        Assert.False(page.CanAcceptDrop);
        var role = Assert.Single(page.Characters);
        Assert.False(page.RemoveCharacterCommand.CanExecute(role));
        Assert.False(page.ConvertCharacterCommand.CanExecute(role));
        Assert.Equal(0, service.RecoveryCalls);

        page.Leave();
        service.GameRunning = false;
        await page.EnterAsync();

        Assert.False(page.GameRunning);
        Assert.Equal(1, service.RecoveryCalls);
        Assert.True(page.PrimaryCommand.CanExecute(null));
        Assert.True(page.CanToggleIas);
    }

    [Theory]
    [InlineData(IasStatusKind.Checking, null, "Gray", "正在检查…", null, null, false)]
    [InlineData(IasStatusKind.NotInstalled, null, "Gray", "IAS 未安装", "将从 IAS 官方 GitHub 下载约 249 MB，默认仅启用护甲部件。", "安装 IAS", false)]
    [InlineData(IasStatusKind.Installed, "1.6.4", "Green", "已安装 1.6.4", "已是最新版本。", null, true)]
    [InlineData(IasStatusKind.UpdateAvailable, "1.6.4", "Blue", "有新版本 1.6.5", "当前 1.6.4。", "更新", true)]
    [InlineData(IasStatusKind.GameMismatch, "1.6.4", "Yellow", "游戏已更新，IAS 尚未适配", "等 IAS 作者发布新版本后再更新。游戏版本信息。", null, false)]
    [InlineData(IasStatusKind.MissingMdl3, "1.6.4", "Yellow", "缺少前置 MDL3", "先在首页把 MDL3 加进当前配置。", null, false)]
    [InlineData(IasStatusKind.NotInProfile, "1.6.4", "Gray", "IAS 已停用（当前配置）", "IAS 和角色都已从当前配置撤下，部件和角色都还在，打开开关即可恢复。", null, false)]
    [InlineData(IasStatusKind.FeedUnavailable, "1.6.4", "Gray", "已安装 1.6.4", "暂时连不上 IAS 官方源：连接超时。", "重试", true)]
    [InlineData(IasStatusKind.FeedUnavailable, null, "Gray", "IAS 未安装", "暂时连不上 IAS 官方源：连接超时。", "重试", false)]
    [InlineData(IasStatusKind.Error, null, "Red", "出错了", "连接超时。", "重试", false)]
    public async Task StatusTable(IasStatusKind kind, string? version, string color, string title, string? detail, string? action, bool ready)
    {
        using var page = CreatePage(new ServiceStub(kind, version) { EnabledInActiveProfile = ready });

        await page.EnterAsync();

        Assert.Equal(color, page.StatusColor);
        Assert.Equal(title, page.StatusText);
        Assert.Equal(detail, page.StatusDetail);
        Assert.Equal(action, page.PrimaryActionText);
        Assert.Equal(action is not null, page.PrimaryCommand.CanExecute(null));
        Assert.Equal(ready, page.CanAcceptDrop);
        Assert.Equal(ready || kind == IasStatusKind.NotInProfile, page.ShowIasToggle);
        Assert.Equal(page.ShowIasToggle, page.CanToggleIas);
        Assert.Equal(ready, page.CanToggleCharacter);
        Assert.Equal(kind is IasStatusKind.NotInstalled or IasStatusKind.UpdateAvailable, page.ShowDownloadSource);
        Assert.Equal(ready ? "把 IAS Mod、普通护甲 Mod 或求生 VPK 拖到窗口任意位置" : kind switch
        {
            IasStatusKind.Checking => "正在检查 IAS 状态…",
            IasStatusKind.GameMismatch => "游戏更新后 IAS 暂不可用，等 IAS 更新后再添加角色",
            IasStatusKind.MissingMdl3 => "先把 MDL3 加进当前配置",
            IasStatusKind.NotInProfile => "先打开 IAS",
            _ => "先安装 IAS 才能添加角色"
        }, page.DropText);
    }

    [Theory]
    [InlineData(null, false, false, IasStatusKind.FeedUnavailable)]
    [InlineData("1.6.4", false, true, IasStatusKind.MissingMdl3)]
    [InlineData("1.6.4", true, false, IasStatusKind.FeedUnavailable)]
    [InlineData("1.6.4", true, true, IasStatusKind.FeedUnavailable)]
    public void BackendCombinesGatesAndFeedState(string? version, bool mdl3, bool inProfile, IasStatusKind expected)
    {
        HD2ModCore.Ias.IasModuleState state = version is null
            ? new HD2ModCore.Ias.IasModuleState.NotInstalled() : new HD2ModCore.Ias.IasModuleState.Installed(version);
        var status = IasModuleService.ProjectStatus(state, new(null, HD2ModCore.Ias.IasFeedFailure.Network, "离线"), version, mdl3, inProfile);
        Assert.Equal(expected, status.Kind);
        Assert.Equal(version, status.InstalledVersion);
        Assert.Equal(inProfile, status.EnabledInActiveProfile);
    }

    [Theory]
    [InlineData(IasStatusKind.Installed, "1.6.4", true, false, false, true)]
    [InlineData(IasStatusKind.Installed, "1.6.4", true, true, false, true)]
    [InlineData(IasStatusKind.Installed, "1.6.4", true, false, true, true)]
    [InlineData(IasStatusKind.GameMismatch, "1.6.4", true, true, true, true)]
    [InlineData(IasStatusKind.NotInProfile, "1.6.4", false, true, true, false)]
    [InlineData(IasStatusKind.NotInstalled, null, false, true, true, false)]
    public async Task ConflictWarningReflectsActiveProfile(IasStatusKind kind, string? version, bool inProfile,
        bool physicsOn, bool mappingInUse, bool showWarning)
    {
        var service = new ServiceStub(kind, version)
        {
            EnabledInActiveProfile = inProfile,
            PhysicsOn = physicsOn,
            MappingInUse = mappingInUse,
        };
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);

        await page.EnterAsync();

        Assert.Equal(showWarning, page.ShowConflictWarning);
        Assert.Equal(showWarning && physicsOn, page.ShowTurnOffPhysics);
        Assert.Equal(showWarning && physicsOn, page.TurnOffPhysicsCommand.CanExecute(null));
        Assert.Equal(showWarning && mappingInUse, page.ShowMappingWarning);
        Assert.Null(dialogs.LastConfirmation);
    }

    [Theory]
    [InlineData(IasStatusKind.NotInstalled, true, true, true)]
    [InlineData(IasStatusKind.UpdateAvailable, true, true, true)]
    [InlineData(IasStatusKind.NotInProfile, true, true, true)]
    [InlineData(IasStatusKind.NotInstalled, true, false, false)]
    [InlineData(IasStatusKind.UpdateAvailable, false, true, false)]
    [InlineData(IasStatusKind.NotInProfile, false, true, false)]
    public async Task PrimaryActionOffersToTurnOffPhysicsAfterSuccessfulDeployment(IasStatusKind kind, bool success,
        bool physicsOn, bool shouldConfirm)
    {
        var service = new ServiceStub(kind, kind == IasStatusKind.NotInstalled ? null : "1.6.4")
        {
            PhysicsOn = physicsOn,
            FinishPrimaryAction = success,
        };
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        if (kind == IasStatusKind.NotInProfile) await page.SetIasEnabledAsync(true);
        else await page.ExecutePrimaryAsync();

        Assert.Equal(shouldConfirm, dialogs.LastConfirmation is not null);
        Assert.Equal(0, service.TurnOffCalls);
        Assert.Equal(success || kind == IasStatusKind.UpdateAvailable, page.ShowConflictWarning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedToggleKeepsRequestedPositionWhileBusyAndRestoresAfterFailure(bool total)
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            Toggle = pending.Task
        };
        using var page = CreatePage(service);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);

        var operation = total ? page.SetIasEnabledAsync(false) : page.SetCharacterEnabledAsync(row, false);

        Assert.True(page.IsBusy);
        Assert.True(page.ShowIasToggle);
        Assert.False(page.CanToggleIas);
        // 角色开关走队列，忙时仍可拨；总开关仍是独占操作。
        Assert.Equal(!total, page.CanToggleCharacter);
        Assert.False(total ? page.IasEnabled : row.IsChecked);
        pending.SetResult(new(false, "模拟部署失败", []));
        await operation;

        Assert.True(page.IasEnabled);
        Assert.True(row.IsChecked);
        Assert.True(page.CanToggleCharacter);
        Assert.Equal("模拟部署失败", page.ResultText);
    }

    [Fact]
    public async Task DisablingIasHidesWarningAndLocksRoleToggleButKeepsRoleActions()
    {
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            FinishPrimaryAction = true, PhysicsOn = true, BuiltInLuaOn = true, CharacterEnabled = false
        };
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        await page.SetIasEnabledAsync(false);

        var row = Assert.Single(page.Characters);
        Assert.True(row.IsDisabled);
        Assert.False(page.ShowConflictWarning);
        Assert.False(page.CanToggleCharacter);
        Assert.True(page.ConvertCharacterCommand.CanExecute(row));
        Assert.True(page.RemoveCharacterCommand.CanExecute(row));
        Assert.True(page.CanToggleIas);
        Assert.Equal("更新", page.PrimaryActionText);
        Assert.Null(dialogs.LastConfirmation);
    }

    [Theory]
    [InlineData(true, false, "关掉管理器物理？", "关掉物理", true)]
    [InlineData(false, true, "关掉管理器内置 Lua？", "关掉内置 Lua", true)]
    [InlineData(true, true, "关掉管理器的物理和内置 Lua？", "全部关掉", true)]
    [InlineData(true, false, "关掉管理器物理？", "关掉物理", false)]
    [InlineData(false, true, "关掉管理器内置 Lua？", "关掉内置 Lua", false)]
    [InlineData(true, true, "关掉管理器的物理和内置 Lua？", "全部关掉", false)]
    public async Task EnablingOffersExactlyTheManagerFeaturesThatAreOn(bool physics, bool lua, string title, string button, bool confirm)
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            EnabledInActiveProfile = false, PhysicsOn = physics, BuiltInLuaOn = lua, FinishPrimaryAction = true
        };
        var dialogs = new DialogStub(confirm ? false : null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        await page.SetIasEnabledAsync(true);

        var confirmation = Assert.IsType<IasConfirmation>(dialogs.LastConfirmation);
        Assert.Equal(title, confirmation.Title);
        Assert.Equal(button, confirmation.ConfirmLabel);
        Assert.Equal("先不关", confirmation.CancelLabel);
        if (physics)
        {
            Assert.Contains("潜兵身上的护甲", confirmation.Message);
            Assert.Contains("NPC 不受影响。", confirmation.Message);
            Assert.Contains("物理 Mod 不再生效", confirmation.Message);
        }
        if (lua) Assert.Contains("也建议关掉管理器的内置 Lua。", confirmation.Message);
        Assert.Equal((confirm && physics, confirm && lua), service.LastTurnOff);
        Assert.Equal(physics && !confirm, page.ShowTurnOffPhysics);
        Assert.Equal(lua && !confirm, page.ShowTurnOffLua);
        Assert.Equal(page.ShowTurnOffPhysics, page.TurnOffPhysicsCommand.CanExecute(null));
        Assert.Equal(page.ShowTurnOffLua, page.TurnOffLuaCommand.CanExecute(null));
    }

    [Fact]
    public async Task WarningLuaButtonOnlyTurnsOffLuaWhenBothAreOn()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4") { PhysicsOn = true, BuiltInLuaOn = true };
        var dialogs = new DialogStub(false);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        await page.ConfirmTurnOffLuaAsync();

        Assert.Equal("关掉管理器内置 Lua？", dialogs.LastConfirmation?.Title);
        Assert.Equal((false, true), service.LastTurnOff);
        Assert.True(page.ShowTurnOffPhysics);
        Assert.False(page.ShowTurnOffLua);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PhysicsChangesOnlyAfterConfirmation(bool confirm, bool afterInstall)
    {
        var service = afterInstall
            ? new ServiceStub(IasStatusKind.NotInstalled) { PhysicsOn = true, FinishPrimaryAction = true, MappingInUse = true }
            : new ServiceStub(IasStatusKind.Installed, "1.6.4") { EnabledInActiveProfile = true, PhysicsOn = true, MappingInUse = true };
        // 对话框的 false 表示按了主按钮且没有勾选框，null 才是取消。
        var dialogs = new DialogStub(confirm ? false : null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        if (afterInstall) await page.ExecutePrimaryAsync();
        else await page.ConfirmTurnOffPhysicsAsync();

        Assert.Equal(new IasConfirmation("关掉管理器物理？",
            "IAS 接管潜兵身上的护甲，和带物理的普通 Mod 会冲突。NPC 不受影响。关闭管理器物理后，管理器物理 Mod 不再生效。以后可以在设置里重新打开。",
            "关掉物理", CancelLabel: "先不关"), dialogs.LastConfirmation);
        Assert.Equal(confirm ? 1 : 0, service.TurnOffCalls);
        Assert.Equal(!confirm, page.ShowTurnOffPhysics);
        Assert.True(page.ShowConflictWarning);
        Assert.True(page.ShowMappingWarning);
    }

    [Fact]
    public async Task InstallationFinishingOffPageOffersConfirmationOnReturn()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.NotInstalled)
        {
            PhysicsOn = true,
            Install = (_, _) => pending.Task,
            ReadStatus = (read, _) => Task.FromResult(read == 1 ? Status(IasStatusKind.NotInstalled, null)
                : Status(IasStatusKind.Installed, "1.6.4") with { EnabledInActiveProfile = true, PhysicsOn = true }),
        };
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        var operation = page.ExecutePrimaryAsync();

        page.Leave();
        pending.SetResult(new(true, "已安装 IAS。", []));
        await operation;

        Assert.Null(dialogs.LastConfirmation);
        await page.EnterAsync();
        Assert.NotNull(dialogs.LastConfirmation);
        Assert.Equal(0, service.TurnOffCalls);
    }

    [Fact]
    public async Task ExclusiveOperationDisablesOtherExclusiveCommandsAndCannotOverlap()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            Install = (progress, _) =>
            {
                progress.Report(new("正在下载…", 62, "正在下载 154 / 249 MB", true));
                return pending.Task;
            },
        };
        using var page = CreatePage(service);
        await page.EnterAsync();
        page.PropertyChanged += (_, _) =>
        {
            if (page.CanCancel) reported.TrySetResult();
        };
        var row = Assert.Single(page.Characters);
        var operation = page.ExecutePrimaryAsync();
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(page.ShowProgress);
            Assert.True(page.IsBusy);
            Assert.Equal("Blue", page.StatusColor);
            Assert.Equal("正在下载…", page.StatusText);
            Assert.Equal("62%", page.ProgressText);
            Assert.Null(page.StatusDetail);
            Assert.Null(page.PrimaryActionText);
            Assert.False(page.PrimaryCommand.CanExecute(null));
            Assert.False(page.UninstallCommand.CanExecute(null));
            Assert.True(page.ChooseLibraryModCommand.CanExecute(null));
            Assert.True(page.ConvertCharacterCommand.CanExecute(row));
            Assert.True(page.RemoveCharacterCommand.CanExecute(row));
            Assert.False(page.OpenOfficialPageCommand.CanExecute(null));
            Assert.False(page.OpenCreatedModCommand.CanExecute(null));
            Assert.True(page.CancelCommand.CanExecute(null));
            Assert.True(page.CanAcceptDrop);
            Assert.True(page.UpdateWindowDrop([AppContext.BaseDirectory]));
            Assert.Contains("队列", page.DropText);
            Assert.Equal(0, service.AddCalls);
            await page.ExecutePrimaryAsync();
            Assert.Equal(1, service.InstallCalls);
        }
        finally
        {
            pending.SetResult(new(true, "已安装 IAS 1.6.5。", []));
            await operation;
        }
        Assert.Equal(2, service.StatusReads);
    }

    [Theory]
    [InlineData(WorkspaceMode.Ias, true)]
    [InlineData(WorkspaceMode.Home, false)]
    [InlineData(WorkspaceMode.ProfileOnly, false)]
    [InlineData(WorkspaceMode.LibraryOnly, false)]
    [InlineData(WorkspaceMode.ProfileLibrarySplit, false)]
    [InlineData(WorkspaceMode.Settings, false)]
    [InlineData(WorkspaceMode.ArmorRegistry, false)]
    public void OnlyIasOwnsWindowDrop(WorkspaceMode mode, bool ownsDrop)
    {
        using var page = CreatePage(new ServiceStub(IasStatusKind.NotInstalled));
        Assert.Same(ownsDrop ? page : null, ShellViewModel.ResolveIasWindowDropPage(mode, page));
    }

    [Theory]
    [InlineData(IasStatusKind.NotInstalled, "先安装 IAS 才能添加角色")]
    [InlineData(IasStatusKind.GameMismatch, "游戏更新后 IAS 暂不可用，等 IAS 更新后再添加角色")]
    public async Task UnavailablePageHighlightsStatusWithoutImporting(IasStatusKind kind, string message)
    {
        var service = new ServiceStub(kind);
        using var page = CreatePage(service);
        await page.EnterAsync();

        Assert.False(page.UpdateWindowDrop([AppContext.BaseDirectory]));
        Assert.True(page.IsWindowDropHighlighted);
        Assert.Equal(message, page.DropText);
        await page.HandleWindowDropAsync([AppContext.BaseDirectory]);
        Assert.False(page.IsWindowDropHighlighted);
        Assert.Equal(0, service.AddCalls);
    }

    [Fact]
    public async Task WindowDropQueuesWithoutConfirmation()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4");
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        Assert.True(page.UpdateWindowDrop([AppContext.BaseDirectory]));
        Assert.True(page.IsWindowDropHighlighted);
        Assert.Equal("松开后加入 IAS 队列", page.DropText);
        Assert.Equal(0, service.AddCalls);

        await page.HandleWindowDropAsync([AppContext.BaseDirectory]);

        Assert.Equal(1, service.AddCalls);
        Assert.Equal(0, dialogs.Confirmations);
        Assert.True(page.ResultSuccess);
        Assert.False(page.IsWindowDropHighlighted);
    }

    [Fact]
    public async Task BusyPageRunsConfirmedItemsInOrderAfterFailureAndSkipsCancelledAndDuplicateItems()
    {
        using var drops = new DropFiles();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            Add = async (paths, _, _) =>
            {
                var name = Path.GetFileName(Assert.Single(paths));
                calls.Add(name);
                if (name == "First.zip")
                {
                    await first.Task;
                    throw new InvalidOperationException("模拟失败");
                }
                return new(true, "已添加", []);
            },
            Convert = (armor, helmet, merge) =>
            {
                Assert.Equal("0x00000009", armor);
                Assert.Equal("0x00000002", helmet);
                Assert.True(merge);
                calls.Add("转换");
                return new(true, "已转换", []);
            },
            AddFromLibrary = id =>
            {
                calls.Add(id);
                return new(true, "已从库添加", []);
            },
        };
        var dialogs = new DialogStub(true) { Selected = [new("library-id", "库中护甲", null, "护甲")] };
        var notifications = new NotificationService();
        var tasks = new BackgroundTaskService();
        var messages = new MessageCenterService(notifications, tasks);
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, dialogs, notifications, queue);
        await page.EnterAsync();

        var active = page.HandleWindowDropAsync([drops.First]);
        Assert.Equal("First.zip", page.CurrentQueueItem?.Name);
        Assert.True(page.UpdateWindowDrop([drops.Second]));
        var second = page.HandleWindowDropAsync([drops.Second]);
        var secondMessage = Assert.Single(messages.ActiveTasks, item => item.Title == "添加 IAS 角色：Second.zip");
        Assert.Equal(BackgroundTaskStatus.Queued, secondMessage.Task!.Status);
        var secondStatuses = new List<BackgroundTaskStatus>();
        secondMessage.Task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BackgroundTaskItem.Status)) secondStatuses.Add(secondMessage.Task.Status);
        };
        await page.HandleWindowDropAsync([Path.Combine(Path.GetDirectoryName(drops.First)!, ".", "FIRST.zip")]);
        var cancelled = page.HandleWindowDropAsync([drops.Cancelled]);
        var row = Assert.Single(page.Characters);
        page.ConvertCharacterCommand.Execute(row);
        var panel = Assert.IsType<IasConversionPanelViewModel>(row.Conversion);
        Assert.Equal("原来那套：原护甲", panel.Armor.SelectionText);
        Assert.Equal("原来那套：原护甲", panel.Armor.Choices.Cast<IasTargetChoice>().First().Name);
        Assert.Null(panel.Armor.SelectionIconPath);
        panel.Armor.SelectCommand.Execute(ArmorChoice);
        panel.MergeHelmet = true;
        page.StartConversionCommand.Execute(row);
        panel.MergeHelmet = false;
        Assert.Null(row.Conversion);
        page.RemoveCharacterCommand.Execute(row);
        Assert.Equal(0, dialogs.Confirmations);
        Assert.Empty(service.RemoveCalls);
        page.ChooseLibraryModCommand.Execute(null);
        Assert.Equal(1, dialogs.Confirmations); // 只有「从库里选」确认；拖入不弹
        Assert.Equal(["Second.zip", "Cancelled.zip", row.Name, "库中护甲"], page.WaitingQueue.Select(item => item.Name));
        Assert.False(page.UninstallCommand.CanExecute(null));
        Assert.False(page.CanToggleIas);
        var waitingMessage = Assert.Single(messages.ActiveTasks, item => item.Title == "添加 IAS 角色：Cancelled.zip");
        Assert.True(waitingMessage.CanCancel);
        waitingMessage.Task!.Cancel();
        await cancelled;
        Assert.Equal(BackgroundTaskStatus.Canceled, waitingMessage.Task.Status);
        Assert.DoesNotContain(waitingMessage, messages.ActiveTasks);
        Assert.Equal(["First.zip"], calls);

        queue.Changed += (_, _) => { if (!queue.HasWork) queueIdle.TrySetResult(); };
        first.SetResult();
        await Task.WhenAll(active, second, queueIdle.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["First.zip", "Second.zip", "转换", "library-id"], calls);
        var failed = Assert.Single(messages.AttentionItems, item => item.Title == "添加 IAS 角色：First.zip");
        Assert.Equal(BackgroundTaskStatus.Failed, failed.Task!.Status);
        Assert.Contains("模拟失败", failed.CopyText);
        Assert.Equal([BackgroundTaskStatus.Running, BackgroundTaskStatus.Completed], secondStatuses);
        Assert.Contains(secondMessage, messages.RecentNotifications);
        Assert.DoesNotContain(notifications.History, item => item.Level == NotificationLevel.Error);
    }

    [Fact]
    public async Task BusyPageQueuesRemovalAndUsesRoleIdAfterRefreshReplacesRow()
    {
        var install = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            Install = (_, _) => install.Task
        };
        var dialogs = new DialogStub(true);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        IReadOnlyList<IasCharacterRow>? rowsAtRemoval = null;
        service.Remove = (_, _) =>
        {
            rowsAtRemoval = page.Characters;
            return Task.FromResult(new IasOperationResult(true, "已卸载", []));
        };

        var active = page.ExecutePrimaryAsync();
        try
        {
            Assert.True(page.IsBusy);
            Assert.True(page.RemoveCharacterCommand.CanExecute(row));
            page.RemoveCharacterCommand.Execute(row);

            Assert.Equal(new IasConfirmation("卸载角色", $"卸载角色 {row.Name}？会从 IAS 和当前配置里移除。", "卸载"),
                dialogs.LastConfirmation);
            var waiting = Assert.Single(page.WaitingQueue);
            Assert.Equal("卸载 IAS 角色", waiting.Operation);
            Assert.Equal(BackgroundTaskStatus.Queued, waiting.Task.Status);
            Assert.Empty(service.RemoveCalls);
            service.CharacterEnabled = false;
        }
        finally
        {
            install.SetResult(new(true, "已更新", []));
            await active.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([row.Id], service.RemoveCalls);
        var refreshedRow = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IasCharacterRow>>(rowsAtRemoval));
        Assert.NotSame(row, refreshedRow);
        Assert.Equal(row.Id, refreshedRow.Id);
        Assert.Empty(page.Characters);
    }

    [Fact]
    public async Task TwoRoleRemovalsAreQueuedAndRunInOrder()
    {
        var firstResult = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            AdditionalCharacter = new("second-role", "第二个角色", null, IasCharacterSource.IasPackage, DateTimeOffset.UnixEpoch, true),
            Remove = (id, _) => id == "character" ? firstResult.Task : Task.FromResult(new IasOperationResult(true, "已卸载", []))
        };
        var notifications = new NotificationService();
        var tasks = new BackgroundTaskService();
        var messages = new MessageCenterService(notifications, tasks);
        var queue = new IasOperationQueue(tasks, service);
        var dialogs = new DialogStub(true);
        using var page = CreatePage(service, dialogs, notifications, queue);
        await page.EnterAsync();
        var first = page.Characters[0];
        var second = page.Characters[1];
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => { if (!queue.HasWork) idle.TrySetResult(); };

        page.RemoveCharacterCommand.Execute(first);
        try
        {
            Assert.True(page.RemoveCharacterCommand.CanExecute(second));
            page.RemoveCharacterCommand.Execute(second);

            Assert.Equal(2, dialogs.Confirmations);
            Assert.Equal([first.Id], service.RemoveCalls);
            Assert.Equal(first.Name, page.CurrentQueueItem?.Name);
            var waiting = Assert.Single(page.WaitingQueue);
            Assert.Equal(second.Name, waiting.Name);
            Assert.Equal(BackgroundTaskStatus.Queued, waiting.Task.Status);
            Assert.Equal(2, messages.ActiveTasks.Count);
            var firstMessage = Assert.Single(messages.ActiveTasks, item => item.Title == $"卸载 IAS 角色：{first.Name}");
            var secondMessage = Assert.Single(messages.ActiveTasks, item => item.Title == $"卸载 IAS 角色：{second.Name}");
            Assert.Same(page.CurrentQueueItem?.Task, firstMessage.Task);
            Assert.Same(waiting.Task, secondMessage.Task);
        }
        finally
        {
            firstResult.SetResult(new(true, "已卸载", []));
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([first.Id, second.Id], service.RemoveCalls);
        Assert.All(tasks.Tasks, task => Assert.Equal(BackgroundTaskStatus.Completed, task.Status));
        Assert.Empty(page.Characters);
    }

    [Fact]
    public async Task RoleToggleQueuesBehindRunningOperationWithoutConfirmation()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4") { Add = (_, _, _) => pending.Task, FinishPrimaryAction = true };
        var dialogs = new DialogStub(true);
        var tasks = new BackgroundTaskService();
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, dialogs, queue: queue);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        var active = page.HandleWindowDropAsync([AppContext.BaseDirectory]);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => { if (!queue.HasWork) idle.TrySetResult(); };
        Task toggle;
        try
        {
            Assert.True(page.IsBusy);
            Assert.True(page.CanToggleCharacter);
            toggle = page.SetCharacterEnabledAsync(row, false);

            var waiting = Assert.Single(page.WaitingQueue);
            Assert.Equal("停用 IAS 角色", waiting.Operation);
            Assert.Equal(0, dialogs.Confirmations);
            Assert.Empty(service.ToggleCalls);
            Assert.False(row.IsChecked);
        }
        finally
        {
            pending.SetResult(new(true, "已添加", []));
        }
        await Task.WhenAll(active, toggle, idle.Task).WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50 && page.Characters[0].Enabled != false; i++) await Task.Delay(20);

        Assert.Equal([(row.Id, false)], service.ToggleCalls);
        Assert.False(Assert.Single(page.Characters).IsChecked);
        Assert.False(page.Characters[0].Enabled);
    }

    [Fact]
    public async Task RoleToggleIsRejectedWhileSameRoleIsQueuedAndSnapsBack()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4") { Add = (_, _, _) => pending.Task };
        var notifications = new NotificationService();
        var notices = new List<string>();
        notifications.Published += (_, item) => notices.Add(item.Message);
        var queue = new IasOperationQueue(new BackgroundTaskService(), service);
        using var page = CreatePage(service, new DialogStub(true), notifications, queue);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        var active = page.HandleWindowDropAsync([AppContext.BaseDirectory]);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => { if (!queue.HasWork) idle.TrySetResult(); };
        try
        {
            page.RemoveCharacterCommand.Execute(row);
            await page.SetCharacterEnabledAsync(row, false);

            var waiting = Assert.Single(page.WaitingQueue);
            Assert.Equal("卸载 IAS 角色", waiting.Operation);
            Assert.True(row.IsChecked);
            Assert.Contains(notices, message => message.Contains("正在处理或等待"));
            page.CancelWaitingCommand.Execute(waiting);
        }
        finally
        {
            pending.SetResult(new(true, "已添加", []));
            await Task.WhenAll(active, idle.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(service.ToggleCalls);
        Assert.Empty(service.RemoveCalls);
    }

    [Fact]
    public async Task CancellingRoleRemovalConfirmationDoesNotEnqueue()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4");
        var dialogs = new DialogStub(null);
        var tasks = new BackgroundTaskService();
        using var page = CreatePage(service, dialogs, queue: new IasOperationQueue(tasks, service));
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);

        page.RemoveCharacterCommand.Execute(row);

        Assert.Equal(1, dialogs.Confirmations);
        Assert.False(page.HasQueue);
        Assert.Empty(tasks.Tasks);
        Assert.Empty(service.RemoveCalls);
        Assert.Same(row, Assert.Single(page.Characters));
    }

    [Fact]
    public async Task RepeatedRoleRemovalOnlyQueuesOnce()
    {
        var previous = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removal = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            Add = (_, _, _) => previous.Task,
            Remove = (_, _) =>
            {
                removalStarted.SetResult();
                return removal.Task;
            }
        };
        var dialogs = new DialogStub(true);
        var notifications = new NotificationService();
        var duplicateNotices = new List<string>();
        notifications.Published += (_, item) => duplicateNotices.Add(item.Message);
        var tasks = new BackgroundTaskService();
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, dialogs, notifications, queue);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        var active = page.HandleWindowDropAsync([AppContext.BaseDirectory]);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => { if (!queue.HasWork) idle.TrySetResult(); };
        try
        {
            page.RemoveCharacterCommand.Execute(row);
            page.RemoveCharacterCommand.Execute(row);
            Assert.Single(page.WaitingQueue);
            Assert.Empty(service.RemoveCalls);
            Assert.Single(duplicateNotices);

            previous.SetResult(new(true, "已添加", []));
            await removalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("卸载 IAS 角色", page.CurrentQueueItem?.Operation);
            page.RemoveCharacterCommand.Execute(row);

            Assert.Empty(page.WaitingQueue);
            Assert.Equal(1, dialogs.Confirmations);
            Assert.Equal(2, tasks.Tasks.Count);
            Assert.Equal(2, duplicateNotices.Count);
            Assert.All(duplicateNotices, message => Assert.Contains("正在处理或等待", message));
        }
        finally
        {
            previous.TrySetResult(new(true, "已添加", []));
            removal.SetResult(new(true, "已卸载", []));
            await Task.WhenAll(active, idle.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([row.Id], service.RemoveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingRoleRemovalCanBeCancelledFromPageOrMessageCenter(bool fromMessageCenter)
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4") { Add = (_, _, _) => pending.Task };
        var notifications = new NotificationService();
        var tasks = new BackgroundTaskService();
        var messages = new MessageCenterService(notifications, tasks);
        using var page = CreatePage(service, new DialogStub(true), notifications, new IasOperationQueue(tasks, service));
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        var active = page.HandleWindowDropAsync([AppContext.BaseDirectory]);

        try
        {
            page.RemoveCharacterCommand.Execute(row);
            var waiting = Assert.Single(page.WaitingQueue);
            var message = Assert.Single(messages.ActiveTasks, item => item.Title == $"卸载 IAS 角色：{row.Name}");
            Assert.Same(waiting.Task, message.Task);
            Assert.True(message.CanCancel);

            if (fromMessageCenter) message.Task!.Cancel();
            else page.CancelWaitingCommand.Execute(waiting);

            Assert.Equal(BackgroundTaskStatus.Canceled, waiting.Task.Status);
            Assert.Empty(page.WaitingQueue);
            Assert.DoesNotContain(message, messages.ActiveTasks);
            Assert.Equal("添加 IAS 角色", page.CurrentQueueItem?.Operation);
            Assert.False(active.IsCompleted);
        }
        finally
        {
            pending.SetResult(new(true, "已添加", []));
            await active.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(service.RemoveCalls);
        Assert.Same(row, Assert.Single(page.Characters));
    }

    [Fact]
    public async Task QueuedRoleRemovalChecksWhetherTheGameStartedBeforeExecution()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4") { Add = (_, _, _) => pending.Task };
        var queue = new IasOperationQueue(new BackgroundTaskService(), service);
        using var page = CreatePage(service, new DialogStub(true), queue: queue);
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        var active = page.HandleWindowDropAsync([AppContext.BaseDirectory]);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => { if (!queue.HasWork) idle.TrySetResult(); };
        IasQueuedOperation waiting;
        try
        {
            page.RemoveCharacterCommand.Execute(row);
            waiting = Assert.Single(page.WaitingQueue);
            service.GameRunning = true;
        }
        finally
        {
            pending.SetResult(new(true, "已添加", []));
            await Task.WhenAll(active, idle.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(BackgroundTaskStatus.Failed, waiting.Task.Status);
        Assert.Empty(service.RemoveCalls);
        Assert.Same(row, Assert.Single(page.Characters));
    }

    [Fact]
    public async Task MultiSelectedLibraryModsQueueOneItemEachAfterOneConfirmation()
    {
        var calls = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IasVpkTargets? seenTargets = null;
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            EnabledInActiveProfile = true,
            AddFromLibraryAsync2 = async (id, targets) =>
            {
                calls.Add(id);
                seenTargets = targets;
                if (id == "a") await first.Task;
                return new(true, "已从库添加", []);
            },
        };
        var unavailable = new IasLibraryCandidate("role", "已是角色", null, "普通 Mod") { UnavailableReason = "已经是 IAS 角色" };
        var dialogs = new DialogStub(true)
        {
            Selected = [new("a", "甲", null, "普通 Mod"), new("b", "乙", null, "求生 VPK") { Source = IasLibrarySource.L4d2Vpk },
                unavailable, new("a", "甲（重复）", null, "普通 Mod")]
        };
        var notifications = new NotificationService();
        var tasks = new BackgroundTaskService();
        var messages = new MessageCenterService(notifications, tasks);
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, dialogs, notifications, queue);
        await page.EnterAsync();

        page.ChooseLibraryModCommand.Execute(null);

        Assert.Equal(1, dialogs.LibraryDialogs);
        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal("把 2 个 Mod 加入队列，转换为 IAS 角色并安装？", dialogs.LastConfirmation!.Message);
        Assert.Equal("甲", page.CurrentQueueItem?.Name);
        Assert.Equal(["乙"], page.WaitingQueue.Select(item => item.Name));
        Assert.Equal(2, messages.ActiveTasks.Count(item => item.Title.StartsWith("从 Mod 库添加 IAS 角色：")));
        Assert.NotNull(seenTargets);

        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Changed += (_, _) => { if (!queue.HasWork) idle.TrySetResult(); };
        first.SetResult();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public async Task SingleSelectedLibraryModConfirmationNamesTheMod()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            EnabledInActiveProfile = true,
            AddFromLibrary = _ => new(true, "已从库添加", []),
        };
        var dialogs = new DialogStub(true) { Selected = [new("a", "甲", null, "普通 Mod")] };
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        page.ChooseLibraryModCommand.Execute(null);

        Assert.Equal("把「甲」加入队列，转换为 IAS 角色并安装？", dialogs.LastConfirmation!.Message);
    }

    [Fact]
    public async Task CancellingLibraryDialogQueuesNothing()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4") { EnabledInActiveProfile = true };
        var dialogs = new DialogStub(true) { Selected = null };
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        page.ChooseLibraryModCommand.Execute(null);

        Assert.Equal(1, dialogs.LibraryDialogs);
        Assert.Equal(0, dialogs.Confirmations);
        Assert.Empty(page.WaitingQueue);
        Assert.Null(page.CurrentQueueItem);
    }

    [Fact]
    public async Task QueueSurvivesPageDisposalAndRebuiltPageShowsSameTasks()
    {
        using var drops = new DropFiles();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            Add = async (paths, _, token) =>
            {
                if (Assert.Single(paths) == drops.First) await first.Task;
                else
                {
                    secondStarted.TrySetResult();
                    await second.Task;
                }
                token.ThrowIfCancellationRequested();
                return new(true, "已添加", []);
            },
        };
        var tasks = new BackgroundTaskService();
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, new DialogStub(true), queue: queue);
        await page.EnterAsync();
        var current = page.HandleWindowDropAsync([drops.First]);
        var waiting = page.HandleWindowDropAsync([drops.Second]);
        page.Leave();
        page.Dispose();

        using var rebuilt = CreatePage(service, queue: queue);
        await rebuilt.EnterAsync();
        Assert.Same(tasks.Tasks[0], rebuilt.CurrentQueueItem?.Task);
        Assert.Same(tasks.Tasks[1], Assert.Single(rebuilt.WaitingQueue).Task);
        Assert.False(rebuilt.CanToggleIas);
        rebuilt.Leave();

        first.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskStatus.Completed, tasks.Tasks[0].Status);
        Assert.Equal(BackgroundTaskStatus.Running, tasks.Tasks[1].Status);
        second.SetResult();
        await Task.WhenAll(current, waiting).WaitAsync(TimeSpan.FromSeconds(5));
        await rebuilt.EnterAsync();
        Assert.Empty(rebuilt.WaitingQueue);
        Assert.Null(rebuilt.CurrentQueueItem);
        Assert.True(rebuilt.CanToggleIas);
        Assert.All(tasks.Tasks, task => Assert.Equal(BackgroundTaskStatus.Completed, task.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationUsesTheTaskTokenAndCloseWaitsForRollback(bool closingManager)
    {
        using var drops = new DropFiles();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken executionToken = default;
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            Add = async (paths, _, token) =>
            {
                if (Assert.Single(paths) != drops.First) return new(true, "已添加", []);
                executionToken = token;
                using var registration = token.Register(() => cancelled.TrySetResult());
                await release.Task;
                token.ThrowIfCancellationRequested();
                return new(true, "已添加", []);
            },
        };
        var tasks = new BackgroundTaskService();
        var notifications = new NotificationService();
        var messages = new MessageCenterService(notifications, tasks);
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, new DialogStub(true), notifications, queue);
        await page.EnterAsync();
        var current = page.HandleWindowDropAsync([drops.First]);
        var waiting = page.HandleWindowDropAsync([drops.Second]);
        page.Leave();
        var running = Assert.Single(messages.ActiveTasks, item => item.Task?.Status == BackgroundTaskStatus.Running);
        Assert.Equal(executionToken, running.Task!.CancellationToken);
        Assert.True(running.CanCancel);
        Task drain;
        if (closingManager) drain = queue.StopAsync();
        else
        {
            running.Task.Cancel();
            drain = current;
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted);
        Assert.False(running.CanCancel);
        release.SetResult();
        await Task.WhenAll(current, waiting, drain).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskStatus.Canceled, running.Task.Status);
        Assert.Equal(closingManager ? 1 : 2, service.AddCalls);
        if (closingManager)
        {
            Assert.All(messages.Messages, item => Assert.Contains("因关闭管理器取消", item.CopyText));
            Assert.All(tasks.Tasks, task => Assert.Equal(BackgroundTaskStatus.Canceled, task.Status));
        }
        else Assert.Equal(BackgroundTaskStatus.Completed, tasks.Tasks[1].Status);
    }

    [Fact]
    public async Task RunningQueueShowsTheSameProgressInThePageAndMessageCenter()
    {
        using var drops = new DropFiles();
        var reports = new TaskCompletionSource<IProgress<IasPageProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            Add = (_, progress, _) =>
            {
                reports.TrySetResult(progress);
                return release.Task;
            },
        };
        var tasks = new BackgroundTaskService();
        var notifications = new NotificationService();
        var messages = new MessageCenterService(notifications, tasks);
        var queue = new IasOperationQueue(tasks, service);
        using var page = CreatePage(service, new DialogStub(true), notifications, queue);
        await page.EnterAsync();
        var work = page.HandleWindowDropAsync([drops.First]);
        var message = Assert.Single(messages.ActiveTasks);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        message.Task!.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BackgroundTaskItem.Progress)) published.TrySetResult();
        };
        var progress = await reports.Task.WaitAsync(TimeSpan.FromSeconds(5));
        progress.Report(new("正在转换", 42, "准备护甲", true));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(page.CurrentQueueItem?.Task, message.Task);
        Assert.Contains("正在转换 · 准备护甲", message.Detail);
        Assert.Equal(message.Task.Stage, page.ProgressDetail);
        Assert.Equal(.42, message.Progress);
        Assert.Equal(42, page.ProgressValue);
        release.SetResult(new(true, "已添加", []));
        await work.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static readonly IasTargetChoice ArmorChoice = new("DP-9 测试护甲", "中甲", [new(9, "0000000000000009", "测试护甲")]);
    private static readonly IasTargetChoice HelmetChoice = new("DP-9 测试头盔", "中甲",
        [new(10, "000000000000000a", "测试头盔", GameDataEquipmentKitKind.Helmet)]);

    [Fact]
    public async Task MissingOriginalBlocksQueueUntilAChoiceIsMade()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            StandardTargets = new(new("原护甲", 1, [ArmorChoice]), new(null, null, [HelmetChoice])),
            Convert = (armor, helmet, merge) =>
            {
                Assert.Equal("0x00000001", armor);
                Assert.Equal("0x0000000a", helmet);
                Assert.False(merge);
                return new(true, "已转换", []);
            },
        };
        var tasks = new BackgroundTaskService();
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs, queue: new(tasks, service));
        await page.EnterAsync();
        var row = Assert.Single(page.Characters);
        page.ConvertCharacterCommand.Execute(row);
        var panel = Assert.IsType<IasConversionPanelViewModel>(row.Conversion);
        Assert.True(panel.Helmet.NeedsSelection);
        page.StartConversionCommand.Execute(row);
        Assert.Empty(tasks.Tasks);
        Assert.Contains("头盔", panel.Message);
        panel.Helmet.SelectCommand.Execute(HelmetChoice);
        page.StartConversionCommand.Execute(row);
        Assert.Equal(BackgroundTaskStatus.Completed, Assert.Single(tasks.Tasks).Status);
        Assert.Equal(0, dialogs.Confirmations);
    }

    [Fact]
    public async Task VpkSettingsRestoreAndInvalidSelectionFailsInTheQueue()
    {
        using var drops = new DropFiles();
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4");
        var tasks = new BackgroundTaskService();
        var queue = new IasOperationQueue(tasks, service);
        IasVpkTargets? saved = new("removed", HelmetChoice.Primary!.ArchiveId);
        service.AddWithTargets = targets => targets?.Error is { } error ? new(false, error, []) : new(true, "已添加", []);
        var dialogs = new DialogStub(null);
        using (var page = new IasPageViewModel(service, dialogs, new(), _ => null, _ => { }, queue, saved, value => saved = value))
        {
            await page.EnterAsync();
            Assert.True(page.VpkArmor.NeedsSelection);
            Assert.Contains("已失效", page.VpkTargetWarning);
            await page.HandleWindowDropAsync([drops.Vpk]);
            Assert.Equal(BackgroundTaskStatus.Failed, Assert.Single(tasks.Tasks).Status);
            page.VpkArmor.SelectCommand.Execute(ArmorChoice);
            Assert.Null(page.VpkTargetWarning);
            Assert.Equal(ArmorChoice.Primary!.ArchiveId, saved!.Body);
        }
        Assert.Equal(0, dialogs.Confirmations);
        using var rebuilt = new IasPageViewModel(service, new DialogStub(true), new(), _ => null, _ => { }, queue, saved);
        await rebuilt.EnterAsync();
        Assert.Equal(ArmorChoice.Name, rebuilt.VpkArmor.SelectionText);
        Assert.Equal(HelmetChoice.Name, rebuilt.VpkHelmet.SelectionText);
        await rebuilt.HandleWindowDropAsync([drops.Second]);
        Assert.Equal(BackgroundTaskStatus.Completed, tasks.Tasks.Last().Status);
    }

    [Fact]
    public async Task LoadingTheVpkCatalogLeavesDropAvailableAndCloseWaitsForCleanup()
    {
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            LoadVpkTargets = async token =>
            {
                using var registration = token.Register(() => cancellation.TrySetResult());
                await release.Task;
                token.ThrowIfCancellationRequested();
                return new([ArmorChoice], [HelmetChoice]);
            },
        };
        using var page = CreatePage(service);
        await page.EnterAsync();
        Assert.True(page.CanAcceptDrop);
        Assert.Contains("正在读取", page.VpkTargetWarning);
        var closing = page.StopPageOperationForCloseAsync();
        await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        release.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProgressNotificationsOnlyRefreshProgressBindings()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var primaryChanges = 0;
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            Install = (progress, _) =>
            {
                changes.Clear();
                primaryChanges = 0;
                progress.Report(new("正在下载…", 62, "测试", true));
                return pending.Task;
            },
        };
        using var page = CreatePage(service);
        await page.EnterAsync();
        page.PrimaryCommand.CanExecuteChanged += (_, _) => Interlocked.Increment(ref primaryChanges);
        page.PropertyChanged += (_, args) =>
        {
            changes.Enqueue(args.PropertyName);
            if (args.PropertyName == nameof(page.CanCancel)) applied.TrySetResult();
        };
        var operation = page.ExecutePrimaryAsync();
        try
        {
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(string.Empty, changes);
            Assert.Contains(nameof(page.ProgressDetail), changes);
            Assert.Equal(0, primaryChanges);
        }
        finally
        {
            pending.SetResult(new(true, "完成", []));
            await operation;
        }
    }

    [Fact]
    public async Task OperationFailureShowsBackendMessageAndWarningsAfterRefresh()
    {
        var notifications = new NotificationService();
        var service = new ServiceStub(IasStatusKind.NotInstalled)
        {
            Install = (_, _) => Task.FromResult(new IasOperationResult(false, "部署失败。", ["文件被占用。"])),
        };
        using var page = CreatePage(service, notifications: notifications);
        await page.EnterAsync();

        await page.ExecutePrimaryAsync();

        Assert.True(page.HasResult);
        Assert.False(page.ResultSuccess);
        Assert.Equal("部署失败。（1 条提示）", page.ResultText);
        Assert.Equal("文件被占用。", Assert.Single(page.ResultWarnings));
        var notice = Assert.Single(notifications.History);
        Assert.Equal("部署失败。", notice.Message);
        Assert.Equal(NotificationLevel.Error, notice.Level);
        Assert.Equal(2, service.StatusReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UninstallPassesConfirmationCheckboxOrSkipsOnCancel(bool? checkedValue)
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4");
        var dialogs = new DialogStub(checkedValue);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        await page.UninstallAsync();

        Assert.Equal("同时删除已导入的 1 个角色", dialogs.LastConfirmation?.CheckBoxLabel);
        Assert.Equal(checkedValue, service.RemoveCharacters);
        Assert.Equal(checkedValue.HasValue ? 1 : 0, service.UninstallCalls);
    }

    [Fact]
    public async Task LeavingCancelsRefreshAndReentryStartsAnother()
    {
        var blocked = new TaskCompletionSource<IasPageStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken refreshToken = default;
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4")
        {
            ReadStatus = (read, ct) =>
            {
                if (read != 1) return Task.FromResult(Status(IasStatusKind.Installed, "1.6.4") with { EnabledInActiveProfile = true });
                refreshToken = ct;
                return blocked.Task;
            },
        };
        using var page = CreatePage(service);
        var firstEntry = page.EnterAsync();

        page.Leave();
        await page.EnterAsync();
        Assert.True(refreshToken.IsCancellationRequested);
        Assert.Equal(1, service.StatusReads);

        blocked.SetResult(Status(IasStatusKind.Error, null));
        await firstEntry;
        Assert.Equal("已安装 1.6.4", page.StatusText);
        Assert.Equal(2, service.StatusReads);
        Assert.Equal(1, service.CharacterReads);
    }

    [Fact]
    public async Task LeavingDoesNotCancelInstallation()
    {
        var pending = new TaskCompletionSource<IasOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        var service = new ServiceStub(IasStatusKind.NotInstalled)
        {
            Install = (_, ct) =>
            {
                operationToken = ct;
                return pending.Task;
            },
        };
        using var page = CreatePage(service);
        await page.EnterAsync();
        var operation = page.ExecutePrimaryAsync();

        page.Leave();
        Assert.False(operationToken.IsCancellationRequested);
        await page.EnterAsync();
        Assert.Equal(1, service.StatusReads);

        pending.SetResult(new(true, "已安装 IAS 1.6.4。", []));
        await operation;
        Assert.Equal(2, service.StatusReads);
    }

    [Theory]
    [InlineData(IasStatusKind.NotInstalled, null, false, false)]
    [InlineData(IasStatusKind.Installed, "1.6.4", false, false)]
    [InlineData(IasStatusKind.GameMismatch, "1.6.4", true, false)]
    [InlineData(IasStatusKind.Installed, "1.6.4", true, true)]
    public async Task GraphicsControlsRequireInstalledAndEnabledIas(IasStatusKind kind, string? version, bool enabled, bool canUse)
    {
        var service = new ServiceStub(kind, version) { EnabledInActiveProfile = enabled };
        using var page = CreatePage(service);
        await page.EnterAsync();
        Assert.Equal(canUse, page.CanToggleReShade);
        Assert.False(page.CanToggleDlss);
        await page.SetDlssAsync(IasDlssMode.FrameGeneration);
        Assert.Equal(0, service.DlssCalls);
    }

    [Fact]
    public async Task FirstReShadeConfirmationCanBeCancelledAndIsRememberedAfterAcceptance()
    {
        var service = new ServiceStub(IasStatusKind.Installed, "1.6.4");
        var dialogs = new DialogStub(null);
        using var page = CreatePage(service, dialogs);
        await page.EnterAsync();

        await page.SetReShadeAsync(true);
        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal("我知道了，打开", dialogs.LastConfirmation?.ConfirmLabel);
        Assert.Equal("取消", dialogs.LastConfirmation?.CancelLabel);
        Assert.Contains("反作弊误判", dialogs.LastConfirmation!.Message);
        Assert.False(page.ReShadeEnabled);
        Assert.Empty(service.ReShadeCalls);
        Assert.False(service.GraphicsRiskAcknowledged);

        dialogs.Answer = false;
        await page.SetReShadeAsync(true);
        Assert.True(page.ReShadeEnabled);
        Assert.True(page.CanToggleDlss);
        await page.SetDlssAsync(IasDlssMode.NeuralRendering);
        Assert.True(page.DlssNeuralRendering);
        await page.SetReShadeAsync(false);
        Assert.False(page.CanToggleDlss);
        Assert.True(page.DlssOff);
        Assert.Contains("同时关闭 DLSS", page.ResultText);

        using var reopened = CreatePage(service, dialogs);
        await reopened.EnterAsync();
        await reopened.SetReShadeAsync(true);
        Assert.True(reopened.ReShadeEnabled);
        Assert.Equal(2, dialogs.Confirmations);
        Assert.Equal(new[] { (true, true), (false, false), (true, false) }, service.ReShadeCalls);
    }

    [Fact]
    public async Task GraphicsControlsAreDisabledDuringAnotherOperation()
    {
        var pending = new TaskCompletionSource<IasOperationResult>();
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            Install = (_, _) => pending.Task,
            Graphics = new(true, IasDlssMode.FrameGeneration)
        };
        using var page = CreatePage(service);
        await page.EnterAsync();
        Assert.True(page.CanToggleDlss);
        var update = page.ExecutePrimaryAsync();
        Assert.False(page.CanToggleReShade);
        Assert.False(page.CanToggleDlss);
        await page.SetReShadeAsync(false);
        Assert.Empty(service.ReShadeCalls);
        pending.SetResult(new(false, "更新失败。", []));
        await update;
        Assert.True(page.CanToggleReShade);
        Assert.True(page.DlssFrameGeneration);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(null, 0)]
    public async Task DownloadFailureOffersSettingsForAnotherSource(bool? answer, int expectedOpens)
    {
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            Install = (_, _) => Task.FromResult(new IasOperationResult(false, "更新 IAS失败：连接超时。", []) { DownloadFailed = true })
        };
        var dialogs = new DialogStub(answer);
        var opens = 0;
        using var page = new IasPageViewModel(service, dialogs, new NotificationService(), _ => null, _ => { },
            new IasOperationQueue(new BackgroundTaskService(), service), openSettings: () => opens++);
        await page.EnterAsync();

        await page.ExecutePrimaryAsync();

        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal("去设置", dialogs.LastConfirmation!.ConfirmLabel);
        Assert.Contains("设置 → 程序更新 → 下载来源", dialogs.LastConfirmation.Message);
        Assert.Equal(expectedOpens, opens);
    }

    [Fact]
    public async Task OrdinaryFailureDoesNotOfferDownloadSource()
    {
        var service = new ServiceStub(IasStatusKind.UpdateAvailable, "1.6.4")
        {
            Install = (_, _) => Task.FromResult(new IasOperationResult(false, "更新失败。", []))
        };
        var dialogs = new DialogStub(true);
        var opens = 0;
        using var page = new IasPageViewModel(service, dialogs, new NotificationService(), _ => null, _ => { },
            new IasOperationQueue(new BackgroundTaskService(), service), openSettings: () => opens++);
        await page.EnterAsync();

        await page.ExecutePrimaryAsync();

        Assert.Equal(0, dialogs.Confirmations);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void DownloadFailureDetectsNetworkErrorsInsideWrappers()
    {
        Assert.True(IasDownloadFailure.Is(new HttpRequestException("下载 IAS 运行库失败")));
        Assert.True(IasDownloadFailure.Is(new InvalidOperationException("包装", new TimeoutException("超时"))));
        Assert.True(IasDownloadFailure.Is(new AggregateException(new IOException("x"),
            new System.Net.Sockets.SocketException())));
        Assert.False(IasDownloadFailure.Is(new InvalidDataException("NuGet 包的 SHA-512 不符。")));
        Assert.False(IasDownloadFailure.Is(new IasGameRunningException()));
        Assert.False(IasDownloadFailure.Is(null));
    }

    private static IasPageViewModel CreatePage(ServiceStub service, DialogStub? dialogs = null,
        NotificationService? notifications = null, IasOperationQueue? queue = null)
        => new(service, dialogs ?? new DialogStub(null), notifications ?? new NotificationService(), _ => null, _ => { },
            queue ?? new IasOperationQueue(new BackgroundTaskService(), service));

    private static IasPageStatus Status(IasStatusKind kind, string? version)
        => new(kind, version, "1.6.5", 249 * 1024 * 1024, null, "https://example.com/releases", "连接超时。", "游戏版本信息。");

    private sealed class DropFiles : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ias-queue-test-").FullName;
        public string First => Path.Combine(_root, "First.zip");
        public string Second => Path.Combine(_root, "Second.zip");
        public string Cancelled => Path.Combine(_root, "Cancelled.zip");
        public string Vpk => Path.Combine(_root, "Source.vpk");

        public DropFiles()
        {
            foreach (var path in new[] { First, Second, Cancelled })
            {
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            }
            File.WriteAllBytes(Vpk, []);
        }

        public void Dispose() => IasFileTree.Delete(_root);
    }

    private sealed class DialogStub(bool? answer) : IIasPageDialogs
    {
        public IReadOnlyList<IasLibraryCandidate>? Selected { get; init; }
        public int LibraryDialogs { get; private set; }
        public bool? Answer { get; set; } = answer;
        public int Confirmations { get; private set; }
        public IasConfirmation? LastConfirmation { get; private set; }
        public bool? Confirm(IasConfirmation confirmation)
        {
            LastConfirmation = confirmation;
            Confirmations++;
            return Answer;
        }
        public IReadOnlyList<IasLibraryCandidate>? ChooseLibraryMods(
            Func<CancellationToken, Task<IReadOnlyList<IasLibraryCandidate>>> loadCandidates, CancellationToken ct)
        {
            LibraryDialogs++;
            return Selected;
        }
    }

    private sealed class ServiceStub(IasStatusKind kind, string? version = null) : IIasModuleService
    {
        public IasRetargetAnalysis? ArmorChoices { get; init; }
        public Func<string, string, IasOperationResult>? Retarget { get; init; }
        public Task<IasRetargetAnalysis> GetRetargetChoicesAsync(string id, CancellationToken ct) =>
            Task.FromResult(ArmorChoices ?? new IasRetargetAnalysis(null, null, []));
        public Task<IasOperationResult> RetargetAsync(string id, IasRetargetChoice choice, IProgress<IasPageProgress> progress, CancellationToken ct) =>
            Task.FromResult(Retarget?.Invoke(id, choice.Archive) ?? new IasOperationResult(false, "无法换护甲。", []));
        public Func<string, Task<IasOperationResult>>? Adopt { get; init; }
        public List<string> AdoptCalls { get; } = [];
        public Task<IasOperationResult> AdoptLibraryRoleAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            AdoptCalls.Add(modId);
            return Adopt?.Invoke(modId) ?? Task.FromResult(new IasOperationResult(true, "已登记并启用。", []));
        }
        public Func<CancellationToken, Task<IReadOnlyList<IasCharacterInfo>>>? ReadCharacters { get; init; }
        public bool GameRunning { get; set; }
        public int RecoveryCalls { get; private set; }
        public bool IsGameRunning() => GameRunning;
        public Task<bool> RecoverPendingAsync(CancellationToken ct)
        {
            RecoveryCalls++;
            return Task.FromResult(false);
        }

        public Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>>? Install { get; init; }
        public Task<IasOperationResult>? Toggle { get; init; }
        public Func<int, CancellationToken, Task<IasPageStatus>>? ReadStatus { get; init; }
        public bool EnabledInActiveProfile { get; set; } = kind is IasStatusKind.Installed or IasStatusKind.UpdateAvailable ||
            kind == IasStatusKind.FeedUnavailable && version is not null;
        public bool PhysicsOn { get; set; }
        public bool BuiltInLuaOn { get; set; }
        public bool CharacterEnabled { get; set; } = true;
        public IasCharacterInfo? AdditionalCharacter { get; init; }
        public List<string> RemoveCalls { get; } = [];
        public List<(string Id, bool Enabled)> ToggleCalls { get; } = [];
        public Func<string, CancellationToken, Task<IasOperationResult>>? Remove { get; set; }
        private readonly HashSet<string> _removedCharacters = [];
        public IasGraphicsState Graphics { get; set; } = new(false, IasDlssMode.Off);
        public bool GraphicsRiskAcknowledged { get; private set; }
        public List<(bool Enabled, bool Acknowledge)> ReShadeCalls { get; } = [];
        public int DlssCalls { get; private set; }
        public (bool Physics, bool Lua) LastTurnOff { get; private set; }
        public bool MappingInUse { get; init; }
        public bool FinishPrimaryAction { get; init; }
        public int AddCalls { get; private set; }
        public Func<IReadOnlyList<string>, IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>>? Add { get; init; }
        public Func<string?, string?, bool, IasOperationResult>? Convert { get; init; }
        public Func<IasVpkTargets?, IasOperationResult>? AddWithTargets { get; set; }
        public Func<CancellationToken, Task<IasVpkTargetCatalog>>? LoadVpkTargets { get; init; }
        public IasStandardTargetCatalog StandardTargets { get; init; } =
            new(new("原护甲", 1, [ArmorChoice]), new("原头盔", 2, [HelmetChoice]));
        public Func<string, IasOperationResult>? AddFromLibrary { get; init; }
        public Func<string, IasVpkTargets?, Task<IasOperationResult>>? AddFromLibraryAsync2 { get; init; }
        public int StatusReads { get; private set; }
        public int CharacterReads { get; private set; }
        public int InstallCalls { get; private set; }
        public int TurnOffCalls { get; private set; }
        public int UninstallCalls { get; private set; }
        public bool? RemoveCharacters { get; private set; }

        public Task<IasPageStatus> GetStatusAsync(CancellationToken ct)
        {
            StatusReads++;
            return ReadStatus?.Invoke(StatusReads, ct) ?? Task.FromResult(Status(kind, version) with
            {
                EnabledInActiveProfile = EnabledInActiveProfile,
                PhysicsOn = PhysicsOn,
                BuiltInLuaOn = BuiltInLuaOn,
                MappingInUse = MappingInUse,
                Graphics = version is null ? null : Graphics,
                HasGraphicsPackages = version is not null,
                GraphicsRiskAcknowledged = GraphicsRiskAcknowledged,
            });
        }

        public Task<IReadOnlyList<IasCharacterInfo>> GetCharactersAsync(CancellationToken ct)
        {
            CharacterReads++;
            if (ReadCharacters is not null) return ReadCharacters(ct);
            var characters = new List<IasCharacterInfo>();
            if (!_removedCharacters.Contains("character"))
                characters.Add(new("character", "Milfy SR-64", null, IasCharacterSource.IasPackage, DateTimeOffset.UnixEpoch, CharacterEnabled));
            if (AdditionalCharacter is { } extra && !_removedCharacters.Contains(extra.Id))
                characters.Add(extra);
            return Task.FromResult<IReadOnlyList<IasCharacterInfo>>(characters);
        }

        public Task<IReadOnlyList<IasLibraryCandidate>> GetConvertibleLibraryModsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IasLibraryCandidate>>([]);
        public Task<IasVpkTargetCatalog> GetVpkTargetCatalogAsync(CancellationToken ct) =>
            LoadVpkTargets?.Invoke(ct) ?? Task.FromResult(new IasVpkTargetCatalog([ArmorChoice], [HelmetChoice]));
        public Task<IasStandardTargetCatalog> GetStandardTargetCatalogAsync(string characterId, CancellationToken ct) =>
            Task.FromResult(StandardTargets);
        public IasDropClassification Classify(IReadOnlyList<string> paths) => throw new NotSupportedException();
        public Task<IasOperationResult> InstallOrUpdateAsync(IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            InstallCalls++;
            return Install?.Invoke(progress, ct) ?? CompletePrimaryAction();
        }

        public Task<IasOperationResult> UninstallAsync(bool removeCharacters, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            UninstallCalls++;
            RemoveCharacters = removeCharacters;
            return Task.FromResult(new IasOperationResult(true, "已卸载 IAS。", []));
        }

        public async Task<IasOperationResult> SetEnabledInActiveProfileAsync(bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            var result = Toggle is not null ? await Toggle : new(FinishPrimaryAction, "已处理 IAS 开关。", []);
            if (result.Success) EnabledInActiveProfile = enabled;
            return result;
        }

        public async Task<IasOperationResult> SetCharacterEnabledAsync(string characterId, bool enabled, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            ToggleCalls.Add((characterId, enabled));
            var result = Toggle is not null ? await Toggle : new(FinishPrimaryAction, "已处理角色开关。", []);
            if (result.Success) CharacterEnabled = enabled;
            return result;
        }

        public Task<IasOperationResult> SetReShadeAsync(bool enabled, bool acknowledgeRisk, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            ReShadeCalls.Add((enabled, acknowledgeRisk));
            GraphicsRiskAcknowledged |= acknowledgeRisk;
            var removedDlss = !enabled && Graphics.Dlss != IasDlssMode.Off;
            Graphics = new(enabled, enabled ? Graphics.Dlss : IasDlssMode.Off);
            return Task.FromResult(new IasOperationResult(true, removedDlss ? "已同时关闭 DLSS。" : "ReShade 已更新。", []));
        }

        public Task<IasOperationResult> SetDlssAsync(IasDlssMode mode, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            DlssCalls++;
            Graphics = Graphics with { Dlss = mode };
            return Task.FromResult(new IasOperationResult(true, "DLSS 已更新。", []));
        }

        private Task<IasOperationResult> CompletePrimaryAction()
        {
            if (!FinishPrimaryAction) return Task.FromResult(new IasOperationResult(false, "部署失败。", []));
            kind = IasStatusKind.Installed;
            version = "1.6.5";
            EnabledInActiveProfile = true;
            return Task.FromResult(new IasOperationResult(true, "已部署 IAS。", []));
        }

        public Task<IasOperationResult> TurnOffManagerFeaturesAsync(bool physics, bool builtInLua, CancellationToken ct)
        {
            TurnOffCalls++;
            LastTurnOff = (physics, builtInLua);
            if (physics) PhysicsOn = false;
            if (builtInLua) BuiltInLuaOn = false;
            return Task.FromResult(new IasOperationResult(true, "已关掉管理器物理。", []));
        }
        public Task<IasOperationResult> AddAsync(IReadOnlyList<string> paths, IProgress<IasPageProgress> progress, CancellationToken ct,
            IasVpkTargets? vpkTargets = null)
        {
            AddCalls++;
            return Add?.Invoke(paths, progress, ct) ?? Task.FromResult(AddWithTargets?.Invoke(vpkTargets) ?? new IasOperationResult(true, "已添加", []));
        }
        public Task<IasOperationResult> AddFromLibraryAsync(string modId, IProgress<IasPageProgress> progress, CancellationToken ct,
            IasVpkTargets? vpkTargets = null) =>
            AddFromLibraryAsync2 is { } gated ? gated(modId, vpkTargets)
                : Task.FromResult((AddFromLibrary ?? throw new NotSupportedException())(modId));
        public async Task<IasOperationResult> RemoveCharacterAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct)
        {
            RemoveCalls.Add(characterId);
            var result = Remove is null ? new IasOperationResult(true, "已卸载", []) : await Remove(characterId, ct);
            if (result.Success) _removedCharacters.Add(characterId);
            return result;
        }
        public Task<IasOperationResult> ConvertToStandardModAsync(string characterId, IProgress<IasPageProgress> progress, CancellationToken ct,
            string? targetArmor = null, string? targetHelmet = null, bool mergeHelmet = false) =>
            Task.FromResult((Convert ?? throw new NotSupportedException())(targetArmor, targetHelmet, mergeHelmet));
    }
}
