using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModManager.Services.Ias;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

/// <summary>「从库里选 Mod 转 IAS」：先打开、在里面异步加载候选，多选后返回勾选的全部候选。</summary>
public partial class IasLibraryPickerDialog : Window
{
    private readonly IasLibraryPickerViewModel _model = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<IasLibraryCandidate>>>? _load;
    private readonly CancellationTokenSource _loading = new();

    internal IasLibraryPickerDialog(Func<CancellationToken, Task<IReadOnlyList<IasLibraryCandidate>>>? load, CancellationToken ct)
    {
        InitializeComponent();
        DataContext = _model;
        _load = load;
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, this)) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 页面离开（取消令牌触发）时关掉对话框。
        var registration = ct.Register(() => Dispatcher.BeginInvoke(() => { if (IsLoaded) Close(); }));
        Closed += (_, _) =>
        {
            registration.Dispose();
            _loading.Cancel();
            _loading.Dispose();
        };
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadAsync();
        };
    }

    /// <summary>已有候选时直接展示（测试与截图用）。</summary>
    internal IasLibraryPickerDialog(IReadOnlyList<IasLibraryCandidate> candidates) : this(null, CancellationToken.None)
        => _model.SetCandidates(candidates);

    internal IasLibraryPickerViewModel Model => _model;
    internal IReadOnlyList<IasLibraryCandidate>? Result { get; private set; }

    private async Task LoadAsync()
    {
        if (_load is null) return;
        try
        {
            _model.SetCandidates(await _load(_loading.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _model.SetLoadFailed($"读取 Mod 库失败：{error.Message}");
        }
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!_model.CanConfirm) return;
        Result = _model.SelectedCandidates;
        DialogResult = true;
    }

    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IasLibraryPickerItem item } card) return;
        card.Focus();
        _model.Toggle(item);
    }

    // 方向键移动焦点、空格切换勾选；Enter 由默认按钮负责确认。
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        if (focused?.DataContext is IasLibraryPickerItem current && focused is Border)
        {
            switch (e.Key)
            {
                case Key.Space:
                    _model.Toggle(current);
                    e.Handled = true;
                    return;
                case Key.Left or Key.Right or Key.Up or Key.Down:
                    var next = Neighbor(current, e.Key);
                    if (next is not null) FocusCard(next);
                    e.Handled = true;
                    return;
            }
            return;
        }
        if (e.Key == Key.Down && ReferenceEquals(focused, SearchBox) && FirstItem() is { } first)
        {
            FocusCard(first);
            e.Handled = true;
        }
    }

    private IasLibraryPickerItem? FirstItem() => _model.CurrentItems.Concat(_model.OtherItems).FirstOrDefault();

    // 两组各自是两列网格：上下移动 ±2，到组边界进入相邻组的同一列。
    private IasLibraryPickerItem? Neighbor(IasLibraryPickerItem item, Key key)
    {
        var groups = new[] { _model.CurrentItems, _model.OtherItems }.Where(group => group.Count != 0).ToArray();
        var gi = Array.FindIndex(groups, group => group.Contains(item));
        if (gi < 0) return null;
        var group = groups[gi];
        var index = group.IndexOf(item);
        var column = index % 2;
        switch (key)
        {
            case Key.Left:
                return index > 0 ? group[index - 1] : gi > 0 ? groups[gi - 1][^1] : null;
            case Key.Right:
                return index < group.Count - 1 ? group[index + 1] : gi < groups.Length - 1 ? groups[gi + 1][0] : null;
            case Key.Down:
                if (index + 2 < group.Count) return group[index + 2];
                if (index / 2 < (group.Count - 1) / 2) return group[^1];
                return gi < groups.Length - 1 ? groups[gi + 1][Math.Min(column, groups[gi + 1].Count - 1)] : null;
            default:
                if (index >= 2) return group[index - 2];
                if (gi == 0) return null;
                var above = groups[gi - 1];
                return above[Math.Min(((above.Count - 1) / 2) * 2 + column, above.Count - 1)];
        }
    }

    private void FocusCard(IasLibraryPickerItem item)
    {
        foreach (var list in new[] { CurrentList, OtherList })
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container) continue;
            if (FindCard(container) is { } card)
            {
                card.Focus();
                card.BringIntoView();
            }
            return;
        }
    }

    private static Border? FindCard(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { Name: "Card" } card) return card;
            if (FindCard(child) is { } found) return found;
        }
        return null;
    }
}

/// <summary>单选分段按钮：IsChecked 绑定到枚举值（ConverterParameter 是目标枚举值）。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
