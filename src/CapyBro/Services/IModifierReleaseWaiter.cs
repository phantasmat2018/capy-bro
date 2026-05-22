namespace CapyBro.Services;

public interface IModifierReleaseWaiter
{
    Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Snapshot read of "is ANY of Ctrl / Alt / Shift / Win currently
    /// held."  Used by the post-paste selection step to skip the
    /// Shift+Left burst when the user is still holding the hotkey
    /// modifiers — without this guard, Ctrl-still-held turns our
    /// Shift+Left into <c>Ctrl+Shift+Left</c> which most editors
    /// interpret as "select previous word", causing selection to
    /// jump unpredictably across word boundaries inside the AI
    /// output.  Cheap (one <c>GetAsyncKeyState</c> per modifier) so
    /// safe to call on the hot path.
    /// </summary>
    bool IsAnyModifierDown();
}
