using System.Windows;
using System.Windows.Controls;
using HD2ModManager.Services.Ias;

namespace HD2ModManager.Views;

public partial class IasPageDialog : Window
{
    internal IasPageDialog(IasConfirmation confirmation) : this()
    {
        Title = confirmation.Title;
        ConfirmationPanel.Visibility = Visibility.Visible;
        ConfirmationText.Text = confirmation.Message;
        ConfirmationOption.Content = confirmation.CheckBoxLabel;
        ConfirmationOption.Visibility = confirmation.CheckBoxLabel is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmation.ConfirmLabel;
        DismissButton.Content = confirmation.CancelLabel;
    }

    private IasPageDialog()
    {
        InitializeComponent();
        if (Application.Current?.MainWindow is { IsLoaded: true } owner) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    internal bool IsOptionChecked => ConfirmationOption.IsChecked == true;
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}

internal sealed class IasPageDialogs : IIasPageDialogs
{
    public bool? Confirm(IasConfirmation confirmation)
    {
        var dialog = new IasPageDialog(confirmation);
        return dialog.ShowDialog() == true ? dialog.IsOptionChecked : null;
    }

    public IReadOnlyList<IasLibraryCandidate>? ChooseLibraryMods(
        Func<CancellationToken, Task<IReadOnlyList<IasLibraryCandidate>>> loadCandidates, CancellationToken ct)
    {
        var dialog = new IasLibraryPickerDialog(loadCandidates, ct);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
