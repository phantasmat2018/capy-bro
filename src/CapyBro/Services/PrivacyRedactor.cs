using System.Text.RegularExpressions;

using CapyBro.Models;

namespace CapyBro.Services;

internal sealed partial class PrivacyRedactor : IPrivacyRedactor
{
    public RedactionResult Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return RedactionResult.Empty(input ?? string.Empty);
        }

        // placeholder → original (the OUTPUT mapping callers use to restore)
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);

        // original → placeholder (the inverse, used for dedup during a single
        // Redact call so identical values reuse the same token).
        var originalToPlaceholder = new Dictionary<string, string>(StringComparer.Ordinal);
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);

        // Pattern processing order matters:
        //   1. URLs first — they may contain "@" or digit sequences that
        //      would otherwise be picked up by the email / phone passes.
        //   2. Emails next — high specificity, low false-positive rate.
        //   3. IBANs — very distinctive (country prefix + checksum length).
        //      Must run BEFORE credit-card / phone so a space-formatted IBAN
        //      ("DE89 3704 0044 …") is not shredded into PHONE matches.
        //   4. Credit cards — 13-19 digits, optionally grouped. Sits between
        //      IBAN and PHONE: IBAN starts with letters so won't collide,
        //      and PHONE's max-13-digit reach won't claim full CC numbers.
        //   5. Phone numbers last — most ambiguous; we only match patterns
        //      that contain at least one separator so random integer
        //      sequences (ISBNs, UPCs, ID numbers) don't get clobbered.
        var working = input;
        working = ApplyPattern(working, UrlRegex(), "URL", counters, mapping, originalToPlaceholder, StripTrailingUrlPunctuation);
        working = ApplyPattern(working, EmailRegex(), "EMAIL", counters, mapping, originalToPlaceholder);
        working = ApplyPattern(working, IbanRegex(), "IBAN", counters, mapping, originalToPlaceholder, validator: PassesIbanLengthCheck);
        working = ApplyPattern(working, CreditCardRegex(), "CARD", counters, mapping, originalToPlaceholder, validator: PassesLuhn);
        working = ApplyPattern(working, PhoneRegex(), "PHONE", counters, mapping, originalToPlaceholder);

        return new RedactionResult
        {
            RedactedText = working,
            Mapping = mapping,
        };
    }

    public string Restore(string text, IReadOnlyDictionary<string, string> mapping)
    {
        if (string.IsNullOrEmpty(text) || mapping.Count == 0)
        {
            return text ?? string.Empty;
        }

        // Replace longest placeholders first. With our naming scheme
        // ("<<EMAIL_10>>" etc.) longer == higher index, so a literal
        // placeholder substring of another placeholder is impossible —
        // but ordering by length is cheap insurance against any future
        // change to the placeholder format.
        var result = text;
        foreach (var kvp in mapping.OrderByDescending(p => p.Key.Length))
        {
            result = result.Replace(kvp.Key, kvp.Value, StringComparison.Ordinal);
        }

        return result;
    }

    [GeneratedRegex(
        @"\b(?:https?://|ftp://|www\.)[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    // Email — Unicode-aware. Uses \p{L} (any letter) and \p{N} (any digit)
    // so Cyrillic addresses (`ваня@почта.рф`), IDN punycode TLDs
    // (`test@xn--80a1acny.xn--p1ai`), and standard ASCII all match. The
    // domain is one-or-more `.label` segments where each label may contain
    // dashes — required for punycode (xn--…) and most real-world domains.
    // Custom lookaround acts as a Unicode-safe word boundary: \b in .NET
    // depends on \w which includes most letters but excludes a few
    // diacritic combos.
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // IBAN: 2-letter country code + 2 check digits + 11–30 alphanumeric.
    // Total length 15–34 per ISO 13616. Allows optional single spaces
    // between alphanumerics so the human-formatted variant
    // ("DE89 3704 0044 0532 0130 00") matches as one IBAN — without that,
    // the PHONE regex would later carve it into bogus phone matches.
    [GeneratedRegex(
        @"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex IbanRegex();

    // Credit card: 13–19 digits in three common shapes:
    //   • 4-4-4-4 grouped (Visa / Mastercard, 16 digits)
    //   • 4-6-5 grouped   (Amex, 15 digits)
    //   • contiguous      (any length 13–19)
    // Separators may be space or hyphen. Must run AFTER IBAN (which
    // starts with letters and so doesn't collide) but BEFORE PHONE
    // (whose 2/3-group shape would otherwise nibble off the first 8
    // digits of a spaced 16-digit card and leak the rest).
    [GeneratedRegex(
        @"\b(?:\d{4}[ \-]\d{4}[ \-]\d{4}[ \-]\d{1,4}|\d{4}[ \-]\d{6}[ \-]\d{5}|\d{13,19})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreditCardRegex();

    // Phone: optional "+CC" country code then 2-3 separator-delimited
    // digit groups. Requires at least one separator (space/dash/dot/paren)
    // — without that constraint we'd catch every long integer in the
    // text, including dates, IDs, and code line numbers.
    [GeneratedRegex(
        @"(?:\+\d{1,3}[\s\-.()]+)?\(?\d{2,4}\)?[\s\-.]+\d{2,4}[\s\-.]+\d{2,5}",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    /// <summary>
    /// Trailing-punctuation peeler for URL matches. The URL regex is
    /// greedy and will swallow sentence-ending punctuation
    /// (`https://example.com.` → `https://example.com.`). We strip those
    /// here so Restore returns the URL without the rogue character and
    /// the punctuation stays where the user typed it.
    /// </summary>
    private static string StripTrailingUrlPunctuation(string url)
    {
        var end = url.Length;
        while (end > 0 && IsUrlTrailingPunctuation(url[end - 1]))
        {
            end--;
        }

        return end == url.Length ? url : url[..end];
    }

    private static bool IsUrlTrailingPunctuation(char c) =>
        c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '>' or '"' or '\'';

    /// <summary>
    /// Luhn-check filter for the credit-card pass.  Without this the
    /// 13–19-digit fallback in <see cref="CreditCardRegex"/> matches every
    /// long integer in the text — order numbers, ISBNs, account IDs,
    /// timestamp digit-runs — and replaces them with <c>&lt;&lt;CARD_n&gt;&gt;</c>.
    /// Real PANs all satisfy Luhn (mod-10 checksum), so requiring it keeps
    /// false positives near zero while still catching every realistic card
    /// number a user might paste.
    /// </summary>
    private static bool PassesLuhn(string match)
    {
        // Strip the visual separators the regex permits (' ', '-') so the
        // checksum runs over the bare digit sequence.
        var sum = 0;
        var digitCount = 0;
        var doubleNext = false;
        for (var i = match.Length - 1; i >= 0; i--)
        {
            var c = match[i];
            if (c is ' ' or '-')
            {
                continue;
            }

            if (c is < '0' or > '9')
            {
                // Defensive — the regex shouldn't admit non-digit/non-sep,
                // but be safe rather than throw.
                return false;
            }

            var digit = c - '0';
            if (doubleNext)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleNext = !doubleNext;
            digitCount++;
        }

        // Range check belt-and-braces; the regex already enforces 13–19.
        return digitCount is >= 13 and <= 19 && sum % 10 == 0;
    }

    /// <summary>
    /// IBAN length sanity check — counts non-space alphanumerics and
    /// verifies the total falls in the ISO 13616 envelope (15–34).
    /// The regex's <c>{11,30}</c> body length plus the 4-character prefix
    /// gives 15–34 in principle, but a space-formatted match like
    /// "DE89 X X X X X X X X X X X" (11 single-letter groups) collapses
    /// to a 13-char IBAN that should be rejected.  We don't validate the
    /// full mod-97 checksum to keep regional false-negatives low — banks
    /// occasionally print malformed checksums on receipts.
    /// </summary>
    private static bool PassesIbanLengthCheck(string match)
    {
        var alphanumerics = 0;
        foreach (var c in match)
        {
            if (c != ' ')
            {
                alphanumerics++;
            }
        }

        return alphanumerics is >= 15 and <= 34;
    }

    private static string ApplyPattern(
        string input,
        Regex regex,
        string typeName,
        Dictionary<string, int> counters,
        Dictionary<string, string> mapping,
        Dictionary<string, string> originalToPlaceholder,
        Func<string, string>? matchTransform = null,
        Func<string, bool>? validator = null)
    {
        return regex.Replace(input, match =>
        {
            var matched = matchTransform is null ? match.Value : matchTransform(match.Value);

            // After transform a match may collapse to empty (e.g. a URL
            // regex matched a single ".") — keep the original text intact
            // so we don't accidentally delete characters.
            if (matched.Length == 0)
            {
                return match.Value;
            }

            // Domain-specific validators (Luhn for cards, length sanity
            // for IBAN) — when they reject, leave the match as-is rather
            // than substituting a placeholder, so the redactor does not
            // turn a 16-digit order number into "<<CARD_1>>".
            if (validator is not null && !validator(matched))
            {
                return match.Value;
            }

            // Dedup: same original value → same placeholder. Helps the
            // model treat "John's email appears twice" as coreference.
            if (originalToPlaceholder.TryGetValue(matched, out var existing))
            {
                return existing + match.Value[matched.Length..];
            }

            counters.TryGetValue(typeName, out var count);
            count++;
            counters[typeName] = count;

            var placeholder = $"<<{typeName}_{count}>>";
            mapping[placeholder] = matched;
            originalToPlaceholder[matched] = placeholder;

            // If a transform shortened the match, splice the leftover
            // suffix back in (e.g. URL trailing "." stays in the text).
            return matched.Length == match.Value.Length
                ? placeholder
                : placeholder + match.Value[matched.Length..];
        });
    }
}
