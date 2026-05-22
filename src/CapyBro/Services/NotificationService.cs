using CapyBro.Models;

namespace CapyBro.Services;

public sealed class NotificationService : INotificationService
{
    private readonly IToastPresenter _presenter;

    internal NotificationService(IToastPresenter presenter)
    {
        _presenter = presenter;
    }

    public NotificationKind? ActiveKind { get; private set; }

    public void ShowProgress(string message, Action? onCancel = null)
    {
        ActiveKind = NotificationKind.Progress;
        _presenter.Show(NotificationKind.Progress, message, onCancel);
    }

    public void ShowInfo(string message)
    {
        ActiveKind = NotificationKind.Info;
        _presenter.Show(NotificationKind.Info, message);
    }

    public void ShowError(string message)
    {
        ActiveKind = NotificationKind.Error;
        _presenter.Show(NotificationKind.Error, message);
    }

    public void UpdateStreamingContent(string accumulatedContent)
    {
        // Streaming updates only make sense while a Progress toast is up.
        // After it auto-closes (info/error replaced it) or never opened
        // (caller forgot to ShowProgress), this is a no-op.
        if (ActiveKind != NotificationKind.Progress)
        {
            return;
        }

        _presenter.UpdateStreamingContent(accumulatedContent);
    }

    public void CloseProgress()
    {
        // §6.1: only close if we're actually showing Progress. If an Error/Info toast was
        // just shown, leave it alone — it has its own lifecycle.
        if (ActiveKind != NotificationKind.Progress)
        {
            return;
        }

        ActiveKind = null;
        _presenter.Hide();
    }
}
