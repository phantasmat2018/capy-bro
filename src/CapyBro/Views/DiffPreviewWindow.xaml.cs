using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using CapyBro.Models;
using CapyBro.Platform;
using CapyBro.ViewModels;

namespace CapyBro.Views;

public partial class DiffPreviewWindow : Window
{
    private readonly DiffPreviewViewModel _viewModel;
    private bool _suppressScrollSync;

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
        _viewModel.Result = DiffPreviewResult.Accept;
        DialogResult = true;
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        _viewModel.Result = DiffPreviewResult.Reject;
        DialogResult = false;
    }

    private void OnRegenerate(object sender, RoutedEventArgs e)
    {
        _viewModel.Result = DiffPreviewResult.Regenerate;
        DialogResult = true;
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
