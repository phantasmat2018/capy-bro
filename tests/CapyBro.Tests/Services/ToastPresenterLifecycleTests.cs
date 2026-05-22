using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

/// <summary>
/// Z10-F13 / L25 regression: <see cref="ToastPresenter"/>'s end-to-end
/// flow needs a WPF Application + STA dispatcher to drive a real
/// <c>ToastWindow</c>, but the <i>lifecycle contracts</i> (idempotent
/// Dispose, no-op-when-no-window for the hot streaming path, post-
/// Dispose short-circuit on every public surface) are reachable
/// without ever creating a window — the production code's guard
/// branches return BEFORE touching any WPF object.
///
/// This test class exercises that subset.  The `ComputeDeferDelay`
/// timing decision lives in <see cref="ToastPresenterTimingTests"/>
/// (Z10-F9 / M29).  Together they pin every branch that does not
/// require a real Window, leaving the "Show ⇒ visible window"
/// path to manual smoke / E2E coverage.
/// </summary>
public class ToastPresenterLifecycleTests
{
    [Fact]
    public void UpdateStreamingContent_WithNoActiveWindow_IsNoOp()
    {
        // First call into the presenter has never created a window —
        // streaming events can land at any point in the pipeline,
        // including before Show fires.  Pre-fix this used to NPE if
        // `_window` was null; post-fix the null-conditional access in
        // `_window?.UpdateStreamingMessage(content)` short-circuits.
        // Verifying via no-throw is sufficient — the contract is
        // "must not crash the hot streaming path".
        var sut = new ToastPresenter();

        var act = () => sut.UpdateStreamingContent("chunk-1");

        act.Should().NotThrow(
            "the streaming pipeline runs at high frequency and pre-Show calls must short-circuit cheaply, not throw");
    }

    [Fact]
    public void Hide_WithNoActiveWindow_IsNoOp()
    {
        // Hide is called from the timer-Tick callback AND from
        // App.OnExit; either may fire when no toast has ever been
        // shown.  Must be a clean no-op rather than throwing.
        var sut = new ToastPresenter();

        var act = () => sut.Hide();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_FromInitialState_DoesNotThrow()
    {
        // Disposal of a freshly-constructed presenter (never shown,
        // never bound to an IHost) must complete without touching the
        // null `_window` / `_autoCloseTimer` / `_pendingShowTimer`
        // fields.  DI containers dispose every registered IDisposable
        // on shutdown including services that were never used during
        // the session.
        var sut = new ToastPresenter();

        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        // DI's IServiceProvider may call Dispose more than once on a
        // singleton during a graceful-then-forced shutdown sequence.
        // The `_disposed` short-circuit at the top of Dispose makes
        // the second call a no-op.
        var sut = new ToastPresenter();

        sut.Dispose();
        var act = () => sut.Dispose();

        act.Should().NotThrow(
            "the _disposed flag must short-circuit the timer-detach block so a second pass doesn't re-Stop a null timer");
    }

    [Fact]
    public void UpdateStreamingContent_AfterDispose_ShortCircuitsBeforeDispatcherMarshal()
    {
        // Pre-fix a streaming event arriving microseconds after
        // App.OnExit had disposed the presenter would dispatcher.Invoke
        // into the disposed _window.  The _disposed guard at the top
        // of UpdateStreamingContent closes that race so the post-
        // dispose call is a cheap no-op rather than an ObjectDisposed
        // chain into the dispatcher.
        var sut = new ToastPresenter();
        sut.Dispose();

        var act = () => sut.UpdateStreamingContent("late-arriving chunk");

        act.Should().NotThrow();
    }

    [Fact]
    public void Hide_AfterDispose_ShortCircuitsBeforeDispatcherMarshal()
    {
        // Same shape as UpdateStreamingContent above — Hide is also
        // dispatcher-routed, so a post-dispose call needs the same
        // guard.  Both are exercised by the timer-tick → callback
        // race during shutdown.
        var sut = new ToastPresenter();
        sut.Dispose();

        var act = () => sut.Hide();

        act.Should().NotThrow();
    }
}
