namespace CapyBro.Models;

/// <summary>
/// Output of <see cref="Services.IPrivacyRedactor.Redact"/>: the
/// placeholder-substituted text plus the lookup needed to invert the
/// substitution after the AI's response comes back.
/// </summary>
public sealed record RedactionResult
{
    public required string RedactedText { get; init; }

    /// <summary>
    /// Placeholder → original-value mapping. Keys are the synthetic
    /// tokens (e.g. <c>&lt;&lt;EMAIL_1&gt;&gt;</c>) inserted into
    /// <see cref="RedactedText"/>; values are the user's original
    /// PII strings. Empty when nothing matched.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Mapping { get; init; }

    /// <summary>Convenience: was anything redacted?</summary>
    public bool HasRedactions => Mapping.Count > 0;

    public static RedactionResult Empty(string passThrough) => new()
    {
        RedactedText = passThrough,
        Mapping = new Dictionary<string, string>(StringComparer.Ordinal),
    };
}
