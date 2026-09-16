namespace WinMtr.Core.Tracing;

/// <summary>Runs a continuously published, fixed-TTL route trace.</summary>
public interface ITraceEngine
{
    /// <summary>
    /// Publishes immutable snapshots until cancellation or disposal.
    /// Cancellation (caller token, enumerator token, or disposal) is normal,
    /// observable shutdown: the enumeration completes (returns false) rather
    /// than throwing <see cref="OperationCanceledException"/>. A pre-cancelled
    /// token yields zero snapshots and completes immediately. Worker faults
    /// propagate as the original exception instance via the channel.
    /// An undisposed abandoned enumerator has no cleanup guarantee.
    /// Publication is lossy: only the latest snapshot is retained, so a slow
    /// consumer skips interim states rather than replaying a backlog.
    /// Probe and resolver implementations must honor cancellation promptly;
    /// shutdown drains owned work, and a non-cancellable seam can stall it.
    /// </summary>
    IAsyncEnumerable<Route> RunAsync(
        Target target,
        ProbeSettings settings,
        CancellationToken ct = default);
}
