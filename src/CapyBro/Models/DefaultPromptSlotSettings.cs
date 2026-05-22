namespace CapyBro.Models;

/// <summary>
/// Per-default-slot user settings that are SHARED across all UI
/// languages.  When a user toggles a checkbox like "Preserve source
/// language" or picks a per-prompt model on the UA copy of a preset,
/// the change applies to the EN/RU copies of the same slot too —
/// these flags are properties of the prompt's purpose (e.g. "fix
/// errors should always show a diff preview"), not of the locale that
/// happened to be active when the user toggled them.  Keyed by the
/// canonical English slot name in <see cref="AppConfig.DefaultPromptSettings"/>.
/// Per-locale state (text edits, renames) lives separately in
/// <see cref="AppConfig.DefaultPromptOverrides"/>.
/// </summary>
public sealed record DefaultPromptSlotSettings
{
    public bool PreserveLanguage { get; init; }

    public bool ShowDiffPreview { get; init; }

    /// <summary>
    /// Per-prompt OpenRouter model override.  See
    /// <see cref="Prompt.Model"/> for full semantics.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Per-prompt Ollama model override.  v15 companion to
    /// <see cref="Model"/> — kept separate so toggling between
    /// providers preserves both picks.  See
    /// <see cref="Prompt.OllamaModel"/> for full semantics.
    /// </summary>
    public string? OllamaModel { get; init; }
}
