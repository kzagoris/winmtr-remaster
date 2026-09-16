// tests/WinMtr.TestSupport/ScriptedTraceEngine.cs
using WinMtr.Core;
using WinMtr.Core.Tracing;

namespace WinMtr.TestSupport;

/// <summary>
/// Deterministic offline <see cref="ITraceEngine"/> substitute that replays a
/// controlled sequence of route snapshots. The single shared scripted
/// route-snapshot source for the console and front-end suites: no live network
/// traffic, no timers, no background workers.
/// </summary>
/// <remarks>
/// Publication here is a replay, not the production engine's lossy
/// publication: every supplied route snapshot is yielded in order, so a slow
/// consumer observes each state rather than skipping interim ones. That is
/// deliberate for determinism; tests asserting lossy coalescing belong with
/// the real engine.
/// Glossary: route snapshot, trace session, retained hop rows, route length.
/// </remarks>
public sealed class ScriptedTraceEngine : ITraceEngine
{
    private readonly IReadOnlyList<Route> _snapshots;
    private readonly Exception? _fault;
    private readonly int? _faultAfterSnapshots;

    /// <param name="snapshots">
    /// The route snapshots to yield in order. Each carries its own timestamp
    /// (<see cref="Route.TakenAt"/>), so elapsed-time behaviour is controlled
    /// by the sequence, not by a clock.
    /// </param>
    /// <param name="fault">
    /// Optional worker fault to raise as the original exception instance
    /// during enumeration, mirroring how the engine propagates worker faults.
    /// Null means the enumeration completes cleanly.
    /// </param>
    /// <param name="faultAfterSnapshots">
    /// How many route snapshots to yield before raising <paramref name="fault"/>.
    /// Null means no fault when <paramref name="fault"/> is null, and a fault
    /// after the whole sequence when <paramref name="fault"/> is supplied.
    /// Zero raises before the first route snapshot. A position beyond the end
    /// of the sequence never fires, since enumeration completes first.
    /// </param>
    public ScriptedTraceEngine(
        IReadOnlyList<Route> snapshots,
        Exception? fault = null,
        int? faultAfterSnapshots = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        if (faultAfterSnapshots is < 0)
            throw new ArgumentOutOfRangeException(
                nameof(faultAfterSnapshots),
                faultAfterSnapshots,
                "Fault position must be zero or greater, or null for no fault.");
        _fault = fault;
        _faultAfterSnapshots = fault is null ? null : faultAfterSnapshots ?? snapshots.Count;
    }

    /// <summary>The target the engine was asked to trace, once run.</summary>
    public Target? SeenTarget { get; private set; }

    /// <summary>The settings the trace session began with, once run.</summary>
    public ProbeSettings? SeenSettings { get; private set; }

    /// <summary>The caller token handed to <see cref="RunAsync"/>, once run.</summary>
    public CancellationToken SeenCallerToken { get; private set; }

    /// <summary>Whether <see cref="RunAsync"/> has been called.</summary>
    public bool WasRun => SeenTarget is not null;

    public IAsyncEnumerable<Route> RunAsync(
        Target target,
        ProbeSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        SeenTarget = target;
        SeenSettings = settings;
        SeenCallerToken = ct;

        return new ScriptedStream(_snapshots, _fault, _faultAfterSnapshots, ct);
    }

    private sealed class ScriptedStream : IAsyncEnumerable<Route>
    {
        private readonly IReadOnlyList<Route> _snapshots;
        private readonly Exception? _fault;
        private readonly int? _faultAfterSnapshots;
        private readonly CancellationToken _callerToken;

        public ScriptedStream(
            IReadOnlyList<Route> snapshots,
            Exception? fault,
            int? faultAfterSnapshots,
            CancellationToken callerToken)
        {
            _snapshots = snapshots;
            _fault = fault;
            _faultAfterSnapshots = faultAfterSnapshots;
            _callerToken = callerToken;
        }

        public IAsyncEnumerator<Route> GetAsyncEnumerator(CancellationToken enumeratorToken = default) =>
            new ScriptedEnumerator(_snapshots, _fault, _faultAfterSnapshots, _callerToken, enumeratorToken);
    }

    private sealed class ScriptedEnumerator : IAsyncEnumerator<Route>
    {
        private readonly IReadOnlyList<Route> _snapshots;
        private readonly Exception? _fault;
        private readonly int? _faultAfterSnapshots;
        private readonly CancellationToken _callerToken;
        private readonly CancellationToken _enumeratorToken;
        private int _index;

        public ScriptedEnumerator(
            IReadOnlyList<Route> snapshots,
            Exception? fault,
            int? faultAfterSnapshots,
            CancellationToken callerToken,
            CancellationToken enumeratorToken)
        {
            _snapshots = snapshots;
            _fault = fault;
            _faultAfterSnapshots = faultAfterSnapshots;
            _callerToken = callerToken;
            _enumeratorToken = enumeratorToken;
        }

        public Route Current { get; private set; } = null!;

        public ValueTask<bool> MoveNextAsync()
        {
            // Cancellation is normal, observable shutdown: the enumeration
            // completes (returns false) rather than throwing
            // OperationCanceledException, mirroring the engine contract. A
            // pre-cancelled token therefore yields zero route snapshots.
            if (_callerToken.IsCancellationRequested || _enumeratorToken.IsCancellationRequested)
                return ValueTask.FromResult(false);

            // A fault raised while tracing propagates as the original
            // exception instance, mirroring worker-fault propagation. It fires
            // once the scripted prefix has been yielded.
            if (_fault is not null
                && _faultAfterSnapshots is int after
                && _index >= after)
                throw _fault;

            if (_index >= _snapshots.Count)
                return ValueTask.FromResult(false);

            Current = _snapshots[_index];
            _index++;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
