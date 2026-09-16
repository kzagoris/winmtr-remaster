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
/// The hop grid contract on a narrow but legal window: when its columns do
/// not fit, the grid shows a horizontal scrollbar so the user can reach every
/// column. The vertical scroll contract does not change.
/// </summary>
public class GridHorizontalScrollTests
{
    private static HopRowViewModel Row(int hop) => new()
    {
        Hop = hop,
        Host = $"host-{hop}.example.net",
        LossPercent = 0,
        Sent = 10,
        Received = 10,
        Status = string.Empty,
    };

    private static MainWindow CreateShell()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        viewModel.Rows.Add(Row(1));
        viewModel.Rows.Add(Row(2));
        viewModel.Rows.Add(Row(3));
        return window;
    }

    private static ScrollBar HorizontalScrollBar(DataGrid grid) =>
        grid.GetVisualDescendants()
            .OfType<ScrollBar>()
            .Single(bar => bar.Name == "PART_HorizontalScrollbar");

    [AvaloniaFact]
    public void Narrow_Legal_Window_Can_Scroll_To_The_Last_Column()
    {
        var window = CreateShell();
        try
        {
            // The narrowest legal window, never below it. A wider window has
            // no overflow, so this width is where the behaviour matters.
            Assert.Equal(560, window.MinWidth);
            Assert.Equal(480, window.MinHeight);
            window.Width = window.MinWidth;

            var grid = window.FindControl<DataGrid>("HopGrid")!;
            grid.Measure(new Size(window.MinWidth, 400));
            grid.Arrange(new Rect(0, 0, window.MinWidth, 400));
            Dispatcher.UIThread.RunJobs();

            // The grid asks for a horizontal scrollbar when one is needed.
            Assert.Equal(ScrollBarVisibility.Auto, grid.HorizontalScrollBarVisibility);

            // At this width it really needs one: the columns are wider than
            // the grid, and the template's horizontal scrollbar is on screen
            // with a travel range that reaches the last column.
            double columnsWidth = grid.Columns.Sum(column => column.ActualWidth);
            string columnWidths = string.Join(
                ", ",
                grid.Columns.Select(column => $"{column.Header}={column.ActualWidth}"));
            Assert.True(
                columnsWidth > grid.Bounds.Width,
                $"The columns ({columnsWidth}) must be wider than the grid ({grid.Bounds.Width}) to prove the overflow. Widths: {columnWidths}.");
            var horizontal = HorizontalScrollBar(grid);
            Assert.True(horizontal.IsVisible, "The horizontal scrollbar must be visible at the narrow width.");
            Assert.True(
                horizontal.Maximum > 0,
                $"The horizontal scrollbar must have travel range, but Maximum is {horizontal.Maximum}.");

            // Vertical scrolling keeps its setting: this change touched only
            // the horizontal axis.
            Assert.Equal(ScrollBarVisibility.Auto, grid.VerticalScrollBarVisibility);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wide_Window_Shows_All_Columns_Without_Horizontal_Scroll()
    {
        var window = CreateShell();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            window.Width = 2400;
            Dispatcher.UIThread.RunJobs();

            // All columns fit, so the scrollbar stays off.
            var horizontal = HorizontalScrollBar(grid);
            Assert.False(horizontal.IsVisible);
            Assert.Equal(0d, horizontal.Maximum);

            // Host keeps its star sizing and fills the free space.
            var host = grid.Columns.Single(column => (string?)column.Header == "Host");
            string widths = string.Join(
                ", ",
                grid.Columns.Select(column => $"{column.Header}={column.ActualWidth}"));
            Assert.True(
                host.ActualWidth > host.MinWidth,
                $"The Host column should fill the free space. Widths: {widths}.");
        }
        finally
        {
            window.Close();
        }
    }
}
