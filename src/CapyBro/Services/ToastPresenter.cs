using System.Windows;
using System.Windows.Threading;

using CapyBro.Models;
using CapyBro.Views;

namespace CapyBro.Services;

internal sealed class ToastPresenter : IToastPresenter, IDisposable
{
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromMilliseconds(3500);

    // Z10-F9 / M29: minimum on-screen time for a non-Progress toast (Info /
    // Error) before it can be replaced by another Show call.  Pre-fix a
    // rapid Processing → Done → Processing sequence (user pressed the
    // hotkey again before the "Done" toast was visible long enough to
    // perceive) would flash "Done" for <100 ms and the user could not tell
    // whether the previous run actually succeeded.  500 ms is the
    // ergonomic floor for a glance-confirm — long enough to read a single
    // word, short enough to feel responsive when chained.
    private static readonly TimeSpan MinNonProgressDisplay = TimeSpan.FromMilliseconds(500);

    private ToastWindow? _window;
    private DispatcherTimer? _autoCloseTimer;
    private DispatcherTimer? _pendingShowTimer;
    private NotificationKind? _lastShownKind;
    private DateTime _lastShownAt;
    private (NotificationKind Kind, string Message, Action? OnCancel)? _pendingShow;
    private bool _disposed;

    public void Show(NotificationKind kind, string message, Action? onCancel = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // ToastPresenter is invoked from TextProcessor's events (see
        // App.xaml.cs WireRuntimeBehavior), which already wraps each call
        // in dispatcher.InvokeAsync. Belt-and-braces: if any future caller
        // forgets, the WPF window touches below would throw a non-obvious
        // "calling thread cannot access this object" exception. Marshal.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Show(kind, message, onCancel));
            return;
        }

        // Z10-F9 / M29 — defer when replacing a too-fresh Info/Error toast.
        var deferBy = ComputeDeferDelay(
            _lastShownKind,
            _lastShownAt,
            DateTime.UtcNow,
            _window?.IsVisible == true,
            MinNonProgressDisplay);

        if (deferBy is { } delay)
        {
            // Latest-wins: if a Show was already pending, its slot is
            // overwritten with this newer content.  The defer-timer's
            // deadline stays anchored to the original Info/Error's
            // _lastShownAt so the min-display contract is honoured
            // regardless of how many Show calls land during the window.
            _pendingShow = (kind, message, onCancel);

            if (_pendingShowTimer is null)
            {
                _pendingShowTimer = new DispatcherTimer();
                _pendingShowTimer.Tick += OnPendingShowTimerTick;
            }

            if (!_pendingShowTimer.IsEnabled)
            {
                _pendingShowTimer.Interval = delay;
                _pendingShowTimer.Start();
            }

            return;
        }

        DoShow(kind, message, onCancel);
    }

    public void UpdateStreamingContent(string content)
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => UpdateStreamingContent(content));
            return;
        }

        // No-op if the window is gone (auto-close fired, user dismissed,
        // or Show was never called). Streaming events arrive at high
        // frequency so this branch is hot and must be cheap.
        _window?.UpdateStreamingMessage(content);
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Hide);
            return;
        }

        StopAutoCloseTimer();
        StopPendingShowTimer();
        _pendingShow = null;
        _lastShownKind = null;

        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.Close();
            _window = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();

        // Detach Tick and null out the timer so the dispatcher's scheduled-
        // events list lets it collect.  DispatcherTimer keeps itself rooted
        // while it has subscribers, so without unsubscribing here a Dispose
        // → re-create cycle (e.g. host re-build under tests, or a future
        // scope that disposes singletons) would leak one timer per pass.
        if (_autoCloseTimer is not null)
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer.Tick -= OnAutoCloseTick;
            _autoCloseTimer = null;
        }

        if (_pendingShowTimer is not null)
        {
            _pendingShowTimer.Stop();
            _pendingShowTimer.Tick -= OnPendingShowTimerTick;
            _pendingShowTimer = null;
        }

        _disposed = true;
    }

    /// <summary>
    /// Pure timing decision lifted out of <see cref="Show"/> so the
    /// Z10-F9 / M29 invariant can be unit-tested without spinning up a WPF
    /// dispatcher + ToastWindow.  Returns a non-null delay when the caller
    /// should defer the next Show; null when it can fire immediately.
    /// </summary>
    /// <remarks>
    /// Defer rules:
    /// • Last shown was Progress (or nothing) — never defer; Progress is
    ///   transient and can be replaced immediately.
    /// • Last shown was Info/Error but the window is no longer visible
    ///   (auto-close fired, user dismissed) — no defer; nothing to protect.
    /// • Last shown was Info/Error and the elapsed time since it was
    ///   shown is less than <paramref name="minNonProgressDisplay"/> —
    ///   defer the new Show by the remaining slice.
    /// </remarks>
    internal static TimeSpan? ComputeDeferDelay(
        NotificationKind? lastShownKind,
        DateTime lastShownAt,
        DateTime now,
        bool windowVisible,
        TimeSpan minNonProgressDisplay)
    {
        if (lastShownKind is not (NotificationKind.Info or NotificationKind.Error))
        {
            return null;
        }

        if (!windowVisible)
        {
            return null;
        }

        var elapsed = now - lastShownAt;
        if (elapsed >= minNonProgressDisplay)
        {
            return null;
        }

        return minNonProgressDisplay - elapsed;
    }

    private void DoShow(NotificationKind kind, string message, Action? onCancel)
    {
        StopAutoCloseTimer();

        if (_window is null)
        {
            _window = new ToastWindow();
            _window.Closed += OnWindowClosed;
        }

        _window.SetContent(kind, message, onCancel);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _lastShownKind = kind;
        _lastShownAt = DateTime.UtcNow;

        if (kind != NotificationKind.Progress)
        {
            StartAutoCloseTimer();
        }
    }

    private void StartAutoCloseTimer()
    {
        // Reuse a single timer instance across Show calls. Re-allocating per
        // Show was leaking when StopAutoCloseTimer skipped (rare exception
        // path) — a Tick'd timer keeps itself rooted in the dispatcher's
        // scheduled-events list and never collects.
        if (_autoCloseTimer is null)
        {
            _autoCloseTimer = new DispatcherTimer { Interval = AutoCloseDelay };
            _autoCloseTimer.Tick += OnAutoCloseTick;
        }

        _autoCloseTimer.Interval = AutoCloseDelay;
        _autoCloseTimer.Start();
    }

    private void StopAutoCloseTimer() => _autoCloseTimer?.Stop();

    private void StopPendingShowTimer() => _pendingShowTimer?.Stop();

    private void OnAutoCloseTick(object? sender, EventArgs e)
    {
        Hide();
    }

    private void OnPendingShowTimerTick(object? sender, EventArgs e)
    {
        _pendingShowTimer?.Stop();
        if (_pendingShow is { } pending)
        {
            _pendingShow = null;
            DoShow(pending.Kind, pending.Message, pending.OnCancel);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        StopAutoCloseTimer();
        _lastShownKind = null;

        // If the user closed the window manually while a Show was pending,
        // run the pending Show now — the min-display contract was about
        // protecting the previous content, which is no longer on screen.
        if (_pendingShow is { } pending)
        {
            StopPendingShowTimer();
            _pendingShow = null;
            DoShow(pending.Kind, pending.Message, pending.OnCancel);
        }
    }
}
