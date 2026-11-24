using HD2ModCore.Ias;

namespace HD2ModManager.Services.Ias;

// Shell-owned. Queue mutations and task projections run on the UI context.
public sealed class IasOperationQueue(BackgroundTaskService tasks, IIasModuleService service)
{
    private const string ClosingReason = "因关闭管理器取消";
    private readonly List<IasQueuedOperation> _waiting = [];
    private int _pauseCount;

    public IasQueuedOperation? Current { get; private set; }
    public IReadOnlyList<IasQueuedOperation> Waiting => _waiting.ToArray();
    public bool HasWaiting => _waiting.Count > 0;
    public bool HasWork => Current is not null || HasWaiting;
    public bool IsPaused => _pauseCount > 0;
    public bool IsStopping { get; private set; }
    public event EventHandler? Changed;
    public event Action<IasOperationResult>? ResultPublished;

    public bool Contains(string[] keys) =>
        Current?.Keys.Intersect(keys, StringComparer.OrdinalIgnoreCase).Any() == true ||
        _waiting.Any(item => item.Keys.Intersect(keys, StringComparer.OrdinalIgnoreCase).Any());

    public Task Enqueue(string operation, string name, string[] keys,
        Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> execute)
    {
        if (IsStopping || Contains(keys)) return Task.CompletedTask;
        var task = tasks.Enqueue(BackgroundTaskKind.IasOperation, $"{operation}：{name}",
            origin: "IAS", presentation: BackgroundTaskPresentation.Foreground);
        var item = new IasQueuedOperation(operation, name, task, keys, execute);
        _waiting.Add(item);
        task.PropertyChanged += OnWaitingTaskChanged;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = DrainAsync();
        return item.Completion.Task;
    }

    public void Pause() => _pauseCount++;

    public void Resume()
    {
        if (--_pauseCount != 0) return;
        Changed?.Invoke(this, EventArgs.Empty);
        _ = DrainAsync();
    }

    public Task StopAsync()
    {
        if (!IsStopping)
        {
            IsStopping = true;
            foreach (var item in _waiting.ToArray()) item.Task.Cancel(ClosingReason);
            Current?.Task.Cancel(ClosingReason);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return Current?.Completion.Task ?? Task.CompletedTask;
    }

    private void OnWaitingTaskChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BackgroundTaskItem.Status) ||
            sender is not BackgroundTaskItem { Status: BackgroundTaskStatus.Canceled } task) return;
        var item = _waiting.Single(item => ReferenceEquals(item.Task, task));
        RemoveWaiting(item);
        Changed?.Invoke(this, EventArgs.Empty);
        item.Completion.TrySetResult();
    }

    private void RemoveWaiting(IasQueuedOperation item)
    {
        _waiting.Remove(item);
        item.Task.PropertyChanged -= OnWaitingTaskChanged;
    }

    private async Task DrainAsync()
    {
        while (!IsStopping && !IsPaused && Current is null && _waiting.Count > 0)
        {
            var item = _waiting[0];
            RemoveWaiting(item);
            Current = item;
            var task = item.Task;
            var token = task.CancellationToken;
            task.MarkRunning("正在处理…");
            Changed?.Invoke(this, EventArgs.Empty);
            // Check again on delivery in case queued dispatcher callbacks arrive together.
            var publishProgress = new IasOperationProgress(value =>
            {
                if (task.Status != BackgroundTaskStatus.Running || token.IsCancellationRequested) return;
                task.UpdateStage(string.IsNullOrWhiteSpace(value.Detail) ? value.Title : $"{value.Title} · {value.Detail}");
                task.UpdateProgress(value.Percent is double percent && double.IsFinite(percent) ? percent / 100 : null);
            });
            var dispatchProgress = (IProgress<IasPageProgress>)new Progress<IasPageProgress>(publishProgress.Report);
            var progress = new IasOperationProgress(dispatchProgress.Report);
            IasOperationResult? result = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (service.IsGameRunning()) throw new IasGameRunningException();
                result = await item.Execute(progress, token);
                if (result.Success) task.MarkCompleted(result.Message);
                else task.MarkFailed(result.Message);
            }
            catch (OperationCanceledException exception) when (token.IsCancellationRequested || exception.CancellationToken == token)
            {
                task.MarkCanceled(token.IsCancellationRequested ? "已取消" : exception.Message);
            }
            catch (Exception exception)
            {
                result = new(false, $"{item.Operation}失败：{exception.Message}", []);
                task.MarkFailed(result.Message);
            }
            finally
            {
                Current = null;
                try
                {
                    if (result is not null) ResultPublished?.Invoke(result);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                finally { item.Completion.TrySetResult(); }
            }
        }
    }
}

public sealed class IasQueuedOperation
{
    internal IasQueuedOperation(string operation, string name, BackgroundTaskItem task, string[] keys,
        Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> execute)
    {
        Operation = operation;
        Name = name;
        Task = task;
        Keys = keys;
        Execute = execute;
    }

    public string Operation { get; }
    public string Name { get; }
    public BackgroundTaskItem Task { get; }
    internal string[] Keys { get; }
    internal Func<IProgress<IasPageProgress>, CancellationToken, Task<IasOperationResult>> Execute { get; }
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class IasOperationProgress(Action<IasPageProgress> next) : IProgress<IasPageProgress>
{
    private readonly IasProgressThrottle<(string Title, string? Detail, double? Percent, bool CanCancel)> _tracker = new();

    public void Report(IasPageProgress value)
    {
        var percent = value.Percent is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : 0;
        if (_tracker.ShouldReport((value.Title, value.Detail, value.Percent is null ? null : Math.Round(percent), value.CanCancel)))
            next(value);
    }
}
