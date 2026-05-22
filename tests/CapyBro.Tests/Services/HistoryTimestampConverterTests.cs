using System.Globalization;

using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

/// <summary>
/// FZ4-F3 / M36 regression suite for <see cref="HistoryTimestampConverter"/>.
///
/// Pre-fix the converter used <see cref="CultureInfo.CurrentCulture"/>
/// (the OS-installed locale) to format month abbreviations.  A Ukrainian
/// or Russian-UI build running on a host whose Windows locale was
/// English would render cross-day dates in English even though every
/// other localised string on the surface was Cyrillic; conversely, a
/// CapyBro running in EN on a Russian Windows leaked Russian month
/// abbreviations into the History list ("май 8" instead of "May 8").
/// Now the converter pulls the active UI language from the Translator
/// singleton, so the date column always matches the rest of the UI.
/// </summary>
[Collection(TranslatorCollection.Name)]
public class HistoryTimestampConverterTests
{
    [Fact]
    public void Convert_TodaysTimestamp_RendersTimeOnly_NoMonth()
    {
        Translator.Instance.SetLanguage(Language.English);
        try
        {
            var now = DateTimeOffset.Now;
            var result = (string)HistoryTimestampConverter.Instance.Convert(
                now,
                typeof(string),
                null!,
                CultureInfo.InvariantCulture);

            // Today's entries are rendered HH:mm only — the date is
            // implied by the "Today" group header and a per-row date
            // would be visual noise.
            result.Should().MatchRegex(
                @"^\d{2}:\d{2}$",
                "today's entries must render as HH:mm only");
        }
        finally
        {
            Translator.Instance.SetLanguage(Language.English);
        }
    }

    [Fact]
    public void Convert_CrossDayTimestamp_English_RendersEnglishMonthAbbreviation()
    {
        Translator.Instance.SetLanguage(Language.English);
        try
        {
            // 2026-03-15 — March, well-defined month abbreviation in EN.
            var stamp = new DateTimeOffset(2026, 3, 15, 14, 32, 0, TimeSpan.Zero);
            var result = (string)HistoryTimestampConverter.Instance.Convert(
                stamp,
                typeof(string),
                null!,
                CultureInfo.InvariantCulture);

            result.Should().Contain(
                "Mar",
                "en-US month abbreviation for March is 'Mar'; the converter must pull from the active UI culture");
            result.Should().Contain("15");
        }
        finally
        {
            Translator.Instance.SetLanguage(Language.English);
        }
    }

    [Fact]
    public void Convert_CrossDayTimestamp_Russian_RendersRussianMonthAbbreviation()
    {
        Translator.Instance.SetLanguage(Language.Russian);
        try
        {
            // 2026-05-08 — May, distinct Russian abbreviation ("мая" or
            // "май" depending on .NET version) that differs from the
            // English "May" letter-for-letter only by Cyrillic
            // characters.  We assert a stable Cyrillic-character
            // detection rather than a specific spelling so the test
            // doesn't break across .NET versions that adjust ru-RU
            // abbreviated month names (CLDR shifts).
            var stamp = new DateTimeOffset(2026, 5, 8, 14, 32, 0, TimeSpan.Zero);
            var result = (string)HistoryTimestampConverter.Instance.Convert(
                stamp,
                typeof(string),
                null!,
                CultureInfo.InvariantCulture);

            result.Should().MatchRegex(
                @"[А-Яа-я]",
                "Russian month abbreviation must contain Cyrillic letters; pre-fix the OS-locale leak " +
                "would render 'May 8' even when the UI language is Russian");
            result.Should().Contain("8");
        }
        finally
        {
            Translator.Instance.SetLanguage(Language.English);
        }
    }

    [Fact]
    public void Convert_CrossDayTimestamp_Ukrainian_RendersUkrainianMonthAbbreviation()
    {
        Translator.Instance.SetLanguage(Language.Ukrainian);
        try
        {
            // 2026-05-08 — May → Ukrainian "тра" or "трав" (CLDR
            // historically; .NET 8 ICU bundle renders "тра" for short).
            var stamp = new DateTimeOffset(2026, 5, 8, 14, 32, 0, TimeSpan.Zero);
            var result = (string)HistoryTimestampConverter.Instance.Convert(
                stamp,
                typeof(string),
                null!,
                CultureInfo.InvariantCulture);

            result.Should().MatchRegex(
                @"[А-Яа-яІіЇїЄєҐґ]",
                "Ukrainian month abbreviation must contain Cyrillic characters (Ukrainian alphabet)");
            result.Should().Contain("8");
        }
        finally
        {
            Translator.Instance.SetLanguage(Language.English);
        }
    }

    [Fact]
    public void Convert_NonDateTimeOffsetValue_ReturnsEmptyString()
    {
        // Defensive: WPF bindings can hand us a transient null / wrong-
        // type value during reload sequences.  The converter must not
        // throw — return empty string and let the row paint blank
        // until the real value arrives.
        var result = HistoryTimestampConverter.Instance.Convert(
            "not-a-timestamp",
            typeof(string),
            null!,
            CultureInfo.InvariantCulture);

        result.Should().Be(string.Empty);
    }
}
