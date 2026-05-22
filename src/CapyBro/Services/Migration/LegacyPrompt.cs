namespace CapyBro.Services.Migration;

internal sealed record LegacyPrompt
{
    public string? Text { get; init; }

    public bool? PreserveLanguage { get; init; }

    /// <summary>
    /// Optional in v1 — defaulted to false on migration. v1 didn't have
    /// the diff-preview feature, so any migrated prompt opts out by default
    /// (matches existing behaviour from the user's perspective).
    /// </summary>
    public bool? ShowDiffPreview { get; init; }

    /// <summary>
    /// Optional in v1 — null on migration since per-prompt model override
    /// is a v5 feature. Legacy prompts continue to use the global model.
    /// </summary>
    public string? Model { get; init; }
}
