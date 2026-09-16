using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace WinMtr.Desktop.Services;

/// <summary>
/// Keeps the text on the accent-filled selected hop row readable.
///
/// The four grid selection keys fill the selected row with the accent.
/// App.axaml does not fix that accent. It is the accent of the operating
/// system. The user can set it to any colour. The user can change it while
/// WinMTR runs. So the two text brushes cannot be fixed colours. This service
/// is their one owner. It reads the accent that Fluent resolved. It sends
/// that accent to <see cref="AccentTextContrast"/>. It writes the result
/// into the application resources. The grid style reads the result through
/// DynamicResource.
///
/// It reads the resolved <c>SystemAccentColor</c>, not the platform value.
/// That key holds the colour that is on the row. Where the desktop reports
/// no accent, Fluent uses its own default blue. This service then reads the
/// same default.
///
/// A headless test host has no platform settings. It may have no accent
/// resource. Then this service writes nothing. The start-up defaults in
/// App.axaml stay in place.
/// </summary>
public static class AccentRowTextTheme
{
    private const string AccentKey = "SystemAccentColor";
    private const string TextKey = "WinMtrRowSelectedTextBrush";
    private const string FrozenTextKey = "WinMtrRowSelectedFrozenTextBrush";

    private static Application? attachedApplication;
    private static IPlatformSettings? attachedSettings;

    /// <summary>
    /// Starts the service for one application and sets the text brushes once.
    /// A second call for the same application does nothing.
    /// A call for a new application removes the old handler first.
    /// </summary>
    public static void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (ReferenceEquals(attachedApplication, application))
        {
            return;
        }

        Detach();

        attachedApplication = application;
        attachedSettings = application.PlatformSettings;
        if (attachedSettings is not null)
        {
            attachedSettings.ColorValuesChanged += OnColorValuesChanged;
        }

        Apply();
    }

    /// <summary>
    /// Stops the service and removes the platform handler.
    /// Only a test calls this method to restore the start state.
    /// </summary>
    internal static void Detach()
    {
        if (attachedSettings is not null)
        {
            attachedSettings.ColorValuesChanged -= OnColorValuesChanged;
            attachedSettings = null;
        }

        attachedApplication = null;
    }

    /// <summary>
    /// Writes the text brushes for one accent.
    /// Only a test calls this method with a fixed accent.
    /// </summary>
    internal static void ApplyAccent(Application application, Color accent)
    {
        ArgumentNullException.ThrowIfNull(application);
        AccentTextContrast.AccentRowText rowText = AccentTextContrast.PickRowText(accent);
        application.Resources[TextKey] = new SolidColorBrush(rowText.Text);
        application.Resources[FrozenTextKey] = new SolidColorBrush(rowText.Text, rowText.MutedOpacity);
    }

    private static void OnColorValuesChanged(object? sender, PlatformColorValues values) => Post();

    // Fluent also handles this event and rebuilds its palette from the new accent.
    // The platform raises the event on the UI thread and runs each handler in turn.
    // The posted read runs after those handlers, so it sees the new accent.
    private static void Post() => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);

    private static void Apply()
    {
        if (attachedApplication is null)
        {
            return;
        }

        if (attachedApplication.TryGetResource(AccentKey, attachedApplication.ActualThemeVariant, out object? value)
            && value is Color accent)
        {
            ApplyAccent(attachedApplication, accent);
        }
    }
}
