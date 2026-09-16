using WinMtr.Core;
using WinMtr.Core.Tracing;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Deterministic harnesses for the trace session suite. Preparation gating and
/// the parked engine below hold the preparing and tracing states open so every
/// lifecycle transition is observable; nothing here sends network traffic.
/// </summary>
internal static class TraceSessionTestHelpers
{
    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the session condition.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A tracer factory whose resolution parks until <c>Release</c> completes,
    /// holding the preparing state open. Releasing after a stop still lets the
    /// pending wait observe the cancellation.
    /// </summary>
    internal static (Func<Tracer> Factory, TaskCompletionSource Release) GatedPreparation(IReadOnlyList<Route> snapshots)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            ResolveAsync = async (_, ct) =>
            {
                await gate.Task.WaitAsync(ct);
                return target;
            },
            Snapshots = snapshots,
        }.BuildFactory();
        return (factory, gate);
    }
}

/// <summary>
/// An engine that replays a snapshot prefix, then parks until the session
/// cancels — mirroring the engine contract, where cancellation completes the
/// enumeration rather than throwing. Records disposal so close and stop
/// draining are observable.
/// </summary>
internal sealed class GatedTraceEngine : ITraceEngine
{
    private readonly IReadOnlyList<Route> _prefix;

    internal readonly TaskCompletionSource EnteredMoveNext = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource? ReleaseDispose { get; set; }

    internal bool Disposed { get; private set; }

    internal GatedTraceEngine(IReadOnlyList<Route> prefix) => _prefix = prefix;

    /// <summary>The settings the session passed in, captured at run start.</summary>
    internal ProbeSettings? RequestedSettings { get; private set; }

    public IAsyncEnumerable<Route> RunAsync(Target target, ProbeSettings settings, CancellationToken ct)
    {
        RequestedSettings = settings;
        return new GatedStream(this, ct);
    }

    private sealed class GatedStream(GatedTraceEngine owner, CancellationToken ct) : IAsyncEnumerable<Route>
    {
        public IAsyncEnumerator<Route> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new GatedEnumerator(owner, ct);
    }

    private sealed class GatedEnumerator(GatedTraceEngine owner, CancellationToken ct) : IAsyncEnumerator<Route>
    {
        private int _index;

        public Route Current { get; private set; } = null!;

        public async ValueTask<bool> MoveNextAsync()
        {
            owner.EnteredMoveNext.TrySetResult();
            if (_index < owner._prefix.Count)
            {
                Current = owner._prefix[_index];
                _index++;
                return true;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (owner.ReleaseDispose is not null)
                await owner.ReleaseDispose.Task;
            owner.Disposed = true;
        }
    }
}
