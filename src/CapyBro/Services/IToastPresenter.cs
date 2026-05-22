using CapyBro.Models;

namespace CapyBro.Services;

internal interface IToastPresenter
{
    void Show(NotificationKind kind, string message, Action? onCancel = null);

    /// <summary>
    /// Replaces the message body of the currently-shown toast in-place
    /// (no re-show, no auto-close timer reset). Used by the streaming
    /// pipeline to push live token output without flicker.
    /// </summary>
    void UpdateStreamingContent(string content);

    void Hide();
}
