using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

using CapyBro.ViewModels;

namespace CapyBro.Views;

public partial class GeneralTab : UserControl
{
    public GeneralTab()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Handler for the OpenRouter sub-hint hyperlink under the API-key
    /// field. WPF's Hyperlink doesn't auto-open external URLs; we route
    /// through the OS shell so the user's default browser handles it.
    /// </summary>
    private void OnOpenRouterLinkClick(object sender, RequestNavigateEventArgs e)
    {
        if (e?.Uri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Serilog.Log.Warning(ex, "Failed to open OpenRouter URL in default browser");
        }
    }

    /// <summary>
    /// Handler for the hidden 20-tap eye-icon gesture surfaced by
    /// <see cref="Controls.RevealablePasswordBox.SecretSequenceTriggered"/>.
    /// Forwards to the bound <see cref="GeneralTabViewModel"/>'s
    /// developer-mode toggle so the visibility binding on the
    /// "Beta features" StackPanel flips immediately.  The view's
    /// DataContext is set by the host (SettingsWindow); if it isn't
    /// the expected VM type (e.g. design-time loader), the gesture
    /// is silently ignored — better than throwing a runtime cast
    /// exception when the only path to surface it is the same
    /// secret nobody else knows.
    /// </summary>
    private void OnSecretSequenceTriggered(object sender, EventArgs e)
    {
        if (DataContext is GeneralTabViewModel vm)
        {
            vm.ToggleDeveloperMode();
        }
    }
}
