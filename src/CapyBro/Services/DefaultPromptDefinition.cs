using CapyBro.Models;

namespace CapyBro.Services;

internal sealed record DefaultPromptDefinition
{
    public required string KeyUk { get; init; }

    public required string KeyRu { get; init; }

    public required string KeyEn { get; init; }

    public required string TextUk { get; init; }

    public required string TextRu { get; init; }

    public required string TextEn { get; init; }

    public bool PreserveLanguage { get; init; } = true;

    /// <summary>
    /// Whether the corresponding <see cref="Prompt"/> should open the diff
    /// preview before pasting back.
    ///
    /// v16: default flipped to <c>true</c> so every prompt definition
    /// in <see cref="PromptRegistry"/> gets the verify-before-paste
    /// safety net unless a slot explicitly opts out.  Pre-v16 only
    /// "Fix errors" had this turned on; the rest silently bypassed
    /// the modal — fixed by reversing the default polarity.
    /// </summary>
    public bool ShowDiffPreview { get; init; } = true;

    /// <summary>
    /// Optional per-prompt model override. Built-in defaults leave this
    /// null (use global model) since we can't assume which model ids the
    /// user has pinned. Users can override per-prompt in the editor.
    /// </summary>
    public string? Model { get; init; }

    public string GetKey(Language language) => language switch
    {
        Language.Russian => KeyRu,
        Language.English => KeyEn,
        _ => KeyUk,
    };

    public string GetText(Language language) => language switch
    {
        Language.Russian => TextRu,
        Language.English => TextEn,
        _ => TextUk,
    };

    public Prompt ToPrompt(Language language) => new()
    {
        Text = GetText(language),
        PreserveLanguage = PreserveLanguage,
        ShowDiffPreview = ShowDiffPreview,
        Model = Model,
    };

    public bool MatchesAnyLanguageKey(string key) =>
        KeyUk == key || KeyRu == key || KeyEn == key;
}
