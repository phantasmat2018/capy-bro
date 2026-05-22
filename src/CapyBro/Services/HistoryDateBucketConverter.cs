using System.Globalization;
using System.Windows.Data;

namespace CapyBro.Services;

/// <summary>
/// Bucket converter for the History tab's date-grouped list.  Maps a
/// <see cref="DateTimeOffset"/> to one of four localised labels:
/// "Today" / "Yesterday" / "This week" / "Older".
///
/// Used as the value-converter on <c>PropertyGroupDescription</c> in
/// <c>HistoryTab.xaml</c>'s <c>CollectionViewSource</c>.  The
/// converter result is both the group key (so equal labels collapse
/// into one group) and the value bound to GroupStyle.HeaderTemplate's
/// DataContext via <c>CollectionViewGroup.Name</c>.
///
/// Bucket boundaries (all wall-clock local time):
///   • Today          — entry's date == today's date
///   • Yesterday      — entry's date == today - 1 day
///   • This week      — entry's date within the last 7 days
///                      (excluding today/yesterday — so days 2-7 ago)
///   • Older          — everything else
///
/// Group ordering relies on the source collection being sorted
/// newest-first (HistoryStore.Snapshot guarantees that): WPF's
/// CollectionView adds groups in the order their key is first
/// encountered, so newest entries → "Today" appears first, then
/// "Yesterday", etc.  Sorting by anything else would scramble
/// the visual reading order.
/// </summary>
public sealed class HistoryDateBucketConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset ts)
        {
            return Translator.Instance["history_bucket_older"];
        }

        // Wall-clock local comparison — "is this today on the user's
        // calendar".  Without ToLocalTime, an entry created near
        // midnight UTC could land in the wrong bucket relative to
        // the user's local day boundary.
        var local = ts.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;

        if (local == today)
        {
            return Translator.Instance["history_bucket_today"];
        }

        if (local == today.AddDays(-1))
        {
            return Translator.Instance["history_bucket_yesterday"];
        }

        // Days 2-7 ago: "This week".  Inclusive on the older side
        // (today - 7 still counts) so the bucket spans a full week
        // window that pairs cleanly with "Older".
        if (local >= today.AddDays(-7))
        {
            return Translator.Instance["history_bucket_this_week"];
        }

        return Translator.Instance["history_bucket_older"];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
