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
    /// preview before pasting back. Defaulted at the registry-level for
    /// "fix errors" since that's where verifying changes is most valuable.
    /// </summary>
    public bool ShowDiffPreview { get; init; }

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
