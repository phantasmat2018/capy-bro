namespace CapyBro.Models;

public sealed record Prompt
{
    public required string Text { get; init; }

    public bool PreserveLanguage { get; init; }

    /// <summary>
    /// When true, TextProcessor opens a side-by-side diff modal between
    /// the original selection and the AI result before pasting back. The
    /// user can Accept (paste), Reject (cancel + restore clipboard), or
    /// Regenerate (re-call the model). Designed for "fix errors" style
    /// prompts where the user wants to verify nothing besides typos got
    /// changed before committing.
    /// </summary>
    public bool ShowDiffPreview { get; init; }

    /// <summary>
    /// Optional per-prompt OpenRouter model override. Null/empty = use
    /// the global <see cref="AppConfig.Model"/>. Honored by
    /// TextProcessor only when <see cref="AppConfig.Provider"/> is
    /// <see cref="LlmProviderKind.OpenRouter"/> AND the
    /// <see cref="AppConfig.ExperimentalPerPromptModel"/> master flag
    /// is on; otherwise the global model is used regardless. Lets
    /// users route cheap operations (typo fixes) to a fast/cheap
    /// model and expensive ones (translation, rewriting) to a stronger
    /// model.
    ///
    /// <para>
    /// v15: the field is OpenRouter-scoped — when the user is on
    /// Ollama, <see cref="OllamaModel"/> is consulted instead.  The
    /// two coexist on disk so a Provider toggle preserves both picks
    /// and switching back restores the original choice.
    /// </para>
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Optional per-prompt Ollama model override (e.g.
    /// <c>"llama3.2:latest"</c>).  Same semantics as <see cref="Model"/>
    /// but consulted when <see cref="AppConfig.Provider"/> is
    /// <see cref="LlmProviderKind.Ollama"/>.  Kept as a separate field
    /// so a user who has a different per-prompt pick for each backend
    /// can toggle the global provider without losing either side's
    /// selection — both values live on disk simultaneously and
    /// TextProcessor picks the matching one at request time.
    /// </summary>
    public string? OllamaModel { get; init; }

    /// <summary>
    /// Used only inside <see cref="AppConfig.DefaultPromptOverrides"/>:
    /// when set, replaces the default slot's localized name in the active
    /// list for the override's language.  Null/empty means "no rename"
    /// (the default's localized name is kept).  Ignored on
    /// <see cref="AppConfig.CustomPrompts"/> entries — those use the
    /// dictionary key as the display name directly.  Letting the rename
    /// live alongside the text/option override lets every preset edit
    /// (rename + content) stay per-language without bleeding into
    /// <see cref="AppConfig.CustomPrompts"/> (which is shown in every UI
    /// language).
    /// </summary>
    public string? OverrideName { get; init; }
}
