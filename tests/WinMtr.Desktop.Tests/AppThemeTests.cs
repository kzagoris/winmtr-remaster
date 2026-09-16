using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Behavioural coverage for the app-level theme choice: defaults, editor
/// copy-edit-publish alongside probe settings, and the Avalonia variant
/// mapping. Theme never enters ProbeSettings and stays in-memory only.
/// </summary>
public class AppThemeTests
{
    [Fact]
    public void Theme_Defaults_To_System_With_All_Options()
    {
        var editor = new SettingsViewModel();

        Assert.Equal(AppTheme.System, editor.Theme);
        Assert.Equal(0, editor.ThemeIndex);
        Assert.Equal(
            new[] { "System (follow OS)", "Light", "Dark" },
            editor.ThemeOptions.Select(option => option.DisplayName).ToArray());
        Assert.Equal(
            new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark },
            editor.ThemeOptions.Select(option => option.Value).ToArray());
    }

    [Theory]
    [InlineData(AppTheme.System, 0)]
    [InlineData(AppTheme.Light, 1)]
    [InlineData(AppTheme.Dark, 2)]
    public void Theme_And_Index_Stay_In_Step(AppTheme theme, int index)
    {
        var byTheme = new SettingsViewModel { Theme = theme };
        Assert.Equal(index, byTheme.ThemeIndex);

        var byIndex = new SettingsViewModel { ThemeIndex = index };
        Assert.Equal(theme, byIndex.Theme);
    }

    [Fact]
    public void AcceptedTheme_Defaults_To_System()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript().BuildFactory());

        Assert.Equal(AppTheme.System, viewModel.AcceptedTheme);
    }

    [Fact]
    public void CreateEditor_Seeds_Theme_And_Apply_Publishes_It()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript().BuildFactory());

        var editor = viewModel.CreateSettingsEditor();
        Assert.Equal(AppTheme.System, editor.Theme);

        editor.Theme = AppTheme.Dark;
        Assert.True(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(AppTheme.Dark, viewModel.AcceptedTheme);

        SettingsViewModel next = viewModel.CreateSettingsEditor();
        Assert.Equal(AppTheme.Dark, next.Theme);
        Assert.Equal(2, next.ThemeIndex);
    }

    [Fact]
    public void Apply_Invalid_Editor_Keeps_Accepted_Theme()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript().BuildFactory());
        var editor = viewModel.CreateSettingsEditor();
        editor.Theme = AppTheme.Light;
        editor.HopLimit = 0;

        Assert.False(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(AppTheme.System, viewModel.AcceptedTheme);
    }

    [Fact]
    public void Editor_Without_Apply_Leaves_Accepted_Theme()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript().BuildFactory());

        var editor = viewModel.CreateSettingsEditor();
        editor.Theme = AppTheme.Dark;

        Assert.Equal(AppTheme.System, viewModel.AcceptedTheme);
    }

    [Theory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void Probe_RoundTrip_Preserves_Theme(AppTheme theme)
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript().BuildFactory());
        var editor = viewModel.CreateSettingsEditor();
        editor.Theme = theme;

        Assert.True(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(theme, viewModel.AcceptedTheme);
        // Probe snapshot itself never carries the theme.
        Assert.Equal(1.0, editor.ProbeIntervalSeconds);
    }

    [Theory]
    [InlineData(AppTheme.System, "Default")]
    [InlineData(AppTheme.Light, "Light")]
    [InlineData(AppTheme.Dark, "Dark")]
    public void Mapper_Maps_Theme_To_Variant(AppTheme theme, string expectedVariant)
    {
        ThemeVariant variant = AppThemeMapper.ToThemeVariant(theme);

        Assert.Equal(expectedVariant, variant.ToString());
        Assert.Equal(theme, AppThemeMapper.FromThemeVariant(variant));
    }

    [Fact]
    public void Mapper_DisplayNames_Match_Picker()
    {
        Assert.Equal("System (follow OS)", AppThemeMapper.DisplayName(AppTheme.System));
        Assert.Equal("Light", AppThemeMapper.DisplayName(AppTheme.Light));
        Assert.Equal("Dark", AppThemeMapper.DisplayName(AppTheme.Dark));
    }

    [AvaloniaFact]
    public void ThemeBox_Binds_To_ThemeIndex_And_Options()
    {
        var editor = new SettingsViewModel { Theme = AppTheme.Dark };
        var dialog = new SettingsWindow { DataContext = editor };
        dialog.Show();
        try
        {
            var themeBox = dialog.FindControl<ComboBox>("ThemeBox");
            Assert.NotNull(themeBox);
            Assert.Equal(3, themeBox!.ItemCount);
            Assert.Equal(2, themeBox.SelectedIndex);

            themeBox.SelectedIndex = 1;
            Assert.Equal(AppTheme.Light, editor.Theme);

            editor.Theme = AppTheme.System;
            Assert.Equal(0, themeBox.SelectedIndex);
        }
        finally
        {
            dialog.Close();
        }
    }
}
