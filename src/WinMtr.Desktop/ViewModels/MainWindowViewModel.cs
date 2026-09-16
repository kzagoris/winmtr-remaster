using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Sessions;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Core.Reporting;
using WinMtr.Infrastructure;

namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// Shell view model: target entry with inline validation, the start/stop
/// control bound to the trace session state machine, the row surface the grid
/// binds to, the status surfaces, the banner stack, and the report format
/// lists derived from the engine's renderer abstraction.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string EmptyDetailText = "Select a hop to inspect it.";
    private const string EmptyIdleText = "No trace yet — enter a target and press Start.";
    private const string EmptyPreparingText = "Resolving target...";
    private const string EmptyRunningText = "Tracing...";
    private const string NoAcceptedTargetText = "Target: —";

    private readonly ITargetHistoryStore _history;
    private readonly ISettingsStore? _settingsStore;
    private bool _persistenceFailureReported;
    private readonly TraceSession _session;
    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;

    // Held only for the length of one recalculation of the visible order: the
    // row the selection names before the grid drifts, and the flag that keeps
    // the detail pane quiet through the drift.
    private HopRowViewModel? _rowUnderReorder;
    private bool _reordering;
    private readonly AppCredit _appCredit;
    private bool _targetValid;

    // The last capability preparation settled, if any. Drives the payload
    // control state of the next settings editor; unknown (never traced)
    // reads as available, never as restricted.
    private PayloadSupport? _knownPayloadSupport;

    // Messages this view model added as capability banners. A new session
    // retires those so a stale restriction notice can never describe the
    // session that follows it; other banners are untouched.
    private readonly HashSet<string> _capabilityBanners = new(StringComparer.Ordinal);

    // Preparation-failure banners (DNS resolution failed, probe init timed
    // out). Tracked separately so editing the target clears only these and a
    // new session retires them, without touching other banners.
    private readonly HashSet<string> _preparationBanners = new(StringComparer.Ordinal);

    // The two writers of the inline error slot. Input syntax is judged on
    // each keystroke; a rejection comes back from preparation. Each writer
    // keeps its own text and TargetError is derived from both, so neither
    // can erase the other and the order of the two calls does not matter.
    private string _syntaxError = string.Empty;

    private string _preparationError = string.Empty;

    // Set only while the target history list is rebuilt. See RefreshHistory.
    private bool _refreshingHistory;

    public MainWindowViewModel()
        : this(new InMemoryTargetHistoryStore())
    {
    }

    public MainWindowViewModel(
        ITargetHistoryStore history,
        Func<Tracer>? tracerFactory = null,
        IClipboardService? clipboardService = null,
        IReportFileSaver? reportFileSaver = null,
        AppCredit? appCredit = null,
        ISettingsStore? settingsStore = null,
        Services.AcceptedSettings? loadedSettings = null)
    {
        _history = history;
        _settingsStore = settingsStore;
        // A caller that already read the settings hands them over, so the
        // file is not read a second time at start.
        Services.AcceptedSettings? loaded = loadedSettings ?? settingsStore?.Load();
        if (loaded is not null)
        {
            AcceptedSettings = loaded.Probe;
            AcceptedTheme = loaded.Theme;
            AcceptedHistorySize = loaded.HistorySize;
            _history.HistorySize = loaded.HistorySize;
        }
        else
        {
            AcceptedHistorySize = _history.HistorySize;
        }
        _appCredit = appCredit ?? AppCredit.Default;
        _session = new TraceSession(tracerFactory);
        ClipboardService = clipboardService;
        ReportFileSaver = reportFileSaver;
        // The early transit-address check can only ever run on Linux; anywhere
        // else the runtime is unavailable, recorded now so the privilege
        // guidance can say so instead of implying a result. Never a pass
        // without an observation either way.
        TransitCheck = new UnprivilegedTransitCheck();
        if (!OperatingSystem.IsLinux())
            TransitCheck.RecordUnavailable();
        _session.PropertyChanged += OnSessionChanged;
        _session.Rows.CollectionChanged += OnRowsChanged;
        _session.SortedRowsReordering += OnSortedRowsReordering;
        _session.SortedRowsReordered += OnSortedRowsReordered;
        ReportFormats =
        [
            new TextReportRenderer(),
            new HtmlReportRenderer(),
            new CsvReportRenderer(),
            new JsonReportRenderer(),
        ];
        // Each menu gets its own rows over the same renderer list, so a row
        // carries the command of its menu and marks itself when its format is
        // the shown one. A new renderer needs no new code here.
        CopyFormatChoices = [.. ReportFormats.Select(format => new ReportFormatChoice(format, CopyAsCommand))];
        ExportFormatChoices = [.. ReportFormats.Select(format => new ReportFormatChoice(format, ExportAsCommand))];
        CopyFormat = ReportFormats[0];
        ExportFormat = ReportFormats[0];
        RefreshHistory();
        ValidateTarget();
        RefreshSessionSurfaces();
    }

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetError))]
    private string _targetError = string.Empty;

    public bool HasTargetError => TargetError.Length > 0;

    public ObservableCollection<string> TargetHistory { get; } = new();

    /// <summary>
    /// The session's live rows. The instance is stable: the grid binds once
    /// and observes in-place updates as snapshots arrive.
    /// </summary>
    public ObservableCollection<HopRowViewModel> Rows => _session.Rows;

    /// <summary>
    /// The grid's visible order: the session's rows arranged by the active
    /// sort descriptor. The same objects as <see cref="Rows"/>; sorting moves
    /// them and never replaces them, so selection survives every reorder.
    /// </summary>
    public ObservableCollection<HopRowViewModel> SortedRows => _session.SortedRows;

    /// <summary>
    /// The active sort descriptor. Defaults to hop-number ascending; a third
    /// select of the active header restores it through <see cref="ResetSort"/>.
    /// </summary>
    public HopSortColumn SortColumn => _session.SortColumn;

    public ListSortDirection SortDirection => _session.SortDirection;

    /// <summary>
    /// Sorts the grid by <paramref name="column"/> in <paramref name="direction"/>.
    /// Grid header clicks arrive here through the view; the session reorders
    /// the same row objects, so the descriptor survives snapshot updates and
    /// the visible order is recalculated as values change.
    /// </summary>
    public void ApplySort(HopSortColumn column, ListSortDirection direction) => _session.ApplySort(column, direction);

    [ObservableProperty]
    private HopRowViewModel? _selectedRow;

    public ObservableCollection<BannerViewModel> Banners { get; } = new();

    [ObservableProperty]
    private string _startStopLabel = "Start";

    [ObservableProperty]
    private string _sessionState = "Idle";

    [ObservableProperty]
    private bool _isSessionIdle = true;

    [ObservableProperty]
    private string _emptyStateText = EmptyIdleText;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _destinationText = "Destination: —";

    [ObservableProperty]
    private string _diagnosticText = string.Empty;

    /// <summary>
    /// The status-bar name of the run the other status surfaces describe: the
    /// target the session accepted, or an em dash while no session has
    /// accepted one. While the session is idle, an accepted target is the
    /// last run and says so; while it prepares or traces, the same target
    /// names the current run. Never the input text, so typing or clearing a
    /// name can never rename the evidence of the previous run; a new start
    /// clears this text with the elapsed time and the destination text
    /// together.
    /// </summary>
    [ObservableProperty]
    private string _acceptedTargetText = NoAcceptedTargetText;

    /// <summary>
    /// Short release label for the footer, e.g. v0.1.0. Reads the shared
    /// build version, so a release bump needs no UI change.
    /// </summary>
    public string FooterVersionText => _appCredit.VersionLabel;

    /// <summary>Author name for the footer credit.</summary>
    public string FooterAuthorText => _appCredit.Author;

    /// <summary>Repository URL opened when the footer author credit is activated.</summary>
    public string FooterRepositoryUrl => _appCredit.RepositoryUrl;

    [ObservableProperty]
    private string _detailText = EmptyDetailText;

    // The detail pane is open at start. The state is view-only and does not
    // persist across restarts.
    [ObservableProperty]
    private bool _isDetailExpanded = true;

    /// <summary>
    /// True while a hop is selected. The detail pane shows its typed fields
    /// while set, and the empty prompt while clear; the flat
    /// <see cref="DetailText"/> stays in step for reports and tests.
    /// </summary>
    [ObservableProperty]
    private bool _hasDetail;

    [ObservableProperty]
    private string _detailHopText = string.Empty;

    [ObservableProperty]
    private string _detailHost = string.Empty;

    [ObservableProperty]
    private string _detailAddress = string.Empty;

    [ObservableProperty]
    private string _detailStatus = string.Empty;

    [ObservableProperty]
    private string _detailLossText = string.Empty;

    [ObservableProperty]
    private string _detailLossCounts = string.Empty;

    [ObservableProperty]
    private string _detailBestText = string.Empty;

    [ObservableProperty]
    private string _detailAverageText = string.Empty;

    [ObservableProperty]
    private string _detailWorstText = string.Empty;

    [ObservableProperty]
    private string _detailLastText = string.Empty;

    [ObservableProperty]
    private string _detailNotice = string.Empty;

    [ObservableProperty]
    private bool _hasDetailNotice;

    [ObservableProperty]
    private bool _hasRows;

    public IReadOnlyList<IReportRenderer> ReportFormats { get; }

    [ObservableProperty]
    private IReportRenderer? _copyFormat;

    [ObservableProperty]
    private IReportRenderer? _exportFormat;

    /// <summary>The rows of the copy format menu, one per report format.</summary>
    public IReadOnlyList<ReportFormatChoice> CopyFormatChoices { get; }

    /// <summary>The rows of the export format menu, one per report format.</summary>
    public IReadOnlyList<ReportFormatChoice> ExportFormatChoices { get; }

    /// <summary>
    /// The confirmation popup anchored to the copy control. Never the status bar.
    /// </summary>
    public ConfirmationFlyout CopyConfirmation { get; } = new();

    /// <summary>The confirmation popup anchored to the export control.</summary>
    public ConfirmationFlyout ExportConfirmation { get; } = new();

    /// <summary>
    /// How long a copy or export confirmation stays open. Tests shorten this
    /// so the suite stays fast; production keeps the grilled 2.5 seconds.
    /// </summary>
    public TimeSpan ConfirmationDuration { get; set; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// The settings the next session starts with. Published only by
    /// confirming the modal dialog; cancel leaves this snapshot unchanged,
    /// and the session captures its own copy at Start, so nothing can change
    /// underneath a running trace.
    /// </summary>
    [ObservableProperty]
    private ProbeSettings _acceptedSettings = ProbeSettings.Default;

    /// <summary>
    /// The accepted app-level theme. Published only by confirming the modal
    /// dialog alongside <see cref="AcceptedSettings"/>; cancel leaves it
    /// unchanged. Defaults to System so the OS preference drives the app
    /// until the user pins Light or Dark; a store, when one is supplied,
    /// carries the choice across a restart.
    /// </summary>
    [ObservableProperty]
    private AppTheme _acceptedTheme = AppTheme.System;

    /// <summary>
    /// The accepted history size, published with the other accepted settings
    /// and applied to the target history at once.
    /// </summary>
    [ObservableProperty]
    private int _acceptedHistorySize = Services.AcceptedSettings.DefaultHistorySize;

    /// <summary>
    /// The early real-Linux check on whether an unprivileged transit-expired
    /// reply exposes the intermediate router address. Recorded as
    /// unavailable or pending when the runtime cannot run it, never as
    /// passed; the privilege banner reflects whatever is recorded.
    /// </summary>
    public UnprivilegedTransitCheck TransitCheck { get; }

    /// <summary>
    /// Writes rendered report text to the clipboard. Set by the window to the
    /// Avalonia clipboard; tests inject a recording substitute. Null (the
    /// default in view model tests) means copy renders but writes nowhere.
    /// </summary>
    public IClipboardService? ClipboardService { get; set; }

    /// <summary>
    /// Saves rendered reports under a suggested filename. Set by the window
    /// to the storage provider; tests inject a recording substitute. Null
    /// (the default in view model tests) means export renders but saves
    /// nowhere.
    /// </summary>
    public IReportFileSaver? ReportFileSaver { get; set; }

    /// <summary>
    /// Reports a failed write once per run. A damaged file is replaced by the
    /// next write and needs no message, but a confirmed choice that never
    /// reached the disk would otherwise be lost without a sign.
    /// </summary>
    public void ReportPersistenceFailure()
    {
        if (_persistenceFailureReported)
            return;
        _persistenceFailureReported = true;
        ShowBanner(
            "Settings and history cannot be saved to this computer. This session works normally, but the values return to their defaults at the next start.",
            BannerSeverity.Warning);
    }

    public void ShowBanner(string message, BannerSeverity severity) =>
        Banners.Add(new BannerViewModel(message, severity, banner => Banners.Remove(banner)));

    /// <summary>
    /// Builds a settings editor seeded from the accepted settings with the
    /// payload control state of the last settled capability. The dialog
    /// edits this copy; confirming publishes it via
    /// <see cref="ApplySettingsEditor"/>, cancelling discards it. Theme is
    /// seeded from <see cref="AcceptedTheme"/> and published the same way,
    /// staying app-level and out of ProbeSettings. A history clear follows
    /// the same rule: the editor carries the request with inline Undo, and
    /// only OK empties the store.
    /// </summary>
    public SettingsViewModel CreateSettingsEditor()
    {
        var editor = new SettingsViewModel();
        editor.FromProbeSettings(AcceptedSettings);
        editor.ApplyCapability(_knownPayloadSupport);
        editor.Theme = AcceptedTheme;
        editor.HistorySize = AcceptedHistorySize;
        editor.HistoryEntryCount = _history.Entries.Count;
        return editor;
    }

    /// <summary>
    /// Publishes a confirmed settings editor as the accepted settings for
    /// the next session plus the accepted app-level theme. Returns false
    /// and changes nothing when the editor is invalid. Applies a requested
    /// history clear; the dialog already reported it, so the status text
    /// stays free for trace diagnostics.
    /// </summary>
    public bool ApplySettingsEditor(SettingsViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.IsValid)
            return false;
        if (editor.IsHistoryCleared)
            _history.Clear();
        AcceptedSettings = editor.ToProbeSettings();
        AcceptedTheme = editor.Theme;
        AcceptedHistorySize = editor.HistorySize!.Value;
        _history.HistorySize = AcceptedHistorySize;
        RefreshHistory();
        if (_settingsStore is not null
            && !_settingsStore.Save(new Services.AcceptedSettings(AcceptedSettings, AcceptedTheme, AcceptedHistorySize)))
        {
            ReportPersistenceFailure();
        }
        return true;
    }

    // Start is available only when idle with a valid target; preparing and
    // tracing accept the stop request; stopping accepts nothing, so a second
    // stop can never queue behind the first.
    private bool CanStartStop => _session.State switch
    {
        TraceSessionState.Idle => _targetValid,
        TraceSessionState.Preparing => true,
        TraceSessionState.Tracing => true,
        _ => false,
    };

    // The start branch awaits the whole trace run, so the command stays
    // executing until the session stops. Without AllowConcurrentExecutions an
    // executing AsyncRelayCommand reports CanExecute false, which disables the
    // button exactly when it must offer Stop. CanStartStop is the only gate:
    // it already refuses a second stop, so nothing can run twice.
    [RelayCommand(CanExecute = nameof(CanStartStop), AllowConcurrentExecutions = true)]
    private async Task StartStopAsync()
    {
        if (_session.State is TraceSessionState.Preparing or TraceSessionState.Tracing)
        {
            await _session.StopAsync();
            return;
        }

        if (_session.State != TraceSessionState.Idle || !_targetValid)
            return;

        string accepted = Target.Trim();
        RetireCapabilityBanners();
        RetirePreparationBanners();
        await _session.StartAsync(accepted, AcceptedSettings);
    }

    // A third select of the active header restores the default ascending
    // hop-number order. Never gated: the reset stays available while tracing.
    // Public so the grid Sorting handler calls the same path as the command.
    [RelayCommand]
    public void ResetSort() => _session.ResetSort();

    private bool CanCopyOrExport => HasRows;

    /// <summary>
    /// The clipboard text for the current copy format: the captured current
    /// route snapshot rendered by the selected renderer. Null before the
    /// first snapshot or when no format is selected. Reads the renderer list,
    /// never a format-specific branch, so a new renderer needs no new code
    /// here.
    /// </summary>
    public string? RenderCopyContent() => _session.RenderReport(CopyFormat);

    /// <summary>
    /// The export request for the current export format: the suggested
    /// target-based filename with the renderer's own extension, plus the
    /// captured current route snapshot rendered by that renderer. Null before
    /// the first snapshot or when no format is selected.
    /// </summary>
    public (string FileName, string Content)? BuildExportRequest()
    {
        IReportRenderer? renderer = ExportFormat;
        Route? snapshot = _session.CurrentSnapshot;
        if (renderer is null || snapshot is null)
            return null;
        return (ExportFileName(renderer, snapshot), renderer.Render(snapshot));
    }

    // The accepted target names the file; before a session accepted one, the
    // snapshot's own target expression does.
    private string ExportFileName(IReportRenderer renderer, Route snapshot)
    {
        string target = _session.AcceptedTarget.Length > 0
            ? _session.AcceptedTarget
            : snapshot.Target.Expression.Text;
        return ReportFileName.Build(target, snapshot.TakenAt, renderer.Extension);
    }

    // Copy and export capture one immutable current snapshot and render only
    // that snapshot, so the report stays coherent while tracing and never
    // includes retained UI rows. Neither stops, resets, nor otherwise changes
    // the session; rows kept after a stop, completion, or fault keep these
    // enabled. The format comes from the bound picker over the
    // renderer-provided list, so each format produces its renderer's content.
    // The split buttons offer each renderer-provided format in one menu: the
    // menu item selects the format and then runs the same copy or export path,
    // so the toolbar keeps one control per action instead of a button beside a
    // picker. A new renderer needs no new code here.
    [RelayCommand(CanExecute = nameof(CanCopyOrExport))]
    private async Task CopyAsAsync(IReportRenderer format)
    {
        CopyFormat = format;
        await CopyAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrExport))]
    private async Task ExportAsAsync(IReportRenderer format)
    {
        ExportFormat = format;
        await ExportAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrExport))]
    private async Task CopyAsync()
    {
        IReportRenderer? renderer = CopyFormat;
        Route? snapshot = _session.CurrentSnapshot;
        if (renderer is null || snapshot is null || ClipboardService is null)
            return;
        string content = renderer.Render(snapshot);
        try
        {
            await ClipboardService.SetTextAsync(content);
        }
        catch (Exception ex)
        {
            ShowBanner($"Copy failed: {ex.Message}", BannerSeverity.Error);
            return;
        }
        CopyConfirmation.Show(BuildCopyConfirmation(snapshot.Hops.Length, renderer.Name), ConfirmationDuration);
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrExport))]
    private async Task ExportAsync()
    {
        IReportRenderer? renderer = ExportFormat;
        Route? snapshot = _session.CurrentSnapshot;
        if (renderer is null || snapshot is null || ReportFileSaver is null)
            return;
        string fileName = ExportFileName(renderer, snapshot);
        string content = renderer.Render(snapshot);
        bool saved;
        try
        {
            saved = await ReportFileSaver.SaveAsync(fileName, content);
        }
        catch (Exception ex)
        {
            ShowBanner($"Export failed: {ex.Message}", BannerSeverity.Error);
            return;
        }
        if (saved)
            ExportConfirmation.Show(BuildExportConfirmation(snapshot.Hops.Length, renderer.Name), ConfirmationDuration);
    }

    /// <summary>
    /// Builds the copy confirmation for the trimmed route snapshot that the
    /// report renders: route length plus renderer name, never retained rows.
    /// Pure so tests pin the wording without a clock.
    /// </summary>
    public static string BuildCopyConfirmation(int routeLength, string formatName) =>
        $"Copied {routeLength} hops as {formatName}";

    /// <summary>
    /// Builds the export confirmation for the trimmed route snapshot.
    /// </summary>
    public static string BuildExportConfirmation(int routeLength, string formatName) =>
        $"Saved {routeLength} hops as {formatName}";

    /// <summary>
    /// Cancels the active session, waits for its enumeration to drain, and
    /// disposes the session. Window-close and quit callers share one completion,
    /// including callers that arrive while the drain is already in flight.
    /// </summary>
    public Task ShutdownAsync()
    {
        TaskCompletionSource completion;
        lock (_shutdownGate)
        {
            if (_shutdownTask is not null)
                return _shutdownTask;

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish before cancellation can notify observers that call back
            // into shutdown. No caller may bypass the in-flight drain.
            _shutdownTask = completion.Task;
        }

        _ = DrainSessionAsync(completion);
        return completion.Task;
    }

    private async Task DrainSessionAsync(TaskCompletionSource completion)
    {
        try
        {
            await _session.DisposeAsync();
            completion.SetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.SetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    // A history refresh never changes the target text. A target picked from
    // the dropdown is the AutoCompleteBox's selected item, but the box keeps
    // its older search text. Any change to the items makes the box lose that
    // selection and write the older search text back, often empty, through
    // the two-way binding. The target is kept and published again, and that
    // write is not an edit, so validation and preparation failures stay.
    private void RefreshHistory()
    {
        string target = Target;
        _refreshingHistory = true;
        try
        {
            TargetHistory.Clear();
            foreach (var entry in _history.Entries)
            {
                TargetHistory.Add(entry);
            }
            Target = target;
        }
        finally
        {
            _refreshingHistory = false;
        }
    }

    // Invalid input is handled before preparation: the session stays idle,
    // the error reads inline, and Start stays disabled.
    private void ValidateTarget()
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            _syntaxError = string.Empty;
            _targetValid = false;
        }
        else if (!TargetExpression.TryParse(Target, out _, out string? error))
        {
            _syntaxError = error ?? string.Empty;
            _targetValid = false;
        }
        else
        {
            _syntaxError = string.Empty;
            _targetValid = true;
        }

        RefreshTargetError();
        StartStopCommand.NotifyCanExecuteChanged();
    }

    // What the person typed is judged first: a syntax error describes the
    // text in the box now, a rejection describes the text preparation last
    // refused.
    private void RefreshTargetError() =>
        TargetError = _syntaxError.Length > 0 ? _syntaxError : _preparationError;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TraceSession.State):
                RefreshSessionSurfaces();
                RefreshAcceptedTargetText();
                MaybeAddHistoryOnTracing();
                MaybeShowCapabilityBanner();
                MaybeShowPreparationFailureBanner();
                break;
            case nameof(TraceSession.ElapsedText):
                ElapsedText = _session.ElapsedText;
                break;
            case nameof(TraceSession.DestinationText):
                DestinationText = _session.DestinationText;
                break;
            case nameof(TraceSession.Diagnostic):
                DiagnosticText = _session.Diagnostic;
                break;
            case nameof(TraceSession.AcceptedTarget):
                RefreshAcceptedTargetText();
                break;
            case nameof(TraceSession.HasFault):
                if (_session.HasFault)
                    ShowBanner(_session.Diagnostic, BannerSeverity.Error);
                break;
            case nameof(TraceSession.SortColumn):
            case nameof(TraceSession.SortDirection):
                OnPropertyChanged(nameof(SortColumn));
                OnPropertyChanged(nameof(SortDirection));
                break;
        }
    }

    private void RefreshSessionSurfaces()
    {
        (SessionState, StartStopLabel) = _session.State switch
        {
            TraceSessionState.Preparing => ("Preparing...", "Cancel"),
            TraceSessionState.Tracing => ("Tracing", "Stop"),
            TraceSessionState.Stopping => ("Stopping...", "Stopping..."),
            _ => ("Idle", "Start"),
        };
        IsSessionIdle = _session.State == TraceSessionState.Idle;
        EmptyStateText = _session.State switch
        {
            TraceSessionState.Preparing => EmptyPreparingText,
            TraceSessionState.Tracing or TraceSessionState.Stopping => EmptyRunningText,
            _ => HasPreparationFailure ? _session.Diagnostic : EmptyIdleText,
        };
        StartStopCommand.NotifyCanExecuteChanged();
    }

    // One rule names the run the other status surfaces describe: while the
    // session is idle, an accepted target belongs to a session that has ended
    // and reads as the last run; else the accepted target names the current
    // run, and no accepted target reads as an em dash. The input text never
    // reaches this surface.
    private void RefreshAcceptedTargetText()
    {
        if (_session.AcceptedTarget.Length == 0)
        {
            AcceptedTargetText = NoAcceptedTargetText;
            return;
        }

        AcceptedTargetText = _session.State == TraceSessionState.Idle
            ? $"Last run: {_session.AcceptedTarget}"
            : $"Target: {_session.AcceptedTarget}";
    }

    private bool HasPreparationFailure =>
        !_session.HasFault
        && _session.Capabilities is null
        && _session.Diagnostic.Length > 0
        && Rows.Count == 0;

    // Preparation settled a capability on the transition into tracing: tell
    // the truth about it once. Supported stays silent; restricted and
    // undetermined each add one dismissible banner and still trace with the
    // default payload. An equal banner is never added twice, so repeated
    // sessions do not stack notices.
    private void MaybeShowCapabilityBanner()
    {
        if (_session.State != TraceSessionState.Tracing)
            return;
        PayloadSupport? support = _session.Capabilities?.PayloadSupport;
        if (support is null)
            return;
        _knownPayloadSupport = support;
        string? banner = CapabilityMessages.CapabilityBanner(support, TransitCheck.Status);
        if (banner is null || Banners.Any(existing => existing.Message == banner))
            return;
        _capabilityBanners.Add(banner);
        ShowBanner(banner, BannerSeverity.Warning);
    }

    // A new session retires the previous session's capability notices so a
    // stale restriction can never describe the session that follows it.
    private void RetireCapabilityBanners() => RetireBanners(_capabilityBanners);

    private void RetirePreparationBanners()
    {
        _preparationError = string.Empty;
        RefreshTargetError();
        RetireBanners(_preparationBanners);
    }

    // Removes the banners this view model added under one purpose and
    // forgets them; banners of other purposes stay.
    private void RetireBanners(HashSet<string> owned)
    {
        if (owned.Count == 0)
            return;
        for (int i = Banners.Count - 1; i >= 0; i--)
        {
            if (owned.Contains(Banners[i].Message))
                Banners.RemoveAt(i);
        }

        owned.Clear();
    }

    // Only targets that resolve enter the target history: preparation runs
    // before the entry is saved, so an unresolvable name never pollutes
    // autocomplete and never turns the first Enter into a history pick.
    private void MaybeAddHistoryOnTracing()
    {
        if (_session.State != TraceSessionState.Tracing)
            return;
        string accepted = _session.AcceptedTarget.Trim();
        if (accepted.Length == 0)
            return;
        _history.Add(accepted);
        RefreshHistory();
    }

    // A preparation failure (DNS miss, init timeout) never reaches tracing,
    // so it never sets HasFault. Surface it here as a red error banner once
    // per session: idle plus a diagnostic with no rows and no capabilities.
    // Cancellation stays silent because its diagnostic is empty.
    private void MaybeShowPreparationFailureBanner()
    {
        if (_session.State != TraceSessionState.Idle || !HasPreparationFailure)
            return;
        string message = _session.Diagnostic;
        // The rejection reads next to the input, in the same slot as a
        // validation error: the target the person just typed was refused, so
        // the explanation belongs where they typed it.
        _preparationError = message;
        RefreshTargetError();
        if (_preparationBanners.Contains(message) || Banners.Any(existing => existing.Message == message))
            return;
        _preparationBanners.Add(message);
        ShowBanner(message, BannerSeverity.Error);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasRows = Rows.Count > 0;
        CopyCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CopyAsCommand.NotifyCanExecuteChanged();
        ExportAsCommand.NotifyCanExecuteChanged();
        // A new session clears the rows: a selection pointing at a row the
        // session no longer holds is released, returning the detail pane to
        // its empty text. Moves within the sorted order never replace the
        // object, so a live reorder keeps the selection.
        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
            SelectedRow = null;
    }

    // Editing the target after a preparation failure clears the stale
    // error: banners retire, the status line clears, and the center text
    // returns to idle. The session keeps its diagnostic until the next
    // start clears it, but nothing surfaces it again until then.
    private void ClearPreparationFailureOnEdit()
    {
        if (_session.State != TraceSessionState.Idle || !HasPreparationFailure)
            return;
        RetirePreparationBanners();
        DiagnosticText = string.Empty;
        if (Rows.Count == 0)
            EmptyStateText = EmptyIdleText;
    }

    partial void OnCopyFormatChanged(IReportRenderer? value) => MarkActive(CopyFormatChoices, value);

    partial void OnExportFormatChanged(IReportRenderer? value) => MarkActive(ExportFormatChoices, value);

    // Exactly one row of a menu stands marked: the row whose format is the
    // shown format of that menu.
    private static void MarkActive(IReadOnlyList<ReportFormatChoice> choices, IReportRenderer? active)
    {
        foreach (ReportFormatChoice choice in choices)
            choice.IsActive = ReferenceEquals(choice.Format, active);
    }

    partial void OnTargetChanged(string value)
    {
        if (_refreshingHistory)
            return;
        ValidateTarget();
        ClearPreparationFailureOnEdit();
    }

    // The selection names a hop row, never a place in the visible order. A
    // recalculation of that order reaches the grid as removals and
    // insertions, and a grid tracks its selection by place, so it hands the
    // selection to whichever row takes the vacated place and the two-way
    // binding writes that wrong row back here. The picked row is recorded
    // before the recalculation and put back after it, so the selection and
    // the detail pane keep the hop the user picked, through a header select
    // and through a live reorder alike.
    private void OnSortedRowsReordering(object? sender, EventArgs e)
    {
        _rowUnderReorder = SelectedRow;
        _reordering = true;
    }

    private void OnSortedRowsReordered(object? sender, EventArgs e)
    {
        HopRowViewModel? picked = _rowUnderReorder;
        _rowUnderReorder = null;
        _reordering = false;

        // A row the session no longer holds (a new session clearing, or the
        // hop-limit bound trimming) is not put back; OnRowsChanged releases it.
        if (picked is not null && Rows.Contains(picked))
            SelectedRow = picked;

        // The pane is quiet through the drift and revises once here, on the
        // settled selection, however many places the rows moved.
        RefreshDetailText();
    }

    partial void OnSelectedRowChanged(HopRowViewModel? oldValue, HopRowViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedRowStateChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedRowStateChanged;
        RefreshDetailText();
    }

    // The detail pane follows the selection through live updates: any value it
    // shows revises the text without reselecting, so the selection survives
    // snapshot updates by hop index even when the hop's address changes. The
    // pane shows every value a row holds, so any change to the selected row
    // revises it; one selected row costs a handful of string formats a second
    // and needs no list to keep in step with the row.
    private void OnSelectedRowStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedRow))
            RefreshDetailText();
    }

    private void RefreshDetailText()
    {
        // Through a recalculation the selection drifts by place; the settled
        // selection revises the pane once, from OnSortedRowsReordered.
        if (_reordering)
            return;

        HopRowViewModel? value = SelectedRow;
        if (value is null)
        {
            DetailText = EmptyDetailText;
            HasDetail = false;
            HasDetailNotice = false;
            DetailNotice = string.Empty;
            return;
        }

        // The loss is derived from the same raw sent and received counts shown
        // beside it, so the arithmetic stays checkable; a hop with no samples
        // reports zero loss with zero counts, and absent measurements render
        // as waiting rather than zero-latency results. An empty latest probe
        // state means an ordinary reply and reads as OK so the line is never
        // blank; while frozen the displayed status carries the frozen marker.
        double loss = value.Sent == 0 ? 0 : 100.0 * (value.Sent - value.Received) / value.Sent;
        long lost = value.Sent - value.Received;
        string status = string.IsNullOrEmpty(value.Status)
            ? (value.IsFrozen ? "OK (frozen)" : "OK")
            : value.DisplayStatus;

        // Each value is held one per field, so the pane can lay them out as
        // labelled tiles instead of one wrapped paragraph.
        DetailHopText = $"Hop {value.Hop}";
        DetailHost = value.Host.Length == 0 ? "--" : value.Host;
        DetailAddress = value.Address.Length == 0 ? "--" : value.Address;
        DetailStatus = status;
        DetailLossText = $"{loss:F2}%";
        DetailLossCounts = $"{lost} lost of {value.Sent} sent";
        DetailBestText = FormatLatency(value.BestMs);
        DetailAverageText = FormatLatency(value.AverageMs);
        DetailWorstText = FormatLatency(value.WorstMs);
        DetailLastText = FormatLatency(value.LastMs);
        DetailNotice = value.IdentityNotice;
        HasDetailNotice = value.HasIdentityChange;
        HasDetail = true;

        // The flat text is the same values in one paragraph, composed from
        // the fields above so every figure is formatted exactly once. The
        // host stays as the row holds it: the identity line reads the hop's
        // own name, never the tile's placeholder.
        DetailText = $"{DetailHopText} — {value.Host}\n"
            + $"Address: {DetailAddress}\n"
            + $"Loss: {DetailLossText} — {DetailLossCounts}, {value.Received} received\n"
            + $"Best: {DetailBestText}, Average: {DetailAverageText}, "
            + $"Worst: {DetailWorstText}, Last: {DetailLastText}\n"
            + $"Status: {DetailStatus}"
            + (HasDetailNotice ? $"\n{DetailNotice}" : string.Empty);
    }

    private static string FormatLatency(double? value) => value is null ? "--" : $"{value:F0} ms";
}
