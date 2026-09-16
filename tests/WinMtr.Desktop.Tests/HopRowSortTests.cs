using System.ComponentModel;
using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The row value contract behind live sorting: numeric columns order by value
/// rather than formatted text, host and status order ordinally, waiting
/// measurements keep a stable last place, and frozen rows participate with
/// their actual values. No Avalonia dependency.
/// </summary>
public class HopRowSortTests
{
    public static TheoryData<HopSortColumn, double, double> NumericCases => new()
    {
        // Each pair distinguishes numeric from lexicographic ordering: as
        // text, "10" and "12.5" and "56" all precede "2" and "9".
        { HopSortColumn.Hop, 2, 10 },
        { HopSortColumn.Loss, 2.0, 12.5 },
        { HopSortColumn.Sent, 9, 56 },
        { HopSortColumn.Received, 9, 56 },
        { HopSortColumn.Best, 9, 80 },
        { HopSortColumn.Average, 9, 80 },
        { HopSortColumn.Worst, 9, 80 },
        { HopSortColumn.Last, 9, 80 },
    };

    [Theory]
    [MemberData(nameof(NumericCases))]
    public void NumericColumns_OrderByValue_NotFormattedText(HopSortColumn column, double low, double high)
    {
        HopRowViewModel first = RowFor(column, low, hop: 1);
        HopRowViewModel second = RowFor(column, high, hop: 2);

        Assert.True(HopRowComparer.Compare(first, second, column, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(second, first, column, ListSortDirection.Ascending) > 0);
        Assert.True(HopRowComparer.Compare(first, second, column, ListSortDirection.Descending) > 0);
        Assert.True(HopRowComparer.Compare(second, first, column, ListSortDirection.Descending) < 0);
    }

    [Fact]
    public void Host_OrdersOrdinal_EmptyFirst()
    {
        var silent = new HopRowViewModel { Hop = 1, Host = string.Empty };
        var upper = new HopRowViewModel { Hop = 2, Host = "Zebra" };
        var lower = new HopRowViewModel { Hop = 3, Host = "apple" };

        // Ordinal, not case-insensitive: 'Z' (0x5A) precedes 'a' (0x61), and
        // the empty host of a silent hop sorts first, deterministically.
        Assert.True(HopRowComparer.Compare(silent, upper, HopSortColumn.Host, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(upper, lower, HopSortColumn.Host, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(lower, upper, HopSortColumn.Host, ListSortDirection.Descending) < 0);
    }

    [Fact]
    public void Status_OrdersDisplayedText()
    {
        var ordinary = new HopRowViewModel { Hop = 1, Status = string.Empty };
        var timeout = new HopRowViewModel { Hop = 2, Status = "No response." };

        Assert.True(HopRowComparer.Compare(ordinary, timeout, HopSortColumn.Status, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(timeout, ordinary, HopSortColumn.Status, ListSortDirection.Descending) < 0);
    }

    [Fact]
    public void Status_IncludesFrozenMarker_Deterministically()
    {
        var live = new HopRowViewModel { Hop = 1, Status = "No response.", IsFrozen = false };
        var frozen = new HopRowViewModel { Hop = 2, Status = "No response.", IsFrozen = true };

        // The frozen row participates through its displayed status text, with
        // no special sort rule: "No response." precedes "No response. (frozen)".
        Assert.True(HopRowComparer.Compare(live, frozen, HopSortColumn.Status, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(frozen, live, HopSortColumn.Status, ListSortDirection.Ascending) > 0);
    }

    public static TheoryData<HopSortColumn> MeasurementColumns => new()
    {
        { HopSortColumn.Best },
        { HopSortColumn.Average },
        { HopSortColumn.Worst },
        { HopSortColumn.Last },
    };

    [Theory]
    [MemberData(nameof(MeasurementColumns))]
    public void MissingMeasurements_SortLast_InBothDirections(HopSortColumn column)
    {
        HopRowViewModel waiting = RowFor(column, null, hop: 1);
        HopRowViewModel measured = RowFor(column, 5, hop: 2);

        // An absent probe never ranks as fastest or slowest.
        Assert.True(HopRowComparer.Compare(measured, waiting, column, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(measured, waiting, column, ListSortDirection.Descending) < 0);
        Assert.True(HopRowComparer.Compare(waiting, measured, column, ListSortDirection.Ascending) > 0);
        Assert.True(HopRowComparer.Compare(waiting, measured, column, ListSortDirection.Descending) > 0);
    }

    [Fact]
    public void EqualValues_BreakTies_ByHopAscending()
    {
        var third = new HopRowViewModel { Hop = 3, LossPercent = 50 };
        var first = new HopRowViewModel { Hop = 1, LossPercent = 50 };

        Assert.True(HopRowComparer.Compare(first, third, HopSortColumn.Loss, ListSortDirection.Ascending) < 0);
        Assert.True(HopRowComparer.Compare(first, third, HopSortColumn.Loss, ListSortDirection.Descending) < 0);
    }

    [Fact]
    public void FrozenFlag_Ignored_ExceptThroughDisplayedStatus()
    {
        var live = new HopRowViewModel { Hop = 1, LossPercent = 90, IsFrozen = false };
        var frozen = new HopRowViewModel { Hop = 1, LossPercent = 90, IsFrozen = true };

        Assert.Equal(0, HopRowComparer.Compare(live, frozen, HopSortColumn.Loss, ListSortDirection.Ascending));
        Assert.Equal(0, HopRowComparer.Compare(live, frozen, HopSortColumn.Loss, ListSortDirection.Descending));
    }

    private static HopRowViewModel RowFor(HopSortColumn column, double? value, int hop)
    {
        var row = new HopRowViewModel { Hop = hop };
        switch (column)
        {
            case HopSortColumn.Hop:
                row.Hop = (int)value!.Value;
                break;
            case HopSortColumn.Loss:
                row.LossPercent = value!.Value;
                break;
            case HopSortColumn.Sent:
                row.Sent = (long)value!.Value;
                break;
            case HopSortColumn.Received:
                row.Received = (long)value!.Value;
                break;
            case HopSortColumn.Best:
                row.BestMs = value;
                break;
            case HopSortColumn.Average:
                row.AverageMs = value;
                break;
            case HopSortColumn.Worst:
                row.WorstMs = value;
                break;
            case HopSortColumn.Last:
                row.LastMs = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(column));
        }

        return row;
    }
}
