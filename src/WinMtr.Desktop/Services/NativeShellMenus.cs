using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Services;

// This module consumes the composition root's profile. The callbacks keep
// application lifetime ownership outside menu construction, including reopen.
internal sealed class NativeShellMenus : IDisposable
{
    private readonly Application _application;
    private readonly Func<MainWindow?> _currentWindow;
    private readonly Func<MainWindow?> _showMainWindow;
    private readonly Func<bool> _canOpenWindow;
    private readonly HashSet<MainWindow> _windows = [];
    private AboutWindow? _about;

    internal IRelayCommand PreferencesCommand { get; }

    internal static NativeShellMenus? Attach(Application application, PlatformProfile profile,
        Func<MainWindow?> currentWindow, Func<MainWindow?> showMainWindow, Func<bool>? canOpenWindow = null) =>
        profile == PlatformProfile.MacOs
            ? new NativeShellMenus(application, currentWindow, showMainWindow, canOpenWindow ?? (() => true))
            : null;

    private NativeShellMenus(Application application, Func<MainWindow?> currentWindow,
        Func<MainWindow?> showMainWindow, Func<bool> canOpenWindow)
    {
        _application = application;
        _currentWindow = currentWindow;
        _showMainWindow = showMainWindow;
        _canOpenWindow = canOpenWindow;
        PreferencesCommand = new RelayCommand(ShowPreferences, CanShowPreferences);
        application.Name = AppCredit.ProductName;
        var menu = new NativeMenu
        {
            new NativeMenuItem($"About {AppCredit.ProductName}…") { Command = new RelayCommand(ShowAbout) },
            new NativeMenuItem("Preferences…")
            {
                Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
                Command = PreferencesCommand,
            },
        };
        menu.NeedsUpdate += (_, _) => PreferencesCommand.NotifyCanExecuteChanged();
        // A non-null application menu replaces Avalonia's About fallback.
        // The native exporter still appends Services, Hide and Quit itself.
        NativeMenu.SetMenu(application, menu);
    }

    internal void AttachWindow(MainWindow window)
    {
        if (!_windows.Add(window))
            return;
        var menu = new NativeMenu();
        if (window.DataContext is MainWindowViewModel viewModel)
        {
            menu.Add(new NativeMenuItem("File")
            {
                Menu = new NativeMenu
                {
                    new NativeMenuItem("Export Report…") { Command = viewModel.ExportCommand },
                    new NativeMenuItem("Copy Report") { Command = viewModel.CopyCommand },
                },
            });
        }
        AddEditingAndClose(window, menu);
        NativeMenu.SetMenu(window, menu);
        window.PreferencesCommand.CanExecuteChanged += OnPreferencesChanged;
        window.PreferencesWindowOpened += AttachDialog;
        window.Closed += OnWindowClosed;
        PreferencesCommand.NotifyCanExecuteChanged();
    }

    private bool CanShowPreferences() => _canOpenWindow()
        && (_currentWindow()?.PreferencesCommand.CanExecute(null) ?? true);

    private void ShowPreferences()
    {
        if (!CanShowPreferences())
            return;
        MainWindow? window = _currentWindow() ?? _showMainWindow();
        if (window is null)
            return;
        AttachWindow(window);
        window.PreferencesCommand.Execute(null);
    }

    private void ShowAbout()
    {
        if (!_canOpenWindow())
            return;
        if (_about is not null)
        {
            _about.Activate();
            return;
        }
        _about = new AboutWindow();
        _about.Closed += (_, _) => _about = null;
        AttachDialog(_about);
        _about.Show();
    }

    private static void AttachDialog(Window window)
    {
        var menu = new NativeMenu();
        AddEditingAndClose(window, menu);
        NativeMenu.SetMenu(window, menu);
    }

    private static void AddEditingAndClose(Window window, NativeMenu menu)
    {
        menu.Add(new NativeMenuItem("Edit") { Menu = FocusedTextMenu.Create(window) });
        menu.Add(new NativeMenuItem("Window")
        {
            Menu = new NativeMenu
            {
                new NativeMenuItem("Close")
                {
                    Gesture = new KeyGesture(Key.W, KeyModifiers.Meta),
                    Command = new RelayCommand(window.Close),
                },
            },
        });
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) => PreferencesCommand.NotifyCanExecuteChanged();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
            DetachWindow(window);
        PreferencesCommand.NotifyCanExecuteChanged();
    }

    private void DetachWindow(MainWindow window)
    {
        window.PreferencesCommand.CanExecuteChanged -= OnPreferencesChanged;
        window.PreferencesWindowOpened -= AttachDialog;
        window.Closed -= OnWindowClosed;
        _windows.Remove(window);
    }

    public void Dispose()
    {
        foreach (MainWindow window in _windows.ToArray())
            DetachWindow(window);
        _about?.Close();
        NativeMenu.SetMenu(_application, null);
    }
}
