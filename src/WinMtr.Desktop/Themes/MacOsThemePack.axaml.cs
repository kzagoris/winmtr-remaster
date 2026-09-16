using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using WinMtr.Desktop.Services;

namespace WinMtr.Desktop.Themes;

// An application-lifetime style pack. Resources and selectors live in XAML;
// this attachment mechanism is independent of the visual values.
internal sealed class MacOsThemePack : Styles, IDisposable
{
    private readonly Dictionary<object, (bool Exists, object? Value)> originals = new();
    private Application? application;

    public MacOsThemePack() => AvaloniaXamlLoader.Load(this);

    public void Attach(Application target, PlatformProfile profile)
    {
        if (profile != PlatformProfile.MacOs || ReferenceEquals(application, target))
            return;

        Dispose();
        application = target;
        foreach (object key in Resources.Keys.Concat(Resources.ThemeDictionaries.Values
            .OfType<IResourceDictionary>().SelectMany(palette => palette.Keys)).Distinct())
        {
            bool exists = target.Resources.TryGetValue(key, out object? value);
            originals.Add(key, (exists, value));
        }

        // App.Initialize calls this after loading XAML. Later selectors win
        // over earlier setters of the same priority and specificity.
        target.Styles.Add(this);
        target.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        ApplyPalette();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs args) => ApplyPalette();

    private void ApplyPalette()
    {
        if (application is null)
            return;

        foreach (object key in originals.Keys)
        {
            if (Resources.TryGetResource(key, application.ActualThemeVariant, out object? value))
                application.Resources[key] = value;
            else
                Restore(key);
        }
    }

    private void Restore(object key)
    {
        var original = originals[key];
        if (original.Exists)
            application!.Resources[key] = original.Value;
        else
            application!.Resources.Remove(key);
    }

    public void Dispose()
    {
        if (application is null)
            return;

        application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        application.Styles.Remove(this);
        foreach (object key in originals.Keys)
            Restore(key);
        originals.Clear();
        application = null;
    }
}
