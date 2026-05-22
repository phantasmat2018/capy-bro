using System.Windows;

using CapyBro.Platform;
using CapyBro.ViewModels;

namespace CapyBro.Views;

public partial class ModelsDialog : Window
{
    public ModelsDialog(ModelsDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public string? SelectedModel => (DataContext as ModelsDialogViewModel)?.SelectedModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Match the active palette in the native caption so the dialog
        // doesn't open with a white title bar on Dark theme.
        WindowBackdrop.TryApplyTitleBarThemeFromPalette(this);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
