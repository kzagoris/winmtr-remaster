using System.Net;
using Microsoft.Extensions.Time.Testing;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;

namespace WinMtr.Core.Tests;

/// <summary>
/// Engine growth under a fixed hop limit (issue 01): lazy worker spawning
/// from the fast-start cohort, headroom growth, the TTL-1 sweep tick, and
/// the silent walk to the ceiling. Glossary: hop limit, route length,
/// active extent, responding hop, silent hop, route snapshot, trace session.
/// </summary>
public sealed class EngineGrowthTests
{
    [Fact]
    public async Task Short_route_converges_within_fast_start_and_never_exceeds_it()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var targetAddr = IPAddress.Parse("203.0.113.9");
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            if (ttl == 3) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
            if (ttl < 3) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 30, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        // No cadence advance: the fast-start cohort probes immediately, so a
        // route inside it converges within 2 cadences of session start.
        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 3);
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await first.WaitAsync(EngineTestTiming.Budget));
        Assert.True(enumerator.Current.DestinationReached);
        Assert.Equal(3, enumerator.Current.Hops.Length);

        // Settle the parking race (a cohort worker may park its first round
        // behind the destination clamp), then apply the resource proxy:
        // E = min(ceiling, max(fastStartInitial, lastResponding + 3,
        // sweepEdge)) never exceeds 8 here, so no TTL above 8 is requested.
        // Settle between the two advances: an advance landing in a worker's
        // inter-probe gap produces no progress for that chunk.
        int settled = probes.TotalCount;
        clock.Advance(settings.Cadence);
        await EngineTestTiming.TrySettleAsync(() => probes.TotalCount >= settled + 3, TimeSpan.FromSeconds(2));
        int mid = probes.TotalCount;
        clock.Advance(settings.Cadence);
        await probes.WaitForCountAsync(mid + 3);
        Assert.True(probes.MaxTtlSeen <= 8, $"probed TTL {probes.MaxTtlSeen} above the fast-start extent");
    }

    [Fact]
    public async Task Growth_of_three_hops_reflects_within_two_cadences()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var targetAddr = IPAddress.Parse("203.0.113.9");
        int phase = 0;
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            if (Volatile.Read(ref phase) == 0)
            {
                if (ttl == 8) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
                if (ttl < 8) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
                return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
            }

            if (ttl == 11) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
            if (ttl < 8) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 30, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 8);
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await first.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(8, enumerator.Current.Hops.Length);

        // TTL 11 is beyond the confirmed boundary and the headroom, so no
        // worker exists for it yet: growth is genuinely pending.
        Volatile.Write(ref phase, 1);
        Assert.Equal(0, probes.ProbeCount(11));

        // One cadence re-probes TTL 8 and invalidates the boundary; the
        // retained last responding hop (8) plus headroom 3 extends the
        // active extent to 11 and spawns the missing workers immediately.
        clock.Advance(settings.Cadence);
        await tap.WaitForAsync(t => t.BoundaryInvalidated);

        // Second cadence: the +3 growth is reflected.
        clock.Advance(settings.Cadence);
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 11);

        Route? grown = null;
        for (int i = 0; i < 20 && grown?.Hops.Length != 11; i++)
            grown = await ReadSnapshotAsync(enumerator, settings, clock);
        Assert.Equal(11, grown!.Hops.Length);
        Assert.True(grown.DestinationReached);

        // Extent proxy: E never exceeded lastResponding(8) + 3 = 11, so no
        // TTL above 11 was requested of the probe channel.
        Assert.True(probes.MaxTtlSeen <= 11, $"probed TTL {probes.MaxTtlSeen} above extent 11");
    }

    [Fact]
    public async Task Responder_beyond_silent_reach_is_found_via_sweep()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            if (ttl <= 5) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            if (ttl == 12) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse("192.0.2.12"), 10);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 30, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> pump = enumerator.MoveNextAsync().AsTask();

        // Settle the fast-start cohort: last responding hop 5 plus headroom
        // 3 gives E = 8 once all transit identities are recorded. A cohort
        // worker whose first check races those records may park one round
        // behind the clamp and rejoins on the next cadence, so advance
        // until every cohort TTL has probed at least once. A per-chunk stall
        // (an advance landing in a worker's inter-probe gap, or a loaded
        // threadpool) costs one settle window and nothing more; the overall
        // budget is what stops a genuinely stuck session from hanging the
        // suite, and the assert below reports it.
        var settling = EngineTestTiming.WithinBudget();
        for (int i = 0; i < 10 && probes.MinProbeCount(1, 8) < 1 && settling(); i++)
        {
            clock.Advance(settings.Cadence);
            await EngineTestTiming.TrySettleAsync(() => probes.MinProbeCount(1, 8) >= 1, TimeSpan.FromSeconds(1));
        }
        Assert.True(probes.MinProbeCount(1, 8) >= 1, "fast-start cohort did not settle");
        Assert.True(probes.MaxTtlSeen <= 8, $"probed TTL {probes.MaxTtlSeen} above the fast-start extent");
        // TTL 12 sits behind six silent hops — beyond the 2-TTL silent
        // reach — so it is undiscovered before the sweep walks there.
        Assert.Equal(0, probes.ProbeCount(12));

        // Walk the sweep: every advance hands the never-parking TTL-1
        // worker another cadence iteration (hence another sweep tick), so
        // settle each chunk on tick progress against the pre-advance
        // baseline — a delta on a guaranteed increment, which inline timer
        // callbacks cannot race. Edge 1 -> 12 needs 110 ticks.
        //
        // Bounded by the wall clock rather than by an iteration count. Tick
        // progress is only a proxy for sweep progress, and a loaded engine
        // satisfies it from backlog: the advance this iteration made has not
        // been processed yet, but a tick queued earlier arrives and settles
        // the chunk regardless. A fixed cap is then spent in milliseconds
        // without the sweep having walked anywhere, reporting load as a sweep
        // defect. The hop limit bounds how far the fake clock can drive the
        // extent, so there is nothing for a cap to protect.
        var sweeping = EngineTestTiming.WithinBudget();
        while (probes.ProbeCount(12) == 0 && sweeping())
        {
            int ticksBefore = probes.ProbeCount(1);
            clock.Advance(TimeSpan.FromSeconds(10));
            await EngineTestTiming.TrySettleAsync(() => probes.ProbeCount(1) > ticksBefore || probes.ProbeCount(12) > 0, TimeSpan.FromSeconds(2));
        }
        Assert.True(probes.ProbeCount(12) > 0, "sweep never reached TTL 12");
        await tap.WaitForAsync(t => t.Ttl == 12 && t.IdentityChanged);

        Route? found = null;
        for (int i = 0; i < 20 && found?.Hops.Length != 12; i++)
            found = await ReadSnapshotAsync(enumerator, settings, clock);
        Assert.Equal(12, found!.Hops.Length);

        // Ceiling bound of the extent rule: nothing above the hop limit.
        Assert.True(probes.MaxTtlSeen <= 30, $"probed TTL {probes.MaxTtlSeen} above the hop limit");
        Assert.True(await pump.WaitAsync(EngineTestTiming.Budget));
    }

    [Fact]
    public async Task Fully_silent_destination_walks_to_ceiling_and_reports_limit_observed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 12, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> pump = enumerator.MoveNextAsync().AsTask();

        // Persistent silence starts inside the fast-start cohort (E stays
        // max(8, sweepEdge), so no worker ever parks): advance until every
        // cohort TTL has probed at least once. Per-chunk stalls only cost one
        // settle window; the overall budget bounds the wait.
        var silentSettling = EngineTestTiming.WithinBudget();
        for (int i = 0; i < 10 && probes.MinProbeCount(1, 8) < 1 && silentSettling(); i++)
        {
            clock.Advance(settings.Cadence);
            await EngineTestTiming.TrySettleAsync(() => probes.MinProbeCount(1, 8) >= 1, TimeSpan.FromSeconds(1));
        }
        Assert.True(probes.MinProbeCount(1, 8) >= 1, "fast-start cohort did not settle");
        Assert.True(probes.MaxTtlSeen <= 8, $"probed TTL {probes.MaxTtlSeen} above the fast-start extent");
        Assert.Equal(0, probes.ProbeCount(12));

        // The sweep edge applies from the start, so silence walks the
        // active extent 8 -> 12 (edge 1 -> 12 needs 110 TTL-1 ticks).
        // Settle each chunk on tick progress against the pre-advance
        // baseline; stalls only cost one settle window each.
        // Wall-clock bounded for the same reason as the sweep walk above.
        var walking = EngineTestTiming.WithinBudget();
        while (probes.ProbeCount(12) == 0 && walking())
        {
            int ticksBefore = probes.ProbeCount(1);
            clock.Advance(TimeSpan.FromSeconds(10));
            await EngineTestTiming.TrySettleAsync(() => probes.ProbeCount(1) > ticksBefore || probes.ProbeCount(12) > 0, TimeSpan.FromSeconds(2));
        }
        Assert.True(probes.ProbeCount(12) > 0, "silent walk never reached the ceiling");

        Route? silent = null;
        for (int i = 0; i < 20 && silent?.HopLimitObserved != true; i++)
            silent = await ReadSnapshotAsync(enumerator, settings, clock);
        Assert.False(silent!.DestinationReached);
        Assert.True(silent.HopLimitObserved);
        Assert.True(silent.IsTruncated);
        Assert.True(probes.MaxTtlSeen <= 12, $"probed TTL {probes.MaxTtlSeen} above the hop limit");
        Assert.True(await pump.WaitAsync(EngineTestTiming.Budget));
    }

    [Fact]
    public async Task Hop_limit_below_fast_start_clamps_the_initial_cohort()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var targetAddr = IPAddress.Parse("203.0.113.9");
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            if (ttl == 2) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
            if (ttl < 2) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 3, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 2);
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await first.WaitAsync(EngineTestTiming.Budget));
        Assert.True(enumerator.Current.DestinationReached);
        Assert.Equal(2, enumerator.Current.Hops.Length);

        // fastStartInitial = min(8, ceiling): the setting is respected.
        Assert.True(probes.MaxTtlSeen <= 3, $"probed TTL {probes.MaxTtlSeen} above the hop limit");
    }

    [Fact]
    public async Task Late_spawned_worker_fault_surfaces_and_drain_joins_siblings()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fault = new InvalidOperationException("late probe failed");
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            // TTLs 1..8 answer transit inside the fast-start cohort, so the
            // active extent grows to lastResponding(8) + 3 = 11 and TTL 11
            // is spawned lazily: its worker skips the cohort barrier.
            if (ttl == 11)
                throw fault;
            if (ttl <= 8) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 30, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await move.WaitAsync(EngineTestTiming.Budget));
        Assert.Same(fault, thrown);
        // The fault came from past the fast-start cohort: growth spawned it.
        Assert.True(probes.MaxTtlSeen >= 11, $"late TTL 11 was never spawned (max {probes.MaxTtlSeen})");

        // Drain evidence: after disposal no worker is left behind to probe.
        await enumerator.DisposeAsync();
        int countAtDispose = probes.TotalCount;
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => probes.WaitForCountAsync(countAtDispose + 1, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(countAtDispose, probes.TotalCount);
    }

    private static async Task<Route> ReadSnapshotAsync(
        IAsyncEnumerator<Route> enumerator, ProbeSettings settings, FakeTimeProvider clock)
    {
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await move.WaitAsync(EngineTestTiming.Budget));
        return enumerator.Current;
    }
}
