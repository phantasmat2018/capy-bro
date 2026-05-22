using System.Windows;

using CapyBro.Models;
using CapyBro.ViewModels;
using CapyBro.Views;

namespace CapyBro.Services;

internal sealed class DiffPreviewService : IDiffPreviewService
{
    public Task<DiffPreviewResult> ShowAsync(string original, string improved, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "DiffPreviewService requires a running WPF Application.");

        // Marshal to the UI thread — TextProcessor's ProcessAsync runs on a
        // threadpool thread (kicked off via Task.Run from the hotkey
        // handler), so we cannot construct a Window from there directly.
        return dispatcher.InvokeAsync(() => ShowOnUiThread(original, improved, ct)).Task;
    }

    private static DiffPreviewResult ShowOnUiThread(string original, string improved, CancellationToken ct)
    {
        var vm = new DiffPreviewViewModel(original, improved);

        // Owner selection prefers the active window, but in the common
        // hotkey-from-tray case there is no CapyBro window active
        // (the user is editing in another app entirely). In that scenario
        // FirstOrDefault returns null, the modal opens with no owner, and
        // Windows is free to z-order it behind other apps — making the
        // diff preview look like a popup that never appeared.  Fall back
        // to any visible application window so ShowDialog has something
        // to attach to; only when the app has zero windows (highly
        // unusual — tray-only after Settings.Close()) do we accept a
        // null owner.
        var owner = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive)
            ?? Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsVisible);

        var window = new DiffPreviewWindow(vm)
        {
            Owner = owner,

            // If there's no owner, force the modal to centre on the
            // primary screen instead of (0,0) and request topmost so the
            // foreground app cannot completely hide it.  Topmost is reset
            // by ShowDialog once activated, so this only shifts the
            // initial z-order.
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Topmost = owner is null,
        };

        // Cancellation closes the modal without committing. ShowDialog
        // returns when the window closes either via the action buttons or
        // via this registration firing.
        using var ctReg = ct.Register(() =>
        {
            // Cancellation may fire from a non-UI thread (the cancellation
            // source is owned by the caller — typically TextProcessor on a
            // threadpool thread). Marshal back to the dispatcher before
            // touching window.DialogResult.
            _ = Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (window.IsVisible)
                {
                    window.DialogResult = false;
                }
            });
        });

        var ok = window.ShowDialog();
        ct.ThrowIfCancellationRequested();

        // VM.Result is set by the action button handlers (Accept/Reject/
        // Regenerate). If the window closed without a button click — e.g.
        // user pressed Esc, clicked X, or some external close — VM.Result
        // remains its default (Reject), which is the conservative outcome.
        return vm.Result;
    }
}
