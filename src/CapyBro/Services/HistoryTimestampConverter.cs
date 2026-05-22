using System.Globalization;
using System.Windows.Data;

using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// Formats a <see cref="DateTimeOffset"/> for the History list-item
/// row so the user can scan "when did this happen" at a glance.
///
/// • Today's entries: <c>HH:mm</c> (just the time, since the date
///   would be a redundant "today, today, today, today…" column).
/// • Older entries: <c>MMM d, HH:mm</c> (e.g. "May 8, 14:32") so the
///   day is visible without ambiguity.
///
/// Lives on a singleton instance so XAML can bind via
/// <c>{x:Static services:HistoryTimestampConverter.Instance}</c>
/// without having to declare it in every consumer's
/// UserControl.Resources.
/// </summary>
public sealed class HistoryTimestampConverter : IValueConverter
{
    // FZ4-F3 / M36: cached cultures keyed by the app's UI language, NOT
    // CultureInfo.CurrentCulture (the OS-installed locale).  Pre-fix the
    // converter pulled month abbreviations from CurrentCulture, so a
    // Ukrainian-UI CapyBro running on a Russian Windows host rendered
    // "май 8, 14:32" instead of "трав 8" — the History column visibly
    // contradicted the rest of the localised surface.  Map directly from
    // the Translator's active language so the rendering follows the
    // user's chosen UI locale regardless of OS settings.
    private static readonly Dictionary<Language, CultureInfo> CulturesByLanguage = new()
    {
        [Language.English] = CultureInfo.GetCultureInfo("en-US"),
        [Language.Ukrainian] = CultureInfo.GetCultureInfo("uk-UA"),
        [Language.Russian] = CultureInfo.GetCultureInfo("ru-RU"),
    };

    public static HistoryTimestampConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset ts)
        {
            return string.Empty;
        }

        // Compare in LOCAL time — DateTimeOffset.Date returns the date
        // component of the OFFSET-LOCAL value, but we want "is this
        // today on the user's wall clock", so swap to local first.
        var local = ts.ToLocalTime();
        var today = DateTimeOffset.Now.Date;

        var uiCulture = ResolveUiCulture();

        if (local.Date == today)
        {
            return local.ToString("HH:mm", uiCulture);
        }

        // Cross-day entries: short month + day + time so the row is
        // self-describing without needing the user to hover for a
        // tooltip.  Culture is the active UI language (NOT OS culture)
        // so month abbreviations follow the user's chosen locale.
        return local.ToString("MMM d, HH:mm", uiCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static CultureInfo ResolveUiCulture()
    {
        // Soft-fail on an unknown language enum value (defensive — the
        // enum is small and exhaustive, but a future addition that
        // forgets to extend the table here would otherwise NullRef on
        // every History row render).  English is the post-rebrand
        // canonical default; matches Translator.Resolve's fallback
        // cascade.
        var language = Translator.Instance.Language;
        return CulturesByLanguage.TryGetValue(language, out var culture)
            ? culture
            : CulturesByLanguage[Language.English];
    }
}
