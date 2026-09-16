using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;

namespace WinMtr.Desktop.Services;

public sealed class WindowIconTheme : AvaloniaObject
{
    private static readonly Lazy<WindowIcon> LightIcon = new(
        () => LoadIcon("winmtr-route-pulse-light.ico"));
    private static readonly Lazy<WindowIcon> DarkIcon = new(
        () => LoadIcon("winmtr-route-pulse-dark.ico"));

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<WindowIconTheme, Window, bool>("IsEnabled");

    static WindowIconTheme()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>(OnIsEnabledChanged);
    }

    private WindowIconTheme()
    {
    }

    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Window window, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            window.ActualThemeVariantChanged += OnActualThemeVariantChanged;
            ApplyIcon(window);
        }
        else
        {
            window.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        }
    }

    private static void OnActualThemeVariantChanged(object? sender, EventArgs args)
    {
        if (sender is Window window)
        {
            ApplyIcon(window);
        }
    }

    private static void ApplyIcon(Window window)
    {
        window.Icon = window.ActualThemeVariant == ThemeVariant.Dark
            ? DarkIcon.Value
            : LightIcon.Value;
    }

    private static WindowIcon LoadIcon(string fileName)
    {
        return new WindowIcon(AssetLoader.Open(AppAssets.Locate(fileName)));
    }
}
