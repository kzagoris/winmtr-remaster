using System.Net;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;

namespace WinMtr.Core.Tests;

/// <summary>
/// State ownership of the active extent behind one accessor (issue 02):
/// E = min(ceiling, destinationTtl ?? max(fastStartInitial, lastResponding+3,
/// sweepEdge)). Glossary: active extent, responding hop, silent hop,
/// route snapshot, retained hop rows.
/// </summary>
public sealed class TraceStateExtentTests
{
    private static Target MakeTarget() =>
        new(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);

    private static TraceState NewState(int hopLimit = 30) => new(MakeTarget(), hopLimit);

    private static ProbeResult ExpiredAt(string address, int rttMs = 10) =>
        new(ProbeOutcome.Expired, IPAddress.Parse(address), rttMs);

    private static ProbeResult ReachedTarget(Target target) =>
        new(ProbeOutcome.Reached, target.Address, 10);

    private static ProbeResult Silent() =>
        new(ProbeOutcome.TimedOut, null, 0);

    [Theory]
    [InlineData(30, 8)]
    [InlineData(8, 8)]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    public void Active_extent_starts_at_fast_start_clamped_to_ceiling(int hopLimit, int expected)
    {
        Assert.Equal(expected, NewState(hopLimit).ActiveExtent);
    }

    [Fact]
    public void Extent_tracks_last_responding_hop_plus_three()
    {
        // Worked example from the spec: a 2-hop route with no confirmed
        // destination settles at E = 5.
        var state = NewState(hopLimit: 30);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        state.RecordProbe(2, ExpiredAt("192.0.2.2"));

        Assert.Equal(5, state.ActiveExtent);
    }

    [Fact]
    public void Fast_start_is_an_initial_value_not_a_standing_floor()
    {
        var state = NewState(hopLimit: 30);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));

        Assert.Equal(4, state.ActiveExtent);
    }

    [Fact]
    public void Extent_clamps_to_the_confirmed_destination()
    {
        // Worked example: once the destination is confirmed at TTL 2, E = 2.
        var target = MakeTarget();
        var state = new TraceState(target, 30);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        state.RecordProbe(2, ReachedTarget(target));

        Assert.Equal(2, state.ActiveExtent);
        Assert.False(state.ShouldProbe(3));
    }

    [Fact]
    public void Sweep_tick_advances_the_edge_every_tenth_call()
    {
        var state = NewState(hopLimit: 30);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        Assert.Equal(4, state.ActiveExtent);

        for (int i = 0; i < 9; i++)
            state.AdvanceSweep();
        Assert.Equal(4, state.ActiveExtent);

        // 40th tick moves the sweep edge to 5, past lastResponding + 3.
        for (int i = 0; i < 31; i++)
            state.AdvanceSweep();
        Assert.Equal(5, state.ActiveExtent);
    }

    [Fact]
    public void Sweep_edge_is_bounded_by_the_ceiling()
    {
        var state = NewState(hopLimit: 30);
        for (int i = 0; i < 10_000; i++)
            state.AdvanceSweep();

        Assert.Equal(30, state.ActiveExtent);
    }

    [Fact]
    public void Silent_walk_reaches_the_ceiling_through_the_sweep()
    {
        var state = NewState(hopLimit: 30);
        for (int i = 0; i < 80; i++)
            state.AdvanceSweep();

        // Sweep edge 9 now dominates the fast-start initial value of 8.
        Assert.Equal(9, state.ActiveExtent);
    }

    [Fact]
    public void ShouldProbe_parks_above_the_extent_and_resumes_on_growth()
    {
        var state = NewState(hopLimit: 30);
        Assert.True(state.ShouldProbe(8));
        Assert.False(state.ShouldProbe(9));

        state.RecordProbe(6, ExpiredAt("192.0.2.6"));
        Assert.Equal(9, state.ActiveExtent);
        Assert.True(state.ShouldProbe(9));
    }

    [Fact]
    public void Invalidation_unparks_workers_through_the_extent()
    {
        var target = MakeTarget();
        var state = new TraceState(target, 30);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        state.RecordProbe(2, ReachedTarget(target));
        Assert.False(state.ShouldProbe(6));

        var transition = state.RecordProbe(2, Silent());

        Assert.True(transition.BoundaryInvalidated);
        // Retained identities keep lastResponding at 2, so the extent
        // reverts to 2 + 3 and parked workers resume.
        Assert.Equal(5, state.ActiveExtent);
        Assert.True(state.ShouldProbe(5));
        Assert.False(state.ShouldProbe(6));
    }

    [Fact]
    public void Shrink_trims_immediately_preserving_epochs_and_stats()
    {
        var target = MakeTarget();
        var state = new TraceState(target, 30);
        state.RecordProbe(5, ExpiredAt("192.0.2.5"));
        state.RecordProbe(5, ExpiredAt("192.0.2.5"));
        state.RecordProbe(6, ExpiredAt("192.0.2.6"));
        state.RecordProbe(8, ReachedTarget(target));
        Assert.Equal(8, state.Snapshot(DateTimeOffset.UnixEpoch).Hops.Length);

        long epochBefore = state.GetEpoch(6);
        int sentBefore = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[5].Stats.Sent;

        state.RecordProbe(5, ReachedTarget(target));

        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.Equal(5, state.DestinationTtl);
        Assert.Equal(5, snapshot.Hops.Length);
        Assert.True(snapshot.DestinationReached);
        // The nearer confirmation replaces hop 5's responder (new epoch by
        // design); the shrink itself must not wipe sibling rows.
        Assert.Equal(target.Address, snapshot.Hops[4].Address);
        Assert.Equal(epochBefore, state.GetEpoch(6));

        // On invalidation the trimmed-away rows come back with stats intact.
        state.RecordProbe(5, Silent());
        var regrown = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.False(regrown.DestinationReached);
        Assert.Equal(8, regrown.Hops.Length);
        Assert.Equal(epochBefore, state.GetEpoch(6));
        Assert.Equal(sentBefore, regrown.Hops[5].Stats.Sent);
        Assert.Equal(IPAddress.Parse("192.0.2.6"), regrown.Hops[5].Address);
    }

    [Fact]
    public void Invalidation_reverts_to_trimming_rules_without_erasing_retained_rows()
    {
        var target = MakeTarget();
        var state = new TraceState(target, 8);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        state.RecordProbe(2, ReachedTarget(target));
        state.RecordProbe(3, ExpiredAt("192.0.2.3"));
        Assert.Equal(2, state.Snapshot(DateTimeOffset.UnixEpoch).Hops.Length);

        state.RecordProbe(2, Silent());

        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.False(snapshot.DestinationReached);
        // Retained identities grow the route length back organically.
        Assert.Equal(3, snapshot.Hops.Length);
        Assert.Equal(target.Address, snapshot.Hops[1].Address);
        Assert.Equal(IPAddress.Parse("192.0.2.3"), snapshot.Hops[2].Address);
    }

    [Fact]
    public void Silent_again_hop_with_retained_identity_never_retrims()
    {
        var state = NewState(hopLimit: 8);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        state.RecordProbe(2, ExpiredAt("192.0.2.2"));
        state.RecordProbe(3, ExpiredAt("192.0.2.3"));
        Assert.Equal(3, state.Snapshot(DateTimeOffset.UnixEpoch).Hops.Length);

        state.RecordProbe(3, Silent());
        state.RecordProbe(2, Silent());

        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.Equal(3, snapshot.Hops.Length);
        Assert.Equal(IPAddress.Parse("192.0.2.2"), snapshot.Hops[1].Address);
        Assert.Equal(IPAddress.Parse("192.0.2.3"), snapshot.Hops[2].Address);
    }

    [Fact]
    public void Destination_at_ceiling_keeps_the_row_with_both_flags()
    {
        var target = MakeTarget();
        var state = new TraceState(target, 3);
        state.RecordProbe(1, ExpiredAt("192.0.2.1"));
        state.RecordProbe(2, ExpiredAt("192.0.2.2"));
        state.RecordProbe(3, ReachedTarget(target));

        var snapshot = state.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.Equal(3, snapshot.Hops.Length);
        Assert.Equal(target.Address, snapshot.Hops[2].Address);
        Assert.True(snapshot.DestinationReached);
        Assert.True(snapshot.HopLimitObserved);
        Assert.False(snapshot.IsTruncated);
    }
}
