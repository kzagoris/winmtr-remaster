using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

public class ShellLifetimeTests
{
    [AvaloniaFact]
    public async Task CloseThenReopen_CreatesFreshUsableShell_AndRereadsStores()
    {
        var text = new FakeTextStore();
        var activation = new FakeActivationSource();
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        int exits = 0;
        using var lifetime = ShellLifetimeCoordinator.Attach(desktop, PlatformProfile.MacOs, CreateWindow,
            () => exits++, activation.Subscribe)!;
        MainWindow first = lifetime.ShowMainWindow()!;
        var firstViewModel = Assert.IsType<MainWindowViewModel>(first.DataContext);
        try
        {
            first.Close();
            await WaitUntilAsync(() => lifetime.State == ShellLifetimeState.Closed);
            Assert.False(first.IsVisible);
            Assert.Null(desktop.MainWindow);
            Assert.Equal(0, exits);
            Assert.Equal(ShutdownMode.OnExplicitShutdown, desktop.ShutdownMode);

            // Change the backing files while closed: a cached VM/store would
            // miss these changes, even if it appeared to preserve old values.
            var saved = AcceptedSettings.Default with { Theme = AppTheme.Dark, HistorySize = 3 };
            Assert.True(new JsonSettingsStore(text).Save(saved));
            new PersistentTargetHistoryStore(text).Add("example.com");

            activation.Reopen();
            MainWindow reopened = Assert.IsType<MainWindow>(lifetime.CurrentWindow);
            var viewModel = Assert.IsType<MainWindowViewModel>(reopened.DataContext);
            Assert.NotSame(first, reopened);
            Assert.NotSame(firstViewModel, viewModel);
            Assert.Same(reopened, desktop.MainWindow);
            activation.Reopen();
            Assert.Same(reopened, lifetime.CurrentWindow);
            Assert.Same(reopened, lifetime.ShowMainWindow());
            Assert.True(reopened.IsVisible);
            Assert.Equal(AppTheme.Dark, viewModel.AcceptedTheme);
            Assert.Equal(3, viewModel.AcceptedHistorySize);
            Assert.Equal(new[] { "example.com" }, viewModel.TargetHistory);
            viewModel.Target = "example.com";
            Assert.True(viewModel.StartStopCommand.CanExecute(null));
            Assert.True(reopened.FindControl<Button>("StartStopButton")!.IsEffectivelyEnabled);
            await viewModel.StartStopCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsSessionIdle);
            Assert.Equal("Last run: example.com", viewModel.AcceptedTargetText);
        }
        finally
        {
            await lifetime.QuitAsync();
        }

        MainWindow CreateWindow()
        {
            var settings = new JsonSettingsStore(text);
            AcceptedSettings accepted = settings.Load();
            var history = new PersistentTargetHistoryStore(text, accepted.HistorySize);
            return new MainWindow
            {
                DataContext = new MainWindowViewModel(history, new TracerScript().BuildFactory(),
                    settingsStore: settings, loadedSettings: accepted),
            };
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuitDuringTrace_AndQuitDuringClose_WaitForOneDrain_AndIgnoreReopen(bool closeFirst)
    {
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new GatedTraceEngine([]) { ReleaseDispose = releaseDrain };
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(),
            new TracerScript { Engine = engine }.BuildFactory())
        { Target = "example.com" };
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        var activation = new FakeActivationSource();
        int exits = 0;
        int created = 0;
        using var lifetime = ShellLifetimeCoordinator.Attach(desktop, PlatformProfile.MacOs, () =>
        {
            created++;
            return new MainWindow { DataContext = viewModel };
        }, () =>
        {
            Assert.True(engine.Disposed);
            Assert.Null(desktop.MainWindow);
            exits++;
        }, activation.Subscribe)!;
        MainWindow window = lifetime.ShowMainWindow()!;
        Task run = viewModel.StartStopCommand.ExecuteAsync(null);
        try
        {
            await engine.EnteredMoveNext.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (closeFirst)
            {
                window.Close();
                Assert.Equal(ShellLifetimeState.Closing, lifetime.State);
                Task close = lifetime.CloseAsync();
                window.Close();
                Assert.Same(close, lifetime.CloseAsync());
                Assert.False(close.IsCompleted);
                activation.Reopen();
                Assert.Same(window, lifetime.CurrentWindow);
                Assert.Equal(1, created);
                Assert.Null(lifetime.ShowMainWindow());
            }

            // This is the same ShutdownRequested path as the standard native
            // Quit item. It must veto Avalonia's immediate shutdown.
            Assert.False(desktop.TryShutdown());
            Task quit = lifetime.QuitAsync();
            Assert.Same(quit, lifetime.QuitAsync());
            Assert.False(desktop.TryShutdown());
            Assert.Equal(ShellLifetimeState.Quitting, lifetime.State);
            Assert.False(quit.IsCompleted);
            Assert.Equal(0, exits);
            Assert.True(window.IsVisible);
            Assert.False(lifetime.CanOpenWindow);
            activation.Reopen();
            Assert.Same(window, lifetime.CurrentWindow);
            Assert.Null(lifetime.ShowMainWindow());
            Assert.Equal(1, created);

            releaseDrain.SetResult();
            await Task.WhenAll(run, quit).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(window.IsVisible);
            Assert.Equal(1, exits);
            Assert.Null(lifetime.CurrentWindow);
            activation.Reopen();
            Assert.Null(lifetime.CurrentWindow);
            Assert.Equal(1, created);
            Assert.Null(lifetime.ShowMainWindow());
            Assert.Same(quit, lifetime.QuitAsync());
            await lifetime.QuitAsync();
            Assert.Equal(1, exits);
        }
        finally
        {
            releaseDrain.TrySetResult();
            await lifetime.QuitAsync();
            await run;
        }
    }

    [AvaloniaFact]
    public async Task QuitWhileResident_ExitsOnce_WithoutCreatingAnotherWindow()
    {
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        int created = 0;
        int exits = 0;
        using var lifetime = ShellLifetimeCoordinator.Attach(desktop, PlatformProfile.MacOs, () =>
        {
            created++;
            return new MainWindow { DataContext = new MainWindowViewModel() };
        }, () => exits++)!;
        lifetime.ShowMainWindow();
        await lifetime.CloseAsync();
        Assert.Equal(ShellLifetimeState.Closed, lifetime.State);
        Assert.False(desktop.TryShutdown());
        Task quit = lifetime.QuitAsync();
        Assert.Same(quit, lifetime.QuitAsync());
        Assert.Null(lifetime.ShowMainWindow());
        await quit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, created);
        Assert.Equal(1, exits);
    }

    [AvaloniaFact]
    public void DefaultProfile_KeepsItsExistingShutdownModeAndWindow()
    {
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        desktop.MainWindow = window;
        ShutdownMode original = desktop.ShutdownMode;
        using var lifetime = ShellLifetimeCoordinator.Attach(desktop, PlatformProfile.Default,
            () => throw new InvalidOperationException("Default must not create a resident shell"));

        Assert.Null(lifetime);
        Assert.Equal(ShutdownMode.OnLastWindowClose, original);
        Assert.Equal(original, desktop.ShutdownMode);
        Assert.Same(window, desktop.MainWindow);
        window.Show();
        window.Close();
        Assert.False(window.IsVisible);
    }

    // Exercise the same event consumed from the native Dock without relying
    // on a platform backend in headless tests.
    private sealed class FakeActivationSource
    {
        private event EventHandler<ActivatedEventArgs>? Activated;

        internal Action Subscribe(EventHandler<ActivatedEventArgs> handler)
        {
            Activated += handler;
            return () => Activated -= handler;
        }

        internal void Reopen() => Activated?.Invoke(this, new ActivatedEventArgs(ActivationKind.Reopen));
    }
}
