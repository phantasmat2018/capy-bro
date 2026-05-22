namespace CapyBro.Services;

public interface INotificationService
{
    /// <summary>
    /// Show an indeterminate-progress toast. If <paramref name="onCancel"/> is supplied, a
    /// Cancel button is rendered on the toast; clicking it invokes the callback.
    /// </summary>
    void ShowProgress(string message, Action? onCancel = null);

    void ShowInfo(string message);

    void ShowError(string message);

    /// <summary>
    /// Replaces the body of the active Progress toast with new text. No-op
    /// when no Progress toast is showing (e.g. user dismissed it). Used to
    /// surface streaming AI output as it arrives — the message is the full
    /// accumulated result so far; the toast formats / truncates for display.
    /// </summary>
    void UpdateStreamingContent(string accumulatedContent);

    /// <summary>
    /// Closes the progress toast IF it is currently active. No-op when an Info/Error toast
    /// is showing — keeping a freshly-displayed error visible until its own auto-close fires.
    /// This ordering rule (per §6.1 of the spec) prevents the failure-finished sequence
    /// from killing an error toast that ShowError just put up.
    /// </summary>
    void CloseProgress();
}
