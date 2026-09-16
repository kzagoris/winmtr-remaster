using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Thin headless checks that the session state and commands, the
/// target/status surfaces, the route grid, and the row fields are connected to
/// the view. Behaviour lives in <see cref="TraceSessionTests"/>; these assert
/// connection, not appearance.
/// </summary>
public class TraceSessionBindingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateShell(Func<Tracer> factory)
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), factory);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        return (window, viewModel);
    }

    private static Func<Tracer> LiveFactory(out Target target)
    {
        target = RouteScript.TestTarget();
        var first = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var second = new Route(
            target,
            ImmutableArray.Create(
                RouteScript.RespondingHop(
                    0, "192.0.2.1", "router.example",
                    HopStatistics.Empty.Record(ProbeOutcome.Expired, 10).Record(ProbeOutcome.Expired, 12)),
                RouteScript.SilentHop(1, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0))),
            T0 + TimeSpan.FromSeconds(5),
            destinationReached: true,
            hopLimitObserved: false);
        return new TracerScript { Target = target, Snapshots = [first, second] }.BuildFactory();
    }

    [AvaloniaFact]
    public async Task SessionState_Grid_And_Status_AreBound_EndToEnd()
    {
        var (window, viewModel) = CreateShell(LiveFactory(out _));
        try
        {
            var startStop = window.FindControl<Button>("StartStopButton")!;
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var state = window.FindControl<TextBlock>("SessionStateText")!;
            var statusTarget = window.FindControl<TextBlock>("StatusTargetText")!;
            var elapsed = window.FindControl<TextBlock>("ElapsedText")!;
            var destination = window.FindControl<TextBlock>("DestinationText")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;
            var empty = window.FindControl<TextBlock>("EmptyStateText")!;

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);

            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 2);

            // State and commands follow the session back to idle with rows kept.
            Assert.Equal("Start", startStop.Content);
            Assert.Equal("Idle", state.Text);

            // The grid is bound to the visible row order.
            Assert.Same(viewModel.SortedRows, grid.ItemsSource);
            Assert.Equal(2, viewModel.SortedRows.Count);
            Assert.False(empty.IsVisible);

            // The status surfaces show the accepted target as the last run
            // (the session already ended idle), elapsed time from the
            // snapshot timestamps, and destination confirmation.
            Assert.Equal("Last run: example.com", statusTarget.Text);
            Assert.Equal("Elapsed 00:05", elapsed.Text);
            Assert.Equal("Destination confirmed", destination.Text);

            // Row fields flow through selection into the detail pane,
            // including the empty host of a silent hop.
            viewModel.SelectedRow = viewModel.Rows[0];
            Assert.Same(viewModel.Rows[0], grid.SelectedItem);
            Assert.Contains("router.example", detail.Text);

            viewModel.SelectedRow = viewModel.Rows[1];
            Assert.StartsWith("Hop 2 — ", detail.Text);
            Assert.Contains("No response.", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Frozen_And_IdentityChanged_Rows_AreExposedToGrid()
    {
        Target target = RouteScript.TestTarget();
        var snapshots = RouteScript.TimestampedSequence(
            target, T0, TimeSpan.FromSeconds(5),
            [
                RouteScript.RespondingHop(
                    0, "192.0.2.1", "first.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)),
                RouteScript.SilentHop(1, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0)),
            ],
            [
                RouteScript.RespondingHop(
                    0, "198.51.100.7", "moved.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 11)),
            ]);
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = snapshots }.BuildFactory();
        var (window, viewModel) = CreateShell(factory);
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);

            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 2);

            // The trimmed hop freezes at its last true values; the surviving
            // hop takes the replacement identity with a transient highlight.
            HopRowViewModel changed = viewModel.Rows[0];
            HopRowViewModel frozen = viewModel.Rows[1];
            Assert.False(changed.IsFrozen);
            Assert.True(changed.HasIdentityChange);
            Assert.Equal("198.51.100.7", changed.Address);
            Assert.Equal("moved.example", changed.Host);
            Assert.Equal(11, changed.LastMs);
            Assert.True(frozen.IsFrozen);
            Assert.Equal("No response. (frozen)", frozen.DisplayStatus);
            Assert.Equal(1, frozen.Sent);

            // The grid realizes both retained rows: the frozen marker, the
            // identity indication, identity fields, and measurements all flow
            // through bound cells and row classes. No appearance is asserted.
            IReadOnlyList<DataGridRow> RealizedRows() => grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(container => container.DataContext is HopRowViewModel)
                .ToList();
            // Row containers appear on a layout pass, which the headless
            // dispatcher does not run on its own: measure and arrange the
            // grid explicitly until both retained rows are realized.
            for (int i = 0; i < 100 && RealizedRows().Count != 2; i++)
            {
                grid.Measure(new Size(1000, 500));
                grid.Arrange(new Rect(0, 0, 1000, 500));
                await Task.Delay(10);
            }

            Assert.Equal(2, RealizedRows().Count);

            DataGridRow changedContainer = RealizedRows().Single(container => ReferenceEquals(container.DataContext, changed));
            DataGridRow frozenContainer = RealizedRows().Single(container => ReferenceEquals(container.DataContext, frozen));
            Assert.Contains("identity-changed", changedContainer.Classes);
            Assert.DoesNotContain("frozen", changedContainer.Classes);
            Assert.Contains("frozen", frozenContainer.Classes);
            Assert.DoesNotContain("identity-changed", frozenContainer.Classes);

            static IReadOnlyList<string> CellTexts(DataGridRow container) => container.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(cell => cell.Text ?? string.Empty)
                .ToList();
            Assert.Contains(CellTexts(changedContainer), text => text.Contains("moved.example", StringComparison.Ordinal));
            Assert.Contains(CellTexts(changedContainer), text => text.Contains("11", StringComparison.Ordinal));
            Assert.Contains(CellTexts(frozenContainer), text => text.Contains("(frozen)", StringComparison.Ordinal));

            // The detail pane follows the selection through both states.
            viewModel.SelectedRow = changed;
            Assert.Contains("moved.example", detail.Text);
            Assert.Contains("Hop identity changed;", detail.Text);

            viewModel.SelectedRow = frozen;
            Assert.Contains("(frozen)", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Host_Cell_Shows_The_Address_Second_Line_Only_When_It_Differs()
    {
        Target target = RouteScript.TestTarget();
        Route snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(
                0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)),
            RouteScript.RespondingHop(
                1, "192.0.2.2", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 11)),
            RouteScript.SilentHop(2, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0)));
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = [snapshot] }.BuildFactory();
        var (window, viewModel) = CreateShell(factory);
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);

            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 3);

            // One responding hop with a name, one with resolution off (the
            // label is the address), and one silent hop.
            HopRowViewModel resolved = viewModel.Rows[0];
            HopRowViewModel unresolved = viewModel.Rows[1];
            HopRowViewModel silent = viewModel.Rows[2];
            Assert.Equal("router.example", resolved.Host);
            Assert.Equal("192.0.2.1", resolved.Address);
            Assert.True(resolved.ShowAddress);
            Assert.Equal("192.0.2.2", unresolved.Host);
            Assert.False(unresolved.ShowAddress);
            Assert.Equal(string.Empty, silent.Address);
            Assert.False(silent.ShowAddress);

            IReadOnlyList<DataGridRow> RealizedRows() => grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(container => container.DataContext is HopRowViewModel)
                .ToList();
            // Row containers appear on a layout pass, which the headless
            // dispatcher does not run on its own: measure and arrange the
            // grid explicitly until all three rows are realized.
            for (int i = 0; i < 100 && RealizedRows().Count != 3; i++)
            {
                grid.Measure(new Size(1000, 500));
                grid.Arrange(new Rect(0, 0, 1000, 500));
                await Task.Delay(10);
            }

            Assert.Equal(3, RealizedRows().Count);

            // The Host cell is a two-line stack: the label TextBlock over the
            // address TextBlock. The address line carries the host-address
            // class, so the cell is found without row-specific names; the
            // other two-line stacks in the grid (severity cells) do not.
            static StackPanel HostCell(DataGridRow container) => container.GetVisualDescendants()
                .OfType<StackPanel>()
                .Single(panel => panel.Children.Count == 2
                    && panel.Children[0] is TextBlock
                    && panel.Children[1] is TextBlock address
                    && address.Classes.Contains("host-address"));

            static (string Host, TextBlock Address) HostCellLines(DataGridRow container)
            {
                StackPanel cell = HostCell(container);
                return (((TextBlock)cell.Children[0]).Text ?? string.Empty, (TextBlock)cell.Children[1]);
            }

            DataGridRow resolvedContainer = RealizedRows().Single(container => ReferenceEquals(container.DataContext, resolved));
            (string resolvedHost, TextBlock resolvedAddress) = HostCellLines(resolvedContainer);
            Assert.Equal("router.example", resolvedHost);
            Assert.Equal("192.0.2.1", resolvedAddress.Text);
            Assert.True(resolvedAddress.IsVisible);

            DataGridRow unresolvedContainer = RealizedRows().Single(container => ReferenceEquals(container.DataContext, unresolved));
            (string unresolvedHost, TextBlock unresolvedAddress) = HostCellLines(unresolvedContainer);
            Assert.Equal("192.0.2.2", unresolvedHost);
            Assert.Equal("192.0.2.2", unresolvedAddress.Text);
            Assert.False(unresolvedAddress.IsVisible);

            DataGridRow silentContainer = RealizedRows().Single(container => ReferenceEquals(container.DataContext, silent));
            (string silentHost, TextBlock silentAddress) = HostCellLines(silentContainer);
            Assert.Equal(string.Empty, silentHost);
            Assert.Equal(string.Empty, silentAddress.Text);
            Assert.False(silentAddress.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Sorting_Reset_Selection_And_Detail_AreBound()
    {
        var (window, viewModel) = CreateShell(LiveFactory(out _));
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;

            // Every grid column offers sorting through the session's row value
            // contract, identified by its sort member; the grid shows the
            // session's visible order. No appearance is asserted.
            Assert.True(grid.CanUserSortColumns);
            Assert.Equal(
                ["Hop", "Host", "Loss", "Sent", "Received", "Best (ms)", "Average (ms)", "Worst (ms)", "Last (ms)", "Status"],
                GridHeaders.Titles(grid));
            Assert.All(grid.Columns, column => Assert.True(column.CanUserSort));
            Assert.All(
                grid.Columns,
                column => Assert.False(string.IsNullOrEmpty(column.SortMemberPath)));
            Assert.Same(viewModel.SortedRows, grid.ItemsSource);

            // No reset button exists: a third select of the active header calls
            // the same reset path, which stays available through the session.
            Assert.Null(window.FindControl<Button>("ResetSortButton"));
            Assert.True(viewModel.ResetSortCommand.CanExecute(null));

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);

            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.SortedRows.Count == 2);

            // Sorting by loss puts the silent 100%-loss hop first; the third
            // select restores hop order through the reset path.
            viewModel.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);
            Assert.Equal([2, 1], viewModel.SortedRows.Select(row => row.Hop).ToArray());

            viewModel.ResetSort();
            Assert.Equal([1, 2], viewModel.SortedRows.Select(row => row.Hop).ToArray());
            Assert.Equal(HopSortColumn.Hop, viewModel.SortColumn);

            // Selection stays two-way bound and the detail pane follows it
            // with the selected hop's fields.
            viewModel.SelectedRow = viewModel.SortedRows[0];
            Assert.Same(viewModel.SelectedRow, grid.SelectedItem);
            Assert.Contains("Hop 1", detail.Text);
            Assert.Contains("router.example", detail.Text);
            Assert.Contains("192.0.2.1", detail.Text);
            Assert.Contains("Loss:", detail.Text);
            Assert.Contains("sent", detail.Text);
            Assert.Contains("received", detail.Text);
            Assert.Contains("Best:", detail.Text);
            Assert.Contains("Status:", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TargetEntry_ValidatesInline_AndGatesStart()
    {
        var (window, viewModel) = CreateShell(LiveFactory(out _));
        try
        {
            var box = window.FindControl<AutoCompleteBox>("TargetInput")!;
            var startStop = window.FindControl<Button>("StartStopButton")!;

            // The toolbar no longer shows the target error, so the test reads
            // the error from the view model. The Start button stays a view fact.
            Assert.False(viewModel.HasTargetError);
            Assert.False(startStop.IsEffectivelyEnabled);

            // Typing an invalid target rejects inline and disables Start.
            box.Text = "999.999.999.999";

            Assert.Equal("999.999.999.999", viewModel.Target);
            Assert.True(viewModel.HasTargetError);
            Assert.False(string.IsNullOrEmpty(viewModel.TargetError));
            Assert.False(startStop.IsEffectivelyEnabled);

            // A valid target clears the error and enables Start.
            box.Text = "example.com";

            Assert.False(viewModel.HasTargetError);
            Assert.True(startStop.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Controls_Disable_WhileSessionRuns()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var (window, viewModel) = CreateShell(factory);
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            var targetInput = window.FindControl<AutoCompleteBox>("TargetInput")!;
            var startStop = window.FindControl<Button>("StartStopButton")!;

            Assert.True(settings.IsEnabled);
            Assert.True(targetInput.IsEnabled);

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);

            await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);

            // Live rows arrive while the session runs.
            Assert.Single(viewModel.Rows);
            Assert.Equal("Stop", startStop.Content);
            // The button that offers Stop must stay operable while the trace
            // runs: only a live command keeps the stop reachable.
            Assert.True(startStop.IsEnabled);
            Assert.True(viewModel.StartStopCommand.CanExecute(null));
            Assert.False(settings.IsEnabled);
            Assert.False(targetInput.IsEnabled);

            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle");

            Assert.True(settings.IsEnabled);
            Assert.True(targetInput.IsEnabled);
            Assert.True(engine.Disposed);
        }
        finally
        {
            window.Close();
        }
    }

    // Ten hops whose averages rise with the hop number, so an Average
    // descending sort reverses the route order exactly and every sort in the
    // regression below moves the picked row a long way.
    private static Func<Tracer> TenHopFactory()
    {
        Target target = RouteScript.TestTarget();
        var hops = new List<Hop>(10);
        for (int i = 0; i < 10; i++)
            hops.Add(RouteScript.RespondingHop(
                i,
                $"192.0.2.{i + 1}",
                $"r{10 - i}.example",
                HopStatistics.Empty.Record(ProbeOutcome.Expired, 10 + (i * 5))));
        Route snapshot = RouteScript.Snapshot(target, T0, ImmutableArray.CreateRange(hops));
        return new TracerScript { Target = target, Snapshots = [snapshot] }.BuildFactory();
    }

    [AvaloniaFact]
    public async Task Selection_NamesTheHop_NotThePlace_AcrossEverySort()
    {
        var (window, viewModel) = CreateShell(TenHopFactory());
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.SortedRows.Count == 10);

            // The user sorts by Average descending and picks the last row,
            // which is hop 1 at that moment.
            viewModel.ApplySort(HopSortColumn.Average, ListSortDirection.Descending);
            Assert.Equal([10, 9, 8, 7, 6, 5, 4, 3, 2, 1], viewModel.SortedRows.Select(row => row.Hop).ToArray());

            grid.SelectedItem = viewModel.SortedRows[9];
            HopRowViewModel picked = viewModel.SelectedRow!;
            Assert.Equal(1, picked.Hop);

            // Three different sorts move that row to three different places.
            // The selection names the hop, so it holds the same row object
            // through all of them, and the detail pane keeps describing it.
            viewModel.ResetSort();
            Assert.Same(picked, viewModel.SelectedRow);
            Assert.Same(picked, grid.SelectedItem);
            Assert.Equal(0, grid.SelectedIndex);

            viewModel.ApplySort(HopSortColumn.Host, ListSortDirection.Ascending);
            Assert.Same(picked, viewModel.SelectedRow);
            Assert.Same(picked, grid.SelectedItem);

            viewModel.ApplySort(HopSortColumn.Average, ListSortDirection.Ascending);
            Assert.Same(picked, viewModel.SelectedRow);
            Assert.Same(picked, grid.SelectedItem);

            Assert.StartsWith("Hop 1 ", detail.Text);
            Assert.Contains("192.0.2.1", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
