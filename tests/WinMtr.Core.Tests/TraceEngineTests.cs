using System.Collections.Immutable;
using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;

namespace WinMtr.Core.Tests;

public sealed class TraceEngineTests
{
    [Fact]
    public void Trace_state_preserves_initial_loss_when_responder_is_first_discovered()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 3);

        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse("192.0.2.1"), 24));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(4, hop.Stats.Sent);
        Assert.Equal(1, hop.Stats.Received);
        Assert.Equal(75, hop.Stats.LossPercent);
        Assert.Equal(24, hop.Stats.BestRttMs);
    }

    [Fact]
    public void Route_reports_discovery_observations_and_compatibility_truncation()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 1);

        var before = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.False(before.DestinationReached);
        Assert.False(before.HopLimitObserved);
        Assert.False(before.IsTruncated);

        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        var after = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.False(after.DestinationReached);
        Assert.True(after.HopLimitObserved);
        Assert.True(after.IsTruncated);

        var legacy = new Route(target, ImmutableArray<Hop>.Empty, DateTimeOffset.UnixEpoch, IsTruncated: true);
        Assert.True(legacy.IsTruncated);
        Assert.False(legacy.DestinationReached);
        Assert.True(legacy.HopLimitObserved);
    }

    [Fact]
    public void Snapshot_carries_the_session_start_and_defaults_it_to_taken_at()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 1);
        var startedAt = DateTimeOffset.UnixEpoch;
        var takenAt = startedAt + TimeSpan.FromMinutes(3);

        var withStart = state.Snapshot(takenAt, startedAt);
        Assert.Equal(startedAt, withStart.StartedAt);
        Assert.Equal(takenAt, withStart.TakenAt);

        // A caller that does not track a session start still gets a route.
        var withoutStart = state.Snapshot(takenAt);
        Assert.Equal(takenAt, withoutStart.StartedAt);
    }

    [Fact]
    public void Responder_replacement_starts_a_new_statistics_epoch()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 3);
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");

        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, first, 10));
        state.ApplyDnsResult(1, first, state.GetEpoch(1), "first.example");
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, second, 20));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(second, hop.Address);
        Assert.Null(hop.HostName);
        Assert.Equal(1, hop.Stats.Sent);
        Assert.Equal(1, hop.Stats.Received);
        Assert.Equal(20, hop.Stats.BestRttMs);
    }

    [Fact]
    public void Timeout_retains_responder_identity_and_statistics()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 3);
        var address = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, address, 10));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(address, hop.Address);
        Assert.Equal(2, hop.Stats.Sent);
        Assert.Equal(1, hop.Stats.Received);
        Assert.Equal(ProbeOutcome.TimedOut, hop.LastOutcome);
    }

    [Fact]
    public void Delayed_dns_completion_for_an_old_epoch_is_ignored()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 3);
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");

        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, first, 10));
        long firstEpoch = state.GetEpoch(1);
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, second, 10));
        long secondEpoch = state.GetEpoch(1);

        Assert.False(state.ApplyDnsResult(1, first, firstEpoch, "stale.example"));
        Assert.True(state.ApplyDnsResult(1, second, secondEpoch, "current.example"));
        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(second, hop.Address);
        Assert.Equal("current.example", hop.HostName);
    }

    [Fact]
    public void Retained_target_identity_does_not_trim_after_destination_evidence_is_lost()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 4);
        state.RecordProbe(2, new ProbeResult(ProbeOutcome.Reached, target.Address, 10));
        state.RecordProbe(2, new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse("192.0.2.2"), 10));
        state.RecordProbe(3, new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse("192.0.2.3"), 10));

        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.False(snapshot.DestinationReached);
        // Organic length is 3 (last responder); a stale-boundary trim would
        // give 2 and the old hop-limit padding would give 4.
        Assert.Equal(3, snapshot.Hops.Length);
    }

    [Fact]
    public async Task Engine_publishes_hop_limit_uncertainty_after_a_completed_silent_probe()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));

        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        // Explicit readiness: RecordProbe done and cadence timer registered.
        await tap.WaitForCountAsync(1);
        clock.Advance(settings.SnapshotInterval);

        Assert.True(await move.WaitAsync(EngineTestTiming.Budget));
        Assert.False(enumerator.Current.DestinationReached);
        Assert.True(enumerator.Current.HopLimitObserved);
        Assert.True(enumerator.Current.IsTruncated);
    }

    [Fact]
    public async Task Worker_fault_is_propagated_and_cancels_sibling_traffic()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var fault = new InvalidOperationException("probe failed");
        // The sibling worker signals here as it enters its probe. The TTL-1
        // fault waits for that signal: this test claims the fault cancels
        // sibling traffic, so sibling traffic must be in flight first. Raised
        // any earlier, the fault can land while the TTL-2 worker still sits at
        // the fast-start barrier, which then leaves that worker through the
        // cancellation path without a probe, so nothing ever observes the
        // cancellation. That order is a scheduling accident, and a slower
        // runner takes it more often.
        var siblingProbing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = new ScriptedProbe(async (ttl, _, ct) =>
        {
            if (ttl == 1)
            {
                await siblingProbing.Task.WaitAsync(EngineTestTiming.Budget);
                throw fault;
            }

            siblingProbing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 2, cadence: TimeSpan.FromSeconds(1));

        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await move.WaitAsync(EngineTestTiming.Budget));
        Assert.Same(fault, thrown);
        await probes.WaitForCancellationAsync().WaitAsync(EngineTestTiming.Budget);
    }

    [Fact]
    public async Task Caller_cancellation_publishes_a_final_snapshot_and_stops_workers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = engine.RunAsync(target, settings, cancellation.Token).GetAsyncEnumerator();

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await probes.WaitForCountAsync(1);
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await first.WaitAsync(EngineTestTiming.Budget));

        int countBeforeCancellation = probes.TotalCount;
        Task<bool> final = enumerator.MoveNextAsync().AsTask();
        cancellation.Cancel();
        Assert.True(await final.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(countBeforeCancellation, probes.TotalCount);
    }

    [Fact]
    public async Task Published_snapshots_keep_the_session_start_time()
    {
        var start = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));

        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await probes.WaitForCountAsync(1);
        clock.Advance(settings.SnapshotInterval);

        Assert.True(await move.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(start, enumerator.Current.StartedAt);
        Assert.Equal(start + settings.SnapshotInterval, enumerator.Current.TakenAt);
    }

    [Fact]
    public async Task Enumerator_disposal_drains_workers_without_an_extra_snapshot()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));
        var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> pending = enumerator.MoveNextAsync().AsTask();
        await probes.WaitForCountAsync(1);
        await enumerator.DisposeAsync();
        int countAfterDispose = probes.TotalCount;
        clock.Advance(settings.Cadence + settings.SnapshotInterval);

        Assert.False(await pending.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(countAfterDispose, probes.TotalCount);
    }

    [Fact]
    public void Late_result_from_an_obsolete_generation_cannot_restore_a_boundary()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 3);
        long beforeDiscovery = state.DiscoveryGeneration;
        state.RecordProbe(2, new ProbeResult(ProbeOutcome.Reached, target.Address, 10), beforeDiscovery);
        long current = state.DiscoveryGeneration;

        state.RecordProbe(2, new ProbeResult(ProbeOutcome.TimedOut, null, 0), beforeDiscovery);

        Assert.Equal(current, state.DiscoveryGeneration);
        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.True(snapshot.DestinationReached);
        Assert.Equal(2, state.DestinationTtl);
    }

    [Fact]
    public void A_failed_boundary_confirmation_invalidates_destination_and_reactivates_probe_range()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 3);
        state.RecordProbe(2, new ProbeResult(ProbeOutcome.Reached, target.Address, 10));
        Assert.False(state.ShouldProbe(3));

        var transition = state.RecordProbe(2, new ProbeResult(ProbeOutcome.TimedOut, null, 0));

        Assert.True(transition.BoundaryInvalidated);
        Assert.False(state.DestinationReached);
        Assert.True(state.ShouldProbe(3));
    }

    [Fact]
    public void Destination_boundary_can_grow_then_shrink_without_stale_rows()
    {
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var state = new TraceState(target, hopLimit: 8);
        var router = IPAddress.Parse("192.0.2.5");

        state.RecordProbe(5, new ProbeResult(ProbeOutcome.Reached, target.Address, 10));
        Assert.False(state.ShouldProbe(6));

        state.RecordProbe(5, new ProbeResult(ProbeOutcome.Expired, router, 10));
        Assert.True(state.ShouldProbe(8));
        state.RecordProbe(8, new ProbeResult(ProbeOutcome.Reached, target.Address, 10));
        Assert.Equal(8, state.DestinationTtl);

        state.RecordProbe(5, new ProbeResult(ProbeOutcome.Reached, target.Address, 10));
        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.Equal(5, state.DestinationTtl);
        Assert.Equal(5, snapshot.Hops.Length);
    }

    [Fact]
    public async Task Worker_waits_only_for_the_positive_cadence_remainder()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((_, count, _) =>
        {
            if (count == 1)
                clock.Advance(TimeSpan.FromMilliseconds(200));
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));
        Task<bool> pump;
        await using (var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator())
        {
            pump = enumerator.MoveNextAsync().AsTask();

            // First probe recorded and 800ms cadence timer registered.
            await tap.WaitForCountAsync(1);
            clock.Advance(TimeSpan.FromMilliseconds(799));
            // Negative signal, not a sleep: a correct worker waits on the fake
            // clock, which this test never advances during the window, so no
            // second probe can appear; an eager worker fails here loudly.
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => probes.WaitForCountAsync(2, TimeSpan.FromMilliseconds(200)));
            Assert.Equal(1, probes.TotalCount);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            await probes.WaitForCountAsync(2);
        }

        // Awaited, but the result is deliberately not asserted: whether the
        // pending read observes a snapshot or observes end-of-sequence depends
        // on whether disposal wins that race, which is not what this test is
        // about. Completing without faulting is the claim - no overlapping
        // Dispose, no unobserved task.
        await pump.WaitAsync(EngineTestTiming.Budget);
    }

    [Fact]
    public async Task Destination_growth_is_rediscovered_within_one_cadence()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var targetAddr = IPAddress.Parse("203.0.113.9");
        var router = IPAddress.Parse("192.0.2.5");
        int phase = 0;
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            if (Volatile.Read(ref phase) == 0)
            {
                if (ttl == 5) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
                return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
            }
            else
            {
                if (ttl == 5) return new ProbeResult(ProbeOutcome.Expired, router, 10);
                if (ttl == 8) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
                if (ttl < 5) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
                return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            }
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 8, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        // Start workers/publisher before waiting: enumeration drives startup.
        Task<bool> firstSnap = enumerator.MoveNextAsync().AsTask();
        // Converge on TTL 5: wait until a destination success at TTL 5 is recorded.
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 5);
        // Drain one snapshot showing length 5.
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await firstSnap.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(5, enumerator.Current.Hops.Length);
        Assert.True(enumerator.Current.DestinationReached);

        // Move the destination to TTL 8. The next TTL-5 probe (non-success)
        // invalidates the boundary; TTLs 6-8 must resume within one cadence.
        Volatile.Write(ref phase, 1);
        clock.Advance(settings.Cadence);
        await tap.WaitForAsync(t => t.BoundaryInvalidated);
        int tapAtInvalidation = tap.Count;
        // Advance one cadence: suspended workers must reactivate and probe TTL 8.
        clock.Advance(settings.Cadence);
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 8);

        Task<bool> grown = enumerator.MoveNextAsync().AsTask();
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await grown.WaitAsync(EngineTestTiming.Budget));
        // Drain any stale buffered snapshots: poll until the post-growth
        // boundary is observed (bounded, no arbitrary sleeps).
        Route? grownRoute = enumerator.Current;
        using (var grownTimeout = new CancellationTokenSource(EngineTestTiming.Budget))
        {
            while ((!grownRoute.DestinationReached || grownRoute.Hops.Length != 8) && !grownTimeout.IsCancellationRequested)
            {
                Task<bool> next = enumerator.MoveNextAsync().AsTask();
                clock.Advance(settings.SnapshotInterval);
                if (!await next.WaitAsync(EngineTestTiming.Budget))
                    break;
                grownRoute = enumerator.Current;
            }
        }
        Assert.Equal(8, grownRoute.Hops.Length);
        Assert.True(grownRoute.DestinationReached);
        Assert.True(tap.Count > tapAtInvalidation);
    }

    [Fact]
    public async Task Destination_shrinkage_suspends_higher_workers()
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
            else
            {
                if (ttl == 5) return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
                if (ttl < 5) return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
                return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
            }
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 8, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> atEight = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 8);
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await atEight.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(8, enumerator.Current.Hops.Length);

        Volatile.Write(ref phase, 1);
        clock.Advance(settings.Cadence);
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 5);

        Task<bool> shrunk = enumerator.MoveNextAsync().AsTask();
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await shrunk.WaitAsync(EngineTestTiming.Budget));
        Route? shrunkRoute = enumerator.Current;
        using (var shrinkTimeout = new CancellationTokenSource(EngineTestTiming.Budget))
        {
            while ((!shrunkRoute.DestinationReached || shrunkRoute.Hops.Length != 5) && !shrinkTimeout.IsCancellationRequested)
            {
                Task<bool> next = enumerator.MoveNextAsync().AsTask();
                clock.Advance(settings.SnapshotInterval);
                if (!await next.WaitAsync(EngineTestTiming.Budget))
                    break;
                shrunkRoute = enumerator.Current;
            }
        }
        Assert.Equal(5, shrunkRoute.Hops.Length);
        Assert.True(shrunkRoute.DestinationReached);

        // Higher TTLs must suspend: record counts, advance several cadences,
        // then require no further probes above the new boundary. Per-TTL
        // tolerance is exactly one: a single probe that entered before
        // suspension may still land; anything more means workers resumed.
        (int b6, int b7, int b8) = (probes.ProbeCount(6), probes.ProbeCount(7), probes.ProbeCount(8));
        clock.Advance(settings.Cadence);
        clock.Advance(settings.Cadence);
        // Settle, not a fixed sleep: already-entered probes land in real time
        // while no further fake timer can fire without another Advance, so
        // sample until two consecutive totals agree or the budget lapses.
        int prev = -1, cur = probes.TotalCount;
        using (var settle = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            while (prev != cur && !settle.IsCancellationRequested)
            {
                prev = cur;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), settle.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                cur = probes.TotalCount;
            }
        }
        Assert.True(probes.ProbeCount(6) - b6 <= 1, $"TTL 6 kept probing after shrinkage: {b6} -> {probes.ProbeCount(6)}");
        Assert.True(probes.ProbeCount(7) - b7 <= 1, $"TTL 7 kept probing after shrinkage: {b7} -> {probes.ProbeCount(7)}");
        Assert.True(probes.ProbeCount(8) - b8 <= 1, $"TTL 8 kept probing after shrinkage: {b8} -> {probes.ProbeCount(8)}");
    }

    [Fact]
    public async Task Silent_destination_resumes_discovery_up_to_hop_limit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var targetAddr = IPAddress.Parse("203.0.113.9");
        int phase = 0;
        var probes = new ScriptedProbe((ttl, _, _) =>
        {
            if (Volatile.Read(ref phase) == 0 && ttl == 2)
                return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
            if (ttl < 3)
                return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 4, cadence: TimeSpan.FromSeconds(1));
        Task<bool> pump;
        await using (var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator())
        {
            pump = enumerator.MoveNextAsync().AsTask();

            await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 2);
            Volatile.Write(ref phase, 1);
            clock.Advance(settings.Cadence);
            await tap.WaitForAsync(t => t.BoundaryInvalidated);

            int highBefore = probes.ProbeCount(3) + probes.ProbeCount(4);
            // Suspended workers reactivate within one cadence, but phase alignment
            // may require up to two cadence advances (one to finish a suspension
            // delay started before invalidation). Poll with bounded advances.
            int highAfter = highBefore;
            using (var resumeTimeout = new CancellationTokenSource(EngineTestTiming.Budget))
            {
                while (highAfter <= highBefore && !resumeTimeout.IsCancellationRequested)
                {
                    clock.Advance(settings.Cadence);
                    try
                    {
                        await probes.WaitForCountAsync(probes.TotalCount + 1).WaitAsync(TimeSpan.FromMilliseconds(200));
                    }
                    catch (TimeoutException)
                    {
                        // No probe in this window; advance again.
                    }
                    highAfter = probes.ProbeCount(3) + probes.ProbeCount(4);
                }
            }
            Assert.True(highAfter > highBefore, "discovery did not resume above the silent destination");
        }

        // Awaited, but the result is deliberately not asserted: whether the
        // pending read observes a snapshot or observes end-of-sequence depends
        // on whether disposal wins that race, which is not what this test is
        // about. Completing without faulting is the claim - no overlapping
        // Dispose, no unobserved task.
        await pump.WaitAsync(EngineTestTiming.Budget);
    }

    [Fact]
    public async Task Obsolete_generation_slow_probe_cannot_restore_old_boundary()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var targetAddr = IPAddress.Parse("203.0.113.9");
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ttl5Calls = 0;
        var probes = new ScriptedProbe(async (ttl, _, ct) =>
        {
            if (ttl == 5 && Interlocked.Increment(ref ttl5Calls) == 1)
            {
                // Long probe started in the old generation; it will return a
                // destination success only after the boundary has moved.
                slowEntered.TrySetResult();
                await releaseSlow.Task.WaitAsync(ct);
                return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
            }
            if (ttl == 8)
                return new ProbeResult(ProbeOutcome.Reached, targetAddr, 10);
            if (ttl == 5)
                return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse("192.0.2.5"), 10);
            return new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse($"192.0.2.{ttl}"), 10);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), targetAddr, null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 8, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> starter = enumerator.MoveNextAsync().AsTask();
        // Let the slow TTL-5 probe start, then establish the boundary at TTL 8
        // (which bumps the discovery generation past the slow probe's).
        await slowEntered.Task.WaitAsync(EngineTestTiming.Budget);
        await tap.WaitForAsync(t => t.DestinationSuccess && t.Ttl == 8);
        long generationAtEight = tap.LastGeneration;
        releaseSlow.TrySetResult();
        // The late TTL-5 success carries the old generation and must not
        // reinstate TTL 5 as the boundary.
        await tap.WaitForCountAsync(tap.Count + 1);

        // Complete the starter read first (single-reader channel), then poll
        // for a fresh post-invalidation snapshot.
        clock.Advance(settings.SnapshotInterval);
        await starter.WaitAsync(EngineTestTiming.Budget);
        Route? obsoleteSeen = null;
        using (var obsoleteTimeout = new CancellationTokenSource(EngineTestTiming.Budget))
        {
            while (!obsoleteTimeout.IsCancellationRequested)
            {
                Task<bool> snap = enumerator.MoveNextAsync().AsTask();
                clock.Advance(settings.SnapshotInterval);
                if (!await snap.WaitAsync(EngineTestTiming.Budget))
                    break;
                obsoleteSeen = enumerator.Current;
                if (obsoleteSeen.DestinationReached && obsoleteSeen.Hops.Length == 8)
                    break;
            }
        }
        Assert.True(obsoleteSeen?.DestinationReached == true);
        Assert.Equal(8, obsoleteSeen?.Hops.Length);
        Assert.True(tap.LastGeneration >= generationAtEight);
    }

    [Fact]
    public async Task Dns_disabled_suppresses_resolution()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var addr = IPAddress.Parse("192.0.2.1");
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.Expired, addr, 10));
        var resolver = new ControllableResolver();
        resolver.Add(addr.ToString(), "gw.local");
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, resolver, clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1)) with { ResolveNames = false };
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await move.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal("192.0.2.1", enumerator.Current.Hops[0].Label);
    }

    [Fact]
    public async Task Dns_enabled_resolves_new_identities()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var addr = IPAddress.Parse("192.0.2.1");
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.Expired, addr, 10));
        var resolver = new ControllableResolver();
        resolver.Add(addr.ToString(), "gw.local");
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, resolver, clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> starter = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForCountAsync(1);
        // Wait for the async DNS dispatch to complete and apply.
        await resolver.WaitForCountAsync(1);
        clock.Advance(settings.SnapshotInterval);
        await starter.WaitAsync(EngineTestTiming.Budget);
        using var cts = new CancellationTokenSource(EngineTestTiming.Budget);
        Route? seen = enumerator.Current.Hops[0].HostName == "gw.local" ? enumerator.Current : null;
        while (!cts.IsCancellationRequested)
        {
            Task<bool> move = enumerator.MoveNextAsync().AsTask();
            clock.Advance(settings.SnapshotInterval);
            if (!await move.WaitAsync(EngineTestTiming.Budget))
                break;
            seen = enumerator.Current;
            if (seen.Hops[0].HostName == "gw.local")
                break;
        }
        Assert.Equal("gw.local", seen?.Hops[0].HostName);
        Assert.Equal("gw.local", seen?.Hops[0].Label);
    }

    [Fact]
    public async Task Stale_dns_result_for_old_epoch_is_rejected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");
        int calls = 0;
        var releaseFirst = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ControllableResolver(async (addr, ct) =>
        {
            if (addr.Equals(first) && Interlocked.Increment(ref calls) == 1)
                return await releaseFirst.Task.WaitAsync(ct);
            return addr.Equals(second) ? "second.example" : null;
        });
        int probeCalls = 0;
        var probes = new ScriptedProbe((_, _, _) =>
        {
            // First probe discovers A, subsequent probes discover B (epoch bump).
            return Interlocked.Increment(ref probeCalls) == 1
                ? new ProbeResult(ProbeOutcome.Expired, first, 10)
                : new ProbeResult(ProbeOutcome.Expired, second, 10);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, resolver, clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromMilliseconds(50));
        await using var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator();

        Task<bool> starter = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForCountAsync(1);
        clock.Advance(settings.Cadence);
        await tap.WaitForCountAsync(2);
        releaseFirst.TrySetResult("stale.example");
        clock.Advance(settings.SnapshotInterval);
        await starter.WaitAsync(EngineTestTiming.Budget);
        // Let the stale completion and the current-epoch lookup both apply.
        using var cts = new CancellationTokenSource(EngineTestTiming.Budget);
        Route? seen = null;
        while (!cts.IsCancellationRequested)
        {
            Task<bool> move = enumerator.MoveNextAsync().AsTask();
            clock.Advance(settings.SnapshotInterval);
            clock.Advance(TimeSpan.FromMilliseconds(60));
            if (!await move.WaitAsync(EngineTestTiming.Budget))
                break;
            seen = enumerator.Current;
            if (seen.Hops[0].Address?.Equals(second) == true && seen.Hops[0].HostName is not null)
                break;
        }
        Assert.Equal(second, seen?.Hops[0].Address);
        Assert.Equal("second.example", seen?.Hops[0].HostName);
    }

    [Fact]
    public async Task Dns_failure_preserves_numeric_label_and_drains_cleanly()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var addr = IPAddress.Parse("192.0.2.1");
        var probes = new ScriptedProbe((_, _, _) => new ProbeResult(ProbeOutcome.Expired, addr, 10));
        var resolver = new ControllableResolver((_, _) => throw new InvalidOperationException("dns down"));
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, resolver, clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource();
        await using var enumerator = engine.RunAsync(target, settings, cts.Token).GetAsyncEnumerator();

        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await tap.WaitForCountAsync(1);
        clock.Advance(settings.SnapshotInterval);
        Assert.True(await move.WaitAsync(EngineTestTiming.Budget));
        Assert.Equal("192.0.2.1", enumerator.Current.Hops[0].Label);

        Task<bool> final = enumerator.MoveNextAsync().AsTask();
        await cts.CancelAsync();
        Assert.True(await final.WaitAsync(EngineTestTiming.Budget));
    }

    [Fact]
    public async Task Slow_probes_do_not_add_an_extra_cadence_delay()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var probes = new ScriptedProbe((_, count, _) =>
        {
            // First probe takes longer than the cadence; the worker must not
            // add another full cadence on top (remainder <= 0 branch).
            if (count == 1)
                clock.Advance(TimeSpan.FromMilliseconds(1200));
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var tap = new ProbeTap();
        var engine = new TraceEngine(probes, new TestResolver(), clock, tap.Handler);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 1, cadence: TimeSpan.FromSeconds(1));
        Task<bool> pump;
        await using (var enumerator = engine.RunAsync(target, settings).GetAsyncEnumerator())
        {
            pump = enumerator.MoveNextAsync().AsTask();

            await tap.WaitForCountAsync(1);
            // No clock advance: the second probe must start promptly via the
            // yield path, not wait for another cadence interval.
            await tap.WaitForCountAsync(2);
            Assert.Equal(2, probes.TotalCount);
        }

        // Awaited, but the result is deliberately not asserted: whether the
        // pending read observes a snapshot or observes end-of-sequence depends
        // on whether disposal wins that race, which is not what this test is
        // about. Completing without faulting is the claim - no overlapping
        // Dispose, no unobserved task.
        await pump.WaitAsync(EngineTestTiming.Budget);
    }

    [Fact]
    public async Task Cancellation_during_an_inflight_probe_stops_traffic()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = new ScriptedProbe(async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProbeResult(ProbeOutcome.TimedOut, null, 0);
        });
        var engine = new TraceEngine(probes, new TestResolver(), clock);
        var target = new Target(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);
        var settings = EngineTestTiming.EngineSettings(hopLimit: 2, cadence: TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource();
        await using var enumerator = engine.RunAsync(target, settings, cts.Token).GetAsyncEnumerator();

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(EngineTestTiming.Budget);
        await cts.CancelAsync();
        // Cancellation is normal shutdown: a final snapshot, then completion.
        // `first` was already pending and receives the final snapshot.
        Assert.True(await first.WaitAsync(EngineTestTiming.Budget));
        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(EngineTestTiming.Budget));

        // Sampled only now that completion has been observed. Sampling at the
        // moment the first probe entered would race worker startup: the tap
        // fires on whichever worker enters first, while the second is still
        // entitled to begin its own first probe before cancellation takes
        // effect. The guarantee under test is that traffic has stopped once
        // the session has shut down, not that no probe starts between an
        // arbitrary sample point and the cancel.
        int countAfterShutdown = probes.TotalCount;
        // Stability signal, not a sleep: with workers exited and the fake
        // clock untouched, no further probe can appear in the window.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => probes.WaitForCountAsync(countAfterShutdown + 1, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(countAfterShutdown, probes.TotalCount);
    }

    private sealed class ControllableResolver : INameResolver
    {
        private readonly Func<IPAddress, CancellationToken, Task<string?>> _handler;
        private readonly ConcurrentDictionary<string, string> _names = new();
        private int _callCount;

        public ControllableResolver() => _handler = DefaultHandlerAsync;

        public ControllableResolver(Func<IPAddress, CancellationToken, Task<string?>> handler) =>
            _handler = handler;

        public int CallCount => Volatile.Read(ref _callCount);

        public ControllableResolver Add(string address, string name)
        {
            _names[address] = name;
            return this;
        }

        public async Task<string?> ResolveAsync(IPAddress address, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            if (_names.TryGetValue(address.ToString(), out var n))
                return n;
            return await _handler(address, ct).ConfigureAwait(false);
        }

        public Task WaitForCountAsync(int n) => EngineTestTiming.WaitUntilAsync(() => CallCount >= n);

        private static Task<string?> DefaultHandlerAsync(IPAddress _, CancellationToken __) =>
            Task.FromResult<string?>(null);
    }
}
