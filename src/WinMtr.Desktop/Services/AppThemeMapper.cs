using Avalonia.Styling;
using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Services;

/// <summary>
/// Maps the settings-level <see cref="AppTheme"/> choice to the Avalonia
/// theme variant. System maps to <see cref="ThemeVariant.Default"/> so the
/// OS light/dark preference drives the app live.
/// </summary>
public static class AppThemeMapper
{
    public static ThemeVariant ToThemeVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static AppTheme FromThemeVariant(ThemeVariant? variant)
    {
        if (variant == ThemeVariant.Light)
            return AppTheme.Light;
        if (variant == ThemeVariant.Dark)
            return AppTheme.Dark;
        return AppTheme.System;
    }

    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "System (follow OS)",
    };
}
