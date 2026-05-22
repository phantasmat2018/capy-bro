using System.Globalization;
using System.Windows.Data;

namespace CapyBro.Services;

/// <summary>
/// Extracts the OpenRouter provider segment from a model id like
/// <c>openai/gpt-4o-mini</c> → <c>openai</c>.  Used by
/// ModelsDialog's <c>CollectionViewSource.GroupDescriptions</c> to
/// group the catalogue by provider — without grouping the flat
/// alphabetical list of ~200 models is hard to scan.
///
/// PropertyGroupDescription uses the converter result as a group key
/// AND as the value bound to GroupStyle.HeaderTemplate's DataContext
/// (via the <c>Name</c> property of the auto-generated CollectionViewGroup).
/// Returning a clean lowercase provider keeps the header text
/// predictable; the user-facing capitalisation is handled at render
/// time by the header template.
///
/// Models without a "/" (rare but possible — OpenRouter's free tier
/// sometimes lists them) fall into the catch-all "other" bucket so
/// they don't disappear from the list.
/// </summary>
public sealed class ProviderPrefixConverter : IValueConverter
{
    private const string FallbackBucket = "other";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id || string.IsNullOrEmpty(id))
        {
            return FallbackBucket;
        }

        var slashIndex = id.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex <= 0)
        {
            return FallbackBucket;
        }

        return id[..slashIndex];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
