using System.Text.RegularExpressions;

namespace CapyBro.Services;

public static partial class ResultStripper
{
    public static string Strip(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();

        // Some models echo the prompt-injection wrapper tags
        // (<text_to_process> … </text_to_process>) that
        // OpenRouterClient adds around the user's clipboard text.
        // Strip them BEFORE the lead-prefix regex so a "Translation:"
        // that sits inside the tags still gets caught by that pass.
        s = WrapperTagRegex().Replace(s, string.Empty).Trim();

        s = LeadPrefixRegex().Replace(s, string.Empty);

        var fenceMatch = CodeFenceRegex().Match(s);
        if (fenceMatch.Success)
        {
            s = fenceMatch.Groups[1].Value;
        }

        s = StripTripleQuotes(s);

        return s.Trim();
    }

    [GeneratedRegex(
        @"^\s*(Translation|Text|Output|Result|Answer|Reply|Response|Текст|Переклад|Результат|Відповідь|Перевод|Ответ)\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadPrefixRegex();

    [GeneratedRegex(
        @"^```[\w\-]*\s*\n?(.*?)\n?\s*```\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();

    // Match the OpenRouterClient anti-injection wrapper tags
    // (open or close, anywhere in the text) so an echoed wrapping
    // does not surface in the user's pasted result.
    [GeneratedRegex(
        @"</?text_to_process>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WrapperTagRegex();

    private static string StripTripleQuotes(string s)
    {
        if (s.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            s = s[3..];
        }
        else if (s.StartsWith("'''", StringComparison.Ordinal))
        {
            s = s[3..];
        }

        if (s.EndsWith("\"\"\"", StringComparison.Ordinal))
        {
            s = s[..^3];
        }
        else if (s.EndsWith("'''", StringComparison.Ordinal))
        {
            s = s[..^3];
        }

        return s;
    }
}
