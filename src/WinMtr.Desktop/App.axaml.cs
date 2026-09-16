using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Themes;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop;

public class App : Application
{
    private readonly PlatformProfile profile;
    private readonly MacOsThemePack? themePack;
    private NativeShellMenus? menus;
    private ShellLifetimeCoordinator? shellLifetime;

    public App() : this(PlatformProfile.Default) { }

    internal App(PlatformProfile profile, MacOsThemePack? themePack = null)
    {
        this.profile = profile;
        this.themePack = themePack;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (profile == PlatformProfile.MacOs)
            (themePack ?? new MacOsThemePack()).Attach(this, profile);
        menus = NativeShellMenus.Attach(this, profile,
            () => shellLifetime?.CurrentWindow,
            () => shellLifetime?.ShowMainWindow(), () => shellLifetime?.CanOpenWindow == true);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The selected hop row uses the accent of the operating system.
        // So the text on it is computed, not fixed. Start the owner of
        // those two brushes before the first window shows.
        AccentRowTextTheme.Attach(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            shellLifetime = ShellLifetimeCoordinator.Attach(desktop, profile, CreateMainWindow,
                subscribeActivation: SubscribeActivation);
            if (shellLifetime is null)
                desktop.MainWindow = CreateMainWindow();
            desktop.Exit += (_, _) =>
            {
                shellLifetime?.Dispose();
                menus?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Keep Avalonia's non-implementable lifetime interface at the composition
    // root. The coordinator owns the subscription through its cleanup action.
    private Action SubscribeActivation(EventHandler<ActivatedEventArgs> handler)
    {
        if (TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime activation)
            return () => { };
        activation.Activated += handler;
        return () => activation.Activated -= handler;
    }

    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        menus?.AttachWindow(window);
        return window;
    }

    // Both stores share one text seam and one read-only view of the previous
    // version (ADR 0005). The settings are read once and handed to both the
    // history store and the view model. The history store writes on its own,
    // so it is told where to report a failed write once the view model exists.
    private static MainWindowViewModel BuildViewModel()
    {
        var text = new FileTextStore();
        var legacy = new WindowsLegacyStore();
        var settings = new JsonSettingsStore(text, legacy);
        AcceptedSettings accepted = settings.Load();
        var history = new PersistentTargetHistoryStore(text, accepted.HistorySize, legacy);

        var viewModel = new MainWindowViewModel(history, settingsStore: settings, loadedSettings: accepted);
        history.WriteFailed = viewModel.ReportPersistenceFailure;
        MainWindow.ApplyTheme(viewModel.AcceptedTheme);
        return viewModel;
    }
}
