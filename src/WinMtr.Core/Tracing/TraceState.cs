using System.Collections.Immutable;
using System.Net;
using WinMtr.Core.Probing;

namespace WinMtr.Core.Tracing;

/// <summary>
/// The thread-safe state machine behind a trace session. Scheduling stays in
/// <see cref="TraceEngine"/>; this type only applies probe/DNS observations and
/// produces immutable snapshots.
/// </summary>
/// <remarks>
/// Statistics belong to the current responder epoch. A responder replacement
/// starts a new epoch, so alternating responders intentionally do not produce a
/// whole-session aggregate; the retained identity is still shown to users.
/// </remarks>
public sealed class TraceState
{
    /// <summary>
    /// Fast-start cohort size: the initial probed span. An initial value,
    /// not a standing floor — it stops contributing once the first
    /// responding hop is observed.
    /// </summary>
    private const int FastStartSize = 8;

    /// <summary>
    /// Headroom past the last responding hop: headroom 1 plus a 2-TTL
    /// silent reach.
    /// </summary>
    private const int ExtentHeadroom = 3;

    /// <summary>
    /// Sweep cadence: every Nth <see cref="AdvanceSweep"/> call advances
    /// the sweep edge one TTL.
    /// </summary>
    private const int SweepIntervalCadences = 10;

    private readonly object _gate = new();
    private readonly Target _target;
    private readonly int _hopLimit;
    private readonly Hop[] _hops;
    private readonly long[] _epochs;

    private long _generation;
    private int? _destinationTtl;
    private bool _destinationReached;
    private bool _hopLimitObserved;
    private int _sweepEdge = 1;
    private int _sweepTicks;
    private bool _hasRespondingHop;
    private int _lastRespondingTtl;

    /// <param name="hopLimit">The hop limit (1..255) bounding the session. Renamed; update named-argument call sites.</param>
    public TraceState(Target target, int hopLimit)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        if (hopLimit is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(hopLimit), hopLimit, "HopLimit must be between 1 and 255.");

        _hopLimit = hopLimit;
        _hops = Enumerable.Range(0, hopLimit).Select(Hop.Empty).ToArray();
        _epochs = new long[hopLimit];
    }

    public Target Target => _target;
    public int HopLimit => _hopLimit;

    /// <summary>
    /// Compatibility forwarder for the previous property name.
    /// New code should use <see cref="HopLimit"/>.
    /// </summary>
    [Obsolete("Use HopLimit instead.")]
    public int MaxHops => _hopLimit;

    public long DiscoveryGeneration
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>The currently confirmed destination TTL, if any.</summary>
    public int? DestinationTtl
    {
        get { lock (_gate) return _destinationTtl; }
    }

    public bool DestinationReached
    {
        get { lock (_gate) return _destinationReached; }
    }

    public bool HopLimitObserved
    {
        get { lock (_gate) return _hopLimitObserved; }
    }

    /// <summary>
    /// The fast-start cohort size (<c>min(8, ceiling)</c>) bounding startup
    /// and fault barriers. Owned here so the engine never duplicates the
    /// term; the engine polls <see cref="ActiveExtent"/> for spawn
    /// decisions and <see cref="ShouldProbe"/> for parking.
    /// </summary>
    public int FastStartCohortSize => Math.Min(FastStartSize, _hopLimit);

    /// <summary>
    /// The active extent: the probed TTL span <c>1..E</c>, owned solely by
    /// the trace state. The engine polls this for spawn decisions.
    /// <c>E = min(ceiling, destinationTtl ?? max(fastStartInitial,
    /// lastResponding + 3, sweepEdge))</c>, where
    /// <c>fastStartInitial = min(8, ceiling)</c> applies until the first
    /// responding hop is observed and <c>sweepEdge</c> applies from the
    /// start — including before any response — so persistent silence walks
    /// to the ceiling. Glossary: active extent, hop limit, route length.
    /// </summary>
    public int ActiveExtent
    {
        get { lock (_gate) return ActiveExtentLocked(); }
    }

    /// <summary>
    /// Advances the sweep term. Invoked by the TTL-1 worker once per its
    /// cadence iteration (the state has no clock of its own); every 10th
    /// invocation advances the sweep edge one TTL, bounded by the ceiling,
    /// so jumps beyond the silent reach are eventually found.
    /// </summary>
    public void AdvanceSweep()
    {
        lock (_gate)
        {
            _sweepTicks++;
            if (_sweepTicks % SweepIntervalCadences == 0 && _sweepEdge < _hopLimit)
                _sweepEdge++;
        }
    }

    /// <summary>
    /// Returns whether a worker should send at this TTL. Workers above the
    /// active extent or above a confirmed destination park on the cadence
    /// (no terminate) and unpark when the extent rises or the boundary
    /// invalidates; the engine re-checks every cadence.
    /// </summary>
    public bool ShouldProbe(int ttl)
    {
        ValidateTtl(ttl);
        lock (_gate)
        {
            return ttl <= ActiveExtentLocked();
        }
    }

    /// <summary>
    /// Returns the epoch currently associated with a hop. Test and diagnostic
    /// surface: the engine itself reads epochs from
    /// <see cref="TraceTransition.Epoch"/>, never through this accessor.
    /// </summary>
    public long GetEpoch(int ttl)
    {
        ValidateTtl(ttl);
        lock (_gate) return _epochs[ttl - 1];
    }

    /// <summary>
    /// Applies one probe result. <paramref name="probeGeneration"/> is the
    /// generation captured immediately before sending. A late result still
    /// contributes to that hop's statistics, but cannot alter discovery state.
    /// </summary>
    public TraceTransition RecordProbe(int ttl, ProbeResult result, long probeGeneration)
    {
        ValidateTtl(ttl);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            int index = ttl - 1;
            Hop before = _hops[index];
            IPAddress? responder = result.Responder;
            bool identityChanged = responder is not null
                && (before.Address is null || !before.Address.Equals(responder));

            Hop next = before;
            if (identityChanged)
            {
                _epochs[index]++;
                // The initial null -> address transition keeps attempts already
                // made in the initial epoch. A -> B starts a fresh epoch.
                var stats = before.Address is null ? before.Stats : HopStatistics.Empty;
                next = before with
                {
                    Address = responder,
                    HostName = null,
                    Stats = stats
                };
            }

            next = next with
            {
                Stats = next.Stats.Record(result.Outcome, result.RttMs),
                LastOutcome = result.Outcome,
                // Each completed attempt replaces the latest probe state.
                // Reached, transit-TTL and timeout observations carry no
                // error; an unreachable or failed attempt replaces the
                // previous reason, and missing specifics clear it rather
                // than reuse a stale one.
                LastErrorReason = result.Outcome is ProbeOutcome.Unreachable or ProbeOutcome.Failed
                    ? result.ErrorReason
                    : null
            };
            _hops[index] = next;

            if (responder is not null)
            {
                // Retained identities keep this monotonic: addresses are
                // never cleared, so a hop that stops responding still
                // counts for the extent and the trimming rules.
                _hasRespondingHop = true;
                if (ttl > _lastRespondingTtl)
                    _lastRespondingTtl = ttl;
            }

            bool currentGeneration = probeGeneration == _generation;
            bool destinationSuccess = result.Outcome == ProbeOutcome.Reached
                && responder is not null
                && _target.Address.Equals(responder);

            bool invalidated = false;
            if (currentGeneration)
            {
                if (ttl == _hopLimit)
                    _hopLimitObserved = true;

                if (destinationSuccess)
                {
                    // Every new boundary starts a discovery generation. This
                    // rejects already in-flight observations from the prior
                    // boundary while preserving their per-hop samples.
                    if (identityChanged || _destinationTtl != ttl || !_destinationReached)
                    {
                        _generation++;
                        _destinationTtl = ttl;
                        _destinationReached = true;
                        _hopLimitObserved = ttl == _hopLimit;
                    }
                    else
                    {
                        _destinationReached = true;
                    }
                }
                else if (_destinationReached && _destinationTtl == ttl)
                {
                    // A single failed confirmation invalidates the active
                    // boundary. Identity remains on the row for display.
                    _generation++;
                    _destinationTtl = null;
                    _destinationReached = false;
                    _hopLimitObserved = false;
                    invalidated = true;
                }
            }

            return new TraceTransition(
                ttl,
                identityChanged,
                identityChanged ? responder : null,
                _epochs[index],
                probeGeneration,
                currentGeneration,
                destinationSuccess,
                invalidated,
                _destinationTtl,
                _generation);
        }
    }

    /// <summary>
    /// Applies a result to the current generation. Single-lock callers should
    /// prefer the explicit-generation overload: this convenience reads the
    /// generation under its own lock, so it is for single-threaded and test
    /// use, not for concurrent workers racing a boundary change.
    /// </summary>
    public TraceTransition RecordProbe(int ttl, ProbeResult result) =>
        RecordProbe(ttl, result, DiscoveryGeneration);

    /// <summary>
    /// Applies an asynchronous DNS result only when address and epoch still
    /// match the request that produced it. A failed lookup (null) leaves the
    /// numeric label intact.
    /// </summary>
    public bool ApplyDnsResult(int ttl, IPAddress address, long epoch, string? hostName)
    {
        ValidateTtl(ttl);
        ArgumentNullException.ThrowIfNull(address);

        lock (_gate)
        {
            int index = ttl - 1;
            var currentAddress = _hops[index].Address;
            if (_epochs[index] != epoch
                || currentAddress is null
                || !currentAddress.Equals(address))
                return false;

            if (hostName is null)
                return false;

            _hops[index] = _hops[index] with { HostName = hostName };
            return true;
        }
    }

    /// <summary>
    /// Creates an immutable route snapshot. Retained target identities are not
    /// used to trim the route after destination evidence has gone stale.
    /// Trailing silence is trimmed to the last responding hop so reports grow
    /// organically instead of padding to the hop limit.
    /// </summary>
    /// <param name="takenAt">The instant the snapshot is taken.</param>
    /// <param name="startedAt">
    /// The trace session start, for the report duration. Defaults to
    /// <paramref name="takenAt"/> so callers that do not track a session
    /// start still get a valid route.
    /// </param>
    public Route Snapshot(DateTimeOffset takenAt, DateTimeOffset? startedAt = null)
    {
        lock (_gate)
        {
            var all = ImmutableArray.Create(_hops);
            int length = _destinationReached && _destinationTtl is int destinationTtl
                ? Math.Min(destinationTtl, all.Length)
                : PathLength.Determine(all, _target.Address, destinationReached: false);

            return new Route(
                _target,
                all[..length],
                takenAt,
                destinationReached: _destinationReached,
                hopLimitObserved: _hopLimitObserved,
                startedAt: startedAt ?? takenAt);
        }
    }

    private void ValidateTtl(int ttl)
    {
        if (ttl is < 1 || ttl > _hopLimit)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL is outside the configured range.");
    }

    /// <summary>
    /// Extent rule, verbatim with the spec:
    /// <c>E = min(ceiling, destinationTtl ?? max(fastStartInitial,
    /// lastResponding + 3, sweepEdge))</c>. Caller holds <see cref="_gate"/>.
    /// </summary>
    private int ActiveExtentLocked()
    {
        if (_destinationReached && _destinationTtl is int destinationTtl)
            return Math.Min(_hopLimit, destinationTtl);

        int extent = _sweepEdge;
        if (!_hasRespondingHop)
            extent = Math.Max(extent, Math.Min(FastStartSize, _hopLimit));
        else
            extent = Math.Max(extent, _lastRespondingTtl + ExtentHeadroom);
        return Math.Min(_hopLimit, extent);
    }
}

/// <summary>Details emitted by <see cref="TraceState.RecordProbe"/>.</summary>
public sealed record TraceTransition(
    int Ttl,
    bool IdentityChanged,
    IPAddress? Address,
    long Epoch,
    long ProbeGeneration,
    bool WasCurrentGeneration,
    bool DestinationSuccess,
    bool BoundaryInvalidated,
    int? DestinationTtl,
    long CurrentGeneration)
{
    public bool NeedsNameResolution => IdentityChanged && Address is not null;
}
