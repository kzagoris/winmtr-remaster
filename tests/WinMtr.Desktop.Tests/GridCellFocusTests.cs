using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The hop grid selects whole rows. The stock Fluent cell template still
/// draws a focus rectangle around the current cell while the grid has focus,
/// which makes one cell look selected. These checks pin the app-level style
/// that keeps the cell rectangle hidden in both theme variants, and show that
/// row selection and the selected-row fill stay unchanged.
/// </summary>
public class GridCellFocusTests
{
    private static HopRowViewModel Row(int hop) => new()
    {
        Hop = hop,
        Host = $"h{hop}",
        LossPercent = 0,
        Sent = 10,
        Received = 10,
        Status = string.Empty,
    };

    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateShell()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        viewModel.Rows.Add(Row(1));
        viewModel.Rows.Add(Row(2));
        viewModel.Rows.Add(Row(3));
        return (window, viewModel);
    }

    // Row and cell containers appear only on a layout pass, which the headless
    // dispatcher does not run on its own: measure and arrange the grid until
    // every row is realized.
    private static void RealizeRows(DataGrid grid)
    {
        for (int i = 0; i < 100 && RealizedRows(grid).Count < 3; i++)
        {
            grid.Measure(new Size(1200, 800));
            grid.Arrange(new Rect(0, 0, 1200, 800));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(3, RealizedRows(grid).Count);
    }

    private static IReadOnlyList<DataGridRow> RealizedRows(DataGrid grid) => grid
        .GetVisualDescendants()
        .OfType<DataGridRow>()
        .Where(container => container.DataContext is HopRowViewModel)
        .OrderBy(container => container.Bounds.Y)
        .ToList();

    private static DataGridCell FirstCell(DataGridRow row) => row
        .GetVisualDescendants()
        .OfType<DataGridCell>()
        .First();

    // The cell template holds its focus rectangle in a grid named FocusVisual.
    // The app style must keep that grid hidden.
    private static Grid CellFocusVisual(DataGridCell cell) => cell
        .GetVisualDescendants()
        .OfType<Grid>()
        .Single(grid => grid.Name == "FocusVisual");

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Focused_Cell_Draws_No_Focus_Visual(string theme)
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");
        ThemeVariant? originalTheme = application.RequestedThemeVariant;
        var (window, _) = CreateShell();
        try
        {
            application.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            RealizeRows(grid);

            DataGridCell cell = FirstCell(RealizedRows(grid)[0]);
            cell.Focus();
            Dispatcher.UIThread.RunJobs();

            // The focus state is true, so the stock theme rule would show the
            // rectangle here if no app style suppressed it.
            Assert.Contains(":focus", cell.Classes);
            Assert.False(CellFocusVisual(cell).IsVisible);
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public void Cell_Click_Shows_Row_Selection_Only()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            RealizeRows(grid);

            DataGridRow first = RealizedRows(grid)[0];
            DataGridCell cell = FirstCell(first);
            Point center = cell.TranslatePoint(
                    new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2),
                    window)
                ?? throw new InvalidOperationException("The cell is not in the window visual tree.");
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // The click still selects the row and leaves the grid focused with
            // that cell current: the stock theme would draw the focus
            // rectangle in this exact state.
            Assert.Same(first.DataContext, grid.SelectedItem);
            Assert.True(grid.IsFocused);
            Assert.Contains(":focus", cell.Classes);
            Assert.Contains(":selected", first.Classes);
            Assert.False(CellFocusVisual(cell).IsVisible);

            // Selection through the view model still reaches the grid.
            viewModel.SelectedRow = viewModel.Rows[2];
            Assert.Same(viewModel.Rows[2], grid.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Keyboard_Navigation_Shows_Row_Level_Focus()
    {
        var (window, _) = CreateShell();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            RealizeRows(grid);

            // Down from an empty current cell selects the first row. The grid
            // keeps focus, so the first cell is current and carries the focus
            // pseudo class.
            grid.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            DataGridRow selected = RealizedRows(grid).Single(row => row.Classes.Contains(":selected"));
            Assert.Same(selected.DataContext, grid.SelectedItem);

            // The selected-row accent fill is the row-level focus cue: it is
            // the visible mark, while the cell rectangle stays hidden.
            Rectangle fill = selected.GetVisualDescendants()
                .OfType<Rectangle>()
                .Single(rectangle => rectangle.Name == "BackgroundRectangle");
            Assert.True(
                fill.Fill is ISolidColorBrush { Color.A: > 0 },
                "The selected row must show its accent fill as the focus cue.");
            DataGridCell current = FirstCell(selected);
            Assert.Contains(":focus", current.Classes);
            Assert.False(CellFocusVisual(current).IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
