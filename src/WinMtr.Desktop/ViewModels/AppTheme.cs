namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// The application theme choice. App-level and in-memory only: it selects
/// the Avalonia <c>RequestedThemeVariant</c> and never enters the per-session
/// <c>ProbeSettings</c>. System follows the OS preference live.
/// </summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// One selectable theme entry for the settings dialog. Carries the stored
/// value plus its display text so the view needs no converter.
/// </summary>
/// <param name="Value">The stored theme choice.</param>
/// <param name="DisplayName">The text shown in the picker.</param>
public sealed record ThemeOption(AppTheme Value, string DisplayName);
