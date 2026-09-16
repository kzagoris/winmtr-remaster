using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Core.Reporting;
using WinMtr.Infrastructure;

namespace WinMtr.Desktop.Sessions;

/// <summary>
/// Lifecycle of one graphical trace session. The view model binds to this
/// state rather than tracking its own flags.
/// </summary>
public enum TraceSessionState
{
    Idle,
    Preparing,
    Tracing,
    Stopping,
}

/// <summary>
/// Owns the whole trace session lifecycle: accepting a target, preparing,
/// running, and stopping. Lives in the application project rather than a view
/// model, and stays testable without a user interface: behaviour is driven
/// through the tracer factory seam shared with the console suite.
/// </summary>
/// <remarks>
/// The consuming loop resumes on the synchronization context of the caller of
/// <see cref="StartAsync"/>; the view model starts the session on the UI
/// thread, so route snapshots are applied there with no dispatching layer.
/// Each published snapshot is applied and then discarded, so no queue
/// accumulates; the engine's lossy publication drops whatever a slow consumer
/// misses. Reconciliation retains one stable row per discovered hop index for
/// the life of the trace session (ADR 0003): a skipped intermediate
/// publication leaves no stale rows, because a row only ever reflects the
/// latest snapshot that contained its hop or freezes at its last true values.
/// </remarks>
public sealed partial class TraceSession : ObservableObject, IAsyncDisposable
{
    private readonly Func<Tracer> _tracerFactory;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private IAsyncEnumerator<Route>? _enumerator;
    private ProbeSettings _settings = ProbeSettings.Default;
    private DateTimeOffset? _firstTakenAt;
    private Route? _currentSnapshot;
    private bool _disposed;
    private bool _suspendSortedSync;

    // Row values that feed the visible order. Identity and diagnostic flags
    // outside this set (address text beyond Host, the frozen flag beyond its
    // displayed status, the identity highlight) never move a row.
    private static readonly HashSet<string> SortAffectingProperties = new(StringComparer.Ordinal)
    {
        nameof(HopRowViewModel.Hop),
        nameof(HopRowViewModel.Host),
        nameof(HopRowViewModel.LossPercent),
        nameof(HopRowViewModel.Sent),
        nameof(HopRowViewModel.Received),
        nameof(HopRowViewModel.BestMs),
        nameof(HopRowViewModel.AverageMs),
        nameof(HopRowViewModel.WorstMs),
        nameof(HopRowViewModel.LastMs),
        nameof(HopRowViewModel.Status),
        nameof(HopRowViewModel.DisplayStatus),
    };

    [ObservableProperty]
    private TraceSessionState _state = TraceSessionState.Idle;

    /// <summary>
    /// The target this session accepted, or empty while no session has
    /// accepted one. A session accepts its target only when preparation
    /// succeeds, so a name that does not resolve never appears here and no
    /// status surface can present a rejected name as the accepted target.
    /// </summary>
    [ObservableProperty]
    private string _acceptedTarget = string.Empty;

    [ObservableProperty]
    private string _diagnostic = string.Empty;

    [ObservableProperty]
    private string _destinationText = "Destination: —";

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private bool _hasFault;

    /// <summary>
    /// The probe capabilities settled by the latest successful preparation,
    /// if any. Cleared when a new session starts and set before tracing
    /// begins, so observers can distinguish supported, restricted, and
    /// undetermined payload support. Null while preparing, after a failed
    /// preparation, and before the first session.
    /// </summary>
    [ObservableProperty]
    private ProbeCapabilities? _capabilities;

    /// <summary>
    /// The active sort column. Defaults to hop number; the descriptor
    /// survives every route-snapshot update and is only changed by
    /// <see cref="ApplySort"/> or <see cref="ResetSort"/>.
    /// </summary>
    [ObservableProperty]
    private HopSortColumn _sortColumn = HopSortColumn.Hop;

    /// <summary>
    /// The active sort direction. Defaults to ascending.
    /// </summary>
    [ObservableProperty]
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public TraceSession(Func<Tracer>? tracerFactory = null)
    {
        _tracerFactory = tracerFactory ?? (static () => new Tracer());
        Rows.CollectionChanged += OnRowsCollectionChanged;
    }

    /// <summary>
    /// The live grid rows, keyed by hop index. The instance is stable for the
    /// life of the session so bindings, selection, and sorting observe steady
    /// row objects; snapshots update rows in place and append newly discovered
    /// hops. Rows are cleared when a new session starts and kept when a
    /// session stops, completes, or faults, so evidence stays available.
    /// </summary>
    public ObservableCollection<HopRowViewModel> Rows { get; } = new();

    /// <summary>
    /// The grid's visible order: the same row objects as <see cref="Rows"/>,
    /// arranged by the active sort descriptor. Sorting moves rows and never
    /// replaces them, so selection and scroll position observe steady row
    /// identities while the visible order is recalculated whenever a row's
    /// values change. Frozen retained rows participate with their actual
    /// values.
    /// </summary>
    public ObservableCollection<HopRowViewModel> SortedRows { get; } = new();

    /// <summary>
    /// Raised immediately before the visible order is recalculated. A view
    /// that holds a selection records which row object it holds, because a
    /// recalculation reaches the grid as removals and insertions (see
    /// <c>ResortSortedRows</c>) and a grid tracks its selection by place, not
    /// by row. Raised on every recalculation, including the ones a live
    /// snapshot causes, and always paired with
    /// <see cref="SortedRowsReordered"/>.
    /// </summary>
    public event EventHandler? SortedRowsReordering;

    /// <summary>
    /// Raised immediately after the visible order is recalculated, once the
    /// grid has applied every removal and insertion. A view that holds a
    /// selection puts back the row object it recorded, so the selection keeps
    /// the hop the user picked instead of the place that hop had.
    /// </summary>
    public event EventHandler? SortedRowsReordered;

    /// <summary>
    /// The latest published route snapshot applied to the rows, if any. The
    /// instance is immutable, so capturing the reference captures a coherent
    /// report source: reports render this trimmed snapshot and never the
    /// retained UI rows, which deliberately diverge from it. Null before the
    /// first snapshot of a session and after a new session clears.
    /// </summary>
    public Route? CurrentSnapshot => _currentSnapshot;

    /// <summary>
    /// The settings governing the current or latest session. Immutable for
    /// the life of the session: the snapshot taken at Start, except that a
    /// restricted or undetermined capability falls back to the default
    /// payload, so this always reports what the session actually uses.
    /// </summary>
    public ProbeSettings ActiveSettings => _settings;

    /// <summary>
    /// Renders the captured current route snapshot with <paramref name="renderer"/>.
    /// Captures one immutable snapshot reference per call, so the report is
    /// internally coherent and never mixes snapshots or retained rows. Returns
    /// null when no snapshot has arrived yet or no renderer is selected. Never
    /// stops, resets, or otherwise changes the session.
    /// </summary>
    public string? RenderReport(IReportRenderer? renderer)
    {
        Route? snapshot = _currentSnapshot;
        if (snapshot is null || renderer is null)
            return null;
        return renderer.Render(snapshot);
    }

    /// <summary>
    /// Sorts the grid by <paramref name="column"/> in <paramref name="direction"/>.
    /// Available at any time, including while tracing: the descriptor
    /// survives snapshot updates and the visible order is recalculated as
    /// values change.
    /// </summary>
    public void ApplySort(HopSortColumn column, ListSortDirection direction)
    {
        if (SortColumn == column && SortDirection == direction)
            return;

        // Both halves land before the visible order is recalculated once, so
        // observers never see a transient half-descriptor order.
        using (SuspendSortedSync())
        {
            SortColumn = column;
            SortDirection = direction;
        }

        SyncSortedRows();
    }

    /// <summary>
    /// Restores the default ascending hop-number order in one action after any
    /// other sort. Available at any time, including while tracing.
    /// </summary>
    public void ResetSort() => ApplySort(HopSortColumn.Hop, ListSortDirection.Ascending);

    /// <summary>
    /// Starts a session for <paramref name="targetText"/>. Returns the session
    /// task, which completes once the session has settled back to
    /// <see cref="TraceSessionState.Idle"/> after stopping, completion, fault,
    /// or preparation failure. Calls arriving outside
    /// <see cref="TraceSessionState.Idle"/> are ignored rather than queued.
    /// </summary>
    public Task StartAsync(string targetText, ProbeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(targetText);
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != TraceSessionState.Idle)
                return Task.CompletedTask;

            // A new session begins with empty rows and statistics so figures
            // from a previous target cannot mislead.
            _settings = settings;
            Capabilities = null;
            _firstTakenAt = null;
            _currentSnapshot = null;
            Rows.Clear();
            // The target is only requested here; the session accepts it when
            // preparation succeeds. A rejected name must leave no trace on the
            // status surfaces.
            AcceptedTarget = string.Empty;
            Diagnostic = string.Empty;
            DestinationText = "Destination: —";
            ElapsedText = "00:00";
            HasFault = false;
            State = TraceSessionState.Preparing;

            _cts = new CancellationTokenSource();
            _runTask = RunAsync(targetText.Trim(), settings, _cts.Token);
            return _runTask;
        }
    }

    /// <summary>
    /// Stops the session. During preparing this cancels promptly back to idle;
    /// during tracing this moves to stopping and completes once cancellation
    /// has drained. A stop arriving while already stopping, or while idle, is
    /// ignored rather than queued. Never throws for state reasons.
    /// </summary>
    public Task StopAsync()
    {
        Task? run;
        lock (_gate)
        {
            if (State is not (TraceSessionState.Preparing or TraceSessionState.Tracing))
                return Task.CompletedTask;

            if (State == TraceSessionState.Tracing)
                State = TraceSessionState.Stopping;
            // Preparing keeps its state until the preparation observes the
            // cancellation and settles to idle.

            _cts?.Cancel();
            run = _runTask;
        }

        return run ?? Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the active session, waits for its enumeration to drain, and
    /// releases it. The window awaits this before closing: an abandoned
    /// undisposed enumerator has no cleanup guarantee.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts?.Cancel();
            run = _runTask;
        }

        if (run is not null)
        {
            try
            {
                await run;
            }
            catch (Exception)
            {
                // Session faults are already surfaced through Diagnostic;
                // disposal itself must not raise them a second time.
            }
        }
    }

    /// <summary>
    /// Applies the latest published route snapshot to the rows and the status
    /// surfaces. Public so deterministic tests can drive exact snapshot
    /// sequences — including skipped intermediates — with no network traffic.
    /// </summary>
    public void ApplySnapshot(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);

        // The report source is the immutable snapshot itself, captured here.
        // Later snapshots replace the reference; an in-progress render holds
        // its own reference, so reports never mix snapshots or retained rows.
        _currentSnapshot = route;

        // Status surfaces describe the snapshot being applied, so set them
        // before mutating rows: row notifications fire synchronously and a
        // concurrent observer polling Rows.Count must never see new rows with
        // the previous snapshot's status text.
        _firstTakenAt ??= route.TakenAt;
        ElapsedText = FormatElapsed(route.TakenAt - _firstTakenAt.Value);
        DestinationText = DescribeDestination(route, _settings.HopLimit);

        // Row updates fire one notification per changed value several times a
        // second; the sorted mirror is reconciled once below instead of after
        // every notification.
        using (SuspendSortedSync())
        {
            var hops = route.Hops;
            for (int i = 0; i < hops.Length; i++)
            {
                if (i < Rows.Count)
                    UpdateLiveRow(Rows[i], hops[i]);
                else
                    Rows.Add(CreateRow(hops[i]));
            }

            // Retained hop rows (ADR 0003): a row that has appeared stays for the
            // rest of the trace session, even when later snapshots trim its hop
            // as trailing silence. Rows beyond the current snapshot are no longer
            // receiving observations, so they freeze at their last true values
            // and say so on their face; nothing is removed, and no filler rows
            // are created for hops never discovered.
            for (int i = hops.Length; i < Rows.Count; i++)
                Rows[i].IsFrozen = true;

            // Bounded retention: the grid holds at most one row per hop index
            // within the session's hop limit. Snapshots trim at the hop limit, so
            // this only bites on unexpected input; retention still never grows
            // with time, snapshots, or probes.
            while (Rows.Count > _settings.HopLimit)
                Rows.RemoveAt(Rows.Count - 1);
        }

        SyncSortedRows();
    }

    private async Task RunAsync(string targetText, ProbeSettings settings, CancellationToken ct)
    {
        // Yield once so preparation and consumption resume on the caller's
        // synchronization context (the UI thread in production) rather than
        // running inline inside StartAsync.
        await Task.Yield();

        PreparedTrace trace;
        try
        {
            trace = await _tracerFactory().CreateTraceAsync(targetText, settings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation during preparation is not a fault: back to idle
            // with no diagnostic.
            SettleIdle();
            return;
        }
        catch (Exception ex)
        {
            Diagnostic = DescribePreparationFailure(targetText, ex);
            SettleIdle();
            return;
        }

        // Preparation settled the capabilities: publish them before the state
        // flip below, because observers present supported, restricted, and
        // undetermined payload support on that transition. A restricted or
        // undetermined capability falls back to the default payload, so the
        // recorded settings always report what the session actually uses.
        // The engine already ignores a custom payload on such platforms (its
        // channel sends the platform default), so only the record changes
        // here; hop limit and cadence are untouched. In all three capability
        // cases tracing proceeds: detection never prevents a session from
        // starting.
        Capabilities = trace.Capabilities;
        _settings = EffectivePayloadSettings(settings, trace.Capabilities);

        // Preparation succeeded, so the session accepts the target now.
        // Observers read this as the name of the run the status surfaces
        // describe.
        AcceptedTarget = targetText;
        State = TraceSessionState.Tracing;

        _enumerator = trace.Snapshots.GetAsyncEnumerator();
        try
        {
            while (await _enumerator.MoveNextAsync())
            {
                ApplySnapshot(_enumerator.Current);
                if (ct.IsCancellationRequested)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is normal shutdown: the engine completes rather
            // than throws, but either shape drains here without a fault.
        }
        catch (Exception ex)
        {
            // A fault while tracing keeps every gathered row and measurement
            // so subsequent evidence actions remain available. The diagnostic
            // is assigned before the fault flag so observers never see a fault
            // with an empty explanation.
            Diagnostic = $"Trace stopped: {ex.Message} Rows gathered so far are kept — copy or export the evidence.";
            HasFault = true;
        }
        finally
        {
            try
            {
                if (_enumerator is not null)
                    await _enumerator.DisposeAsync();
            }
            catch (Exception)
            {
                // Disposal drains owned work; a disposal failure must neither
                // mask gathered rows nor strand the session outside idle.
            }
            finally
            {
                _enumerator = null;
                SettleIdle();
            }
        }
    }

    private void SettleIdle() => State = TraceSessionState.Idle;

    partial void OnSortColumnChanged(HopSortColumn value)
    {
        if (!_suspendSortedSync)
            SyncSortedRows();
    }

    partial void OnSortDirectionChanged(ListSortDirection value)
    {
        if (!_suspendSortedSync)
            SyncSortedRows();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Rows is the session's hop-keyed store; the visible order mirrors it.
        // Snapshot batches reconcile once at the end of ApplySnapshot instead.
        if (!_suspendSortedSync)
            SyncSortedRows();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspendSortedSync)
            return;
        if (e.PropertyName is null || SortAffectingProperties.Contains(e.PropertyName))
            ResortSortedRows();
    }

    private void SyncSortedRows()
    {
        // Membership is decided by two set lookups rather than a scan per row:
        // this runs on every snapshot, several times a second.
        var live = new HashSet<HopRowViewModel>(Rows);

        // Rows that left the session (a new session clearing, or the hop-limit
        // bound trimming) leave the visible order and drop their subscription.
        // Rows are only ever removed here, never replaced, so a selection
        // observing a retained row stays valid across every snapshot.
        for (int i = SortedRows.Count - 1; i >= 0; i--)
        {
            HopRowViewModel row = SortedRows[i];
            if (!live.Contains(row))
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                SortedRows.RemoveAt(i);
            }
        }

        var shown = new HashSet<HopRowViewModel>(SortedRows);
        foreach (HopRowViewModel row in Rows)
        {
            if (shown.Add(row))
            {
                row.PropertyChanged += OnRowPropertyChanged;
                SortedRows.Add(row);
            }
        }

        ResortSortedRows();
    }

    private void ResortSortedRows()
    {
        // Minimal changes only: row objects are never replaced, so selection
        // and scroll position observe steady identities while the visible
        // order tracks the values. The comparer breaks every tie by hop
        // number, so the result is deterministic for equal values.
        //
        // A row changes place as a removal and an insertion, not as a move
        // notification: the grid keeps its own view of this collection, that
        // view ignores move notifications and stays in its stale order, but it
        // applies removals and insertions correctly.
        //
        // A grid tracks its selection by place, so the removal of the selected
        // row hands the selection to whichever row takes that place. The two
        // events bracket the whole recalculation: a view records its selected
        // row before and puts it back after, which keeps the selection on the
        // hop the user picked. The bracket holds for every caller, so a live
        // reorder during a trace behaves as a header select does.
        SortedRowsReordering?.Invoke(this, EventArgs.Empty);
        try
        {
            ReorderSortedRows();
        }
        finally
        {
            SortedRowsReordered?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReorderSortedRows()
    {
        List<HopRowViewModel> ordered =
            Rows.OrderBy(row => row, new HopRowComparer(SortColumn, SortDirection)).ToList();

        // One position map, kept in step with each move, instead of searching
        // the visible order for every row on every snapshot.
        var positions = new Dictionary<HopRowViewModel, int>(SortedRows.Count);
        for (int i = 0; i < SortedRows.Count; i++)
            positions[SortedRows[i]] = i;

        for (int i = 0; i < ordered.Count; i++)
        {
            HopRowViewModel row = ordered[i];
            int current = positions[row];
            if (current == i)
                continue;

            SortedRows.RemoveAt(current);
            SortedRows.Insert(i, row);
            // Everything between the two places shifted one step towards the
            // vacated slot; only that span moved, so only it is renumbered.
            for (int j = i; j <= current; j++)
                positions[SortedRows[j]] = j;
        }
    }

    // Batches row and descriptor changes: the visible order is reconciled once
    // by the caller afterwards instead of after every single notification.
    private SortedSyncScope SuspendSortedSync()
    {
        _suspendSortedSync = true;
        return new SortedSyncScope(this);
    }

    private readonly struct SortedSyncScope(TraceSession session) : IDisposable
    {
        public void Dispose() => session._suspendSortedSync = false;
    }

    private static string DescribePreparationFailure(string targetText, Exception ex) => ex switch
    {
        // A detection that never completed is a genuine failure, not an
        // inconclusive probe: the trace did not start, so the message says
        // what to do rather than merely naming the timeout. A valid but
        // unhelpful probe answer never reaches here; it settles as
        // undetermined support and traces with the default payload.
        TimeoutException => $"Probe initialization timed out: {ex.Message} The trace did not start — check local firewall or loopback filtering and try again.",
        SocketException => $"Could not resolve \"{targetText}\". Check the spelling and try again.",
        _ => $"Trace preparation failed for \"{targetText}\": {ex.Message}",
    };

    /// <summary>
    /// The settings the session actually traces with: the requested snapshot,
    /// except that restricted or undetermined payload support falls back to
    /// the default payload. The record is immutable, so this replaces rather
    /// than mutates, and only the payload differs.
    /// </summary>
    private static ProbeSettings EffectivePayloadSettings(ProbeSettings requested, ProbeCapabilities capabilities) =>
        capabilities.PayloadSupport == PayloadSupport.Supported
            ? requested
            : requested with { PayloadBytes = ProbeSettings.Default.PayloadBytes };

    private static string DescribeDestination(Route route, int hopLimit) =>
        route.DestinationReached
            ? "Destination confirmed"
            : route.HopLimitObserved
                ? $"Destination not confirmed within hop limit of {hopLimit}."
                : "Destination: —";

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static HopRowViewModel CreateRow(Hop hop)
    {
        // A newly discovered hop has no held address, so first discovery can
        // never read as an identity replacement and raises no highlight.
        var row = new HopRowViewModel();
        UpdateRow(row, hop);
        return row;
    }

    private static void UpdateLiveRow(HopRowViewModel row, Hop hop)
    {
        // The hop is present in the current snapshot, so live updates resume
        // and any frozen marker clears.
        row.IsFrozen = false;

        string incomingAddress = hop.Address?.ToString() ?? string.Empty;
        // Identity-change detection compares the incoming address against the
        // one the row already holds and needs two different non-null
        // addresses, so the initial unknown-to-known transition, null
        // identities, and a repeated identical address raise no highlight.
        // The flag is transient: an ordinary update without a replacement
        // clears it again. Statistics are replaced, never merged, so after a
        // replacement the row shows only the new hop's figures.
        row.HasIdentityChange = HopRowViewModel.IsIdentityReplacement(row.Address, incomingAddress);

        UpdateRow(row, hop);
    }

    private static void UpdateRow(HopRowViewModel row, Hop hop)
    {
        // Identity stays in Host only: a silent hop leaves it empty and probe
        // error descriptions stay in Status, never in the host column. Loss
        // uses the accurate percentage, not legacy integer rounding. A hop
        // with no completed probe keeps null measurements, shown as waiting
        // rather than zeroes.
        row.Hop = hop.Index.DisplayNumber;
        row.Address = hop.Address?.ToString() ?? string.Empty;
        row.Host = hop.Label;
        row.LossPercent = hop.Stats.LossPercent;
        row.Sent = hop.Stats.Sent;
        row.Received = hop.Stats.Received;
        row.BestMs = hop.Stats.BestRttMs;
        row.AverageMs = hop.Stats.Received > 0 ? (double?)hop.Stats.AverageRttMs : null;
        row.WorstMs = hop.Stats.WorstRttMs;
        row.LastMs = hop.Stats.LastRttMs;
        // Severity is a view of the same statistics: derived in one pure
        // place and replaced wholesale on every update, so it cannot drift
        // from the values shown in the cells.
        row.Severity = HopSeverity.From(hop.Stats);
        row.Status = hop.StatusDescription;
    }
}
