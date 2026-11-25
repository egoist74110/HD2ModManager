using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using HD2ModCore.Ias;
using HD2ModManager.Services;
using HD2ModManager.Services.Ias;

namespace HD2ModManager.ViewModels;

public sealed class IasPageViewModel : FullWorkspacePageViewModel
{
    private const string DropPrompt = "把 IAS Mod、普通护甲 Mod 或求生 VPK 拖到窗口任意位置";
    private static readonly IasConfirmation PhysicsConfirmation = new("关掉管理器物理？",
        "IAS 接管潜兵身上的护甲，和带物理的普通 Mod 会冲突。NPC 不受影响。关闭管理器物理后，管理器物理 Mod 不再生效。以后可以在设置里重新打开。",
        "关掉物理", CancelLabel: "先不关");
    private static readonly IasConfirmation LuaConfirmation = new("关掉管理器内置 Lua？",
        "IAS 启用时，也建议关掉管理器的内置 Lua。以后可以在设置里重新打开。",
        "关掉内置 Lua", CancelLabel: "先不关");
    private static readonly IasConfirmation CombinedConfirmation = new("关掉管理器的物理和内置 Lua？",
        "IAS 接管潜兵身上的护甲，和带物理的普通 Mod 会冲突。NPC 不受影响。关闭管理器物理后，管理器物理 Mod 不再生效。也建议关掉管理器的内置 Lua。以后可以在设置里重新打开这两项。",
        "全部关掉", CancelLabel: "先不关");
    private static readonly IasConfirmation GraphicsRiskConfirmation = new("打开 ReShade 滤镜？",
        "ReShade 和 DLSS 是 IAS 作者提供的可选/实验功能，会注入游戏画面；联网游戏存在被反作弊误判的可能，出问题请关闭。",
        "我知道了，打开");
    private readonly IIasModuleService _service;
    private readonly IIasPageDialogs _dialogs;
    private readonly NotificationService _notifications;
    private readonly IasOperationQueue _queue;
    private readonly Func<string, string?> _findModName;
    private readonly Action<string> _openMod;
    private readonly Action<IasVpkTargets>? _saveVpkTargets;
    private IasVpkTargets? _savedVpkTargets;
    private IasVpkTargets _currentVpkTargets = new(null, null);
    private IasCharacterRow? _expandedCharacter;
    private CancellationTokenSource? _loadTargets;
    private CancellationTokenSource? _loadVpkTargets;
    private bool _vpkTargetsResolved;
    private Task _vpkCatalogTask = Task.CompletedTask;
    private string? _vpkTargetError;
    private IasPageStatus _status = new(IasStatusKind.Checking, null, null, null, null,
        IasModuleService.OfficialReleaseUrl, null, null);
    private bool _active;
    private bool _disposed;
    private bool _refreshPending;
    private bool _recoveryPending;
    private DispatcherTimer? _gameProcessTimer;
    private bool _offerConflictsAfterRefresh;
    private bool? _pendingProfileEnabled;
    private bool _isBusy;
    private BackgroundTaskItem? _observedTask;
    private bool _observedQueue;
    private bool _confirmingQueueItem;
    private string[]? _hoverPaths;
    private bool _hoverHasSupportedFile;
    private string? _windowDropHint;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _chooseLibrary;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationCompletion;
    private CancellationTokenSource? _resultExpiry;
    private IasPageProgress? _progress;
    private IasOperationResult? _result;
    private bool _areWarningsExpanded;
    private IReadOnlyList<IasCharacterRow> _characters = [];

    public IasPageViewModel(IIasModuleService service, IIasPageDialogs dialogs, NotificationService notifications,
        Func<string, string?> findModName, Action<string> openMod, IasOperationQueue queue,
        IasVpkTargets? savedVpkTargets = null, Action<IasVpkTargets>? saveVpkTargets = null)
    {
        _service = service;
        _dialogs = dialogs;
        _notifications = notifications;
        _queue = queue;
        _findModName = findModName;
        _openMod = openMod;
        _savedVpkTargets = savedVpkTargets;
        _saveVpkTargets = saveVpkTargets;
        VpkArmor = new("护甲", choice => SaveVpkTarget(false, choice));
        VpkHelmet = new("头盔", choice => SaveVpkTarget(true, choice));
        Title = "IAS 综合护甲系统";

        PrimaryCommand = new(_ => ExecutePrimaryAsync(), _ => CanWrite && PrimaryActionText is not null);
        UninstallCommand = new(_ => UninstallAsync(), _ => CanWrite && HasInstalledVersion);
        TurnOffPhysicsCommand = new(_ => ConfirmTurnOffPhysicsAsync(), _ => CanWrite && ShowTurnOffPhysics);
        TurnOffLuaCommand = new(_ => ConfirmTurnOffLuaAsync(), _ => CanWrite && ShowTurnOffLua);
        ChooseLibraryModCommand = new(_ => ChooseLibraryModAsync(), _ => CanChooseLibraryMod);
        ConvertCharacterCommand = new(character => ConvertAsync(character as IasCharacterRow), CanQueueConversion);
        CloseConversionCommand = new(_ => CloseConversion());
        StartConversionCommand = new(character => StartConversionAsync(character as IasCharacterRow), CanQueueConversion);
        RemoveCharacterCommand = new(character => RemoveAsync(character as IasCharacterRow), CanQueueConversion);
        CancelCommand = new(_ => CancelOperation(), _ => CanCancel);
        CancelWaitingCommand = new(value =>
        {
            if (value is IasQueuedOperation { Task.Status: BackgroundTaskStatus.Queued } item) item.Task.Cancel();
        });
        OpenOfficialPageCommand = new(_ => OpenOfficialPage(), _ => CanAct);
        OpenCreatedModCommand = new(_ => OpenCreatedMod(), _ => CanAct && CanOpenCreatedMod);
        ToggleReShadeCommand = new(_ => SetReShadeAsync(!ReShadeEnabled), _ => CanToggleReShade);
        SelectDlssCommand = new(value => SetDlssAsync((IasDlssMode)value!),
            value => value is IasDlssMode mode && CanSelectDlss(mode));
        RepairGraphicsCommand = new(_ => RunOperationAsync(_service.InstallOrUpdateAsync), _ => CanWrite && IsReady && ShowGraphicsRepair);
    }

    public AsyncRelayCommand PrimaryCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand TurnOffPhysicsCommand { get; }
    public AsyncRelayCommand TurnOffLuaCommand { get; }
    public AsyncRelayCommand ChooseLibraryModCommand { get; }
    public AsyncRelayCommand ConvertCharacterCommand { get; }
    public RelayCommand CloseConversionCommand { get; }
    public AsyncRelayCommand StartConversionCommand { get; }
    public IasTargetPickerViewModel VpkArmor { get; }
    public IasTargetPickerViewModel VpkHelmet { get; }
    public string? VpkTargetWarning => _loadVpkTargets is not null && !_vpkTargetsResolved ? "正在读取 IAS 底子装备…"
        : _vpkTargetError ?? (VpkArmor.NeedsSelection || VpkHelmet.NeedsSelection
            ? $"底子{(VpkArmor.NeedsSelection && VpkHelmet.NeedsSelection ? "护甲和头盔" : VpkArmor.NeedsSelection ? "护甲" : "头盔")} 在当前 IAS 或游戏中已失效，请重选。"
            : null);
    public AsyncRelayCommand RemoveCharacterCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CancelWaitingCommand { get; }
    public RelayCommand OpenOfficialPageCommand { get; }
    public RelayCommand OpenCreatedModCommand { get; }
    public AsyncRelayCommand ToggleReShadeCommand { get; }
    public AsyncRelayCommand SelectDlssCommand { get; }
    public AsyncRelayCommand RepairGraphicsCommand { get; }

    public IasQueuedOperation? CurrentQueueItem => _queue.Current;
    public IReadOnlyList<IasQueuedOperation> WaitingQueue => _queue.Waiting;
    private bool HasCurrentQueueItem => CurrentQueueItem is not null;
    public bool HasWaitingQueue => _queue.HasWaiting;
    public bool HasQueue => _queue.HasWork;
    public bool IsBusy => _isBusy || HasQueue || _queue.IsPaused;
    private bool CanQueue => _active && !_disposed && !_queue.IsStopping && !GameRunning && !_confirmingQueueItem;
    private bool CanAct => _active && !_disposed && !_queue.IsStopping && !IsBusy;
    private bool CanWrite => CanAct && !GameRunning;
    public bool GameRunning { get; private set; }
    public string GameRunningNotice => IasGameRunningException.Notice;
    public bool ShowIasToggle => HasInstalledVersion && _status.Kind is
        IasStatusKind.Installed or IasStatusKind.UpdateAvailable or IasStatusKind.FeedUnavailable or IasStatusKind.NotInProfile;
    public bool CanToggleIas => CanWrite && ShowIasToggle;
    public bool IasEnabled
    {
        get => _pendingProfileEnabled ?? _status.EnabledInActiveProfile;
        set => _ = SetIasEnabledAsync(value);
    }
    private bool IsDisabledInProfile => ShowIasToggle && !_status.EnabledInActiveProfile;
    public bool IsReady => ShowIasToggle && _status.EnabledInActiveProfile;
    // 角色开关走 IAS 队列：队列里有操作在跑时照样能拨，排在后面执行；
    // 但总开关/画面/卸载 IAS 这类独占操作进行中仍锁住（它们可能改变 IAS 是否可用）。
    public bool CanToggleCharacter => CanQueue && _operation is null && IsReady;
    public bool CanAcceptDrop => CanQueue && IsReady;
    private bool CanChooseLibraryMod => CanAcceptDrop && _chooseLibrary is null;
    public bool ReShadeEnabled => _status.Graphics?.ReShadeEnabled == true;
    public IasDlssMode DlssMode => _status.Graphics?.Dlss ?? IasDlssMode.Off;
    public bool DlssOff => DlssMode == IasDlssMode.Off;
    public bool DlssFrameGeneration => DlssMode == IasDlssMode.FrameGeneration;
    public bool DlssNeuralRendering => DlssMode == IasDlssMode.NeuralRendering;
    public bool CanToggleReShade => CanWrite && IsReady && _status.Graphics is not null && (_status.HasGraphicsPackages || ReShadeEnabled);
    public bool CanToggleDlss => CanWrite && IsReady && ReShadeEnabled;
    public bool ShowGraphicsRepair => HasInstalledVersion && !_status.HasGraphicsPackages && _status.Graphics is not null;
    public string? GraphicsError => _status.GraphicsError;
    public bool HasGraphicsError => GraphicsError is not null;
    private bool CanSelectDlss(IasDlssMode mode) => CanToggleDlss &&
        (mode == IasDlssMode.Off || _status.HasGraphicsPackages && Enum.IsDefined(mode));
    public bool HasInstalledVersion => !string.IsNullOrWhiteSpace(_status.InstalledVersion);
    public string InstalledCompatibility => IasModuleState.IsVerifiedVersion(_status.InstalledVersion)
        ? $"IAS {_status.InstalledVersion} · 已验证" : $"IAS {_status.InstalledVersion} · 未经验证的新版本";
    public bool ShowConflictWarning => HasInstalledVersion && _status.EnabledInActiveProfile;
    public bool ShowTurnOffPhysics => ShowConflictWarning && _status.PhysicsOn;
    public bool ShowTurnOffLua => ShowConflictWarning && _status.BuiltInLuaOn;
    public bool ShowMappingWarning => ShowConflictWarning && _status.MappingInUse;
    public IReadOnlyList<IasCharacterRow> Characters => _characters;
    public string CharacterTitle => $"角色（{Characters.Count}）";
    public double DropHeight => Characters.Count == 0 ? 180 : 72;
    public bool IsWindowDropHighlighted => _windowDropHint is not null;
    public string DropText => _windowDropHint ?? DropStatusText;
    private string DropStatusText => _status.Kind == IasStatusKind.Checking ? "正在检查 IAS 状态…"
        : GameRunning ? GameRunningNotice
        : IsReady ? IsBusy ? "IAS 正在处理，可以继续拖入排队" : DropPrompt
        : IsDisabledInProfile ? "先打开 IAS" : _status.Kind switch
    {
        IasStatusKind.GameMismatch => "游戏更新后 IAS 暂不可用，等 IAS 更新后再添加角色",
        IasStatusKind.MissingMdl3 => "先把 MDL3 加进当前配置",
        IasStatusKind.Error when HasInstalledVersion => "先重试检查 IAS 状态",
        _ => "先安装 IAS 才能添加角色"
    };
    public string? ReleaseNotes => _status.ReleaseNotes;
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
    public bool ShowDownloadSource => !ShowProgress && _status.Kind is IasStatusKind.NotInstalled or IasStatusKind.UpdateAvailable;
    private double? CurrentPercent => CurrentQueueItem?.Task.Progress * 100 ?? _progress?.Percent;
    public bool ShowProgress => _progress is not null || HasCurrentQueueItem;
    public bool CanCancel => CurrentQueueItem?.Task.CanCancel ??
        (_progress?.CanCancel == true && _operation?.IsCancellationRequested == false);
    public bool IsIndeterminate => CurrentPercent is null;
    public double ProgressValue => CurrentPercent is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    public string? ProgressText => IsIndeterminate ? null : $"{ProgressValue:0}%";
    public string? ProgressDetail => CurrentQueueItem?.Task.Stage ?? _progress?.Detail;
    public bool HasResult => _result is not null && !ShowProgress;
    public bool ResultSuccess => _result?.Success == true;
    public IReadOnlyList<string> ResultWarnings => _result?.Warnings ?? [];
    public bool HasResultWarnings => ResultWarnings.Count > 0;
    public bool AreWarningsExpanded { get => _areWarningsExpanded; set => SetField(ref _areWarningsExpanded, value); }
    public string? ResultText => HasResultWarnings ? $"{_result?.Message}（{ResultWarnings.Count} 条提示）" : _result?.Message;
    public bool CanOpenCreatedMod => _result is { Success: true, CreatedModId: { } id } && _findModName(id) is not null;

    public string StatusColor => ShowProgress ? "Blue" : IsDisabledInProfile ? "Gray" : _status.Kind switch
    {
        IasStatusKind.Installed => "Green",
        IasStatusKind.UpdateAvailable => "Blue",
        IasStatusKind.GameMismatch or IasStatusKind.MissingMdl3 or IasStatusKind.NotInProfile => "Yellow",
        IasStatusKind.Error => "Red",
        _ => "Gray",
    };

    public string StatusText => CurrentQueueItem?.Task.Name ?? _progress?.Title ?? (IsDisabledInProfile ? "IAS 已停用（当前配置）" : _status.Kind switch
    {
        IasStatusKind.Checking => "正在检查…",
        IasStatusKind.NotInstalled => "IAS 未安装",
        IasStatusKind.Installed => $"已安装 {_status.InstalledVersion}",
        IasStatusKind.UpdateAvailable => $"有新版本 {_status.LatestVersion}",
        IasStatusKind.GameMismatch => "游戏已更新，IAS 尚未适配",
        IasStatusKind.MissingMdl3 => "缺少前置 MDL3",
        IasStatusKind.FeedUnavailable => HasInstalledVersion ? $"已安装 {_status.InstalledVersion}" : "IAS 未安装",
        _ => "出错了",
    });

    public string? StatusDetail => ShowProgress ? null : IsDisabledInProfile
        ? "IAS 和角色都已从当前配置撤下，部件和角色都还在，打开开关即可恢复。" : _status.Kind switch
    {
        IasStatusKind.NotInstalled => $"将从 IAS 官方 GitHub 下载约 {FormatSize(_status.DownloadBytes)}，默认仅启用护甲部件。",
        IasStatusKind.Installed => "已是最新版本。",
        IasStatusKind.UpdateAvailable => $"当前 {_status.InstalledVersion}。",
        IasStatusKind.GameMismatch => $"等 IAS 作者发布新版本后再更新。{_status.Detail}",
        IasStatusKind.MissingMdl3 => "先在首页把 MDL3 加进当前配置。",
        IasStatusKind.FeedUnavailable => $"暂时连不上 IAS 官方源：{_status.Message}",
        IasStatusKind.Error => _status.Message,
        _ => null,
    };
    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(StatusDetail);

    public string? PrimaryActionText => ShowProgress ? null : _status.Kind switch
    {
        IasStatusKind.NotInstalled => "安装 IAS",
        IasStatusKind.UpdateAvailable => "更新",
        IasStatusKind.FeedUnavailable or IasStatusKind.Error => "重试",
        _ => null,
    };
    public bool HasPrimaryAction => PrimaryActionText is not null;

    public Task EnterAsync()
    {
        if (_active || _disposed) return Task.CompletedTask;
        _active = true;
        _queue.Changed += OnQueueChanged;
        _queue.ResultPublished += OnQueueResult;
        GameRunning = _service.IsGameRunning();
        if (Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher)
            _gameProcessTimer = new(TimeSpan.FromSeconds(3), DispatcherPriority.Background, OnGameProcessTick, dispatcher);
        _recoveryPending = true;
        _refreshPending = true;
        ObserveQueue();
        NotifyState();
        return RefreshPendingAsync();
    }

    public void Leave()
    {
        if (!_active) return;
        _active = false;
        _queue.Changed -= OnQueueChanged;
        _queue.ResultPublished -= OnQueueResult;
        ObserveTask(null);
        _gameProcessTimer?.Stop();
        _gameProcessTimer = null;
        ClearWindowDrop();
        _refreshPending = false;
        _refresh?.Cancel();
        _chooseLibrary?.Cancel();
        _loadVpkTargets?.Cancel();
        CloseConversion();
        NotifyState();
    }

    private async void OnGameProcessTick(object? sender, EventArgs e)
    {
        var running = _service.IsGameRunning();
        if (running == GameRunning) return;
        GameRunning = running;
        ClearWindowDrop();
        NotifyState();
        if (!running && _recoveryPending)
        {
            _refreshPending = true;
            await RefreshPendingAsync();
        }
    }

    public override void Dispose()
    {
        _disposed = true;
        Leave();
        _operation?.Cancel();
        ClearResult();
    }

    public Task StopPageOperationForCloseAsync()
    {
        var current = _operationCompletion?.Task ?? Task.CompletedTask;
        var catalog = _vpkCatalogTask;
        Dispose();
        return Task.WhenAll(current, catalog);
    }

    public bool UpdateWindowDrop(string[] paths)
    {
        if (_hoverPaths is null || !_hoverPaths.SequenceEqual(paths, StringComparer.Ordinal))
        {
            _hoverPaths = paths;
            _hoverHasSupportedFile = paths.Any(IasPackageDetector.MayAccept);
        }
        var accept = CanAcceptDrop && _hoverHasSupportedFile;
        SetWindowDropHint(!CanAcceptDrop ? DropStatusText
            : !accept ? "不支持这些文件" : "松开后加入 IAS 队列");
        return accept;
    }

    public void ClearWindowDrop()
    {
        _hoverPaths = null;
        SetWindowDropHint(null);
    }

    private void SetWindowDropHint(string? hint)
    {
        if (_windowDropHint == hint) return;
        _windowDropHint = hint;
        OnPropertyChanged(nameof(DropText));
        OnPropertyChanged(nameof(IsWindowDropHighlighted));
    }

    public Task HandleWindowDropAsync(string[] paths)
    {
        var accept = UpdateWindowDrop(paths);
        ClearWindowDrop();
        if (!accept) return Task.CompletedTask;
        var inputs = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var name = string.Join("、", inputs.Select(path => Path.GetFileName(path) is { Length: > 0 } leaf ? leaf : path));
        var service = _service;
        var targets = _vpkTargetsResolved ? _currentVpkTargets with { Error = VpkTargetWarning }
            : _savedVpkTargets ?? new IasVpkTargets(null, null);
        return QueueAsync("添加 IAS 角色", name, inputs.Select(path => "file:" + path).ToArray(),
            // 拖入直接排队，不弹确认；有问题由识别/转换/安装结果在消息中心报告。
            null,
            (progress, ct) => service.AddAsync(inputs, progress, ct, targets));
    }

    public Task ExecutePrimaryAsync()
    {
        if (!CanWrite) return Task.CompletedTask;
        return _status.Kind switch
        {
            IasStatusKind.NotInstalled or IasStatusKind.UpdateAvailable => RunOperationAsync(_service.InstallOrUpdateAsync, offerConflicts: true),
            IasStatusKind.FeedUnavailable or IasStatusKind.Error => RetryAsync(),
            _ => Task.CompletedTask,
        };
    }

    public async Task SetIasEnabledAsync(bool enabled)
    {
        if (!CanToggleIas || enabled == IasEnabled) return;
        _pendingProfileEnabled = enabled;
        try
        {
            await RunOperationAsync((progress, ct) => _service.SetEnabledInActiveProfileAsync(enabled, progress, ct),
                offerConflicts: enabled);
        }
        finally
        {
            _pendingProfileEnabled = null;
            NotifyState();
        }
    }

    public async Task SetCharacterEnabledAsync(IasCharacterRow character, bool enabled)
    {
        if (!CanToggleCharacter || !Characters.Contains(character) || character.Enabled is null || character.Enabled == enabled) return;
        var id = character.Id;
        var service = _service;
        var succeeded = false;
        // 排队期间开关先显示目标位置；成功后保持到列表刷新换上新行，失败/取消/没排上才弹回。
        character.ShowPendingEnabled(enabled);
        try
        {
            await QueueAsync(enabled ? "启用 IAS 角色" : "停用 IAS 角色", character.Name, ["role:" + id], null,
                async (progress, ct) =>
                {
                    var result = await service.SetCharacterEnabledAsync(id, enabled, progress, ct);
                    succeeded = result.Success;
                    return result;
                });
        }
        finally
        {
            if (succeeded) _refreshPending = _active;
            else character.ShowPendingEnabled(null);
            NotifyState();
            _ = RefreshPendingAsync();
        }
    }

    public Task SetReShadeAsync(bool enabled)
        => CanToggleReShade && enabled != ReShadeEnabled ? RunExclusiveAsync(async () =>
        {
            var acknowledgeRisk = enabled && !_status.GraphicsRiskAcknowledged;
            if (acknowledgeRisk && !_dialogs.Confirm(GraphicsRiskConfirmation).HasValue) return;
            if (_active)
                await ExecuteOperationAsync((progress, ct) => _service.SetReShadeAsync(enabled, acknowledgeRisk, progress, ct));
        }) : Task.CompletedTask;

    public Task SetDlssAsync(IasDlssMode mode)
        => CanSelectDlss(mode) && mode != DlssMode
            ? RunOperationAsync((progress, ct) => _service.SetDlssAsync(mode, progress, ct))
            : Task.CompletedTask;

    public Task UninstallAsync()
        => CanWrite && HasInstalledVersion
            ? ConfirmAndRunAsync(new("卸载 IAS", "将移除 IAS 部件，并从所有配置撤下 IAS 和它的角色。角色文件默认保留，重装 IAS 后可恢复。", "卸载",
                $"同时删除已导入的 {Characters.Count} 个角色"), _service.UninstallAsync)
            : Task.CompletedTask;

    public Task ConfirmTurnOffPhysicsAsync()
        => ShowTurnOffPhysics ? RunExclusiveAsync(() => OfferTurnOffFeaturesAsync(true, false)) : Task.CompletedTask;

    public Task ConfirmTurnOffLuaAsync()
        => ShowTurnOffLua ? RunExclusiveAsync(() => OfferTurnOffFeaturesAsync(false, true)) : Task.CompletedTask;

    private async Task OfferTurnOffFeaturesAsync(bool physics, bool lua)
    {
        if (!_active || GameRunning || (!physics && !lua)) return;
        var confirmation = physics && lua ? CombinedConfirmation : physics ? PhysicsConfirmation : LuaConfirmation;
        if (_dialogs.Confirm(confirmation).HasValue && _active)
            await ExecuteOperationAsync((_, ct) => _service.TurnOffManagerFeaturesAsync(physics, lua, ct));
    }

    private async Task ConvertAsync(IasCharacterRow? character)
    {
        if (character is null || !CanQueueConversion(character)) return;
        var wasOpen = ReferenceEquals(character, _expandedCharacter);
        CloseConversion();
        if (wasOpen) return;
        _expandedCharacter = character;
        var panel = new IasConversionPanelViewModel { Message = "正在读取包里的原套和可用装备…" };
        character.Conversion = panel;
        using var cancellation = new CancellationTokenSource();
        _loadTargets = cancellation;
        try
        {
            var catalog = await _service.GetStandardTargetCatalogAsync(character.Id, cancellation.Token);
            if (!cancellation.IsCancellationRequested) panel.ShowCatalog(catalog);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { panel.Message = $"无法读取转换目标：{error.Message}"; }
        finally
        {
            if (ReferenceEquals(_loadTargets, cancellation)) _loadTargets = null;
        }
    }

    private void CloseConversion()
    {
        _loadTargets?.Cancel();
        if (_expandedCharacter is not null) _expandedCharacter.Conversion = null;
        _expandedCharacter = null;
    }

    private Task StartConversionAsync(IasCharacterRow? character)
    {
        if (character?.Conversion is not { } panel || !CanQueueConversion(character) ||
            !panel.TryGetTargets(out var armor, out var helmet)) return Task.CompletedTask;
        var id = character.Id;
        string[] keys = ["role:" + id];
        if (_queue.Contains(keys))
        {
            panel.Message = "这个角色已在队列中。";
            return Task.CompletedTask;
        }
        var merge = panel.MergeHelmet;
        CloseConversion();
        return _queue.Enqueue("转为普通 Mod", character.Name, keys,
            (progress, ct) => _service.ConvertToStandardModAsync(id, progress, ct, armor, helmet, merge));
    }

    private void ShowVpkTargets(IasVpkTargetCatalog catalog)
    {
        _vpkTargetError = null;
        _vpkTargetsResolved = true;
        var body = Bind(VpkArmor, catalog.Armor, _savedVpkTargets?.Body);
        var helmet = Bind(VpkHelmet, catalog.Helmet, _savedVpkTargets?.Helmet);
        _currentVpkTargets = new(body, helmet);
        if (catalog.Armor.Count == 0 || catalog.Helmet.Count == 0)
            _vpkTargetError = "当前 IAS 绑定目录与游戏护甲目录没有可用的底子护甲或头盔。";
        OnPropertyChanged(nameof(VpkTargetWarning));

        static string? Bind(IasTargetPickerViewModel picker, IReadOnlyList<IasTargetChoice> choices, string? saved)
        {
            var selected = saved is null ? choices.FirstOrDefault() : choices.FirstOrDefault(choice =>
                choice.Members.Any(member => string.Equals(member.ArchiveId, saved, StringComparison.OrdinalIgnoreCase)));
            picker.SetChoices(choices, selected, selected is null);
            return saved ?? selected?.Primary?.ArchiveId;
        }
    }

    private void SaveVpkTarget(bool helmet, IasTargetChoice choice)
    {
        if (choice.Primary is not { } selected) return;
        var saved = _savedVpkTargets ?? new IasVpkTargets(null, null);
        _savedVpkTargets = helmet ? saved with { Helmet = selected.ArchiveId } : saved with { Body = selected.ArchiveId };
        _currentVpkTargets = helmet ? _currentVpkTargets with { Helmet = selected.ArchiveId }
            : _currentVpkTargets with { Body = selected.ArchiveId };
        _saveVpkTargets?.Invoke(_savedVpkTargets);
        OnPropertyChanged(nameof(VpkTargetWarning));
    }

    private async Task RefreshVpkTargetsAsync()
    {
        _loadVpkTargets?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _loadVpkTargets = cancellation;
        OnPropertyChanged(nameof(VpkTargetWarning));
        try
        {
            if (HasInstalledVersion)
            {
                var catalog = await _service.GetVpkTargetCatalogAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                ShowVpkTargets(catalog);
            }
            else
            {
                ShowVpkTargets(new([], []));
                _vpkTargetError = "安装 IAS 后可选择求生 VPK 底子。";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (cancellation.IsCancellationRequested) return;
            ShowVpkTargets(new([], []));
            _vpkTargetError = error.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadVpkTargets, cancellation))
            {
                _loadVpkTargets = null;
                OnPropertyChanged(nameof(VpkTargetWarning));
            }
        }
    }

    // 卸载角色也走 IAS 队列：别的操作在跑时照样能点，排在后面依次执行。
    private Task RemoveAsync(IasCharacterRow? character)
    {
        if (character is null || !CanQueueConversion(character)) return Task.CompletedTask;
        var id = character.Id;
        var service = _service;
        return QueueAsync("卸载 IAS 角色", character.Name, ["role:" + id],
            new("卸载角色", $"卸载角色 {character.Name}？会从 IAS 和当前配置里移除。", "卸载"),
            (progress, ct) => service.RemoveCharacterAsync(id, progress, ct));
    }

    private bool CanQueueConversion(object? character) => CanQueue && ShowIasToggle &&
        character is IasCharacterRow row && Characters.Contains(row);

    private Task ConfirmAndRunAsync(IasConfirmation confirmation,
        Func<bool, IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> execute)
        => RunExclusiveAsync(async () =>
        {
            if (_dialogs.Confirm(confirmation) is bool answer && _active)
                await ExecuteOperationAsync((progress, ct) => execute(answer, progress, ct));
        });

    private async Task ChooseLibraryModAsync()
    {
        if (!CanChooseLibraryMod) return;
        var cancellation = new CancellationTokenSource();
        _chooseLibrary = cancellation;
        ChooseLibraryModCommand.RaiseCanExecuteChanged();
        IReadOnlyList<IasLibraryCandidate>? selected;
        try
        {
            // 对话框先打开，候选在对话框里异步加载（显示加载中），不占着界面线程。
            selected = CanAcceptDrop
                ? _dialogs.ChooseLibraryMods(_service.GetConvertibleLibraryModsAsync, cancellation.Token) : null;
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            PublishResult(new(false, exception.Message, []));
            return;
        }
        finally
        {
            _chooseLibrary = null;
            cancellation.Dispose();
            ChooseLibraryModCommand.RaiseCanExecuteChanged();
        }
        if (selected is not { Count: > 0 } || !CanAcceptDrop) return;
        var service = _service;
        var targets = _vpkTargetsResolved ? _currentVpkTargets with { Error = VpkTargetWarning }
            : _savedVpkTargets ?? new IasVpkTargets(null, null);
        await QueueManyAsync(selected.Where(item => item.IsConvertible).DistinctBy(item => item.ModId)
            .Select(item => new QueuedWork("从 Mod 库添加 IAS 角色", item.Name, ["library:" + item.ModId],
                (progress, ct) => service.AddFromLibraryAsync(item.ModId, progress, ct, targets))).ToArray(),
            work => new("转为 IAS 并安装", work.Count == 1
                ? $"把「{work[0].Name}」加入队列，转换为 IAS 角色并安装？"
                : $"把 {work.Count} 个 Mod 加入队列，转换为 IAS 角色并安装？", "加入队列"));
    }

    private readonly record struct QueuedWork(string Operation, string Name, string[] Keys,
        Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> Execute);

    private Task QueueAsync(string operation, string name, string[] keys,
        IasConfirmation? confirmation, Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> execute)
        => QueueManyAsync([new(operation, name, keys, execute)], _ => confirmation);

    // 一次确认、逐项各排一个队列项（每项一条消息中心记录）；已在队列里的项跳过并提示。
    private Task QueueManyAsync(IReadOnlyList<QueuedWork> requested, Func<IReadOnlyList<QueuedWork>, IasConfirmation?> confirmationFor)
    {
        if (!CanQueue) return Task.CompletedTask;
        var work = new List<QueuedWork>();
        foreach (var item in requested)
        {
            if (_queue.Contains(item.Keys))
                _notifications.Show($"「{item.Name}」包含正在处理或等待的输入。", NotificationLevel.Info, TimeSpan.FromSeconds(6), "IAS");
            else
                work.Add(item);
        }
        if (work.Count == 0) return Task.CompletedTask;

        Task EnqueueAll() => Task.WhenAll(work.Select(item => _queue.Enqueue(item.Operation, item.Name, item.Keys, item.Execute)));
        var confirmation = confirmationFor(work);
        if (confirmation is null) return EnqueueAll();

        _confirmingQueueItem = true;
        _queue.Pause();
        try
        {
            if (!_dialogs.Confirm(confirmation).HasValue || !_active || _disposed) return Task.CompletedTask;
            return EnqueueAll();
        }
        catch (Exception exception)
        {
            PublishResult(new(false, exception.Message, []));
            return Task.CompletedTask;
        }
        finally
        {
            _confirmingQueueItem = false;
            _queue.Resume();
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        ObserveQueue();
        NotifyState();
        _ = RefreshPendingAsync();
    }

    private void ObserveQueue()
    {
        if (_observedQueue && !HasQueue) _refreshPending = true;
        _observedQueue = HasQueue;
        ObserveTask(CurrentQueueItem?.Task);
    }

    private void ObserveTask(BackgroundTaskItem? task)
    {
        if (ReferenceEquals(_observedTask, task)) return;
        if (_observedTask is not null) _observedTask.PropertyChanged -= OnQueueTaskChanged;
        _observedTask = task;
        if (task is null) return;
        ClearResult();
        task.PropertyChanged += OnQueueTaskChanged;
    }

    private void OnQueueTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackgroundTaskItem.Stage)) OnPropertyChanged(nameof(ProgressDetail));
        if (e.PropertyName == nameof(BackgroundTaskItem.Progress))
        {
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(ProgressText));
        }
        if (e.PropertyName == nameof(BackgroundTaskItem.CanCancel)) NotifyCancel();
    }

    private void OnQueueResult(IasOperationResult result) => PublishResult(result, notify: false);

    private Task RunOperationAsync(Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> execute,
        bool offerConflicts = false)
        => RunExclusiveAsync(async () =>
        {
            if (await ExecuteOperationAsync(execute) && offerConflicts)
                _offerConflictsAfterRefresh = true;
        });

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (!CanWrite) return;
        _queue.Pause();
        _isBusy = true;
        NotifyState();
        try { await action(); }
        catch (Exception exception) { PublishResult(new(false, exception.Message, [])); }
        finally
        {
            _isBusy = false;
            try { await RefreshPendingAsync(allowPaused: true); }
            finally { _queue.Resume(); }
        }
    }

    private async Task<bool> ExecuteOperationAsync(Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> execute)
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationCompletion = completion;
        _operation = cancellation;
        ClearResult();
        _progress = new("正在处理…", null, null, false);
        NotifyState();
        var dispatchProgress = (IProgress<IasPageProgress>)new Progress<IasPageProgress>(value =>
        {
            if (!ReferenceEquals(_operation, cancellation)) return;
            var oldCanCancel = CanCancel;
            _progress = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ProgressDetail));
            if (CanCancel != oldCanCancel) NotifyCancel();
        });
        var progress = new IasOperationProgress(dispatchProgress.Report);
        try
        {
            var result = await execute(progress, cancellation.Token);
            PublishResult(result);
            return result.Success;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _operation = null;
            _operationCompletion = null;
            _progress = null;
            _refreshPending = _active;
            NotifyState();
            completion.TrySetResult();
        }
    }

    private void CancelOperation()
    {
        if (!CanCancel) return;
        if (CurrentQueueItem is { } item) item.Task.Cancel();
        else _operation?.Cancel();
        NotifyCancel();
    }

    private void NotifyCancel()
    {
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.RaiseCanExecuteChanged();
    }

    private Task RetryAsync()
    {
        ClearResult();
        _recoveryPending = true;
        _refreshPending = true;
        return RefreshPendingAsync();
    }

    private async Task RefreshPendingAsync(bool allowPaused = false)
    {
        if (!_active || _isBusy || !_refreshPending || HasCurrentQueueItem || _queue.IsStopping ||
            (_queue.IsPaused && !allowPaused)) return;
        _queue.Pause();
        _isBusy = true;
        try
        {
            // 快速离开再返回时，等上一轮刷新退出后再开始下一轮。
            while (_active && _refreshPending)
            {
                _refreshPending = false;
                using var cancellation = new CancellationTokenSource();
                _refresh = cancellation;
                NotifyState();
                try
                {
                    if (_recoveryPending && !GameRunning)
                    {
                        await _service.RecoverPendingAsync(cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        _recoveryPending = false;
                    }
                    var status = await _service.GetStatusAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var characters = await _service.GetCharactersAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    _status = status;
                    var oldRows = _characters.ToDictionary(row => row.Info);
                    _characters = characters.Select(item => oldRows.GetValueOrDefault(item) ??
                        new IasCharacterRow(item, SetCharacterEnabledAsync)).ToArray();
                    if (_expandedCharacter is not null && !_characters.Contains(_expandedCharacter)) CloseConversion();
                    var previousCatalog = _vpkCatalogTask;
                    var currentCatalog = RefreshVpkTargetsAsync();
                    _vpkCatalogTask = previousCatalog.IsCompletedSuccessfully ? currentCatalog : Task.WhenAll(previousCatalog, currentCatalog);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { continue; }
                catch (Exception exception)
                {
                    if (!cancellation.IsCancellationRequested)
                        _status = _status with { Kind = IasStatusKind.Error, Message = exception.Message };
                    continue;
                }
                finally { _refresh = null; }

                if (_offerConflictsAfterRefresh && _active)
                {
                    _offerConflictsAfterRefresh = false;
                    NotifyState();
                    try { await OfferTurnOffFeaturesAsync(ShowTurnOffPhysics, ShowTurnOffLua); }
                    catch (Exception exception) { PublishResult(new(false, exception.Message, [])); }
                }
            }
        }
        finally
        {
            _isBusy = false;
            _queue.Resume();
        }
    }

    private void PublishResult(IasOperationResult result, bool notify = true)
    {
        if (result is { Success: true, CreatedModId: { } id } && _findModName(id) is { } name)
            result = result with { Message = $"已放进 Mod 库：{name}" };
        if (notify)
            _notifications.Show(result.Message, result.Success ? NotificationLevel.Info : NotificationLevel.Error,
                TimeSpan.FromSeconds(6), "IAS");
        if (_disposed) return;
        ClearResult();
        _result = result;
        if (result.Success)
        {
            _resultExpiry = new CancellationTokenSource();
            _ = ExpireResultAsync(_resultExpiry);
        }
        NotifyState();
    }

    private async Task ExpireResultAsync(CancellationTokenSource expiry)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), expiry.Token);
            if (!ReferenceEquals(_resultExpiry, expiry)) return;
            _resultExpiry = null;
            _result = null;
            NotifyState();
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested) { }
        finally { expiry.Dispose(); }
    }

    private void ClearResult()
    {
        var expiry = _resultExpiry;
        _resultExpiry = null;
        expiry?.Cancel();
        _result = null;
        AreWarningsExpanded = false;
    }

    private void OpenCreatedMod()
    {
        if (CanAct && CanOpenCreatedMod && _result?.CreatedModId is { } id) _openMod(id);
    }

    private void OpenOfficialPage()
    {
        if (!CanAct) return;
        var url = Uri.TryCreate(_status.ReleaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.AbsoluteUri : IasModuleService.OfficialReleaseUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception exception) { PublishResult(new(false, exception.Message, [])); }
    }

    private void NotifyState()
    {
        OnPropertyChanged(string.Empty);
        PrimaryCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        TurnOffPhysicsCommand.RaiseCanExecuteChanged();
        TurnOffLuaCommand.RaiseCanExecuteChanged();
        ChooseLibraryModCommand.RaiseCanExecuteChanged();
        ConvertCharacterCommand.RaiseCanExecuteChanged();
        StartConversionCommand.RaiseCanExecuteChanged();
        RemoveCharacterCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenOfficialPageCommand.RaiseCanExecuteChanged();
        OpenCreatedModCommand.RaiseCanExecuteChanged();
        ToggleReShadeCommand.RaiseCanExecuteChanged();
        SelectDlssCommand.RaiseCanExecuteChanged();
        RepairGraphicsCommand.RaiseCanExecuteChanged();
    }

    private static string FormatSize(long? bytes) => bytes is { } value ? $"{value / (1024d * 1024):0.##} MB" : "未知大小";

}

public sealed class IasCharacterRow(IasCharacterInfo character, Func<IasCharacterRow, bool, Task> setEnabled) : INotifyPropertyChanged
{
    private bool? _pendingEnabled;
    private IasConversionPanelViewModel? _conversion;
    internal IasCharacterInfo Info => character;
    public IasConversionPanelViewModel? Conversion
    {
        get => _conversion;
        set
        {
            _conversion = value;
            PropertyChanged?.Invoke(this, new(nameof(Conversion)));
            PropertyChanged?.Invoke(this, new(nameof(IsConversionOpen)));
        }
    }
    public bool IsConversionOpen => Conversion is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => character.Id;
    public string Name => character.Name;
    public string? IconPath => character.IconPath;
    public bool? Enabled => character.Enabled;
    public bool IsDisabled => Enabled == false;
    public bool IsChecked
    {
        get => _pendingEnabled ?? Enabled ?? false;
        set => _ = setEnabled(this, value);
    }
    public string ToggleHint => IsChecked ? "停用这个角色" : "启用这个角色";

    internal void ShowPendingEnabled(bool? enabled)
    {
        _pendingEnabled = enabled;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    public string SourceText => character.Source switch
    {
        IasCharacterSource.IasPackage => "IAS Mod",
        IasCharacterSource.PatchMod => "由普通 Mod 转",
        IasCharacterSource.L4d2Vpk => "由求生 VPK 转",
        _ => string.Empty,
    };
}
