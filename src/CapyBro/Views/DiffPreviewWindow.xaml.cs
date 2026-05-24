using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using CapyBro.Models;
using CapyBro.Platform;
using CapyBro.ViewModels;

namespace CapyBro.Views;

public partial class DiffPreviewWindow : Window
{
    private readonly DiffPreviewViewModel _viewModel;
    private bool _suppressScrollSync;

    // HWND of the foreground window at the moment ShowDialog was called —
    // i.e. the user's "target app" (Word, browser, terminal, …) they
    // pressed Ctrl+Shift+E in.  We steal foreground in
    // OnSourceInitialized so the modal is reachable; without restoring
    // it on close the subsequent SendInput-driven Ctrl+V can land in
    // the wrong window (a CapyBro Settings tab if it happened to be
    // open, or wherever Windows picked as "next foreground").  Captured
    // by DiffPreviewService before the call to ShowDialog and assigned
    // here so the OnAccept / OnReject handlers can restore it
    // synchronously while our process still owns foreground rights.
    public IntPtr TargetForegroundWindow { get; set; }

    // HWND of the actual focused CHILD inside TargetForegroundWindow
    // (e.g. the Scintilla edit control inside Notepad++'s top-level
    // frame).  Without this, SetFocus on the top-level frame after
    // restoring foreground leaves the post-modal Ctrl+V echoing into
    // the frame's WindowProc rather than the edit surface — manifests
    // as "clipboard has the text but the selection wasn't replaced".
    // Captured by DiffPreviewService via GetGUIThreadInfo before the
    // modal steals foreground.
    public IntPtr TargetForegroundFocusedChild { get; set; }

    public DiffPreviewWindow(DiffPreviewViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (WindowBackdrop.TryApply(this, BackdropType.Mica))
        {
            Background = Brushes.Transparent;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "SurfaceCanvasBrush");
        }

        WindowBackdrop.TryApplyTitleBarThemeFromPalette(this);

        // Hotkey-triggered modal: when the user pressed Ctrl+Shift+E from
        // another app, that app still owns the foreground. ShowDialog alone
        // doesn't promote our window — Topmost in XAML keeps it on top of
        // z-order, but the OS still holds the input focus on the previous
        // foreground process. SetForegroundWindow + Activate flips both.
        // We're inside a registered-hotkey handler so the OS allows the
        // foreground promotion (the docs gate it on "process received the
        // last input event" which RegisterHotKey delivery satisfies).
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }

        Activate();
        Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing the window via X / Esc / Alt+F4 without choosing an
        // explicit action means "I do not want to commit this result".
        // Default Reject is set in the VM ctor, so as long as no Click
        // handler ran, we land here with Result == Reject — correct.
        base.OnClosed(e);
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        // If the user is currently in Edit mode, the editable text is the
        // authoritative result. Persist it back into the VM's Improved
        // pipeline so DiffPreviewService.ShowOnUiThread can hand it to
        // TextProcessor for the paste step.
        _viewModel.CommitEditableImproved();
        _viewModel.Result = DiffPreviewResult.Accept;

        // Voluntarily drop Topmost BEFORE foreground restoration: a
        // topmost source window blocks SetForegroundWindow's z-order
        // promotion of the target.  Pairing this with the AttachThreadInput
        // sandwich inside ForegroundRestorer is what makes Notepad++ /
        // VS Code / Word reliably receive the post-modal Ctrl+V.
        Topmost = false;

        // Restore the user's original app to foreground BEFORE setting
        // DialogResult — our process still owns the "last input event"
        // privilege at this exact moment (user just left-clicked us), so
        // ForegroundRestorer's AttachThreadInput sandwich has the best
        // chance of succeeding here.  Without this, the subsequent
        // Ctrl+V in TextProcessor lands in whatever Windows picked as
        // the next foreground (often a stale Settings window or
        // nothing at all).
        RestoreTargetForeground();

        DialogResult = true;
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        _viewModel.Result = DiffPreviewResult.Reject;

        Topmost = false;

        // Courtesy: return focus to the user's app on cancel too, so
        // they're not stranded looking at a CapyBro window after dismiss.
        // No paste fires on Reject so missing the restore is non-fatal,
        // but the UX is markedly worse without it.
        RestoreTargetForeground();

        DialogResult = false;
    }

    private void OnRegenerate(object sender, RoutedEventArgs e)
    {
        _viewModel.Result = DiffPreviewResult.Regenerate;

        // Intentionally NOT restoring foreground here: TextProcessor will
        // immediately re-call the LLM and spin up a fresh preview modal
        // that steals foreground again — restoring in between would just
        // produce a visual flicker for no benefit.
        DialogResult = true;
    }

    private void RestoreTargetForeground()
    {
        if (TargetForegroundWindow == IntPtr.Zero)
        {
            return;
        }

        var ourHandle = new WindowInteropHelper(this).Handle;
        if (TargetForegroundWindow == ourHandle)
        {
            // The foreground at ShowDialog time was actually ourselves
            // (rare — happens if e.g. Settings was already open and was
            // the active foreground when the user triggered preview from
            // an in-app source).  Nothing to restore.
            return;
        }

        ForegroundRestorer.RestoreToForeground(TargetForegroundWindow, TargetForegroundFocusedChild);
    }

    /// <summary>
    /// Auto-focus the edit-mode TextBox when it becomes visible so the
    /// user can start typing immediately after flipping the Edit toggle
    /// — without this, the toggle visually swaps the panes but the
    /// keyboard focus stays on the CheckBox, and the user has to
    /// manually click into the TextBox before typing.  Dispatcher-
    /// deferred to <see cref="DispatcherPriority.Input"/> so layout has
    /// committed before we call Focus + position the caret.
    /// </summary>
    private void ImprovedEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox tb)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () =>
            {
                tb.Focus();
                tb.CaretIndex = tb.Text?.Length ?? 0;
            },
            DispatcherPriority.Input);
    }

    // Synchronised scrolling between the two columns: scrolling on either
    // side moves the other so the user's eye stays on the same diff row
    // across panes. Without this, scrolling original drifts out of sync
    // with improved (different line counts after deletions/insertions —
    // though SideBySideDiffBuilder pads to equal length, vertical scroll
    // position still differs if heights drift).
    private void OriginalScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync || sender is not ScrollViewer src)
        {
            return;
        }

        _suppressScrollSync = true;
        try
        {
            ImprovedScroller.ScrollToVerticalOffset(src.VerticalOffset);
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    private void ImprovedScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollSync || sender is not ScrollViewer src)
        {
            return;
        }

        _suppressScrollSync = true;
        try
        {
            OriginalScroller.ScrollToVerticalOffset(src.VerticalOffset);
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }
}
