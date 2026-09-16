using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

public class MacOsMenuTests
{
    [AvaloniaFact]
    public void DefaultProfile_DoesNotTouchApplicationOrWindowMenus()
    {
        var application = new Application();
        var existing = new NativeMenu { new NativeMenuItem("Existing") };
        NativeMenu.SetMenu(application, existing);
        using var menus = NativeShellMenus.Attach(application, PlatformProfile.Default,
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException());

        Assert.Null(menus);
        Assert.Same(existing, NativeMenu.GetMenu(application));
        Assert.Null(NativeMenu.GetMenu(new MainWindow()));
    }

    [AvaloniaFact]
    public void MacOsMenus_HaveProductItems_AndOnlyTheApprovedGestures()
    {
        var application = new Application();
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        using var menus = NativeShellMenus.Attach(application, PlatformProfile.MacOs, () => window, () => window)!;
        menus.AttachWindow(window);

        var appMenu = NativeMenu.GetMenu(application)!;
        Assert.Equal(new[] { "About WinMTR…", "Preferences…" }, Headers(appMenu));
        Assert.Null(Item(appMenu, "About WinMTR…").Gesture);
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), Item(appMenu, "Preferences…").Gesture);
        var menu = NativeMenu.GetMenu(window)!;
        Assert.Equal(new[] { "File", "Edit", "Window" }, Headers(menu));
        var file = Item(menu, "File").Menu!;
        Assert.Equal(new[] { "Export Report…", "Copy Report" }, Headers(file));
        Assert.Same(viewModel.ExportCommand, Item(file, "Export Report…").Command);
        Assert.Same(viewModel.CopyCommand, Item(file, "Copy Report").Command);
        Assert.All(file.Items.Cast<NativeMenuItem>(), item => Assert.Null(item.Gesture));
        Assert.False(Item(file, "Copy Report").IsEnabled);
        Assert.False(Item(file, "Export Report…").IsEnabled);
        Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Meta), Item(Item(menu, "Window").Menu!, "Close").Gesture);
        var edit = Item(menu, "Edit").Menu!;
        Assert.Equal(new[] { "Cut", "Copy", "Paste", "Select All" }, Headers(edit));
        Assert.Equal(new[] { Key.X, Key.C, Key.V, Key.A }, edit.Items.Cast<NativeMenuItem>().Select(item => item.Gesture!.Key));
        Assert.All(edit.Items.Cast<NativeMenuItem>(), item => Assert.Equal(KeyModifiers.Meta, item.Gesture!.KeyModifiers));
    }

    [AvaloniaFact]
    public async Task Preferences_ReopensMainWindow_AndSharesTheSettingsButtonGuard()
    {
        MainWindow? window = null;
        bool allowOpen = true;
        int created = 0;
        using var menus = NativeShellMenus.Attach(new Application(), PlatformProfile.MacOs,
            () => window, () =>
            {
                created++;
                window = new MainWindow { DataContext = new MainWindowViewModel() };
                window.Show();
                return window;
            }, () => allowOpen)!;
        try
        {
            allowOpen = false;
            menus.PreferencesCommand.Execute(null);
            Assert.Null(window);
            allowOpen = true;
            menus.PreferencesCommand.Execute(null);
            Assert.Equal(1, created);
            Assert.True(window!.IsVisible);
            var dialog = Assert.Single(window.OwnedWindows.OfType<SettingsWindow>());
            menus.PreferencesCommand.Execute(null);
            window.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(dialog, Assert.Single(window.OwnedWindows.OfType<SettingsWindow>()));
            dialog.Close(false);
            await WaitUntilAsync(() => !window.PreferencesCommand.IsRunning);
        }
        finally
        {
            window?.Close();
        }
    }

    [AvaloniaFact]
    public async Task SessionTransitions_UpdateMenuEnablement_AndGuardDirectPreferencesExecution()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(target, DateTimeOffset.UtcNow,
            RouteScript.RespondingHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new GatedTraceEngine([snapshot]) { ReleaseDispose = drain };
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript
        {
            Target = target,
            ResolveAsync = async (_, ct) => { await prepare.Task.WaitAsync(ct); return target; },
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory())
        { Target = "example.com" };
        var window = new MainWindow { DataContext = viewModel };
        var application = new Application();
        using var menus = NativeShellMenus.Attach(application, PlatformProfile.MacOs, () => window, () => window)!;
        menus.AttachWindow(window);
        window.Show();
        var preferences = Item(NativeMenu.GetMenu(application)!, "Preferences…");
        var file = Item(NativeMenu.GetMenu(window)!, "File").Menu!;
        try
        {
            Assert.True(preferences.IsEnabled);
            Task run = viewModel.StartStopCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Preparing...");
            AssertBlocked();
            Assert.False(Item(file, "Copy Report").IsEnabled);
            prepare.SetResult();
            await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.HasRows);
            AssertBlocked();
            Assert.True(Item(file, "Copy Report").IsEnabled);
            Assert.True(Item(file, "Export Report…").IsEnabled);
            Task stop = viewModel.StartStopCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Stopping...");
            AssertBlocked();
            drain.SetResult();
            await Task.WhenAll(run, stop);
            Assert.True(preferences.IsEnabled);
            Assert.True(Item(file, "Copy Report").IsEnabled);
        }
        finally
        {
            prepare.TrySetResult();
            drain.TrySetResult();
            await viewModel.ShutdownAsync();
            window.Close();
        }

        void AssertBlocked()
        {
            Assert.False(preferences.IsEnabled);
            menus.PreferencesCommand.Execute(null);
            window.PreferencesCommand.Execute(null);
            Assert.Empty(window.OwnedWindows);
        }
    }

    [AvaloniaFact]
    public void EditCommands_TargetTheFocusedEditor_NotTheReport()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        var editor = new TextBox { Text = "example.com" };
        window.Content = editor;
        using var menus = NativeShellMenus.Attach(new Application(), PlatformProfile.MacOs, () => window, () => window)!;
        menus.AttachWindow(window);
        window.Show();
        try
        {
            editor.Focus();
            var edit = Item(NativeMenu.GetMenu(window)!, "Edit").Menu!;
            Item(edit, "Select All").Command!.Execute(null);
            Assert.Equal("example.com", editor.SelectedText);
            bool copied = false;
            editor.CopyingToClipboard += (_, e) => { copied = true; e.Handled = true; };
            Item(edit, "Copy").Command!.Execute(null);
            Assert.True(copied);
            editor.IsReadOnly = true;
            Assert.False(Item(edit, "Cut").Command!.CanExecute(null));
            Assert.False(Item(edit, "Paste").Command!.CanExecute(null));
            Item(Item(NativeMenu.GetMenu(window)!, "Window").Menu!, "Close").Command!.Execute(null);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AboutWindow_UsesSharedCredit_AndIsStandalone()
    {
        var credit = new AppCredit("Test author", "https://example.test/repository");
        var about = new AboutWindow(credit);
        Assert.Equal("About WinMTR", about.Title);
        Assert.Null(about.Owner);
        var content = Assert.IsType<StackPanel>(about.Content);
        Assert.Contains(content.Children.OfType<TextBlock>(), text => text.Text == credit.VersionLabel);
        Assert.Contains(content.Children.OfType<Button>(), button => Equals(button.Content, credit.RepositoryUrl));
    }

    private static NativeMenuItem Item(NativeMenu menu, string header) =>
        Assert.Single(menu.Items.OfType<NativeMenuItem>(), item => item.Header == header);

    private static string?[] Headers(NativeMenu menu) => menu.Items.OfType<NativeMenuItem>().Select(item => item.Header).ToArray();
}
