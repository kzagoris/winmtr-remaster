using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using WinMtr.Core.Probing;

namespace WinMtr.Core.Tracing;

/// <summary>
/// Lazily spawned fixed-TTL workers, DNS dispatch and immutable snapshot
/// publication for one target. The probe and resolver seams are deliberately
/// injected so the scheduling contract can be tested with controlled time.
/// </summary>
public sealed class TraceEngine : ITraceEngine
{
    private readonly IProbeChannel _probeChannel;
    private readonly INameResolver _nameResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Action<TraceTransition>? _probeObserver;

    /// <param name="probeObserver">
    /// Test/diagnostic hook invoked synchronously on a TTL worker after its
    /// probe is recorded and its cadence wait is registered. It runs
    /// concurrently from every worker, so it must be thread-safe and
    /// non-blocking; a throwing observer faults the session.
    /// </param>
    public TraceEngine(
        IProbeChannel probeChannel,
        INameResolver nameResolver,
        TimeProvider? timeProvider = null,
        Action<TraceTransition>? probeObserver = null)
    {
        _probeChannel = probeChannel ?? throw new ArgumentNullException(nameof(probeChannel));
        _nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _probeObserver = probeObserver;
    }

    public IAsyncEnumerable<Route> RunAsync(
        Target target,
        ProbeSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new TraceSession(_probeChannel, _nameResolver, _timeProvider, target, settings, ct, _probeObserver);
    }

    private sealed class TraceSession : IAsyncEnumerable<Route>
    {
        internal readonly IProbeChannel _probeChannel;
        internal readonly INameResolver _nameResolver;
        internal readonly TimeProvider _timeProvider;
        internal readonly Target _target;
        internal readonly ProbeSettings _settings;
        internal readonly CancellationToken _callerToken;
        internal readonly Action<TraceTransition>? _probeObserver;

        public TraceSession(
            IProbeChannel probeChannel,
            INameResolver nameResolver,
            TimeProvider timeProvider,
            Target target,
            ProbeSettings settings,
            CancellationToken callerToken,
            Action<TraceTransition>? probeObserver = null)
        {
            _probeChannel = probeChannel;
            _nameResolver = nameResolver;
            _timeProvider = timeProvider;
            _target = target;
            _settings = settings;
            _callerToken = callerToken;
            _probeObserver = probeObserver;
        }

        public IAsyncEnumerator<Route> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new TraceEnumerator(this, cancellationToken);
        }

        private sealed class TraceEnumerator : IAsyncEnumerator<Route>
        {
            private readonly TraceSession _owner;
            private readonly CancellationToken _enumeratorToken;
            private TraceRuntime? _runtime;
            private bool _started;
            private bool _disposed;

            public TraceEnumerator(TraceSession owner, CancellationToken enumeratorToken)
            {
                _owner = owner;
                _enumeratorToken = enumeratorToken;
            }

            public Route Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_disposed)
                    return false;

                if (!_started)
                {
                    _started = true;
                    _runtime = new TraceRuntime(_owner, _enumeratorToken);
                    _runtime.Start();
                }

                try
                {
                    Current = await _runtime!.ReadAsync().ConfigureAwait(false);
                    return true;
                }
                catch (ChannelClosedException)
                {
                    // A worker fault that happens to be a ChannelClosedException
                    // must not read as clean shutdown: ReadAsync rethrows the
                    // original instance, which is indistinguishable by type here.
                    if (_runtime!.HasFault)
                        ExceptionDispatchInfo.Capture(_runtime.GetFault()).Throw();
                    return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                    return;

                _disposed = true;
                if (_runtime is not null)
                    await _runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class TraceRuntime : IAsyncDisposable
    {
        private readonly TraceSession _owner;
        private readonly CancellationToken _enumeratorToken;
        // Bounded to the latest snapshot: a stalled consumer (modal dialog,
        // blocked console write) drops stale backlog instead of accumulating
        // 4 Route objects/second forever and replaying history on resume.
        private readonly Channel<Route> _snapshots = Channel.CreateBounded<Route>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private readonly TaskCompletionSource<Exception> _fault =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _workersReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _dnsGate = new();
        private readonly List<Task> _dnsTasks = [];

        private CancellationTokenSource? _sessionCts;
        private CancellationTokenRegistration _callerRegistration;
        private CancellationTokenRegistration _enumeratorRegistration;
        private TraceState? _state;
        // Captured once at Start so every snapshot can report the session
        // duration (TakenAt - StartedAt).
        private DateTimeOffset _startedAt;
        // Live worker set under _workersGate: lazily spawned fixed-TTL
        // workers, always covering the contiguous span 1..N. The
        // coordinator and the drain snapshot this set under the same lock
        // that publishes new workers and re-check after each join, so a
        // worker spawned mid-shutdown is still drained before completion.
        private readonly object _workersGate = new();
        private readonly List<Task> _liveWorkers = [];
        private int _fastStartCount;
        private Task? _publisher;
        private Task? _coordinator;
        private int _disposed;
        private int _normalCancellation;
        private int _workersReadyCount;
        private int _workersEnteredInitialProbe;

        public TraceRuntime(TraceSession owner, CancellationToken enumeratorToken)
        {
            _owner = owner;
            _enumeratorToken = enumeratorToken;
        }

        public void Start()
        {
            if (_owner._callerToken.IsCancellationRequested || _enumeratorToken.IsCancellationRequested)
            {
                _snapshots.Writer.TryComplete();
                return;
            }

            _startedAt = _owner._timeProvider.GetUtcNow();
            _state = new TraceState(_owner._target, _owner._settings.HopLimit);
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
                _owner._callerToken, _enumeratorToken);
            // Registered on the source tokens, not the linked session token:
            // an internal fault-driven Cancel must not present as caller
            // cancellation (only the graceful branch may publish a final
            // snapshot on _normalCancellation).
            _callerRegistration = _owner._callerToken.Register(static state =>
            {
                ((TraceRuntime)state!).RequestNormalCancellation();
            }, this);
            _enumeratorRegistration = _enumeratorToken.Register(static state =>
            {
                ((TraceRuntime)state!).RequestNormalCancellation();
            }, this);

            // Start the fast-start cohort before starting the coordinator.
            // Worker loops yield at the cadence floor, so synchronously
            // completing fakes do not monopolise startup or prevent
            // publication from starting. Further TTLs spawn lazily as the
            // polled active extent grows. Startup and fault barriers cover
            // this cohort only; later workers probe immediately.
            _fastStartCount = _state.FastStartCohortSize;
            lock (_workersGate)
            {
                for (int ttl = 1; ttl <= _fastStartCount; ttl++)
                    SpawnWorkerLocked(ttl, isFastStart: true);
            }

            _publisher = PublishLoopAsync();
            _coordinator = CoordinateAsync();
        }

        public async ValueTask<Route> ReadAsync()
        {
            try
            {
                return await _snapshots.Reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        /// <summary>
        /// True once a worker or publisher fault has been recorded. Consulted
        /// by the enumerator so a fault whose type is ChannelClosedException is
        /// rethrown rather than mistaken for clean shutdown.
        /// </summary>
        internal bool HasFault => _fault.Task.IsCompletedSuccessfully;

        internal Exception GetFault() => _fault.Task.Result;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _sessionCts?.Cancel();
            if (_coordinator is not null)
            {
                try
                {
                    await _coordinator.ConfigureAwait(false);
                }
                catch
                {
                    // Disposal is responsible for draining owned work. Any
                    // worker fault remains observable to the active reader;
                    // disposal itself must not mask it.
                }
            }

            _callerRegistration.Dispose();
            _enumeratorRegistration.Dispose();
            _sessionCts?.Dispose();
            _snapshots.Writer.TryComplete();
        }

        private async Task WorkerLoopAsync(int ttl, bool isFastStart)
        {
            CancellationToken token = _sessionCts!.Token;
            try
            {
                // Give every fast-start worker a chance to enter before a
                // synchronously faulting channel can cancel the session.
                // Lazily spawned workers skip the cohort barrier and probe
                // immediately without re-arming it.
                await Task.Yield();
                if (isFastStart)
                {
                    if (Interlocked.Increment(ref _workersReadyCount) == _fastStartCount)
                        _workersReady.TrySetResult();
                    await _workersReady.Task.WaitAsync(token).ConfigureAwait(false);
                }

                bool firstProbe = true;
                while (!token.IsCancellationRequested)
                {
                    if (firstProbe)
                    {
                        firstProbe = false;
                        if (isFastStart)
                        {
                            Interlocked.Increment(ref _workersEnteredInitialProbe);
                            MaybeCancelAfterInitialProbes();
                        }
                    }

                    // The TTL-1 worker drives the state's sweep term once per
                    // cadence iteration (every 10th tick advances the sweep
                    // edge one TTL). The tick sits above the park check so
                    // the sweep clock never freezes even if TTL 1 parks.
                    if (ttl == 1)
                        _state!.AdvanceSweep();

                    if (!_state!.ShouldProbe(ttl))
                    {
                        await DelayAsync(_owner._settings.Cadence, token).ConfigureAwait(false);
                        continue;
                    }

                    long started = _owner._timeProvider.GetTimestamp();
                    long generation = _state.DiscoveryGeneration;
                    ProbeResult result = await _owner._probeChannel.SendAsync(
                        _owner._target.Address,
                        ttl,
                        _owner._settings.PayloadBytes,
                        _owner._settings.ReplyTimeout,
                        token).ConfigureAwait(false);

                    TraceTransition transition = _state.RecordProbe(ttl, result, generation);
                    if (_owner._settings.ResolveNames && transition.NeedsNameResolution)
                        DispatchNameResolution(ttl, transition.Address!, transition.Epoch, token);

                    TimeSpan elapsed = _owner._timeProvider.GetElapsedTime(started);
                    TimeSpan remainder = _owner._settings.Cadence - elapsed;
                    // Create the delay task first so the test observer fires only
                    // after both RecordProbe and the cadence timer exist. Tests
                    // awaiting the observer can then Advance(fakeTime) without
                    // racing timer registration.
                    Task cadenceWait = remainder > TimeSpan.Zero
                        ? DelayAsync(remainder, token)
                        : YieldOnceAsync();
                    try
                    {
                        _owner._probeObserver?.Invoke(transition);
                    }
                    catch (Exception observerEx)
                    {
                        // A throwing test observer is a loud session fault, not
                        // a silent pass: surfacing it here beats swallowing an
                        // assertion thrown inside the hook.
                        SignalFault(observerEx);
                    }

                    // Every worker then grows the live set to the polled
                    // active extent. Spawn decisions only — the worker set
                    // never shrinks mid-session.
                    EnsureExtentWorkers();

                    await cadenceWait.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancellation is normal shutdown only when the session token
                // was actually canceled. An unrelated OCE is a worker fault.
            }
            catch (Exception ex)
            {
                SignalFault(ex);
            }
        }

        private async Task PublishLoopAsync()
        {
            CancellationToken token = _sessionCts!.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await DelayAsync(_owner._settings.SnapshotInterval, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                        break;

                    _snapshots.Writer.TryWrite(_state!.Snapshot(_owner._timeProvider.GetUtcNow(), _startedAt));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                SignalFault(ex);
            }
        }

        private async Task CoordinateAsync()
        {
            Exception? failure = null;
            try
            {
                // Await the live worker set, re-checking for workers spawned
                // mid-session after each join returns so growth is never
                // left unjoined and shutdown never strands a new worker.
                while (true)
                {
                    Task[] live = LiveWorkersSnapshot();
                    Task allWorkers = Task.WhenAll(live);
                    Task completed = await Task.WhenAny(allWorkers, _fault.Task).ConfigureAwait(false);
                    if (completed == _fault.Task || _fault.Task.IsCompleted)
                    {
                        failure = await _fault.Task.ConfigureAwait(false);
                        _sessionCts!.Cancel();
                        await DrainOwnedWorkAsync().ConfigureAwait(false);
                        _snapshots.Writer.TryComplete(failure);
                        return;
                    }

                    await allWorkers.ConfigureAwait(false);
                    lock (_workersGate)
                    {
                        if (_liveWorkers.Count == live.Length)
                            break;
                    }
                }

                _sessionCts!.Cancel();
                if (_publisher is not null)
                    await _publisher.ConfigureAwait(false);
                // A publisher fault can land after the initial WhenAny check
                // (workers done, publisher still awaiting its snapshot delay),
                // so re-check before reporting graceful completion.
                if (_fault.Task.IsCompletedSuccessfully)
                {
                    failure = await _fault.Task.ConfigureAwait(false);
                    await DrainDnsAsync().ConfigureAwait(false);
                    _snapshots.Writer.TryComplete(failure);
                    return;
                }

                await DrainDnsAsync().ConfigureAwait(false);

                // Caller cancellation is a normal, observable shutdown. A
                // disposed enumerator intentionally receives no extra yield.
                if (Volatile.Read(ref _normalCancellation) != 0
                    && Volatile.Read(ref _disposed) == 0)
                {
                    _snapshots.Writer.TryWrite(_state!.Snapshot(_owner._timeProvider.GetUtcNow(), _startedAt));
                }

                _snapshots.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                failure ??= ex;
                _sessionCts?.Cancel();
                await DrainOwnedWorkAsync().ConfigureAwait(false);
                _snapshots.Writer.TryComplete(failure);
            }
        }

        private async Task DrainOwnedWorkAsync()
        {
            await DrainWorkersAsync().ConfigureAwait(false);

            if (_publisher is not null)
            {
                try { await _publisher.ConfigureAwait(false); }
                catch { }
            }

            await DrainDnsAsync().ConfigureAwait(false);
        }

        private async Task DrainDnsAsync()
        {
            // All workers have finished, so no more lookups can be dispatched.
            // Self-pruning can only remove tasks from this captured set.
            Task[] tasks;
            lock (_dnsGate)
                tasks = _dnsTasks.ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts the worker for <paramref name="ttl"/> and publishes it to
        /// the live set. Caller holds <see cref="_workersGate"/>. The live
        /// set always covers the contiguous span 1..N, so every spawn path
        /// extends it by exactly one TTL. The worker prefix runs only to
        /// its first yield, so no blocking happens under the lock.
        /// </summary>
        private void SpawnWorkerLocked(int ttl, bool isFastStart)
        {
            _liveWorkers.Add(WorkerLoopAsync(ttl, isFastStart));
        }

        /// <summary>
        /// Polls the state's active extent and spawns workers for any TTLs
        /// not yet covered. The live set only grows mid-session; parking
        /// above a confirmed destination stays in
        /// <see cref="TraceState.ShouldProbe"/>, which the worker loop
        /// re-checks every cadence.
        /// </summary>
        private void EnsureExtentWorkers()
        {
            if (_sessionCts?.IsCancellationRequested == true)
                return;

            int extent = _state!.ActiveExtent;
            lock (_workersGate)
            {
                // The live set always covers the contiguous span 1..N.
                for (int ttl = _liveWorkers.Count + 1; ttl <= extent; ttl++)
                    SpawnWorkerLocked(ttl, isFastStart: false);
            }
        }

        private Task[] LiveWorkersSnapshot()
        {
            lock (_workersGate)
                return _liveWorkers.ToArray();
        }

        /// <summary>
        /// Joins the live worker set, re-checking under the publishing lock
        /// after each join so a worker spawned mid-shutdown is still
        /// drained. The original worker exception stays in
        /// <see cref="_fault"/>.
        /// </summary>
        private async Task DrainWorkersAsync()
        {
            while (true)
            {
                Task[] live = LiveWorkersSnapshot();
                try
                {
                    await Task.WhenAll(live).ConfigureAwait(false);
                }
                catch
                {
                }

                lock (_workersGate)
                {
                    if (_liveWorkers.Count == live.Length)
                        return;
                }
            }
        }

        private void DispatchNameResolution(int ttl, IPAddress address, long epoch, CancellationToken token)
        {
            // One dispatch per identity change, not per probe, so in-flight
            // lookups scale with responder churn times resolver latency, not
            // with probe count. Stale completions are rejected by epoch in
            // ApplyDnsResult rather than coalesced here, so a current-epoch
            // lookup is never skipped behind an older outstanding one.
            Task task = ResolveNameAsync(ttl, address, epoch, token);
            lock (_dnsGate)
                _dnsTasks.Add(task);
            // Completed lookups remove themselves so they do not accumulate
            // during long sessions.
            // The continuation itself is not retained.
            _ = task.ContinueWith(static (antecedent, state) =>
            {
                var self = (TraceRuntime)state!;
                lock (self._dnsGate)
                    self._dnsTasks.Remove(antecedent);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task ResolveNameAsync(int ttl, IPAddress address, long epoch, CancellationToken token)
        {
            try
            {
                string? name = await _owner._nameResolver.ResolveAsync(address, token).ConfigureAwait(false);
                _state!.ApplyDnsResult(ttl, address, epoch, name);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch
            {
                // DNS is advisory. Numeric identity remains available when a
                // resolver fails or returns no PTR record.
            }
        }

        private void SignalFault(Exception ex)
        {
            if (ex is OperationCanceledException && _sessionCts?.IsCancellationRequested == true)
                return;

            _fault.TrySetResult(ex);
            MaybeCancelAfterInitialProbes();
        }

        private void MaybeCancelAfterInitialProbes()
        {
            // A fault can be raised synchronously by the first probe. Delay
            // cancellation until every fast-start worker has entered its
            // initial probe section so sibling traffic cannot be skipped
            // at startup. Lazily spawned workers neither stall this
            // cohort barrier nor escape the drain.
            if (_fault.Task.IsCompleted && Volatile.Read(ref _workersEnteredInitialProbe) >= _fastStartCount)
                _sessionCts?.Cancel();
        }

        private void RequestNormalCancellation()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            Interlocked.Exchange(ref _normalCancellation, 1);
            _sessionCts?.Cancel();
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken token) =>
            Task.Delay(delay, _owner._timeProvider, token);

        private static async Task YieldOnceAsync()
        {
            await Task.Yield();
        }
    }
}
