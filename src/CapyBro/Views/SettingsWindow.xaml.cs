using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;

using CapyBro.Platform;
using CapyBro.ViewModels;

namespace CapyBro.Views;

public partial class SettingsWindow : Window
{
    private readonly GeneralTabViewModel _generalTab;

    public SettingsWindow(SettingsWindowViewModel viewModel, GeneralTabViewModel generalTab)
    {
        InitializeComponent();
        DataContext = viewModel;
        _generalTab = generalTab;

        // v15: keep IsOllamaAvailable in sync with the real local
        // endpoint state across every window-lifecycle event the
        // user explicitly triggers — minimize, restore, maximize,
        // and close-to-tray.  Combined with the existing probe-
        // on-Show and probe-on-sidebar-tab-click hooks, this
        // covers every visible-to-user transition without resorting
        // to a background timer.
        StateChanged += OnWindowStateChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Phase C task 9: apply Mica on Win11 22H2+ AND clear the
        // window Background so the DWM-managed blur shows through. The
        // sidebar Border still paints SurfaceSidebarMicaBrush solid —
        // it acts as a divider against the Mica-tinted content pane,
        // matching the Win11 Settings idiom (project_design_guide.md
        // §6.2). On Win10 / older builds TryApply returns false and we
        // fall back to a SurfaceCanvas solid background so the content
        // pane stays opaque.
        if (WindowBackdrop.TryApply(this, BackdropType.Mica))
        {
            Background = Brushes.Transparent;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "SurfaceCanvasBrush");
        }

        // Native title bar follows the active palette so the caption
        // strip doesn't read white-against-dark on Dark theme.
        // ThemeService.ApplyTheme also re-walks Application.Windows on
        // every swap, so this is just the first-paint hint.
        WindowBackdrop.TryApplyTitleBarThemeFromPalette(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // §6.13: X closes to tray instead of exiting; Quit only via tray menu.
        if (e is not null)
        {
            e.Cancel = true;
        }

        // Flush any pending debounced API-key write before hiding so a key
        // that was typed-but-not-yet-persisted (within the 400ms debounce)
        // survives the close. If the user later quits via tray, the debounce
        // timer may not fire and the key would be silently lost.
        // Fire-and-forget on the threadpool — Hide() is synchronous and we
        // don't want to block the UI on credential-store I/O.
        _ = Task.Run(() => _generalTab.FlushApiKeyAsync());

        // v15: re-probe Ollama on close-to-tray.  The window is
        // about to be hidden so the user won't see the probe's
        // outcome immediately, but if Ollama went down while the
        // window was open the auto-revert + toast surface fires
        // through the tray notification — and the next time they
        // open Settings, IsOllamaAvailable already reflects truth
        // without a perceptible probe delay.
        _ = _generalTab.RefreshOllamaAvailabilityAsync();

        Hide();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Fires on Normal → Minimized (минимize), Normal → Maximized
        // (maximize), Minimized/Maximized → Normal (restore).  All
        // three are moments the user controls and where the
        // Provider-card visibility should reflect live truth on the
        // next paint.  Fire-and-forget; the probe's in-flight guard
        // collapses overlap with rapid clicks.
        _ = _generalTab.RefreshOllamaAvailabilityAsync();
    }

    /// <summary>
    /// Handler for the homepage hyperlink in the sidebar footer.  WPF
    /// <see cref="System.Windows.Documents.Hyperlink"/> doesn't auto-open
    /// external URLs — we hand the absolute URI to the OS shell so the
    /// user's default browser handles it, same pattern as the OpenRouter
    /// link on the GeneralTab.
    /// </summary>
    private void OnHomepageLinkClick(object sender, RequestNavigateEventArgs e)
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
            Serilog.Log.Warning(ex, "Failed to open homepage URL in default browser");
        }
    }
}
