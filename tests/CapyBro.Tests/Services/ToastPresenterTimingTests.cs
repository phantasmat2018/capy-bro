using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

/// <summary>
/// Z10-F9 / M29 regression tests for <see cref="ToastPresenter.ComputeDeferDelay"/>.
///
/// Pre-fix a rapid Processing → Done → Processing sequence (e.g. user
/// pressed the hotkey again before the success toast had been visible
/// long enough to perceive) replaced the "Done" toast immediately and the
/// user could not tell whether the previous run succeeded.  The fix
/// enforces a minimum on-screen time for Info/Error toasts before any
/// subsequent Show can take effect.
///
/// These tests exercise the pure timing decision in isolation so WPF
/// dispatcher + ToastWindow are not needed.  ToastPresenter wires the
/// decision into a DispatcherTimer; the wiring itself is a thin shell and
/// is exercised by manual smoke + the broader test suite.
/// </summary>
public class ToastPresenterTimingTests
{
    private static readonly TimeSpan MinDisplay = TimeSpan.FromMilliseconds(500);
    private static readonly DateTime ShownAt = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoPriorToast_DoesNotDefer()
    {
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: null,
            lastShownAt: default,
            now: ShownAt,
            windowVisible: false,
            minNonProgressDisplay: MinDisplay);

        result.Should().BeNull("first Show ever has nothing to protect");
    }

    [Fact]
    public void PriorProgress_DoesNotDefer()
    {
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: NotificationKind.Progress,
            lastShownAt: ShownAt,
            now: ShownAt.AddMilliseconds(10),
            windowVisible: true,
            minNonProgressDisplay: MinDisplay);

        result.Should().BeNull("Progress toasts are transient and may be replaced immediately");
    }

    [Fact]
    public void PriorInfoButWindowClosed_DoesNotDefer()
    {
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: NotificationKind.Info,
            lastShownAt: ShownAt,
            now: ShownAt.AddMilliseconds(100),
            windowVisible: false,
            minNonProgressDisplay: MinDisplay);

        result.Should().BeNull("if the toast already auto-closed there is nothing to keep visible");
    }

    [Fact]
    public void PriorInfo_WindowVisible_ElapsedZero_DefersByFullWindow()
    {
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: NotificationKind.Info,
            lastShownAt: ShownAt,
            now: ShownAt,
            windowVisible: true,
            minNonProgressDisplay: MinDisplay);

        result.Should().Be(
            MinDisplay,
            "Show landed in the same instant the Info toast appeared; full min-display must elapse first");
    }

    [Fact]
    public void PriorInfo_WindowVisible_HalfElapsed_DefersByRemainder()
    {
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: NotificationKind.Info,
            lastShownAt: ShownAt,
            now: ShownAt.AddMilliseconds(200),
            windowVisible: true,
            minNonProgressDisplay: MinDisplay);

        result.Should().Be(
            TimeSpan.FromMilliseconds(300),
            "Info has been on screen for 200 ms of the 500 ms minimum; defer the new content by the remaining 300 ms");
    }

    [Fact]
    public void PriorInfo_WindowVisible_FullyElapsed_DoesNotDefer()
    {
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: NotificationKind.Info,
            lastShownAt: ShownAt,
            now: ShownAt.AddMilliseconds(600),
            windowVisible: true,
            minNonProgressDisplay: MinDisplay);

        result.Should().BeNull("Info has been visible past the min-display threshold; safe to replace immediately");
    }

    [Fact]
    public void PriorError_WindowVisible_ElapsedSmall_DefersByRemainder()
    {
        // Errors get the same min-display contract as Info — the user must
        // have a chance to read the failure reason before the toast is
        // overwritten by the next Progress / Info / Error.
        var result = ToastPresenter.ComputeDeferDelay(
            lastShownKind: NotificationKind.Error,
            lastShownAt: ShownAt,
            now: ShownAt.AddMilliseconds(50),
            windowVisible: true,
            minNonProgressDisplay: MinDisplay);

        result.Should().Be(TimeSpan.FromMilliseconds(450));
    }
}
