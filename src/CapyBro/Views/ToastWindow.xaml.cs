using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

using CapyBro.Models;

namespace CapyBro.Views;

public partial class ToastWindow : Window
{
    private const double EdgeOffset = 16.0;
    private const int StreamingTailChars = 80;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>
    /// Maps each <see cref="NotificationKind"/> to a <c>Themes/Tokens.xaml</c>
    /// resource key. <see cref="SetResourceReference"/> is then used so the
    /// kind-indicator strip re-tints automatically when the theme swaps —
    /// no theme-aware code in this view-model.
    ///
    /// Mapping rationale (project_design_guide.md §2.3.4):
    ///   Progress -> Status.Info     (in-flight HTTP, blue accent)
    ///   Info     -> Status.Success  (the "Готово" completion toast)
    ///   Error    -> Status.Error
    /// The earlier hard-coded Catppuccin Color constants were not
    /// theme-aware and bypassed the §2.3 token system; they're gone now.
    /// </summary>
    private static readonly Dictionary<NotificationKind, string> KindBrushKeys = new()
    {
        [NotificationKind.Progress] = "StatusInfoBrush",
        [NotificationKind.Info] = "StatusSuccessBrush",
        [NotificationKind.Error] = "StatusErrorBrush",
    };

    private Action? _onCancel;

    public ToastWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetContent(NotificationKind kind, string message, Action? onCancel = null)
    {
        MessageText.Text = message;
        IndeterminateProgress.Visibility = kind == NotificationKind.Progress
            ? Visibility.Visible
            : Visibility.Collapsed;

        // SetResourceReference (rather than direct Background = (Brush)
        // FindResource(...)) so the indicator picks up live theme swaps.
        var brushKey = KindBrushKeys.TryGetValue(kind, out var key) ? key : "StatusInfoBrush";
        KindIndicator.SetResourceReference(Border.BackgroundProperty, brushKey);

        // Cancel button only when there's a callback AND we're showing progress.
        // Info/Error toasts auto-close, no point in offering cancel.
        _onCancel = onCancel;
        CancelButton.Visibility = (kind == NotificationKind.Progress && onCancel is not null)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Live update from streaming AI output. We show only the tail of the
    /// accumulated text (last ~80 chars) — gives a sense of progress
    /// without forcing the toast to grow unbounded as the response runs.
    /// "…" prefix tells the user this is a window into a longer string.
    /// </summary>
    public void UpdateStreamingMessage(string accumulatedContent)
    {
        if (string.IsNullOrEmpty(accumulatedContent))
        {
            return;
        }

        // Trim leading whitespace so a model that opens with "\n\n" doesn't
        // make the toast look empty for the first few chunks.
        var trimmed = accumulatedContent.TrimStart();
        if (trimmed.Length == 0)
        {
            return;
        }

        // Newlines inside a single-line toast collapse weirdly. Show one
        // logical line: replace whitespace runs with single spaces.
        var oneLine = WhitespaceRunRegex().Replace(trimmed, " ");

        MessageText.Text = oneLine.Length <= StreamingTailChars
            ? oneLine
            : "…" + oneLine[^StreamingTailChars..];
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        var callback = _onCancel;
        _onCancel = null;
        callback?.Invoke();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position bottom-right within the work area (excluding taskbar) — DPI handled by WPF
        // since app.manifest declares PerMonitorV2 awareness.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - EdgeOffset;
        Top = workArea.Bottom - ActualHeight - EdgeOffset;
    }
}
