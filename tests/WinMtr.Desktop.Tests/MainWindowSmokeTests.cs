using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Thin headless smoke coverage for the shell: the application starts, the
/// main window constructs, its data context and bindings resolve, and the
/// grid is connected to the shell's row surface. No trace session and no
/// network traffic are involved. Appearance is never asserted.
/// </summary>
public class MainWindowSmokeTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateShell()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        return (window, viewModel);
    }

    [AvaloniaFact]
    public void Shell_Starts_In_Empty_Idle_State_With_All_Regions()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            Assert.Equal(560, window.MinWidth);
            Assert.Equal(480, window.MinHeight);
            Assert.True(window.CanResize);

            var targetInput = window.FindControl<AutoCompleteBox>("TargetInput");
            var startStop = window.FindControl<Button>("StartStopButton");
            var settings = window.FindControl<Button>("SettingsButton");
            var copy = window.FindControl<SplitButton>("CopyButton");
            var export = window.FindControl<SplitButton>("ExportButton");
            var banners = window.FindControl<ItemsControl>("BannerStack");
            var grid = window.FindControl<DataGrid>("HopGrid");
            var detail = window.FindControl<TextBlock>("DetailPane");
            var status = window.FindControl<Border>("StatusBar");
            var state = window.FindControl<TextBlock>("SessionStateText");
            var elapsed = window.FindControl<TextBlock>("ElapsedText");
            var empty = window.FindControl<TextBlock>("EmptyStateText");

            Assert.All(
                new object?[]
                {
                    targetInput, startStop, settings, copy, export, banners, grid, detail, status, state, elapsed, empty,
                },
                Assert.NotNull);

            // Bindings resolved: control values track the idle view model.
            Assert.Equal("Start", startStop!.Content);
            Assert.Equal("Idle", state!.Text);
            Assert.Equal("Elapsed 00:00", elapsed!.Text);
            Assert.Equal("Select a hop to inspect it.", detail!.Text);
            Assert.True(empty!.IsVisible);

            // Grid connected to the shell's visible row order, still empty and
            // in default hop order. The third select of the active header
            // restores hop order through the same reset path, available from
            // the outset. No reset button exists.
            Assert.Same(viewModel.SortedRows, grid!.ItemsSource);
            Assert.Empty(viewModel.SortedRows);
            Assert.Null(window.FindControl<Button>("ResetSortButton"));
            Assert.True(viewModel.ResetSortCommand.CanExecute(null));
            Assert.Equal(
                ["Hop", "Host", "Loss", "Sent", "Received", "Best (ms)", "Average (ms)", "Worst (ms)", "Last (ms)", "Status"],
                GridHeaders.Titles(grid));

            // No evidence yet, so copy/export stay disabled. Avalonia keeps
            // IsEnabled as set and folds command state into
            // IsEffectivelyEnabled, which is what the user experiences.
            Assert.False(copy!.IsEffectivelyEnabled);
            Assert.False(export!.IsEffectivelyEnabled);
            Assert.False(viewModel.CopyCommand.CanExecute(null));
            Assert.False(viewModel.ExportCommand.CanExecute(null));

            // Banner stack present but empty.
            Assert.Equal(0, banners!.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Shell_Grid_Is_Connected_To_Row_Surface()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var detail = window.FindControl<TextBlock>("DetailPane")!;
            var empty = window.FindControl<TextBlock>("EmptyStateText")!;

            var row = new HopRowViewModel
            {
                Hop = 2,
                Host = "isp-core.example",
                LossPercent = 12.5,
                Sent = 56,
                Received = 49,
                BestMs = 8,
                AverageMs = 11,
                WorstMs = 45,
                LastMs = 9,
                Status = string.Empty,
            };
            viewModel.Rows.Add(row);

            Assert.Same(viewModel.SortedRows, grid.ItemsSource);
            Assert.Contains(row, viewModel.SortedRows);
            Assert.True(viewModel.HasRows);
            Assert.False(empty.IsVisible);
            Assert.True(viewModel.CopyCommand.CanExecute(null));
            Assert.True(window.FindControl<SplitButton>("CopyButton")!.IsEffectivelyEnabled);

            viewModel.SelectedRow = row;
            Assert.Same(row, grid.SelectedItem);
            Assert.Contains("Hop 2 — isp-core.example", detail.Text);
            Assert.Contains($"{12.5:F2}%", detail.Text);
            Assert.Contains("7 lost of 56 sent, 49 received", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// WM-19: the invalid-target message has its own row below the toolbar.
    /// Showing and clearing it must not move a button, because the pointer can
    /// be over one. Both themes are checked: the message uses a theme brush.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Target_Error_Has_Its_Own_Row_And_Moves_No_Button(string theme)
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");
        ThemeVariant variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        ThemeVariant? originalTheme = application.RequestedThemeVariant;
        application.RequestedThemeVariant = variant;
        var (window, viewModel) = CreateShell();
        try
        {
            var targetInput = window.FindControl<AutoCompleteBox>("TargetInput")!;
            var start = window.FindControl<Button>("StartStopButton")!;
            var settings = window.FindControl<Button>("SettingsButton")!;
            var copy = window.FindControl<SplitButton>("CopyButton")!;
            var export = window.FindControl<SplitButton>("ExportButton")!;
            var message = window.FindControl<TextBlock>("TargetErrorText")!;
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            var toolbar = start.GetVisualParent<WrapPanel>()!;

            // The target box grows with its own text. Pin it at its widest so
            // this test measures only the message's effect on the toolbar.
            targetInput.Width = targetInput.MaxWidth;
            RunLayout(window);

            // The message starts hidden and takes the theme error brush.
            Assert.False(message.IsVisible);
            Assert.True(application.TryGetResource("WinMtrErrorTextBrush", variant, out object? errorBrush));
            ISolidColorBrush expected = Assert.IsAssignableFrom<ISolidColorBrush>(errorBrush);
            ISolidColorBrush actual = Assert.IsAssignableFrom<ISolidColorBrush>(message.Foreground);
            Assert.Equal(expected.Color, actual.Color);

            Rect restingStart = start.Bounds;
            Rect restingSettings = settings.Bounds;
            Rect restingCopy = copy.Bounds;
            Rect restingExport = export.Bounds;
            double restingGridTop = grid.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            double toolbarBottom = toolbar.TranslatePoint(new Point(0, toolbar.Bounds.Height), window)!.Value.Y;

            void AssertButtonsAtRest()
            {
                Assert.Equal(restingStart, start.Bounds);
                Assert.Equal(restingSettings, settings.Bounds);
                Assert.Equal(restingCopy, copy.Bounds);
                Assert.Equal(restingExport, export.Bounds);
            }

            // An invalid target shows the message on its own line, below the
            // toolbar. Every button keeps its exact place.
            viewModel.Target = "999.999.999.999";
            RunLayout(window);

            Assert.True(message.IsVisible);
            Assert.False(string.IsNullOrEmpty(message.Text));

            // The message is not part of the toolbar: it sits below its bottom
            // edge and is not one of its children.
            Assert.DoesNotContain(message, toolbar.GetVisualDescendants());
            double messageTop = message.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(
                messageTop >= toolbarBottom,
                $"The message top {messageTop} is above the toolbar bottom {toolbarBottom}.");

            AssertButtonsAtRest();

            // The visible message pushes the grid down: the row holds it.
            double shownGridTop = grid.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(shownGridTop > restingGridTop);

            // A valid target hides the message, moves no button, and gives the
            // row back to the grid.
            viewModel.Target = "example.com";
            RunLayout(window);

            Assert.False(message.IsVisible);
            AssertButtonsAtRest();
            Assert.Equal(restingGridTop, grid.TranslatePoint(new Point(0, 0), window)!.Value.Y);
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = originalTheme;
        }
    }

    // Forces a full layout pass: the headless dispatcher does not run one on
    // its own, and every bound is read before and after the message toggles.
    private static void RunLayout(MainWindow window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Shell_Banner_Stack_Shows_And_Dismisses()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var banners = window.FindControl<ItemsControl>("BannerStack")!;

            viewModel.ShowBanner("Tracing with the default payload.", BannerSeverity.Warning);
            Assert.Equal(1, banners.ItemCount);

            viewModel.Banners[0].DismissCommand.Execute(null);
            Assert.Equal(0, banners.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }
}
