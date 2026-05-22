using CapyBro.Models;

namespace CapyBro.Services;

public interface IPromptSelector
{
    /// <summary>
    /// Selects a prompt based on hotkey kind. For <see cref="HotkeyKind.Default"/>, returns the
    /// configured default prompt (never null in practice, falls back if config is empty).
    /// For <see cref="HotkeyKind.Menu"/>, shows a picker UI and returns the chosen prompt, or
    /// null if the user cancelled / timed out.
    /// </summary>
    Task<Prompt?> SelectAsync(HotkeyKind kind, CancellationToken ct = default);
}
