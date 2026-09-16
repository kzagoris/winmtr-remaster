using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Core;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Themes;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

public class MacOsRouteChromeTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Profiles_Resolve_Grid_Chrome_Inspector_And_BottomBar(bool macOs, bool dark)
    {
        Application app = Application.Current!;
        ThemeVariant? original = app.RequestedThemeVariant;
        using var pack = new MacOsThemePack();
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        pack.Attach(app, macOs ? PlatformProfile.MacOs : PlatformProfile.Default);
        var vm = new MainWindowViewModel();
        vm.Rows.Add(new HopRowViewModel { Hop = 1, Host = "router.example", Address = "192.0.2.1" });
        vm.Rows.Add(new HopRowViewModel { Hop = 2, Host = "core.example", Address = "192.0.2.2" });
        var window = new MainWindow { DataContext = vm };
        var settings = new SettingsWindow { DataContext = new SettingsViewModel() };
        var about = new AboutWindow();
        try
        {
            window.Show();
            settings.Show();
            about.Show();
            Layout(window);

            Assert.Equal(macOs ? 800 : 560, window.MinWidth);
            Assert.Equal("WinMTR", window.Title);
            Assert.False(window.ExtendClientAreaToDecorationsHint);
            Assert.False(settings.ExtendClientAreaToDecorationsHint);
            Assert.False(about.ExtendClientAreaToDecorationsHint);
            Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
            Assert.Equal(-1, window.ExtendClientAreaTitleBarHeightHint);
            var toolbar = window.FindControl<ToolbarPanel>("Toolbar")!;
            var chrome = window.FindControl<Border>("ToolbarChrome")!;
            Assert.True(toolbar.CanWrap);
            Assert.Equal(WindowDecorationsElementRole.None,
                WindowDecorationProperties.GetElementRole(chrome));
            foreach (string name in new[] { "TargetInput", "TargetHistoryButton", "StartStopButton", "SettingsButton", "CopyButton", "ExportButton" })
            {
                Assert.Equal(WindowDecorationsElementRole.None,
                    WindowDecorationProperties.GetElementRole(window.FindControl<Control>(name)!));
            }
            Assert.Equal(macOs ? 160 : 320, window.FindControl<AutoCompleteBox>("TargetInput")!.MaxWidth);
            Assert.True(double.IsNaN(chrome.Height));

            var grid = window.FindControl<DataGrid>("HopGrid")!;
            Assert.Equal(macOs ? 24d : double.NaN, grid.RowHeight);
            DataGridRow[] rows = grid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Index).ToArray();
            Assert.Equal(2, rows.Length);
            var cell = rows[0].GetVisualDescendants().OfType<DataGridCell>().First();
            Assert.Equal(macOs ? 24 : 32, cell.MinHeight);
            Assert.Equal(macOs ? 13 : 15, cell.FontSize);
            var label = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("host-label"));
            var address = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("host-address"));
            Assert.Equal(macOs, label.IsEffectivelyVisible);
            Assert.Equal(!macOs, address.IsEffectivelyVisible);
            Assert.Equal("router.example", label.Text);
            Assert.Equal("192.0.2.1", address.Text);
            Assert.Equal("Host", grid.Columns[1].SortMemberPath);
            if (macOs)
            {
                Assert.Equal(24, rows[0].Bounds.Height);
                Assert.Same(Resource(app, "WinMtrRowPlainBrush"), rows[0].Background);
                Assert.Same(Resource(app, "WinMtrRowStripeBrush"), rows[1].Background);
            }
            else
            {
                Assert.Equal(Colors.Transparent, Solid(rows[0].Background).Color);
                Assert.Equal(Colors.Transparent, Solid(rows[1].Background).Color);
            }

            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(part => Equals(part.Content, "Host"));
            Assert.Equal(macOs ? 24 : 32, header.MinHeight);
            Assert.Equal(macOs ? 11 : 12, header.FontSize);
            Assert.Equal(macOs ? 11 : 9, grid.GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Classes.Contains("sort-glyph")).FontSize);
            if (macOs)
            {
                Assert.Equal(new Thickness(0, 0, 0, 0.5), header.BorderThickness);
                var separator = header.GetVisualDescendants().OfType<Rectangle>().Single(part => part.Name == "VerticalSeparator");
                Assert.True(separator.IsVisible);
                Assert.Equal(0.5, separator.Width);
                var divider = grid.GetVisualDescendants().OfType<Rectangle>().Single(part => part.Name == "PART_ColumnHeadersAndRowsSeparator");
                Assert.Equal(0.5, divider.Height);
            }

            var disclosure = window.FindControl<StackPanel>("DetailDisclosure")!;
            var heading = window.FindControl<TextBlock>("DetailHeading")!;
            Assert.Equal(macOs, disclosure.IsVisible);
            Assert.Equal(!macOs, heading.IsVisible);
            var toggle = window.FindControl<ToggleButton>("DetailToggle")!;
            Assert.True(toggle.Focusable);
            Assert.Equal("Show or hide the hop details", AutomationProperties.GetName(toggle));
            Assert.Equal("Show or hide the hop details.", ToolTip.GetTip(toggle));
            Assert.Equal(macOs ? 0 : 1, Grid.GetColumn(toggle));
            if (macOs)
            {
                var chevron = window.FindControl<PathIcon>("DetailChevron")!;
                Assert.NotNull(chevron.Data);
                Assert.True(chevron.Bounds.Width >= 12 && chevron.Bounds.Height >= 12);
                Assert.All(disclosure.Children.OfType<TextBlock>(), text =>
                {
                    Assert.Equal(11, text.FontSize);
                    Assert.Equal(FontWeight.SemiBold, text.FontWeight);
                });
            }
            Assert.Equal("Select a hop to inspect it.", window.FindControl<TextBlock>("DetailPane")!.Text);
            Geometry? expandedChevron = window.FindControl<PathIcon>("DetailChevron")!.Data;
            toggle.IsChecked = false;
            Layout(window);
            Assert.False(vm.IsDetailExpanded);
            Assert.False(window.FindControl<TextBlock>("DetailPane")!.IsVisible);
            if (macOs)
                Assert.NotSame(expandedChevron, window.FindControl<PathIcon>("DetailChevron")!.Data);
            vm.IsDetailExpanded = true;
            Assert.True(toggle.IsChecked);
            vm.SelectedRow = vm.Rows[0];
            Layout(window);
            Assert.Equal("router.example", vm.DetailHost);
            Assert.Equal("192.0.2.1", vm.DetailAddress);
            foreach (Border tile in window.GetVisualDescendants().OfType<Border>().Where(part => part.Classes.Contains("detail-tile")))
            {
                Assert.Equal(new CornerRadius(macOs ? 0 : 4), tile.CornerRadius);
                Assert.Equal(new Thickness(0, 0, macOs ? 0.5 : 0, 0), tile.BorderThickness);
                if (macOs)
                    Assert.Equal(Colors.Transparent, Solid(tile.Background).Color);
            }

            var status = window.FindControl<Border>("StatusBar")!;
            Assert.Equal(macOs ? 22d : double.NaN, status.Height);
            Assert.Equal(macOs ? 11 : 14, window.FindControl<TextBlock>("SessionStateText")!.FontSize);
            Assert.Same(Resource(app, "WinMtrStatusBackgroundBrush"), status.Background);
            Assert.Equal(macOs ? new Thickness(10, 0) : new Thickness(8, 4), status.Padding);
            Assert.Equal(Dock.Right, DockPanel.GetDock(window.FindControl<StackPanel>("FooterCredit")!));
            if (macOs)
                Assert.Equal(Surface(window).Color, Solid(status.Background).Color);
        }
        finally
        {
            about.Close();
            settings.Close();
            window.Close();
            pack.Dispose();
            app.RequestedThemeVariant = original;
        }
    }

    // This is also the 12.1.2 selector probe: native activation notifications
    // change Window.IsActive and must reach a realized row template and text.
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Window_Activity_Changes_Selection_Without_Changing_Retained_Row_State(bool macOs, bool dark)
    {
        Application app = Application.Current!;
        ThemeVariant? original = app.RequestedThemeVariant;
        using var pack = new MacOsThemePack();
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        pack.Attach(app, macOs ? PlatformProfile.MacOs : PlatformProfile.Default);
        var vm = new MainWindowViewModel();
        var model = new HopRowViewModel { Hop = 1, Host = "router.example", Address = "192.0.2.1", HasIdentityChange = true };
        vm.Rows.Add(model);
        vm.SelectedRow = model;
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Layout(window);
            var row = Assert.Single(window.GetVisualDescendants().OfType<DataGridRow>());
            var fill = Assert.Single(row.GetVisualDescendants().OfType<Rectangle>(), part => part.Name == "BackgroundRectangle");
            var text = row.GetVisualDescendants().OfType<TextBlock>().First(part => part.Text == "router.example" && part.IsEffectivelyVisible);
            Assert.Equal(macOs ? new Thickness(4, 1) : default, fill.Margin);
            Assert.Equal(macOs ? 4 : 0, fill.RadiusX);
            Assert.Equal(1, fill.Opacity);
            foreach (bool frozen in new[] { false, true })
            {
                model.IsFrozen = frozen;
                foreach (bool active in new[] { true, false, true })
                {
                    SetActive(window, active);
                    Layout(window);
                    Assert.Equal(active, window.IsActive);
                    string fillKey = macOs && !active ? "WinMtrInactiveSelectionBrush" : "SystemControlHighlightAccentBrush";
                    Assert.Equal(Solid(Resource(app, fillKey)).Color, Solid(fill.Fill).Color);
                    string textKey = macOs && !active
                        ? (frozen ? "WinMtrInactiveSelectionFrozenTextBrush" : "WinMtrInactiveSelectionTextBrush")
                        : (frozen ? "WinMtrRowSelectedFrozenTextBrush" : "WinMtrRowSelectedTextBrush");
                    Assert.Equal(Solid(Resource(app, textKey)).Color, Solid(text.Foreground).Color);
                    Assert.Equal(Solid(Resource(app, textKey)).Opacity, Solid(text.Foreground).Opacity);
                    Assert.Equal(frozen, model.IsFrozen);
                    Assert.True(model.HasIdentityChange);
                    Assert.Same(model, vm.SelectedRow);
                }
            }
        }
        finally
        {
            window.Close();
            pack.Dispose();
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void Disclosure_Header_Has_A_Large_Pointer_Target()
    {
        using var pack = new MacOsThemePack();
        pack.Attach(Application.Current!, PlatformProfile.MacOs);
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Layout(window);
            var toggle = window.FindControl<ToggleButton>("DetailToggle")!;
            var header = (Grid)toggle.Parent!;
            var point = header.TranslatePoint(new Point(header.Bounds.Width - 8, 16), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.False(vm.IsDetailExpanded);
            Assert.False(window.FindControl<TextBlock>("DetailPane")!.IsVisible);

            Layout(window);
            point = toggle.TranslatePoint(new Point(8, 28), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.True(vm.IsDetailExpanded);
            Assert.True(window.FindControl<TextBlock>("DetailPane")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Disclosure_Toggles_With_Keyboard_And_Toolbar_Stays_In_Content_In_FullScreen()
    {
        using var pack = new MacOsThemePack();
        pack.Attach(Application.Current!, PlatformProfile.MacOs);
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            var toggle = window.FindControl<ToggleButton>("DetailToggle")!;
            toggle.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.False(vm.IsDetailExpanded);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.True(vm.IsDetailExpanded);

            window.WindowState = WindowState.FullScreen;
            Layout(window);
            Assert.Equal(default, window.FindControl<Border>("ToolbarChrome")!.Padding);
            Assert.True(window.FindControl<ToolbarPanel>("Toolbar")!.CanWrap);
            window.WindowState = WindowState.Normal;
            Layout(window);
            Assert.Equal(default,
                window.FindControl<Border>("ToolbarChrome")!.Padding);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Live_Appearance_Changes_Update_Stripes_And_Inactive_Selection()
    {
        Application app = Application.Current!;
        ThemeVariant? original = app.RequestedThemeVariant;
        using var pack = new MacOsThemePack();
        pack.Attach(app, PlatformProfile.MacOs);
        var vm = new MainWindowViewModel();
        vm.Rows.Add(new HopRowViewModel { Hop = 1, Host = "first" });
        vm.Rows.Add(new HopRowViewModel { Hop = 2, Host = "second" });
        vm.SelectedRow = vm.Rows[0];
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            // Show queues a native activation; drain it before deactivating so
            // the later layout pass cannot re-activate the window.
            Layout(window);
            SetActive(window, false);
            foreach (bool dark in new[] { false, true, false })
            {
                app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                Layout(window);
                var rows = window.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Index).ToArray();
                Assert.Equal(Color.Parse(dark ? "#303032" : "#F0F0F2"), Solid(rows[1].Background).Color);
                var fill = rows[0].GetVisualDescendants().OfType<Rectangle>().Single(part => part.Name == "BackgroundRectangle");
                Assert.Equal(Color.Parse(dark ? "#505054" : "#DCDCE0"), Solid(fill.Fill).Color);
                var status = window.FindControl<Border>("StatusBar")!;
                Assert.Equal(Surface(window).Color, Solid(status.Background).Color);
            }
        }
        finally
        {
            window.Close();
            pack.Dispose();
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stripes_Keep_Loss_And_Identity_Cues_But_Selection_Wins(bool dark)
    {
        Application app = Application.Current!;
        ThemeVariant? original = app.RequestedThemeVariant;
        using var pack = new MacOsThemePack();
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        pack.Attach(app, PlatformProfile.MacOs);
        var vm = new MainWindowViewModel();
        for (int hop = 1; hop <= 2; hop++)
        {
            vm.Rows.Add(new HopRowViewModel
            {
                Hop = hop,
                Host = $"router-{hop}",
                LossPercent = 25,
                Severity = new HopSeverity(LossLevel.Moderate, false, false, false, false)
            });
        }
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Layout(window);
            DataGridRow[] rows = window.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Index).ToArray();
            Assert.Equal(2, rows.Length);
            for (int index = 0; index < rows.Length; index++)
            {
                DataGridRow row = rows[index];
                HopRowViewModel model = vm.Rows[index];
                var tint = row.GetVisualDescendants().OfType<Border>().Single(part => part.Classes.Contains("loss-tint"));
                var bar = row.GetVisualDescendants().OfType<ProgressBar>().Single(part => part.Classes.Contains("loss-bar"));
                Assert.Same(Resource(app, "WinMtrLossModerateBackgroundBrush"), tint.Background);
                Assert.Equal(1, tint.Opacity);
                Assert.True(bar.IsEffectivelyVisible);
                Assert.Equal(25, bar.Value);
                Assert.Same(Resource(app, "WinMtrLossModerateBarBrush"), bar.Foreground);

                model.HasIdentityChange = true;
                Layout(window);
                Assert.Same(Resource(app, "WinMtrIdentityChangeBackgroundBrush"), row.Background);
                vm.SelectedRow = model;
                Layout(window);
                Assert.Equal(0, tint.Opacity);
                Assert.True(bar.IsEffectivelyVisible);

                model.IsFrozen = true;
                vm.SelectedRow = null;
                model.HasIdentityChange = false;
                Layout(window);
                var label = row.GetVisualDescendants().OfType<TextBlock>().Single(part => part.Classes.Contains("host-label"));
                Assert.Same(Resource(app, "WinMtrFrozenTextBrush"), label.Foreground);
                Assert.Same(Resource(app, index == 0 ? "WinMtrRowPlainBrush" : "WinMtrRowStripeBrush"), row.Background);
                Assert.True(bar.IsEffectivelyVisible);
            }
        }
        finally
        {
            window.Close();
            pack.Dispose();
            app.RequestedThemeVariant = original;
        }
    }

    // The headless platform raises activation through internal handlers on
    // WindowBase: no public deactivate exists, so the probe reaches them by
    // reflection. Production code never does this.
    private static void SetActive(Window window, bool active)
    {
        var method = typeof(WindowBase).GetMethod(
            active ? "HandleActivated" : "HandleDeactivated",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The headless activation handler was not found.");
        method.Invoke(window, null);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static object? Resource(Application app, string key)
    {
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out object? value));
        return value;
    }

    private static ISolidColorBrush Solid(object? value) => Assert.IsAssignableFrom<ISolidColorBrush>(value);

    // The Windows build asks for Mica and makes the window transparent, so the
    // host paints the fallback brush. Other hosts keep the window brush. The
    // status bar must agree with the color that the host paints.
    private static ISolidColorBrush Surface(Window window)
    {
        ISolidColorBrush background = Solid(window.Background);
        return background.Color == Colors.Transparent
            ? Solid(window.TransparencyBackgroundFallback)
            : background;
    }
}
