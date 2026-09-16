using System.ComponentModel;
using WinMtr.Desktop.Sessions;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Live sorting at the session surface, driven by scripted route snapshots
/// with no Avalonia dependency and no network traffic: numeric ordering that
/// survives updates, live reordering as values change, retained-value sorting,
/// one-action reset, and stable row identity throughout.
/// </summary>
public class TraceSessionSortingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static TraceSession CreateSession()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = [] }.BuildFactory();
        return new TraceSession(factory);
    }

    private static Hop LossHop(int index, int sent, int received, string? hostName = null) =>
        RouteScript.RespondingHop(
            index, $"192.0.2.{index + 1}", hostName ?? $"router-{index + 1}.example",
            Stats(sent, received));

    private static HopStatistics Stats(int sent, int received, int rttMs = 10)
    {
        HopStatistics stats = HopStatistics.Empty;
        for (int i = 0; i < received; i++)
            stats = stats.Record(ProbeOutcome.Expired, rttMs);
        for (int i = received; i < sent; i++)
            stats = stats.Record(ProbeOutcome.TimedOut, 0);
        return stats;
    }

    [Fact]
    public void DefaultOrder_IsHopAscending_WithStableIdentities()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, LossHop(0, 100, 91), LossHop(1, 8, 7), LossHop(2, 1, 1)));

        Assert.Equal(HopSortColumn.Hop, session.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, session.SortDirection);
        Assert.Equal([1, 2, 3], session.SortedRows.Select(row => row.Hop).ToArray());
        Assert.Equal(session.Rows.ToArray(), session.SortedRows.ToArray());
    }

    [Fact]
    public void SortByLoss_OrdersNumerically_NotLexicographically()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        // 9% ("9...") versus 12.5% ("12.5..."): lexicographic text order would
        // place 12.5 before 9 in both directions.
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, LossHop(0, 100, 91), LossHop(1, 8, 7), LossHop(2, 2, 0)));

        session.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);
        Assert.Equal([3, 2, 1], session.SortedRows.Select(row => row.Hop).ToArray());

        session.ApplySort(HopSortColumn.Loss, ListSortDirection.Ascending);
        Assert.Equal([1, 2, 3], session.SortedRows.Select(row => row.Hop).ToArray());
    }

    [Fact]
    public void SortBySent_OrdersNumerically()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, LossHop(0, 9, 9), LossHop(1, 56, 56)));

        session.ApplySort(HopSortColumn.Sent, ListSortDirection.Ascending);

        // Lexicographic text order would place "56" before "9".
        Assert.Equal([1, 2], session.SortedRows.Select(row => row.Hop).ToArray());
    }

    [Fact]
    public void SortSurvivesSnapshots_AndReordersAsValuesChange()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, LossHop(0, 10, 9), LossHop(1, 2, 1)));
        HopRowViewModel first = session.Rows[0];
        HopRowViewModel second = session.Rows[1];

        session.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);
        Assert.Equal([2, 1], session.SortedRows.Select(row => row.Hop).ToArray());

        // The next snapshot reverses the losses: the visible order follows the
        // values while the descriptor and the row objects stand still.
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1), LossHop(0, 10, 2), LossHop(1, 10, 9)));

        Assert.Equal(HopSortColumn.Loss, session.SortColumn);
        Assert.Equal(ListSortDirection.Descending, session.SortDirection);
        Assert.Equal([1, 2], session.SortedRows.Select(row => row.Hop).ToArray());
        Assert.Same(first, session.Rows[0]);
        Assert.Same(second, session.Rows[1]);
        Assert.Same(first, session.SortedRows[0]);
        Assert.Same(second, session.SortedRows[1]);
    }

    [Fact]
    public void FrozenRows_SortOnActualValues()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, LossHop(0, 2, 2), LossHop(1, 2, 1), LossHop(2, 1, 0)));
        session.ApplySnapshot(RouteScript.Snapshot(target, T0 + TimeSpan.FromSeconds(1), LossHop(0, 2, 2)));

        Assert.True(session.Rows[1].IsFrozen);
        Assert.True(session.Rows[2].IsFrozen);

        session.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);

        // The retained rows rank by the values they froze with: 100, 50, 0.
        Assert.Equal([3, 2, 1], session.SortedRows.Select(row => row.Hop).ToArray());
        Assert.Equal(100, session.SortedRows[0].LossPercent);
        Assert.Equal(50, session.SortedRows[1].LossPercent);
        Assert.Equal(0, session.SortedRows[2].LossPercent);
    }

    [Fact]
    public void ResetSort_RestoresHopOrder_InOneAction()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0,
            LossHop(0, 2, 2, "zebra.example"),
            LossHop(1, 2, 2, "apple.example"),
            LossHop(2, 2, 2, "middle.example")));

        session.ApplySort(HopSortColumn.Host, ListSortDirection.Ascending);
        Assert.Equal([2, 3, 1], session.SortedRows.Select(row => row.Hop).ToArray());

        session.ResetSort();

        Assert.Equal(HopSortColumn.Hop, session.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, session.SortDirection);
        Assert.Equal([1, 2, 3], session.SortedRows.Select(row => row.Hop).ToArray());
    }

    [Fact]
    public void Sorting_NeverReplacesRowObjects()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, LossHop(0, 10, 9), LossHop(1, 2, 1), LossHop(2, 4, 4)));
        HopRowViewModel[] stable = session.Rows.ToArray();

        session.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);
        session.ApplySort(HopSortColumn.Worst, ListSortDirection.Ascending);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1), LossHop(0, 10, 2), LossHop(1, 10, 9)));
        session.ResetSort();

        Assert.Equal(stable, session.Rows.ToArray());
        Assert.Equal(stable.OrderBy(row => row.Hop).ToArray(), session.SortedRows.ToArray());
    }

    [Fact]
    public void TrimmingSnapshot_RetainsVisibleOrder_UnderDescriptor()
    {
        Target target = RouteScript.TestTarget();
        var session = CreateSession();
        session.ApplySnapshot(RouteScript.Snapshot(target, T0, LossHop(0, 2, 2), LossHop(1, 2, 1)));
        session.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);

        session.ApplySnapshot(RouteScript.Snapshot(target, T0 + TimeSpan.FromSeconds(1), LossHop(0, 2, 2)));

        // Trailing-silence trimming retains rather than removes: both rows
        // stay, one frozen, and the descriptor still governs the order, with
        // the frozen 50%-loss row ahead of the live 0%-loss row.
        Assert.Equal(2, session.SortedRows.Count);
        Assert.Equal([2, 1], session.SortedRows.Select(row => row.Hop).ToArray());
        Assert.True(session.SortedRows[0].IsFrozen);
        Assert.False(session.SortedRows[1].IsFrozen);
    }
}
