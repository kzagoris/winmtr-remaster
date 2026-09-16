using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The grid header contract: a sort reaches the displayed rows, and the
/// active column, and only it, shows a glyph that points the way the sort
/// goes.
/// </summary>
public class GridSortHeaderTests
{
    private static HopRowViewModel Row(int hop, double loss) => new()
    {
        Hop = hop,
        Host = $"h{hop}",
        LossPercent = loss,
        Sent = 10,
        Received = 10,
        Status = string.Empty,
    };

    [AvaloniaFact]
    public void Grid_Shows_Sorted_Order()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            viewModel.Rows.Add(Row(1, 5));
            viewModel.Rows.Add(Row(2, 50));
            viewModel.Rows.Add(Row(3, 0));
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            grid.Measure(new Size(1200, 800));
            grid.Arrange(new Rect(0, 0, 1200, 800));

            viewModel.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);
            grid.Measure(new Size(1200, 800));
            grid.Arrange(new Rect(0, 0, 1200, 800));
            Dispatcher.UIThread.RunJobs();

            int[] vmOrder = viewModel.SortedRows.Select(r => r.Hop).ToArray();
            int[] visual = grid.GetVisualDescendants().OfType<DataGridRow>()
                .OrderBy(r => r.Bounds.Y)
                .Select(r => ((HopRowViewModel)r.DataContext!).Hop).ToArray();
            Assert.Equal([2, 1, 3], vmOrder);
            Assert.Equal(vmOrder, visual);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Header_Shows_Direction_Glyph_On_The_Active_Column_Only()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            grid.Measure(new Size(1200, 800));
            grid.Arrange(new Rect(0, 0, 1200, 800));
            Dispatcher.UIThread.RunJobs();

            // The default descriptor is hop ascending.
            Assert.Equal(SortGlyphConverter.Ascending, GridHeaders.Glyph(grid, "Hop"));
            Assert.Equal(string.Empty, GridHeaders.Glyph(grid, "Loss"));

            viewModel.ApplySort(HopSortColumn.Loss, ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SortGlyphConverter.Descending, GridHeaders.Glyph(grid, "Loss"));
            Assert.Equal(string.Empty, GridHeaders.Glyph(grid, "Hop"));

            viewModel.ApplySort(HopSortColumn.Loss, ListSortDirection.Ascending);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SortGlyphConverter.Ascending, GridHeaders.Glyph(grid, "Loss"));

            // A latency header carries the (ms) unit and must still show its
            // glyph: the unit is presentation, not part of the sort key.
            viewModel.ApplySort(HopSortColumn.Best, ListSortDirection.Ascending);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SortGlyphConverter.Ascending, GridHeaders.Glyph(grid, "Best (ms)"));
            Assert.Equal(string.Empty, GridHeaders.Glyph(grid, "Loss"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Latency_Headers_State_The_Millisecond_Unit()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;

            string[] titles = GridHeaders.Titles(grid);
            Assert.Equal(
                ["Hop", "Host", "Loss", "Sent", "Received", "Best (ms)", "Average (ms)", "Worst (ms)", "Last (ms)", "Status"],
                titles);
            // The value contract keeps its row property names: the unit lives
            // in the title only.
            Assert.Equal(
                ["Hop", "Host", "LossPercent", "Sent", "Received", "BestMs", "AverageMs", "WorstMs", "LastMs", "DisplayStatus"],
                grid.Columns.Select(column => column.SortMemberPath).ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData("Hop", HopSortColumn.Hop)]
    [InlineData("Host", HopSortColumn.Host)]
    [InlineData("LossPercent", HopSortColumn.Loss)]
    [InlineData("Sent", HopSortColumn.Sent)]
    [InlineData("Received", HopSortColumn.Received)]
    [InlineData("BestMs", HopSortColumn.Best)]
    [InlineData("AverageMs", HopSortColumn.Average)]
    [InlineData("WorstMs", HopSortColumn.Worst)]
    [InlineData("LastMs", HopSortColumn.Last)]
    [InlineData("DisplayStatus", HopSortColumn.Status)]
    public void Sort_member_path_names_the_displayed_column(string path, HopSortColumn expected) =>
        Assert.Equal(expected, HopSortColumns.FromSortMemberPath(path));

    [Theory]
    [InlineData("Hop", HopSortColumn.Hop)]
    [InlineData("Loss", HopSortColumn.Loss)]
    [InlineData("Best (ms)", HopSortColumn.Best)]
    [InlineData("Average (ms)", HopSortColumn.Average)]
    [InlineData("Worst (ms)", HopSortColumn.Worst)]
    [InlineData("Last (ms)", HopSortColumn.Last)]
    public void Header_title_names_the_displayed_column(string title, HopSortColumn expected) =>
        Assert.Equal(expected, HopSortColumns.FromHeaderTitle(title));

    [Theory]
    [InlineData(HopSortColumn.Best, ListSortDirection.Ascending, "Best (ms)", SortGlyphConverter.Ascending)]
    [InlineData(HopSortColumn.Best, ListSortDirection.Descending, "Best (ms)", SortGlyphConverter.Descending)]
    [InlineData(HopSortColumn.Loss, ListSortDirection.Ascending, "Best (ms)", "")]
    public void Sort_glyph_survives_the_unit_suffix(
        HopSortColumn active, ListSortDirection direction, string title, string expected) =>
        Assert.Equal(expected, SortGlyphConverter.Instance.Convert(
            [active, direction, title], typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Unknown_sort_member_path_maps_to_no_column() =>
        Assert.Null(HopSortColumns.FromSortMemberPath("NotAColumn"));
}

/// <summary>
/// Reads what a grid column header shows: its title text and its sort glyph.
/// A column carries only its title; one shared header template renders that
/// title beside the glyph, so the glyph is read from the realized header
/// rather than from the column object.
/// </summary>
public static class GridHeaders
{
    public static string[] Titles(DataGrid grid) => grid.Columns
        .Select(column => column.Header as string ?? string.Empty)
        .ToArray();

    public static string Glyph(DataGrid grid, string title)
    {
        StackPanel header = grid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .SelectMany(realized => realized.GetVisualDescendants().OfType<StackPanel>())
            .Single(panel => panel.Children.Count == 2
                && panel.Children[0] is TextBlock text
                && text.Text == title);
        return ((TextBlock)header.Children[1]).Text ?? string.Empty;
    }
}
