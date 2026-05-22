using System.Globalization;
using System.Windows.Data;

namespace CapyBro.Services;

public sealed class NullToFalseConverter : IValueConverter
{
    public static NullToFalseConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && (value is not string s || !string.IsNullOrEmpty(s));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
