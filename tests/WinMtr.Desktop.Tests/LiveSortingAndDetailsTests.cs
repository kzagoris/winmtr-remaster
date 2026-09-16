using System.ComponentModel;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Sorting, selection, and hop details at the shell view model, with no
/// Avalonia dependency: the visible order forwards the session descriptor and
/// recalculates as values change, selection follows the same hop index while
/// it is retained, and the detail pane derives its loss from the raw counts.
/// </summary>
public class LiveSortingAndDetailsTests
{
    private static MainWindowViewModel CreateViewModel()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = [] }.BuildFactory();
        return new MainWindowViewModel(new InMemoryTargetHistoryStore(), factory);
    }

    private static HopRowViewModel Row(
        int hop,
        string host = "",
        string address = "",
        double loss = 0,
        long sent = 0,
        long received = 0,
        double? best = null,
        double? average = null,
        double? worst = null,
        double? last = null,
        string status = "") => new()
        {
            Hop = hop,
            Host = host,
            Address = address,
            LossPercent = loss,
            Sent = sent,
            Received = received,
            BestMs = best,
            AverageMs = average,
            WorstMs = worst,
            LastMs = last,
            Status = status,
        };

    [Fact]
    public void ApplySort_OrdersVisibleRows_WithoutReplacingThem()
    {
        var viewModel = CreateViewModel();
        viewModel.Rows.Add(Row(1, loss: 9, sent: 100, received: 91));
        viewModel.Rows.Add(Row(2, loss: 12.5, sent: 8, received: 7));
        viewModel.Rows.Add(Row(3, loss: 0, sent: 2, received: 2));
        HopRowViewModel[] stable = viewModel.Rows.ToArray();

        viewModel.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);

        Assert.Equal(HopSortColumn.Loss, viewModel.SortColumn);
        Assert.Equal(ListSortDirection.Descending, viewModel.SortDirection);
        Assert.Equal([2, 1, 3], viewModel.SortedRows.Select(row => row.Hop).ToArray());
        // Session order is untouched; the visible order holds the same objects.
        Assert.Equal([1, 2, 3], viewModel.Rows.Select(row => row.Hop).ToArray());
        Assert.Equal(stable.OrderByDescending(row => row.LossPercent).ToArray(), viewModel.SortedRows.ToArray());
    }

    [Fact]
    public void RowValueChange_RecalculatesVisibleOrder()
    {
        var viewModel = CreateViewModel();
        HopRowViewModel first = Row(1, sent: 9, received: 9);
        HopRowViewModel second = Row(2, sent: 56, received: 56);
        viewModel.Rows.Add(first);
        viewModel.Rows.Add(second);

        viewModel.ApplySort(HopSortColumn.Sent, ListSortDirection.Ascending);
        Assert.Equal([1, 2], viewModel.SortedRows.Select(row => row.Hop).ToArray());

        first.Sent = 100;

        Assert.Equal([2, 1], viewModel.SortedRows.Select(row => row.Hop).ToArray());
        Assert.Same(first, viewModel.SortedRows[1]);
        Assert.Same(second, viewModel.SortedRows[0]);
    }

    [Fact]
    public void ResetSortCommand_RestoresHopOrder_AndStaysAvailable()
    {
        var viewModel = CreateViewModel();
        viewModel.Rows.Add(Row(1, worst: 10));
        viewModel.Rows.Add(Row(2, worst: 90));
        viewModel.Rows.Add(Row(3, worst: 45));

        viewModel.ApplySort(HopSortColumn.Worst, ListSortDirection.Descending);
        Assert.Equal([2, 3, 1], viewModel.SortedRows.Select(row => row.Hop).ToArray());

        // Never gated: the reset stays available while tracing.
        Assert.True(viewModel.ResetSortCommand.CanExecute(null));
        viewModel.ResetSortCommand.Execute(null);

        Assert.Equal(HopSortColumn.Hop, viewModel.SortColumn);
        Assert.Equal(ListSortDirection.Ascending, viewModel.SortDirection);
        Assert.Equal([1, 2, 3], viewModel.SortedRows.Select(row => row.Hop).ToArray());
    }

    [Fact]
    public void Selection_FollowsHopIndex_AcrossValueAndAddressChanges()
    {
        var viewModel = CreateViewModel();
        viewModel.Rows.Add(Row(1, host: "first.example", address: "192.0.2.1"));
        HopRowViewModel selected = Row(2, host: "second.example", address: "198.51.100.7");
        viewModel.Rows.Add(selected);
        viewModel.Rows.Add(Row(3));

        viewModel.SelectedRow = selected;

        // A later snapshot replaces the hop's address and refreshes its
        // values in place: the selection tracks the hop index, not the
        // address, and the detail pane follows with the new figures.
        selected.Address = "203.0.113.9";
        selected.Host = "moved.example";
        selected.Status = "No response.";
        selected.Sent = 4;
        selected.Received = 2;
        selected.LossPercent = 50;
        selected.BestMs = 10;
        selected.AverageMs = 20;
        selected.WorstMs = 30;
        selected.LastMs = 30;

        Assert.Same(selected, viewModel.SelectedRow);
        Assert.Equal(2, viewModel.SelectedRow.Hop);
        Assert.Contains("Hop 2 — moved.example", viewModel.DetailText);
        Assert.Contains("203.0.113.9", viewModel.DetailText);
        Assert.Contains("No response.", viewModel.DetailText);
        Assert.Contains($"{50.0:F2}%", viewModel.DetailText);
        Assert.Contains("2 lost of 4 sent, 2 received", viewModel.DetailText);
    }

    [Fact]
    public void DetailText_ShowsLoss_WithRawCounts()
    {
        var viewModel = CreateViewModel();
        viewModel.Rows.Add(Row(
            2, host: "isp-core.example", address: "198.51.100.7",
            loss: 12.5, sent: 56, received: 49,
            best: 8, average: 11, worst: 45, last: 9));

        viewModel.SelectedRow = viewModel.Rows[0];

        Assert.Contains("Hop 2 — isp-core.example", viewModel.DetailText);
        Assert.Contains("198.51.100.7", viewModel.DetailText);
        Assert.Contains($"{12.5:F2}%", viewModel.DetailText);
        Assert.Contains("7 lost of 56 sent, 49 received", viewModel.DetailText);
        Assert.Contains("8 ms", viewModel.DetailText);
        Assert.Contains("11 ms", viewModel.DetailText);
        Assert.Contains("45 ms", viewModel.DetailText);
        Assert.Contains("9 ms", viewModel.DetailText);
    }

    [Fact]
    public void DetailText_ZeroSample_ShowsCounts_WithoutZeroLatency()
    {
        var viewModel = CreateViewModel();
        viewModel.Rows.Add(Row(1, sent: 0, received: 0, status: "Waiting for first result."));

        viewModel.SelectedRow = viewModel.Rows[0];

        Assert.Contains($"{0.0:F2}%", viewModel.DetailText);
        Assert.Contains("0 lost of 0 sent, 0 received", viewModel.DetailText);
        Assert.Contains("--", viewModel.DetailText);
        Assert.Contains("Waiting for first result.", viewModel.DetailText);
        Assert.DoesNotContain("0 ms", viewModel.DetailText);
    }

    [Fact]
    public void DetailText_FollowsFrozenMarker()
    {
        var viewModel = CreateViewModel();
        var row = Row(2, sent: 1, received: 0, status: "No response.");
        viewModel.Rows.Add(row);

        viewModel.SelectedRow = row;
        Assert.Contains("Status: No response.", viewModel.DetailText);

        row.IsFrozen = true;

        Assert.Contains("Status: No response. (frozen)", viewModel.DetailText);
    }

    [Fact]
    public void DetailText_ShowsIdentityNotice_BelowMeasurements()
    {
        var viewModel = CreateViewModel();
        var row = Row(1, host: "second.example", address: "198.51.100.7");
        viewModel.Rows.Add(row);

        viewModel.SelectedRow = row;
        row.HasIdentityChange = true;

        Assert.Contains("Hop identity changed; statistics describe only the new hop.", viewModel.DetailText);
    }

    [Fact]
    public void Selection_Releases_WhenSessionClears()
    {
        var viewModel = CreateViewModel();
        viewModel.Rows.Add(Row(1));
        viewModel.SelectedRow = viewModel.Rows[0];
        Assert.NotNull(viewModel.SelectedRow);

        // A new session begins with empty rows: the orphaned selection is
        // released and the detail pane returns to its empty text.
        viewModel.Rows.Clear();

        Assert.Null(viewModel.SelectedRow);
        Assert.Empty(viewModel.SortedRows);
        Assert.Equal("Select a hop to inspect it.", viewModel.DetailText);
    }
}
