// tests/WinMtr.TestSupport/TracerScript.cs
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;
using WinMtr.Infrastructure;

namespace WinMtr.TestSupport;

/// <summary>
/// Scripts one offline <see cref="Tracer"/> through the same seam the console
/// command uses: a tracer factory whose tracer accepts substitutes for target
/// resolution, probe channel, capability detection, name resolution, clock,
/// and engine. The front-end trace session reuses exactly this seam, so an
/// Avalonia test project consumes scripted route snapshots through
/// <see cref="BuildFactory"/> with no further production seam and no live
/// network trace.
/// </summary>
/// <remarks>
/// Preparation outcomes (resolution success or failure, capability detection
/// success or failure, caller cancellation) are decided before enumeration;
/// enumeration then replays the scripted route snapshots and optionally raises
/// a fault at a chosen point. Glossary: route snapshot, trace session.
/// </remarks>
public sealed class TracerScript
{
    /// <summary>
    /// Custom target resolution. When null, <see cref="ResolveFailure"/> wins
    /// next, then <see cref="Target"/>, then a documentation-range default.
    /// Awaiting this delegate is where cancellation during preparation lands.
    /// </summary>
    public Func<TargetExpression, CancellationToken, Task<Target>>? ResolveAsync { get; init; }

    /// <summary>Resolution success value, used when <see cref="ResolveAsync"/> is null.</summary>
    public Target? Target { get; init; }

    /// <summary>Resolution failure, used when <see cref="ResolveAsync"/> is null.</summary>
    public Exception? ResolveFailure { get; init; }

    /// <summary>
    /// Custom channel factory. When null, <see cref="Channel"/> wins next,
    /// then a silent scripted channel under <see cref="Capabilities"/>.
    /// A throwing factory asserts preparation never reaches channel creation.
    /// </summary>
    public Func<IProbeChannel>? ChannelFactory { get; init; }

    /// <summary>Channel instance, used when <see cref="ChannelFactory"/> is null.</summary>
    public IProbeChannel? Channel { get; init; }

    /// <summary>
    /// Custom capability detection. When null, <see cref="InitializeFailure"/>
    /// wins next, then <see cref="Capabilities"/>. An endlessly pending
    /// delegate plus <see cref="InitTimeout"/> scripts a detection timeout.
    /// </summary>
    public Func<IProbeChannel, CancellationToken, Task<ProbeCapabilities>>? InitializeAsync { get; init; }

    /// <summary>Capability success value, used when <see cref="InitializeAsync"/> is null.</summary>
    public ProbeCapabilities? Capabilities { get; init; }

    /// <summary>Capability-detection failure, used when <see cref="InitializeAsync"/> is null.</summary>
    public Exception? InitializeFailure { get; init; }

    /// <summary>
    /// Custom resolver factory. When null, <see cref="Resolver"/> wins next,
    /// then a nameless scripted resolver. A throwing factory asserts
    /// preparation never reaches resolver creation.
    /// </summary>
    public Func<INameResolver>? ResolverFactory { get; init; }

    /// <summary>Resolver instance, used when <see cref="ResolverFactory"/> is null.</summary>
    public INameResolver? Resolver { get; init; }

    /// <summary>Clock handed to the engine factory. Null uses the default.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>Initialization deadline. Null uses the tracer default.</summary>
    public TimeSpan? InitTimeout { get; init; }

    /// <summary>
    /// Custom engine factory. When null, <see cref="Engine"/> wins next, then
    /// a scripted engine replaying <see cref="Snapshots"/> with
    /// <see cref="EnumerationFault"/> after <see cref="FaultAfterSnapshots"/>.
    /// </summary>
    public Func<IProbeChannel, INameResolver, TimeProvider, ITraceEngine>? EngineFactory { get; init; }

    /// <summary>Engine instance, used when <see cref="EngineFactory"/> is null.</summary>
    public ITraceEngine? Engine { get; init; }

    /// <summary>Route snapshots the default scripted engine replays in order.</summary>
    public IReadOnlyList<Route>? Snapshots { get; init; }

    /// <summary>
    /// Worker fault the default scripted engine raises as the original
    /// exception instance during enumeration. Null means clean completion.
    /// </summary>
    public Exception? EnumerationFault { get; init; }

    /// <summary>
    /// How many route snapshots precede <see cref="EnumerationFault"/>.
    /// Null means no fault when <see cref="EnumerationFault"/> is null, and a
    /// fault after the whole sequence when it is supplied.
    /// </summary>
    public int? FaultAfterSnapshots { get; init; }

    /// <summary>Builds the scripted tracer.</summary>
    public Tracer BuildTracer()
    {
        Target target = Target ?? RouteScript.TestTarget();
        ProbeCapabilities capabilities = Capabilities ?? new ProbeCapabilities(PayloadSupport.Supported);

        Func<TargetExpression, CancellationToken, Task<Target>> resolve =
            ResolveAsync
            ?? (ResolveFailure is not null
                ? (_, _) => Task.FromException<Target>(ResolveFailure)
                : (_, _) => Task.FromResult(target));

        Func<IProbeChannel> channelFactory =
            ChannelFactory
            ?? (Channel is not null
                ? () => Channel
                : () => new ScriptedProbeChannel(capabilities));

        Func<IProbeChannel, CancellationToken, Task<ProbeCapabilities>> initialize =
            InitializeAsync
            ?? (InitializeFailure is not null
                ? (_, _) => Task.FromException<ProbeCapabilities>(InitializeFailure)
                : (_, _) => Task.FromResult(capabilities));

        Func<INameResolver> resolverFactory =
            ResolverFactory
            ?? (Resolver is not null
                ? () => Resolver
                : () => new ScriptedNameResolver());

        Func<IProbeChannel, INameResolver, TimeProvider, ITraceEngine> engineFactory =
            EngineFactory
            ?? (Engine is not null
                ? (_, _, _) => Engine
                : (_, _, _) => new ScriptedTraceEngine(
                    Snapshots ?? [],
                    EnumerationFault,
                    FaultAfterSnapshots));

        return new Tracer(resolve, channelFactory, initialize, resolverFactory, Clock, InitTimeout, engineFactory);
    }

    /// <summary>
    /// Builds the tracer factory the console command and the front-end trace
    /// session consume: each invocation returns a freshly scripted tracer.
    /// </summary>
    public Func<Tracer> BuildFactory() => BuildTracer;
}
