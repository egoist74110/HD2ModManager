using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HD2ModManager.Services.Ias;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

public partial class IasTargetPicker : UserControl
{
    public IasTargetPicker() => InitializeComponent();

    private IasTargetPickerViewModel? Picker => DataContext as IasTargetPickerViewModel;

    private void OnOpened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        // 等弹层布局完成后，把已选项滚到可见位置。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Picker?.Selected is { } selected) ChoiceList.ScrollIntoView(selected);
        });
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: IasTargetChoice choice })
        {
            Picker?.SelectCommand.Execute(choice);
            e.Handled = true;
        }
    }

    private void OnPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (Picker is not { } picker) return;
        switch (e.Key)
        {
            case Key.Escape:
                picker.IsOpen = false;
                PickerButton.Focus();
                e.Handled = true;
                break;
            case Key.Down:
                MoveFocus(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveFocus(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (Keyboard.FocusedElement is ListBoxItem { DataContext: IasTargetChoice focused })
                    picker.SelectCommand.Execute(focused);
                else if (picker.Query.Trim().Length > 0 &&
                         ChoiceList.Items.OfType<IasTargetChoice>().FirstOrDefault(choice => choice.Primary is not null) is { } first)
                    picker.SelectCommand.Execute(first);
                e.Handled = true;
                break;
        }
    }

    // 搜索框与列表行之间用焦点移动：Down 进列表，Up 从首行回搜索框。
    private void MoveFocus(int step)
    {
        var items = ChoiceList.Items;
        if (items.Count == 0) return;
        var current = Keyboard.FocusedElement as ListBoxItem;
        var index = current is null ? -1 : items.IndexOf(current.DataContext);
        var next = index + step;
        if (current is null && step < 0) return;
        if (next < 0)
        {
            SearchBox.Focus();
            return;
        }
        if (next >= items.Count) return;
        ChoiceList.ScrollIntoView(items[next]);
        ChoiceList.UpdateLayout();
        (ChoiceList.ItemContainerGenerator.ContainerFromIndex(next) as ListBoxItem)?.Focus();
    }
}
