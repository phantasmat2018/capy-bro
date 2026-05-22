using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CapyBro.Services;

/// <summary>
/// Multi-value converter for "is this item the chosen one" comparisons
/// against another bound value.  Today it powers the default-prompt
/// Check glyph on PromptsTab — an ItemTemplate binds the per-row
/// string and the VM's <c>DefaultPromptKey</c> property as the two
/// values, and the converter flips a sibling Path's
/// <see cref="Visibility"/> so the indicator only paints next to the
/// row that matches.
///
/// Returns <see cref="Visibility.Hidden"/> (not Collapsed) for the
/// non-matching case so the layout column reserves space — without
/// that, the row's text would jump left when the indicator
/// disappears, creating an awkward shimmy as the user scrolls.
///
/// Ordinal string comparison rather than CurrentCulture: prompt keys
/// are user-typed identifiers stored verbatim, and a culture-aware
/// compare on something like "İ" vs "I" would produce surprising
/// "default" highlights on prompts whose names visually differ.
/// </summary>
public sealed class StringEqualsToVisibilityConverter : IMultiValueConverter
{
    public static StringEqualsToVisibilityConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
        {
            return Visibility.Hidden;
        }

        var a = values[0]?.ToString();
        var b = values[1]?.ToString();
        return string.Equals(a, b, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Hidden;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
