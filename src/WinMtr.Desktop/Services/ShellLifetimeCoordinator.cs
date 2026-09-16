using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Services;

internal enum ShellLifetimeState { Running, Closing, Quitting, Closed }

// All entry points run on the UI thread. Publish each completion before
// cancellation or window events can reenter: close and Quit share one drain,
// and no activation may reuse a disposed trace session.
internal sealed class ShellLifetimeCoordinator : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Func<MainWindow> _createWindow;
    private readonly Action _shutdown;
    private readonly Action? _unsubscribeActivation;
    private Task? _closeTask;
    private Task? _quitTask;

    internal ShellLifetimeState State { get; private set; } = ShellLifetimeState.Closed;
    internal MainWindow? CurrentWindow { get; private set; }
    internal bool CanOpenWindow => State is ShellLifetimeState.Running or ShellLifetimeState.Closed;

    internal static ShellLifetimeCoordinator? Attach(IClassicDesktopStyleApplicationLifetime desktop,
        PlatformProfile profile, Func<MainWindow> createWindow, Action? shutdown = null,
        Func<EventHandler<ActivatedEventArgs>, Action>? subscribeActivation = null) =>
        profile == PlatformProfile.MacOs
            ? new ShellLifetimeCoordinator(desktop, createWindow, shutdown ?? (() => desktop.Shutdown()), subscribeActivation)
            : null;

    private ShellLifetimeCoordinator(IClassicDesktopStyleApplicationLifetime desktop,
        Func<MainWindow> createWindow, Action shutdown,
        Func<EventHandler<ActivatedEventArgs>, Action>? subscribeActivation)
    {
        _desktop = desktop;
        _createWindow = createWindow;
        _shutdown = shutdown;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += OnShutdownRequested;
        _unsubscribeActivation = subscribeActivation?.Invoke(OnActivated);
        CreateWindow();
    }

    internal MainWindow? ShowMainWindow()
    {
        if (!CanOpenWindow)
            return null;
        MainWindow window = CurrentWindow ?? CreateWindow();
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    private MainWindow CreateWindow()
    {
        MainWindow window = _createWindow();
        _closeTask = null;
        CurrentWindow = window;
        _desktop.MainWindow = window;
        window.CloseRequested = CloseAsync;
        window.Closed += OnWindowClosed;
        State = ShellLifetimeState.Running;
        return window;
    }

    internal Task CloseAsync()
    {
        if (_closeTask is not null)
            return _closeTask;
        if (CurrentWindow is not { } window)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeTask = completion.Task;
        if (State != ShellLifetimeState.Quitting)
            State = ShellLifetimeState.Closing;
        _ = DrainAndCloseAsync(window, completion);
        return completion.Task;
    }

    private static async Task DrainAndCloseAsync(MainWindow window, TaskCompletionSource completion)
    {
        try
        {
            if (window.DataContext is MainWindowViewModel viewModel)
                await viewModel.ShutdownAsync();
            // An idle drain can complete synchronously. Leave the original
            // OnClosing stack before issuing the final, uncancelled close.
            await Task.Yield();
            window.CloseAfterShutdown();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    internal Task QuitAsync()
    {
        if (_quitTask is not null)
            return _quitTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _quitTask = completion.Task;
        State = ShellLifetimeState.Quitting;
        _ = DrainAndQuitAsync(completion);
        return completion.Task;
    }

    private async Task DrainAndQuitAsync(TaskCompletionSource completion)
    {
        try
        {
            await CloseAsync();
            // Also leave ShutdownRequested before forced shutdown when no
            // window exists. Avalonia's native request must see Cancel first.
            await Task.Yield();
            _shutdown();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        e.Cancel = true;
        await QuitAsync();
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen)
            ShowMainWindow();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window)
            return;
        window.Closed -= OnWindowClosed;
        window.CloseRequested = null;
        if (!ReferenceEquals(CurrentWindow, window))
            return;
        CurrentWindow = null;
        _desktop.MainWindow = null;
        if (State != ShellLifetimeState.Quitting)
            State = ShellLifetimeState.Closed;
    }

    public void Dispose()
    {
        _desktop.ShutdownRequested -= OnShutdownRequested;
        _unsubscribeActivation?.Invoke();
    }
}
