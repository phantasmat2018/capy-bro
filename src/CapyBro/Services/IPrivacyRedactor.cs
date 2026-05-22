using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// Strips PII (emails / URLs / IBANs / phone numbers) from text before it
/// leaves the user's machine and reverses the substitution on the AI's
/// response. Used by the experimental privacy-redaction feature; callers
/// MUST gate invocation on <see cref="AppConfig.ExperimentalPrivacyRedaction"/>
/// — this service does not check the flag itself.
/// </summary>
public interface IPrivacyRedactor
{
    /// <summary>
    /// Replaces matched PII patterns with synthetic placeholder tokens of
    /// the form <c>&lt;&lt;TYPE_N&gt;&gt;</c> (e.g. <c>&lt;&lt;EMAIL_1&gt;&gt;</c>).
    /// Identical input values share a single placeholder so the AI sees
    /// "this is the same email twice" — preserves coreference semantics.
    /// </summary>
    RedactionResult Redact(string input);

    /// <summary>
    /// Reverses a <see cref="Redact"/> mapping on the AI's response,
    /// putting the user's original values back where the AI preserved
    /// our placeholders verbatim. Placeholders the AI mangled or dropped
    /// stay as-is in the result (visible to the user — better than
    /// silently corrupting their data).
    /// </summary>
    string Restore(string text, IReadOnlyDictionary<string, string> mapping);
}
