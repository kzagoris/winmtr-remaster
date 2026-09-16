// src/WinMtr.Infrastructure/Tracer.cs
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;

namespace WinMtr.Infrastructure;

/// <summary>
/// Simple tracing entry point. Prepares a trace with one call, hiding the
/// resolver, probe-channel, and engine setup. Reuses the existing networking
/// implementations and tracing engine; introduces no new scheduling or
/// resource-management layer. Holds no shared trace state: each call creates
/// its own channel and resolver.
/// </summary>
public sealed class Tracer
{
    private static readonly TimeSpan DefaultInitTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<TargetExpression, CancellationToken, Task<Target>>? _resolveAsync;
    private readonly Func<IProbeChannel>? _channelFactory;
    private readonly Func<IProbeChannel, CancellationToken, Task<ProbeCapabilities>>? _initializeAsync;
    private readonly Func<INameResolver>? _nameResolverFactory;
    private readonly TimeProvider? _timeProvider;
    private readonly TimeSpan _initTimeout;
    private readonly Func<IProbeChannel, INameResolver, TimeProvider, ITraceEngine>? _engineFactory;

    public Tracer()
        : this(null, null, null, null, null, null, null)
    {
    }

    internal Tracer(
        Func<TargetExpression, CancellationToken, Task<Target>>? resolveAsync,
        Func<IProbeChannel>? channelFactory,
        Func<IProbeChannel, CancellationToken, Task<ProbeCapabilities>>? initializeAsync,
        Func<INameResolver>? nameResolverFactory,
        TimeProvider? timeProvider,
        TimeSpan? initTimeout,
        Func<IProbeChannel, INameResolver, TimeProvider, ITraceEngine>? engineFactory)
    {
        _resolveAsync = resolveAsync;
        _channelFactory = channelFactory;
        _initializeAsync = initializeAsync;
        _nameResolverFactory = nameResolverFactory;
        _timeProvider = timeProvider;
        _initTimeout = initTimeout ?? DefaultInitTimeout;
        _engineFactory = engineFactory;
    }

    public async Task<PreparedTrace> CreateTraceAsync(
        string host,
        ProbeSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (!TargetExpression.TryParse(host, out var expression, out var error))
            throw new ArgumentException(error, nameof(host));

        settings ??= ProbeSettings.Default;
        settings.Validate();

        cancellationToken.ThrowIfCancellationRequested();

        var resolve = _resolveAsync
            ?? (static (expr, ct) => new DnsTargetResolver().ResolveAsync(expr, ct));
        Target target = await resolve(expression, cancellationToken).ConfigureAwait(false);

        IProbeChannel channel = _channelFactory?.Invoke() ?? new PingProbeChannel();

        var initialize = _initializeAsync ?? DefaultInitializeAsync;
        ProbeCapabilities capabilities;
        using (var initCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            initCts.CancelAfter(_initTimeout);
            try
            {
                capabilities = await initialize(channel, initCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
                when (initCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"probe initialization timed out after {_initTimeout.TotalSeconds:g0} seconds.",
                    ex);
            }
        }

        // A cancellation landing after a successful init but before the engine
        // is handed the caller token must still surface as preparation
        // cancellation (exit 1), not as a zero-snapshot success (exit 0).
        cancellationToken.ThrowIfCancellationRequested();

        INameResolver nameResolver = _nameResolverFactory?.Invoke() ?? new CachingNameResolver();
        TimeProvider timeProvider = _timeProvider ?? TimeProvider.System;

        ITraceEngine engine = _engineFactory?.Invoke(channel, nameResolver, timeProvider)
            ?? new TraceEngine(channel, nameResolver, timeProvider);

        // Return the engine's enumerable directly: no wrapping iterator, no
        // second cancellation source. The initialization deadline is detached
        // here (initCts disposed above) so it cannot cancel later tracing.
        IAsyncEnumerable<Route> snapshots = engine.RunAsync(target, settings, cancellationToken);

        return new PreparedTrace(target, capabilities, snapshots);
    }

    private static Task<ProbeCapabilities> DefaultInitializeAsync(IProbeChannel channel, CancellationToken ct)
    {
        // Production always builds a PingProbeChannel; test doubles inject
        // their own initialize delegate instead of reaching this fallback.
        if (channel is PingProbeChannel ping)
            return ping.InitializeAsync(ct);
        throw new InvalidOperationException(
            "Tracer requires a PingProbeChannel for default initialization.");
    }
}
