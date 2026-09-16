using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;

namespace WinMtr.Core.Tests;

/// <summary>
/// Shared timing helpers for engine tests.
/// </summary>
internal static class EngineTestTiming
{
    /// <summary>
    /// Wall-clock budget for a wait that is expected to succeed. These tests
    /// pair a fake clock with real worker threads, so progress needs the
    /// threadpool to actually schedule them: a budget tuned to an idle machine
    /// fails when the solution runs several test processes at once, reporting
    /// CPU contention as an engine defect. Generous on purpose - a correct
    /// session satisfies its condition immediately and never spends this, and
    /// only a genuinely stuck one waits it out.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Guards a fake-clock advance loop with one overall wall-clock deadline.
    /// Counting consecutive stalls cannot do this job: a stall means only that
    /// one chunk saw no progress, which under load says nothing about whether
    /// the session is stuck, so a run of them abandons a perfectly healthy
    /// walk. The loop's own iteration cap still bounds how far the fake clock
    /// travels; this bounds how long the suite will wait for the real one.
    /// </summary>
    internal static Func<bool> WithinBudget(TimeSpan? budget = null)
    {
        var elapsed = Stopwatch.StartNew();
        var limit = budget ?? Budget;
        return () => elapsed.Elapsed < limit;
    }

    internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        if (predicate())
            return;

        using var budget = new CancellationTokenSource(timeout ?? Budget);
        while (!predicate())
            await Task.Delay(TimeSpan.FromMilliseconds(1), budget.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles one fake-clock chunk without failing on a stall: an advance
    /// that lands in a worker's inter-probe gap (or a loaded threadpool)
    /// legitimately produces no progress for that chunk, so a timeout only
    /// reports the stall and the caller decides whether to keep advancing.
    /// Returns the condition either way, so a win that lands just after the
    /// budget still counts.
    /// </summary>
    internal static async Task<bool> TrySettleAsync(Func<bool> condition, TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(condition, timeout).ConfigureAwait(false);
            return true;
        }
        catch (TaskCanceledException)
        {
            return condition();
        }
    }

    internal static ProbeSettings EngineSettings(int hopLimit, TimeSpan cadence) => new(
        cadence,
        PayloadBytes: 64,
        hopLimit,
        ReplyTimeout: TimeSpan.FromSeconds(1),
        ResolveNames: true,
        SnapshotInterval: TimeSpan.FromMilliseconds(10));
}

internal sealed class TestResolver : INameResolver
{
    public Task<string?> ResolveAsync(IPAddress address, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

internal sealed class ProbeTap
{
    private readonly ConcurrentQueue<TraceTransition> _transitions = new();
    private int _count;
    private long _lastGeneration;

    public int Count => Volatile.Read(ref _count);
    public long LastGeneration => Volatile.Read(ref _lastGeneration);
    public Action<TraceTransition> Handler => OnNext;

    private void OnNext(TraceTransition transition)
    {
        _transitions.Enqueue(transition);
        Volatile.Write(ref _lastGeneration, transition.CurrentGeneration);
        Interlocked.Increment(ref _count);
    }

    public Task WaitForCountAsync(int n) => EngineTestTiming.WaitUntilAsync(() => Count >= n, EngineTestTiming.Budget);

    public Task WaitForAsync(Func<TraceTransition, bool> predicate) =>
        EngineTestTiming.WaitUntilAsync(() => _transitions.Any(predicate), TimeSpan.FromSeconds(5));
}

internal sealed class ScriptedProbe : IProbeChannel
{
    private readonly Func<int, int, CancellationToken, Task<ProbeResult>> _script;
    private readonly ConcurrentDictionary<int, int> _counts = new();
    private readonly TaskCompletionSource _firstCancellation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedProbe(Func<int, int, CancellationToken, ProbeResult> script)
        : this((ttl, count, ct) => Task.FromResult(script(ttl, count, ct)))
    {
    }

    public ScriptedProbe(Func<int, int, CancellationToken, Task<ProbeResult>> script) => _script = script;

    public ProbeCapabilities Capabilities { get; } = new(PayloadSupport.Supported);
    public int TotalCount => _counts.Values.Sum();
    public int ProbeCount(int ttl) => _counts.TryGetValue(ttl, out var n) ? n : 0;
    public int MaxTtlSeen => _counts.Keys.DefaultIfEmpty(0).Max();

    public int MinProbeCount(int fromTtl, int toTtl)
    {
        int min = int.MaxValue;
        for (int ttl = fromTtl; ttl <= toTtl; ttl++)
            min = Math.Min(min, ProbeCount(ttl));
        return min;
    }

    public Task<ProbeResult> SendAsync(IPAddress dest, int ttl, int payloadBytes, TimeSpan timeout, CancellationToken ct)
    {
        int count = _counts.AddOrUpdate(ttl, 1, static (_, n) => n + 1);
        return InvokeAsync(ttl, count, ct);

        async Task<ProbeResult> InvokeAsync(int currentTtl, int currentCount, CancellationToken token)
        {
            try
            {
                return await _script(currentTtl, currentCount, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _firstCancellation.TrySetResult();
                throw;
            }
        }
    }

    public Task WaitForCountAsync(int count, TimeSpan? timeout = null) =>
        EngineTestTiming.WaitUntilAsync(() => TotalCount >= count, timeout);
    public Task WaitForCancellationAsync() => _firstCancellation.Task;
}
