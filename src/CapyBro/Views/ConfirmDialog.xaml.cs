using System.Windows;

using CapyBro.Platform;

namespace CapyBro.Views;

public partial class ConfirmDialog : Window
{
    // H21 (FZ2-F1) fix: guards against two confirm dialogs stacking with
    // ambiguous z-order — ESC and the close button can otherwise dismiss
    // the wrong one.  Live functional QA reproduced this under cross-
    // agent UIAutomation contention, but the same shape can in principle
    // appear via a re-entrant event handler.  Static counter is fine
    // because ConfirmDialog is always shown on the dispatcher thread;
    // Interlocked is defensive against future off-thread misuse.
    private static int _activeDialogCount;

    public ConfirmDialog(string title, string body, string confirmText)
    {
        InitializeComponent();
        DataContext = new
        {
            Title = title,
            Body = body,
            ConfirmText = confirmText,
        };
    }

    public static bool? Ask(string title, string body, string confirmText, Window? owner)
    {
        if (Interlocked.Increment(ref _activeDialogCount) > 1)
        {
            // Another ConfirmDialog is already on screen — refuse to open
            // a second one.  Null mirrors a user-cancelled dialog, which
            // matches existing call-site expectations (callers treat
            // anything but `true` as "do not proceed").
            Interlocked.Decrement(ref _activeDialogCount);
            return null;
        }

        try
        {
            var dialog = new ConfirmDialog(title, body, confirmText) { Owner = owner };
            return dialog.ShowDialog();
        }
        finally
        {
            Interlocked.Decrement(ref _activeDialogCount);
        }
    }

    // Test-only reset — sealed assembly otherwise; tests can poke this
    // via InternalsVisibleTo to clear state between cases.
    internal static void ResetActiveDialogCountForTests() =>
        Interlocked.Exchange(ref _activeDialogCount, 0);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBackdrop.TryApplyTitleBarThemeFromPalette(this);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
