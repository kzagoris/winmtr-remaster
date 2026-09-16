// src/WinMtr.Infrastructure/PreparedTrace.cs
using WinMtr.Core;
using WinMtr.Core.Probing;

namespace WinMtr.Infrastructure;

/// <summary>
/// The result of <see cref="Tracer.CreateTraceAsync"/>. A result container,
/// not an active session: preparation completes DNS resolution and capability
/// detection but starts no trace workers. Workers start on the first
/// enumeration advance, as they do today, and the engine owns enumerator
/// cleanup. Ordinary use is one enumeration; re-enumeration runs a new trace
/// with fresh statistics using the same prepared target, channel, resolver,
/// and caller token. It does not repeat preparation or replay old snapshots.
/// Preparing a trace without enumerating it starts no background tracing work
/// and requires no explicit disposal.
/// </summary>
public sealed class PreparedTrace
{
    internal PreparedTrace(Target target, ProbeCapabilities capabilities, IAsyncEnumerable<Route> snapshots)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Capabilities = capabilities;
        Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    public Target Target { get; }

    public ProbeCapabilities Capabilities { get; }

    public IAsyncEnumerable<Route> Snapshots { get; }
}
