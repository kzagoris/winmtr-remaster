using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Themes;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

public class MacOsThemePackTests
{
    private const string PaletteKey = "WinMtrWindowBackgroundBrush";

    [AvaloniaFact]
    public void MacOs_Profile_Applies_Palette_Above_Theme_Dictionaries_And_Appends_Styles()
    {
        using var pack = TestPack();
        var application = new App(PlatformProfile.MacOs, pack)
        {
            RequestedThemeVariant = ThemeVariant.Light
        };

        application.Initialize();

        Assert.Same(pack, application.Styles.Last());
        Assert.Same(Brushes.Red, application.Resources[PaletteKey]);
        Assert.Same(Brushes.Red, Resolve(application, PaletteKey));
        Assert.NotSame(Brushes.Red,
            Palette(application.Resources, ThemeVariant.Light)[PaletteKey]);
    }

    [AvaloniaFact]
    public void Other_Profile_Does_Not_Apply_Or_Subscribe_To_Variant_Changes()
    {
        using var pack = TestPack();
        var application = new App(PlatformProfile.Default, pack);
        application.Initialize();
        pack.Attach(application, PlatformProfile.Default);

        // Styles is enumerable; xUnit's structural equality can match another
        // empty style collection. Check whether this pack instance was attached.
        Assert.DoesNotContain(application.Styles, style => ReferenceEquals(style, pack));
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            application.RequestedThemeVariant = variant;
            Assert.False(application.Resources.ContainsKey(PaletteKey));
            Assert.Same(Palette(application.Resources, variant)[PaletteKey],
                Resolve(application, PaletteKey));
        }
    }

    [AvaloniaFact]
    public void Actual_Variant_Changes_Reapply_Palette_Without_Duplicating_Styles()
    {
        using var pack = TestPack();
        var application = new App(PlatformProfile.MacOs, pack)
        {
            RequestedThemeVariant = ThemeVariant.Light
        };
        application.Initialize();
        int styleCount = application.Styles.Count;

        application.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal(ThemeVariant.Dark, application.ActualThemeVariant);
        Assert.Same(Brushes.Blue, Resolve(application, PaletteKey));
        Assert.Same(Brushes.Blue, application.Resources[PaletteKey]);

        application.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Same(Brushes.Red, Resolve(application, PaletteKey));
        Assert.Equal(styleCount, application.Styles.Count);
    }

    [AvaloniaFact]
    public void Pack_Metric_And_Selector_Setters_Win_Over_Earlier_Styles()
    {
        Application application = Application.Current!;
        var earlier = new Style(selector => selector.OfType<Button>().Class("theme-pack-test"))
        {
            Setters = { new Setter(Button.MinHeightProperty, 20d) }
        };
        using var pack = new MacOsThemePack();
        pack.Add(new Style(selector => selector.OfType<Button>().Class("theme-pack-test"))
        {
            Setters = { new Setter(Button.MinHeightProperty, 42d) }
        });
        var button = new Button { Classes = { "theme-pack-test" } };
        var window = new Window { Content = button };
        application.Styles.Add(earlier);
        try
        {
            pack.Attach(application, PlatformProfile.MacOs);
            window.Show();

            Assert.Equal(42d, button.MinHeight);
        }
        finally
        {
            window.Close();
            application.Styles.Remove(earlier);
        }
    }

    [AvaloniaFact]
    public void Other_Profile_Keeps_Extracted_Metrics_And_Palette()
    {
        var baseline = new App(PlatformProfile.Default);
        baseline.Initialize();
        using var pack = new MacOsThemePack();
        var application = new App(PlatformProfile.Default, pack);
        application.Initialize();

        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            baseline.RequestedThemeVariant = variant;
            application.RequestedThemeVariant = variant;
            Assert.Equal(baseline.Resources.Count, application.Resources.Count);
            foreach (object key in Palette(baseline.Resources, variant).Keys)
            {
                Assert.Equal(Resolve(baseline, key)?.ToString(), Resolve(application, key)?.ToString());
            }
        }

        // Name exposes the first family, not the serialized fallback list.
        Assert.Equal("Segoe UI Variable Text",
            Assert.IsType<FontFamily>(Resolve(application, "WinMtrShellFontFamily")).Name);
        Assert.Equal(new CornerRadius(6), Resolve(application, "WinMtrSurfaceCornerRadius"));
        Assert.Equal(new Thickness(12, 8), Resolve(application, "WinMtrPopupPadding"));
        Assert.Equal(new Thickness(16), Resolve(application, "WinMtrSettingsMargin"));
        Assert.Equal(560d, Resolve(application, "WinMtrMainWindowMinWidth"));
        Assert.Equal(baseline.Styles.Count, application.Styles.Count);
    }

    [AvaloniaFact]
    public void MacOs_Uses_System_Font_And_Metrics_But_Does_Not_Override_Route_Treatments_Or_Accent()
    {
        using var pack = new MacOsThemePack();
        var application = new App(PlatformProfile.MacOs, pack);
        application.Initialize();

        Assert.Equal(ThemeVariant.Default, application.RequestedThemeVariant);
        Assert.Equal(".AppleSystemUIFont",
            Assert.IsType<FontFamily>(Resolve(application, "WinMtrShellFontFamily")).Name);
        Assert.Equal(new CornerRadius(8), Resolve(application, "WinMtrSurfaceCornerRadius"));
        Assert.Equal(28d, Resolve(application, "WinMtrControlMinHeight"));
        Assert.Equal(800d, Resolve(application, "WinMtrMainWindowMinWidth"));

        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            application.RequestedThemeVariant = variant;
            Assert.Same(Palette(pack.Resources, variant)["WinMtrDetailBorderBrush"],
                Resolve(application, "WinMtrDetailBorderBrush"));
            foreach (object key in Palette(application.Resources, variant).Keys)
            {
                string name = key.ToString()!;
                if (name.StartsWith("DataGrid", StringComparison.Ordinal)
                    || name.StartsWith("WinMtrLoss", StringComparison.Ordinal)
                    || name.StartsWith("WinMtrLatency", StringComparison.Ordinal)
                    || name is "WinMtrFrozenTextBrush" or "WinMtrIdentityChangeBackgroundBrush" or "WinMtrSubtleTextBrush")
                {
                    Assert.False(pack.Resources.TryGetResource(key, variant, out _));
                    Assert.Same(Palette(application.Resources, variant)[key], Resolve(application, key));
                }
            }
            Assert.DoesNotContain(Palette(pack.Resources, variant).Keys,
                key => key.ToString()!.Contains("Accent", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(pack.Resources.Keys,
            key => key.ToString()!.Contains("Accent", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void MacOs_Dropdown_Surfaces_Resolve_Floating_Tokens_And_Row_Metrics()
    {
        using var pack = new MacOsThemePack();
        var application = new App(PlatformProfile.MacOs, pack);
        application.Initialize();

        Assert.Equal(new CornerRadius(6), Resolve(application, "WinMtrFloatingCornerRadius"));
        Assert.Equal(new Thickness(5), Resolve(application, "WinMtrFloatingPadding"));
        Assert.Equal(new CornerRadius(4), Resolve(application, "WinMtrHighlightCornerRadius"));
        foreach (string key in new[] { "MenuFlyoutItemThemePaddingNarrow", "ComboBoxItemThemePadding", "ListBoxItemPadding" })
            Assert.Equal(new Thickness(10, 3), Resolve(application, key));
        foreach (string key in new[] { "WinMtrHistoryItemPadding", "MenuFlyoutScrollerMargin", "ComboBoxDropdownContentMargin", "AutoCompleteListPadding" })
            Assert.Equal(new Thickness(0), Resolve(application, key));

        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            application.RequestedThemeVariant = variant;
            string surface = variant == ThemeVariant.Light ? "#FFFFFF" : "#2A2A2C";
            string border = variant == ThemeVariant.Light ? "#D1D1D6" : "#48484A";
            Assert.Equal(Color.Parse(surface),
                Assert.IsAssignableFrom<ISolidColorBrush>(Resolve(application, "WinMtrFloatingSurfaceBrush")).Color);
            Assert.Equal(Color.Parse(border),
                Assert.IsAssignableFrom<ISolidColorBrush>(Resolve(application, "WinMtrFloatingBorderBrush")).Color);
        }
    }

    [AvaloniaFact]
    public void Default_Profile_Keeps_Current_Dropdown_Metrics()
    {
        var application = new App(PlatformProfile.Default);
        application.Initialize();

        Assert.Equal(new Thickness(4, 4, 8, 4), Resolve(application, "MenuFlyoutItemThemePaddingNarrow"));
        Assert.Equal(new Thickness(11, 5, 11, 7), Resolve(application, "ComboBoxItemThemePadding"));
        Assert.Equal(new Thickness(4, 2), Resolve(application, "ListBoxItemPadding"));
        Assert.Equal(new Thickness(0, 2, 0, 2), Resolve(application, "AutoCompleteListMargin"));
        Assert.Equal(new Thickness(-1, 0, -1, 0), Resolve(application, "AutoCompleteListPadding"));
        Assert.Equal(new Thickness(0), Resolve(application, "ComboBoxDropdownBorderPadding"));
        Assert.Equal(new Thickness(0, 4, 0, 4), Resolve(application, "ComboBoxDropdownContentMargin"));
        Assert.Equal(new Thickness(0, 4, 0, 4), Resolve(application, "MenuFlyoutScrollerMargin"));
        Assert.Equal(new CornerRadius(5), Resolve(application, "OverlayCornerRadius"));
        Assert.Equal(new Thickness(0), Resolve(application, "FlyoutBorderThemePadding"));
        Assert.Equal(new Thickness(8, 6), Resolve(application, "WinMtrHistoryItemPadding"));
        Assert.False(application.TryGetResource("WinMtrFloatingSurfaceBrush", application.ActualThemeVariant, out _));
    }

    [AvaloniaFact]
    public void MacOs_Format_Menus_Use_Checkmark_Toggle_Type()
    {
        Application application = Application.Current!;
        using var pack = new MacOsThemePack();
        pack.Attach(application, PlatformProfile.MacOs);

        AssertFormatMenuToggleTypes(MenuItemToggleType.CheckBox);
    }

    [AvaloniaFact]
    public void Default_Profile_Keeps_Radio_Format_Menu_Toggle_Type()
    {
        Application application = Application.Current!;
        using var pack = new MacOsThemePack();
        pack.Attach(application, PlatformProfile.Default);

        AssertFormatMenuToggleTypes(MenuItemToggleType.Radio);
    }

    [AvaloniaFact]
    public void MacOs_Dropdown_Cards_Take_The_Floating_Treatment()
    {
        Application application = Application.Current!;
        using var pack = new MacOsThemePack();
        pack.Attach(application, PlatformProfile.MacOs);
        var history = new InMemoryTargetHistoryStore();
        history.Add("example.com");
        var window = new MainWindow { DataContext = new MainWindowViewModel(history) };
        var settings = new SettingsWindow { DataContext = new SettingsViewModel() };
        try
        {
            window.Show();
            settings.Show();
            var copy = window.FindControl<SplitButton>("CopyButton")!;
            var flyout = Assert.IsType<MenuFlyout>(copy.Flyout);
            flyout.ShowAt(copy);
            Dispatcher.UIThread.RunJobs();
            AssertFloatingCard(window, "LayoutRoot");
            flyout.Hide();

            var target = window.FindControl<AutoCompleteBox>("TargetInput")!;
            target.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            AssertFloatingCard(window, "PART_SuggestionsContainer");
            target.IsDropDownOpen = false;

            settings.FindControl<ComboBox>("ThemeBox")!.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            AssertFloatingCard(settings, "PopupBorder");
        }
        finally
        {
            settings.Close();
            window.Close();
        }

        void AssertFloatingCard(Window owner, string name)
        {
            var border = Assert.Single(owner.GetVisualDescendants().OfType<Border>(), part => part.Name == name);
            Assert.Equal(new Thickness(5), border.Padding);
            Assert.Equal(new CornerRadius(6), border.CornerRadius);
            Assert.Same(Resolve(application, "WinMtrFloatingSurfaceBrush"), border.Background);
        }
    }

    [AvaloniaFact]
    public void MacOs_Theme_Picker_Shows_PopupButton_Chevron_And_Separator()
    {
        Application application = Application.Current!;
        using var pack = new MacOsThemePack();
        pack.Attach(application, PlatformProfile.MacOs);
        var window = new SettingsWindow { DataContext = new SettingsViewModel() };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var combo = window.FindControl<ComboBox>("ThemeBox")!;
            var glyph = Assert.Single(combo.GetVisualDescendants().OfType<PathIcon>(), part => part.Name == "DropDownGlyph");
            var geometry = Assert.IsAssignableFrom<Geometry>(Resolve(application, "WinMtrPopupButtonChevronGeometry"));
            Assert.NotNull(glyph.Data);
            Assert.Equal(geometry.Bounds, glyph.Data.Bounds);
            var overlay = Assert.Single(combo.GetVisualDescendants().OfType<Border>(), part => part.Name == "DropDownOverlay");
            Assert.True(overlay.IsVisible);
            Assert.Equal(new Thickness(1, 0, 0, 0), overlay.BorderThickness);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(800)]
    [InlineData(1100)]
    public void MacOs_Shell_Controls_Fit_At_Documented_Widths(int width)
    {
        Application application = Application.Current!;
        using var pack = new MacOsThemePack();
        pack.Attach(application, PlatformProfile.MacOs);
        var window = new MainWindow { DataContext = new MainWindowViewModel(), Width = width, Height = 480 };
        try
        {
            window.Show();
            var target = window.FindControl<AutoCompleteBox>("TargetInput")!;
            target.Width = target.MaxWidth;
            // Exercise the longest action labels without a live trace session.
            window.FindControl<Button>("StartStopButton")!.Content = "Stopping...";
            window.FindControl<SplitButton>("CopyButton")!.Content = "Copy: HTML";
            window.FindControl<SplitButton>("ExportButton")!.Content = "Export: HTML";
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var toolbar = window.FindControl<ToolbarPanel>("Toolbar")!;
            Assert.Equal(800, window.MinWidth);
            Assert.Equal(480, window.MinHeight);
            Assert.True(toolbar.CanWrap);
            var chrome = window.FindControl<Border>("ToolbarChrome")!;
            foreach (string name in new[] { "TargetInput", "StartStopButton", "SettingsButton", "CopyButton", "ExportButton" })
            {
                var control = window.FindControl<Control>(name)!;
                Point position = control.TranslatePoint(default, chrome)!.Value;
                Assert.True(control.Bounds.Height >= 28);
                Assert.True(position.X >= 0 && position.X + control.Bounds.Width <= chrome.Bounds.Width);
                Assert.True(position.Y >= 4, $"{name} needs space above it; top was {position.Y}.");
                Assert.True(position.Y + control.Bounds.Height <= chrome.Bounds.Height - 4,
                    $"{name} needs space below it.");
            }
            Assert.True(window.FindControl<DataGrid>("HopGrid")!.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Dialog_Confirmation_Controls_Fit_Before_And_After_MacOs_Restyle(bool macOs, bool minimumHeight)
    {
        Application application = Application.Current!;
        using var pack = new MacOsThemePack();
        pack.Attach(application, macOs ? PlatformProfile.MacOs : PlatformProfile.Default);
        var window = new SettingsWindow { DataContext = new SettingsViewModel() };
        if (minimumHeight)
        {
            // Headless posts resize notifications. Set the desired sizing mode
            // before Show, rather than racing its initial content-size request.
            window.SizeToContent = SizeToContent.Manual;
            window.Height = window.MinHeight;
        }
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal(window.MinWidth, window.Bounds.Width);
            Assert.Equal(window.ClientSize, window.Bounds.Size);
            if (minimumHeight)
                Assert.Equal(window.MinHeight, window.Bounds.Height);
            else
                Assert.Equal(SizeToContent.Height, window.SizeToContent);
            Assert.Equal(Resolve(application, "WinMtrShellFontFamily"), window.FontFamily);
            if (macOs)
            {
                var combo = window.FindControl<ComboBox>("ThemeBox")!;
                Assert.Equal(28, combo.MinHeight);
                Assert.Equal(new CornerRadius(6), combo.CornerRadius);
            }
            foreach (string name in new[] { "RestoreDefaultsButton", "OkButton", "CancelButton" })
            {
                var button = window.FindControl<Button>(name)!;
                Point position = button.TranslatePoint(default, window)!.Value;
                if (macOs)
                    Assert.Equal(28, button.MinHeight);
                // The narrow confirmation row keeps Fluent Compact's horizontal
                // footprint. Wider macOS toolbar padding would cause overflow.
                Assert.Equal(new Thickness(6, 4), button.Padding);
                Assert.True(button.Bounds.Width > 0 && button.Bounds.Height >= button.MinHeight);
                Assert.True(position.X >= 0 && position.X + button.Bounds.Width <= window.Bounds.Width,
                    $"{name} at {position} with size {button.Bounds.Size} exceeds window {window.Bounds.Size} horizontally.");
                Assert.True(position.Y >= 0 && position.Y + button.Bounds.Height <= window.Bounds.Height,
                    $"{name} at {position} with size {button.Bounds.Size} exceeds window {window.Bounds.Size} vertically.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Detaching_Shipped_Pack_Restores_Extracted_Resources()
    {
        var application = new App(PlatformProfile.Default);
        application.Initialize();
        object? originalFont = Resolve(application, "WinMtrShellFontFamily");
        object? originalRadius = Resolve(application, "WinMtrSurfaceCornerRadius");
        using var pack = new MacOsThemePack();
        pack.Attach(application, PlatformProfile.MacOs);

        pack.Dispose();

        Assert.Same(originalFont, Resolve(application, "WinMtrShellFontFamily"));
        Assert.Same(originalRadius, Resolve(application, "WinMtrSurfaceCornerRadius"));
        Assert.False(application.Resources.ContainsKey("WinMtrControlMinHeight"));
        Assert.DoesNotContain(application.Styles, style => ReferenceEquals(style, pack));
    }

    private static void AssertFormatMenuToggleTypes(MenuItemToggleType toggleType)
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        try
        {
            window.Show();
            foreach (string name in new[] { "CopyButton", "ExportButton" })
            {
                var button = window.FindControl<SplitButton>(name)!;
                var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
                flyout.ShowAt(button);
                Dispatcher.UIThread.RunJobs();
                var items = window.GetVisualDescendants().OfType<MenuItem>().ToArray();
                Assert.Equal(new[] { "Text", "HTML", "CSV", "JSON" }, items.Select(item => item.Header));
                Assert.All(items, item => Assert.Equal(toggleType, item.ToggleType));
                flyout.Hide();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static MacOsThemePack TestPack()
    {
        var pack = new MacOsThemePack();
        Palette(pack.Resources, ThemeVariant.Light)[PaletteKey] = Brushes.Red;
        Palette(pack.Resources, ThemeVariant.Dark)[PaletteKey] = Brushes.Blue;
        return pack;
    }

    private static IResourceDictionary Palette(IResourceDictionary resources, ThemeVariant variant) =>
        Assert.IsAssignableFrom<IResourceDictionary>(resources.ThemeDictionaries[variant]);

    private static object? Resolve(Application application, object key)
    {
        Assert.True(application.TryGetResource(key, application.ActualThemeVariant, out object? value));
        return value;
    }
}
