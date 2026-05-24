using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CapyBro.Services;

/// <summary>
/// Inverse of WPF's built-in <see cref="BooleanToVisibilityConverter"/>:
/// <c>true</c> → <see cref="Visibility.Collapsed"/>, <c>false</c> →
/// <see cref="Visibility.Visible"/>.  Used where two views toggle between
/// the SAME visual slot (e.g. Diff scroller vs. Edit TextBox in
/// <see cref="Views.DiffPreviewWindow"/>) and one of the two needs
/// "show when bool is false" semantics — flipping a Visibility binding's
/// branch with a converter keeps both elements in the same parent panel,
/// avoiding the layout reflow of a DataTrigger swap.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public static InverseBooleanToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Collapsed;
}
