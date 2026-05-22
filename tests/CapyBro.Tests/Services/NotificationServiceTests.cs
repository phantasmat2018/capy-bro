using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

public class NotificationServiceTests
{
    [Fact]
    public void ShowProgress_SetsActiveKindAndCallsShow()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.ShowProgress("Working…");

        sut.ActiveKind.Should().Be(NotificationKind.Progress);
        presenter.ShowCalls.Should().ContainSingle()
            .Which.Should().Be((NotificationKind.Progress, "Working…"));
    }

    [Fact]
    public void ShowInfo_SetsActiveKindToInfo()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.ShowInfo("Done");

        sut.ActiveKind.Should().Be(NotificationKind.Info);
        presenter.ShowCalls.Should().ContainSingle()
            .Which.Should().Be((NotificationKind.Info, "Done"));
    }

    [Fact]
    public void ShowError_SetsActiveKindToError()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.ShowError("Boom");

        sut.ActiveKind.Should().Be(NotificationKind.Error);
        presenter.ShowCalls.Should().ContainSingle()
            .Which.Should().Be((NotificationKind.Error, "Boom"));
    }

    [Fact]
    public void CloseProgress_WhenActiveProgress_HidesAndClearsActive()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);
        sut.ShowProgress("Working…");

        sut.CloseProgress();

        sut.ActiveKind.Should().BeNull();
        presenter.HideCount.Should().Be(1);
    }

    [Fact]
    public void CloseProgress_WhenNoActive_IsNoOp()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.CloseProgress();

        sut.ActiveKind.Should().BeNull();
        presenter.HideCount.Should().Be(0);
    }

    [Fact]
    public void CloseProgress_WhenActiveIsError_DoesNotHide()
    {
        // The §6.1 race: process fires Failed -> ShowError, then Failed handler also calls
        // CloseProgress. CloseProgress must NOT close the freshly-shown Error toast.
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);
        sut.ShowProgress("Working…");
        sut.ShowError("Network down");

        sut.CloseProgress();

        sut.ActiveKind.Should().Be(NotificationKind.Error, "Error toast must survive a stray CloseProgress");
        presenter.HideCount.Should().Be(0);
    }

    [Fact]
    public void CloseProgress_WhenActiveIsInfo_DoesNotHide()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);
        sut.ShowProgress("Working…");
        sut.ShowInfo("Done");

        sut.CloseProgress();

        sut.ActiveKind.Should().Be(NotificationKind.Info);
        presenter.HideCount.Should().Be(0);
    }

    [Fact]
    public void ShowProgress_OnCancel_PassedThroughToPresenter()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);
        var cancelInvoked = false;
        Action onCancel = () => cancelInvoked = true;

        sut.ShowProgress("Working…", onCancel);

        presenter.CancelCallbacks.Should().HaveCount(1);
        presenter.CancelCallbacks[0].Should().BeSameAs(onCancel);
        presenter.CancelCallbacks[0]!.Invoke();
        cancelInvoked.Should().BeTrue();
    }

    [Fact]
    public void ShowInfo_DoesNotPassCancelCallback()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.ShowInfo("done");

        presenter.CancelCallbacks.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public void HappyPath_ShowProgress_CloseProgress_ShowInfo()
    {
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.ShowProgress("Working…");
        sut.CloseProgress();
        sut.ShowInfo("Done");

        sut.ActiveKind.Should().Be(NotificationKind.Info);
        presenter.ShowCalls.Should().HaveCount(2);
        presenter.ShowCalls[0].Should().Be((NotificationKind.Progress, "Working…"));
        presenter.ShowCalls[1].Should().Be((NotificationKind.Info, "Done"));
        presenter.HideCount.Should().Be(1, "progress toast was explicitly closed before info toast");
    }

    [Fact]
    public void FailurePath_ShowProgress_ShowError_StaleCloseProgress_ErrorRemains()
    {
        // Recreates the exact prod incident referenced by §6.1 — the error toast must outlive
        // the trailing CloseProgress() that the failure-finished handler still calls.
        var presenter = new FakeToastPresenter();
        var sut = new NotificationService(presenter);

        sut.ShowProgress("Working…");
        sut.ShowError("Server died");
        sut.CloseProgress();

        sut.ActiveKind.Should().Be(NotificationKind.Error);
        presenter.HideCount.Should().Be(0);
        presenter.LastShown.Should().Be((NotificationKind.Error, "Server died"));
    }

    private sealed class FakeToastPresenter : IToastPresenter
    {
        public List<(NotificationKind Kind, string Message)> ShowCalls { get; } = [];

        public int HideCount { get; private set; }

        public (NotificationKind Kind, string Message)? LastShown =>
            ShowCalls.Count > 0 ? ShowCalls[^1] : null;

        public List<Action?> CancelCallbacks { get; } = [];

        public List<string> StreamingUpdates { get; } = [];

        public void Show(NotificationKind kind, string message, Action? onCancel = null)
        {
            ShowCalls.Add((kind, message));
            CancelCallbacks.Add(onCancel);
        }

        public void UpdateStreamingContent(string content) => StreamingUpdates.Add(content);

        public void Hide() => HideCount++;
    }
}
