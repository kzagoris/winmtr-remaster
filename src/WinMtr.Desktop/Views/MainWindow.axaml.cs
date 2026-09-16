using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;
    private bool _preferencesUnavailable;
    private SettingsWindow? _settingsWindow;
    private MainWindowViewModel? _preferencesViewModel;

    internal IAsyncRelayCommand PreferencesCommand { get; }
    internal event Action<Window>? PreferencesWindowOpened;
    internal Func<Task>? CloseRequested { get; set; }

    public MainWindow()
    {
        PreferencesCommand = new AsyncRelayCommand(ShowPreferencesAsync,
            () => !_preferencesUnavailable && DataContext is MainWindowViewModel { IsSessionIdle: true },
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        InitializeComponent();
        // Mica/blur is Windows-only: on Linux compositors a transparent
        // hint shows the wallpaper through the window, so keep the XAML
        // opaque default everywhere else.
        if (OperatingSystem.IsWindows())
        {
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
            };
            Background = global::Avalonia.Media.Brushes.Transparent;
        }
    }

    // Copy and export reach the platform through the view model's service
    // seams: the window supplies the Avalonia clipboard and storage provider
    // once the top level exists, while tests inject recording substitutes.
    // Injected substitutes win, so headless checks keep their fakes.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            if (viewModel.ClipboardService is null && Clipboard is not null)
                viewModel.ClipboardService = new AvaloniaClipboardService(Clipboard);
            viewModel.ReportFileSaver ??= new StorageReportFileSaver(this);
        }
    }

    // Closing during preparation or tracing cancels the active session, waits
    // for its enumeration to drain, and disposes the session before the window
    // closes: an abandoned undisposed enumerator has no cleanup guarantee.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        _preferencesUnavailable = true;
        PreferencesCommand.NotifyCanExecuteChanged();
        if (!_shutdownComplete && CloseRequested is { } closeRequested)
        {
            e.Cancel = true;
            await closeRequested();
            return;
        }
        if (!_shutdownComplete
            && DataContext is MainWindowViewModel viewModel
            && !viewModel.IsSessionIdle)
        {
            e.Cancel = true;
            await viewModel.ShutdownAsync();
            _shutdownComplete = true;
            Close();
            return;
        }

        base.OnClosing(e);
        if (e.Cancel)
        {
            _preferencesUnavailable = false;
            PreferencesCommand.NotifyCanExecuteChanged();
        }
    }

    internal void CloseAfterShutdown()
    {
        _shutdownComplete = true;
        Close();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_preferencesViewModel is not null)
            _preferencesViewModel.PropertyChanged -= OnPreferencesStateChanged;
        _preferencesViewModel = DataContext as MainWindowViewModel;
        if (_preferencesViewModel is not null)
            _preferencesViewModel.PropertyChanged += OnPreferencesStateChanged;
        PreferencesCommand.NotifyCanExecuteChanged();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_preferencesViewModel is not null)
            _preferencesViewModel.PropertyChanged -= OnPreferencesStateChanged;
        base.OnClosed(e);
    }

    private void OnPreferencesStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSessionIdle))
            PreferencesCommand.NotifyCanExecuteChanged();
    }

    // The history chevron only nudges the suggestion popup: opening and
    // focusing are pure view concerns, so no view model call is involved.
    // A plain Button is used instead of a ToggleButton so no :checked
    // accent block paints while the popup stands open. Focus comes before
    // opening: focusing an open AutoCompleteBox closes its popup, so the
    // previous open-then-focus order opened and instantly closed the
    // history, leaving an empty textbox with no options.
    private void OnTargetHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (TargetInput.IsDropDownOpen)
        {
            TargetInput.IsDropDownOpen = false;
            return;
        }
        TargetInput.Focus();
        TargetInput.IsDropDownOpen = true;
    }

    // Enter in the target box does what the Start button does: the box is
    // only enabled while the session is idle, so the command can only start a
    // trace here. A key the AutoCompleteBox already used — Enter that commits
    // a history suggestion — arrives handled and is left alone, so the first
    // Enter picks the suggestion and the next one starts the trace.
    private void OnTargetInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || DataContext is not MainWindowViewModel viewModel
            || !viewModel.StartStopCommand.CanExecute(null))
            return;

        e.Handled = true;
        viewModel.StartStopCommand.Execute(null);
    }

    // Showing a modal dialog is a view concern; the dialog edits a copy of
    // the accepted settings seeded by the view model, and only confirmation
    // publishes it back, so cancel leaves the accepted settings unchanged.
    // Theme is app-level: confirming also pins the application variant
    // (System follows the OS live), cancelling leaves it alone.
    private void OnFooterLinkClicked(object? sender, RoutedEventArgs e)
    {
        // Opening a browser is a view concern: the view model only carries
        // the repository URL, so headless tests keep no launcher. Failures
        // stay silent so a missing browser never disturbs a trace.
        if (DataContext is not MainWindowViewModel viewModel
            || string.IsNullOrWhiteSpace(viewModel.FooterRepositoryUrl))
            return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                _ = launcher.LaunchUriAsync(new Uri(viewModel.FooterRepositoryUrl));
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(viewModel.FooterRepositoryUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // No browser available: leave the session untouched.
        }
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e) => PreferencesCommand.Execute(null);

    private async Task ShowPreferencesAsync()
    {
        // ICommand.Execute can be called even when CanExecute is false.
        // Both entry points use this guard, including repeated menu requests.
        if (!PreferencesCommand.CanExecute(null) || DataContext is not MainWindowViewModel viewModel)
            return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var editor = viewModel.CreateSettingsEditor();
        var dialog = new SettingsWindow { DataContext = editor };
        _settingsWindow = dialog;
        try
        {
            PreferencesWindowOpened?.Invoke(dialog);
            bool confirmed = await dialog.ShowDialog<bool>(this);
            if (confirmed && !_preferencesUnavailable && viewModel.IsSessionIdle)
            {
                viewModel.ApplySettingsEditor(editor);
                ApplyTheme(viewModel.AcceptedTheme);
            }
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    internal static void ApplyTheme(AppTheme theme)
    {
        if (global::Avalonia.Application.Current is not null)
            global::Avalonia.Application.Current.RequestedThemeVariant = AppThemeMapper.ToThemeVariant(theme);
    }

    private void OnHopGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        // The grid's own sorting is suppressed: the session owns the single
        // sort descriptor (column plus direction) so the order survives
        // snapshots and is recalculated as values change, with numeric columns
        // ordered by value and waiting measurements kept last. A header click
        // only selects the descriptor in a three-step cycle — a new column
        // sorts ascending, repeating the active column toggles to descending,
        // selecting it a third time restores hop-number order — and the
        // session reorders the same row objects, so selection is never lost
        // to a sort.
        // The sort key is the column's SortMemberPath, the row value the
        // session orders by; the header title is presentation only, because
        // the latency headers carry the (ms) unit while SortMemberPath stays
        // the row property name.
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        if (HopSortColumns.FromSortMemberPath(e.Column?.SortMemberPath) is not { } column)
            return;

        e.Handled = true;
        if (viewModel.SortColumn != column)
            viewModel.ApplySort(column, ListSortDirection.Ascending);
        else if (viewModel.SortDirection == ListSortDirection.Ascending)
            viewModel.ApplySort(column, ListSortDirection.Descending);
        else
            viewModel.ResetSort();

        // A sort rewrites every row's display index, and so every stripe.
        // Restripe once the grid has refreshed its containers.
        Dispatcher.UIThread.Post(ResyncHopRowClasses, DispatcherPriority.Background);
    }

    // Retained-row treatments stay in XAML theme resources; the code-behind
    // only keeps the row classes in step with the row view model. LoadingRow
    // fires as containers are realized (and recycled), UnloadingRow detaches
    // the subscription before a container is reused for another row.
    private void OnHopRowLoading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not HopRowViewModel row)
            return;

        SyncHopRowClasses(e.Row, row);
        row.PropertyChanged += OnHopRowStateChanged;
    }

    private void OnHopRowUnloading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is HopRowViewModel row)
            row.PropertyChanged -= OnHopRowStateChanged;
    }

    private void OnHopRowStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(HopRowViewModel.IsFrozen) or nameof(HopRowViewModel.HasIdentityChange)))
            return;

        ResyncHopRowClasses();
    }

    // Updates arrive several times a second while only a handful of rows are
    // realized, so re-syncing every realized container is cheaper than
    // tracking which container shows which row.
    private void ResyncHopRowClasses()
    {
        foreach (DataGridRow container in HopGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (container.DataContext is HopRowViewModel row)
                SyncHopRowClasses(container, row);
        }
    }

    private static void SyncHopRowClasses(DataGridRow container, HopRowViewModel row)
    {
        container.Classes.Set("frozen", row.IsFrozen);
        container.Classes.Set("identity-changed", row.HasIdentityChange);
        // Striping is macOS-only presentation, but the class is profile-free:
        // the macOS pack styles it, and the default profile has no such rule.
        container.Classes.Set("stripe", container.Index % 2 == 1);
    }
}
