using System.ComponentModel;

namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// The grid's sortable columns, one per displayed column. The session pairs
/// one of these with a direction as its sort descriptor; the default is
/// <see cref="Hop"/> ascending, always one reset action away.
/// </summary>
public enum HopSortColumn
{
    Hop,
    Host,
    Loss,
    Sent,
    Received,
    Best,
    Average,
    Worst,
    Last,
    Status,
}

/// <summary>
/// Maps a grid column's sort member to the displayed column. The header text
/// is presentation only: the latency headers carry the millisecond unit, so
/// the sort key is the row value the column orders by, never the title.
/// </summary>
public static class HopSortColumns
{
    /// <summary>The unit suffix the four latency headers carry.</summary>
    public const string MillisecondsSuffix = " (ms)";

    public static HopSortColumn? FromSortMemberPath(string? sortMemberPath) => sortMemberPath switch
    {
        nameof(HopRowViewModel.Hop) => HopSortColumn.Hop,
        nameof(HopRowViewModel.Host) => HopSortColumn.Host,
        nameof(HopRowViewModel.LossPercent) => HopSortColumn.Loss,
        nameof(HopRowViewModel.Sent) => HopSortColumn.Sent,
        nameof(HopRowViewModel.Received) => HopSortColumn.Received,
        nameof(HopRowViewModel.BestMs) => HopSortColumn.Best,
        nameof(HopRowViewModel.AverageMs) => HopSortColumn.Average,
        nameof(HopRowViewModel.WorstMs) => HopSortColumn.Worst,
        nameof(HopRowViewModel.LastMs) => HopSortColumn.Last,
        nameof(HopRowViewModel.DisplayStatus) => HopSortColumn.Status,
        _ => null,
    };

    /// <summary>
    /// Maps a header title to the displayed column, for presentation that
    /// only receives the title. The latency titles state their unit, so the
    /// trailing <see cref="MillisecondsSuffix"/> is stripped before the column
    /// name is read; the unit is written here once, and
    /// <see cref="FromSortMemberPath"/> stays the authoritative sort key.
    /// </summary>
    public static HopSortColumn? FromHeaderTitle(string? title)
    {
        if (title is null)
            return null;
        string name = title.EndsWith(MillisecondsSuffix, StringComparison.Ordinal)
            ? title[..^MillisecondsSuffix.Length]
            : title;
        return Enum.TryParse(name, out HopSortColumn column) ? column : null;
    }
}

/// <summary>
/// The row value contract behind live sorting. Every grid column orders by
/// its underlying value rather than its formatted text, so 9 sent precedes 56
/// and 2% loss precedes 12.5%:
/// <list type="bullet">
/// <item>Hop, loss, sent, received, and all round-trip measurements compare
/// numerically on the row's stored values.</item>
/// <item>Host and status compare with ordinal string ordering on the displayed
/// text (status includes the frozen marker while frozen); empty hosts sort
/// first, deterministically.</item>
/// <item>A missing round-trip measurement (a hop still waiting, shown as
/// <c>--</c>) sorts after every real measurement in both directions: an
/// absent probe never ranks as fastest or slowest.</item>
/// <item>Frozen retained rows participate with their actual values; the frozen
/// flag itself is not consulted.</item>
/// <item>Equal values break the tie by hop number ascending, so the order is
/// fully deterministic.</item>
/// </list>
/// </summary>
public sealed class HopRowComparer(HopSortColumn column, ListSortDirection direction) : IComparer<HopRowViewModel>
{
    public HopSortColumn Column { get; } = column;

    public ListSortDirection Direction { get; } = direction;

    public int Compare(HopRowViewModel? x, HopRowViewModel? y) => Compare(x, y, Column, Direction);

    public static int Compare(HopRowViewModel? x, HopRowViewModel? y, HopSortColumn column, ListSortDirection direction)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        // A missing measurement keeps its absolute last place in both
        // directions: an absent probe never ranks as fastest or slowest, so
        // only real measurements are reversed by descending.
        if (column is HopSortColumn.Best or HopSortColumn.Average or HopSortColumn.Worst or HopSortColumn.Last)
        {
            double? measuredX = MeasurementOf(x, column);
            double? measuredY = MeasurementOf(y, column);
            if (measuredX is null || measuredY is null)
            {
                int missing = CompareMeasurement(measuredX, measuredY);
                return missing != 0 ? missing : x.Hop.CompareTo(y.Hop);
            }

            int real = measuredX.Value.CompareTo(measuredY.Value);
            if (real != 0 && direction == ListSortDirection.Descending)
                real = -real;
            return real != 0 ? real : x.Hop.CompareTo(y.Hop);
        }

        int primary = column switch
        {
            HopSortColumn.Hop => x.Hop.CompareTo(y.Hop),
            HopSortColumn.Host => string.CompareOrdinal(x.Host ?? string.Empty, y.Host ?? string.Empty),
            HopSortColumn.Loss => x.LossPercent.CompareTo(y.LossPercent),
            HopSortColumn.Sent => x.Sent.CompareTo(y.Sent),
            HopSortColumn.Received => x.Received.CompareTo(y.Received),
            HopSortColumn.Status => string.CompareOrdinal(
                x.DisplayStatus ?? string.Empty, y.DisplayStatus ?? string.Empty),
            _ => 0,
        };

        if (primary != 0 && direction == ListSortDirection.Descending)
            primary = -primary;
        if (primary != 0)
            return primary;

        return x.Hop.CompareTo(y.Hop);
    }

    private static double? MeasurementOf(HopRowViewModel row, HopSortColumn column) => column switch
    {
        HopSortColumn.Best => row.BestMs,
        HopSortColumn.Average => row.AverageMs,
        HopSortColumn.Worst => row.WorstMs,
        HopSortColumn.Last => row.LastMs,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    private static int CompareMeasurement(double? x, double? y)
    {
        if (x is null)
            return y is null ? 0 : 1;
        if (y is null)
            return -1;
        return x.Value.CompareTo(y.Value);
    }
}
