using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The detail pane collapse toggle: the operator hides the pane and shows it
/// again without a restart, the route grid takes the freed height, and the
/// selection and the detail content keep working after the restore. Appearance
/// is never asserted.
/// </summary>
public class DetailPaneCollapseTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateShell()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        return (window, viewModel);
    }

    private static HopRowViewModel Row(int hop, string host, string address) => new()
    {
        Hop = hop,
        Host = host,
        Address = address,
        Sent = 2,
        Received = 2,
        BestMs = 10,
        AverageMs = 12,
        WorstMs = 15,
        LastMs = 11,
        Status = string.Empty,
    };

    // The typed detail block, found by the automation name the pane declares.
    private static StackPanel DetailContent(MainWindow window) =>
        window.GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(panel => AutomationProperties.GetName(panel) == "Selected hop details");

    // Lays the root grid out at one fixed size. The headless dispatcher does
    // not run a layout pass on its own, so the grid and the pane take their
    // places only when this runs.
    private static void Layout(Grid root, double width = 1100, double height = 650)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DetailToggle_StartsChecked_AndHidesAndShowsThePaneContent()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var toggle = window.FindControl<ToggleButton>("DetailToggle")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;
            var details = DetailContent(window);

            // The pane starts open: the toggle reads checked, the empty prompt
            // fills the pane, and the typed details stay hidden with no hop.
            Assert.True(toggle.IsChecked);
            Assert.True(viewModel.IsDetailExpanded);
            Assert.True(detail.IsVisible);
            Assert.False(details.IsVisible);
            Assert.Equal("Select a hop to inspect it.", detail.Text);

            // The operator hides the pane through the toggle: the two-way
            // binding writes the flag back, the content hides, and the header
            // line, and so the toggle, stays visible.
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewModel.IsDetailExpanded);
            Assert.False(detail.IsVisible);
            Assert.False(details.IsVisible);
            Assert.True(toggle.IsVisible);

            // The view-model flag drives the toggle as well, so both ends of
            // the binding stay in step.
            viewModel.IsDetailExpanded = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(toggle.IsChecked);
            Assert.True(detail.IsVisible);
            Assert.False(details.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CollapsedDetailPane_GivesTheGridTheFreedHeight_AndTheRestoredPaneFollowsSelection()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var toggle = window.FindControl<ToggleButton>("DetailToggle")!;
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;
            var details = DetailContent(window);
            var root = (Grid)window.Content!;

            viewModel.Rows.Add(Row(1, "router.example", "192.0.2.1"));
            viewModel.Rows.Add(Row(2, "isp-core.example", "198.51.100.7"));
            viewModel.SelectedRow = viewModel.Rows[1];

            // The selected hop fills the pane with its typed details.
            Assert.True(details.IsVisible);
            Assert.False(detail.IsVisible);

            Layout(root);
            double expandedGridHeight = grid.Bounds.Height;
            Assert.True(expandedGridHeight > 0);

            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Layout(root);
            double collapsedGridHeight = grid.Bounds.Height;

            // The pane content is hidden, the header stays, and the grid row
            // takes the freed height.
            Assert.False(details.IsVisible);
            Assert.True(toggle.IsVisible);
            Assert.True(
                collapsedGridHeight > expandedGridHeight,
                $"the collapsed grid height {collapsedGridHeight} is not greater than the expanded grid height {expandedGridHeight}");

            // Restoring brings the pane back with the selected hop, and a new
            // selection still updates the detail text.
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Layout(root);

            Assert.True(details.IsVisible);
            Assert.Equal(expandedGridHeight, grid.Bounds.Height, 1);
            Assert.Contains("Hop 2 — isp-core.example", detail.Text);
            Assert.Contains("198.51.100.7", detail.Text);

            viewModel.SelectedRow = viewModel.Rows[0];

            Assert.Contains("Hop 1 — router.example", detail.Text);
            Assert.Contains("192.0.2.1", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
