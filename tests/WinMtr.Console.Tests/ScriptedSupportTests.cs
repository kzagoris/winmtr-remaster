using System.Net;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.TestSupport;

namespace WinMtr.Console.Tests;

/// <summary>
/// Regression checks for the shared deterministic offline tracing support:
/// scripted route snapshots in controlled timestamped sequences (including
/// preparation outcomes, cancellation, and a fault at a chosen point),
/// probe-channel outcomes (silent hops, changing hop identities), and name
/// resolution. The console suite consumes the same seam the front-end trace
/// session will use later, with no live network trace.
/// </summary>
public sealed class ScriptedSupportTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scripted_snapshots_replay_in_timestamped_order()
    {
        var target = RouteScript.TestTarget();
        var snapshots = RouteScript.TimestampedSequence(
            target,
            Start,
            TimeSpan.FromSeconds(1),
            [RouteScript.RespondingHop(0, "192.0.2.1")],
            [RouteScript.RespondingHop(0, "192.0.2.1"), RouteScript.RespondingHop(1, "192.0.2.2")]);
        var engine = new ScriptedTraceEngine(snapshots);

        var seen = new List<Route>();
        await foreach (var snapshot in engine.RunAsync(target, ProbeSettings.Default))
            seen.Add(snapshot);

        Assert.Equal(2, seen.Count);
        Assert.Equal(Start, seen[0].TakenAt);
        Assert.Equal(Start + TimeSpan.FromSeconds(1), seen[1].TakenAt);
        Assert.Single(seen[0].Hops);
        Assert.Equal(2, seen[1].Hops.Length);
        Assert.Same(target, engine.SeenTarget);
        Assert.Equal(ProbeSettings.Default, engine.SeenSettings);
    }

    [Fact]
    public async Task Precancelled_caller_token_yields_zero_snapshots()
    {
        var target = RouteScript.TestTarget();
        var route = RouteScript.Snapshot(target, Start, RouteScript.RespondingHop(0, "192.0.2.1"));
        var engine = new ScriptedTraceEngine([route]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        int count = 0;
        await foreach (var _ in engine.RunAsync(target, ProbeSettings.Default, cts.Token))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Mid_enumeration_cancellation_completes_without_throwing()
    {
        var target = RouteScript.TestTarget();
        var snapshots = RouteScript.TimestampedSequence(
            target, Start, TimeSpan.FromSeconds(1),
            [RouteScript.RespondingHop(0, "192.0.2.1")],
            [RouteScript.RespondingHop(0, "192.0.2.1")]);
        var engine = new ScriptedTraceEngine(snapshots);
        using var cts = new CancellationTokenSource();

        await using var enumerator = engine.RunAsync(target, ProbeSettings.Default, cts.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        await cts.CancelAsync();
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Enumerator_token_cancellation_completes_without_throwing()
    {
        var target = RouteScript.TestTarget();
        var route = RouteScript.Snapshot(target, Start, RouteScript.RespondingHop(0, "192.0.2.1"));
        var engine = new ScriptedTraceEngine([route, route]);
        using var cts = new CancellationTokenSource();

        await using var enumerator = engine.RunAsync(target, ProbeSettings.Default).GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());
        await cts.CancelAsync();
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Fault_after_chosen_point_propagates_original_instance()
    {
        var target = RouteScript.TestTarget();
        var first = RouteScript.Snapshot(target, Start, RouteScript.RespondingHop(0, "192.0.2.1"));
        var second = RouteScript.Snapshot(target, Start + TimeSpan.FromSeconds(1), RouteScript.RespondingHop(0, "192.0.2.1"));
        var fault = new InvalidOperationException("worker boom");
        var engine = new ScriptedTraceEngine([first, second], fault, faultAfterSnapshots: 1);

        await using var enumerator = engine.RunAsync(target, ProbeSettings.Default).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(first, enumerator.Current);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task Fault_at_zero_raises_before_first_snapshot()
    {
        var target = RouteScript.TestTarget();
        var route = RouteScript.Snapshot(target, Start, RouteScript.RespondingHop(0, "192.0.2.1"));
        var fault = new InvalidOperationException("immediate boom");
        var engine = new ScriptedTraceEngine([route], fault, faultAfterSnapshots: 0);

        await using var enumerator = engine.RunAsync(target, ProbeSettings.Default).GetAsyncEnumerator();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task Fault_without_position_raises_after_whole_sequence()
    {
        var target = RouteScript.TestTarget();
        var route = RouteScript.Snapshot(target, Start, RouteScript.RespondingHop(0, "192.0.2.1"));
        var fault = new InvalidOperationException("end boom");
        var engine = new ScriptedTraceEngine([route], fault);

        await using var enumerator = engine.RunAsync(target, ProbeSettings.Default).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(route, enumerator.Current);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task Preparation_resolve_failure_propagates_and_creates_no_channel()
    {
        var failure = new InvalidOperationException("dns down");
        var tracer = new TracerScript
        {
            ResolveFailure = failure,
            ChannelFactory = () => throw new InvalidOperationException("channel must not be created"),
            ResolverFactory = () => throw new InvalidOperationException("resolver must not be created"),
        }.BuildTracer();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracer.CreateTraceAsync("example.com", ProbeSettings.Default));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Preparation_init_failure_propagates()
    {
        var failure = new InvalidOperationException("init failed");
        var tracer = new TracerScript
        {
            Target = RouteScript.TestTarget("127.0.0.1", "127.0.0.1", null),
            InitializeFailure = failure,
        }.BuildTracer();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracer.CreateTraceAsync("127.0.0.1", ProbeSettings.Default));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Probe_channel_replays_silent_then_responding_and_tracks_calls()
    {
        var channel = new ScriptedProbeChannel(
            new ProbeCapabilities(PayloadSupport.Supported),
            [
                new ProbeResult(ProbeOutcome.TimedOut, null, 0),
                new ProbeResult(ProbeOutcome.Expired, IPAddress.Parse("192.0.2.1"), 12),
            ]);
        var dest = IPAddress.Parse("203.0.113.9");

        ProbeResult silent = await channel.SendAsync(dest, 1, 64, TimeSpan.FromSeconds(1), CancellationToken.None);
        ProbeResult responding = await channel.SendAsync(dest, 1, 64, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(silent.Responder);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), responding.Responder);
        Assert.Equal(2, channel.TotalProbes);
        Assert.Equal([1, 1], channel.Calls.Select(c => c.Ttl).ToArray());
    }

    [Fact]
    public async Task Probe_channel_identity_can_change_per_call()
    {
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");
        var channel = new ScriptedProbeChannel(
            new ProbeCapabilities(PayloadSupport.Supported),
            (dest, ttl, _, _, _) => Task.FromResult(
                ttl == 1 && dest.Equals(IPAddress.Parse("203.0.113.9"))
                    ? new ProbeResult(ProbeOutcome.Expired, first, 5)
                    : new ProbeResult(ProbeOutcome.Expired, second, 6)));
        var dest = IPAddress.Parse("203.0.113.9");

        ProbeResult a = await channel.SendAsync(dest, 1, 64, TimeSpan.FromSeconds(1), CancellationToken.None);
        ProbeResult b = await channel.SendAsync(dest, 2, 64, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(first, a.Responder);
        Assert.Equal(second, b.Responder);
    }

    [Fact]
    public async Task Probe_channel_honors_cancellation()
    {
        var channel = new ScriptedProbeChannel(new ProbeCapabilities(PayloadSupport.Supported));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.SendAsync(IPAddress.Parse("203.0.113.9"), 1, 64, TimeSpan.FromSeconds(1), cts.Token));
    }

    [Fact]
    public async Task Name_resolver_maps_names_and_falls_back_to_null()
    {
        var known = IPAddress.Parse("192.0.2.1");
        var unknown = IPAddress.Parse("192.0.2.99");
        var resolver = new ScriptedNameResolver(
            new Dictionary<IPAddress, string?> { [known] = "router.example" });

        Assert.Equal("router.example", await resolver.ResolveAsync(known, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(unknown, CancellationToken.None));
        Assert.Equal([known, unknown], resolver.Calls.ToArray());
    }

    [Fact]
    public async Task Tracer_script_replays_route_snapshots_offline_without_probing()
    {
        var target = RouteScript.TestTarget("127.0.0.1", "127.0.0.1", null);
        var snapshots = RouteScript.TimestampedSequence(
            target, Start, TimeSpan.FromMilliseconds(250),
            [RouteScript.RespondingHop(0, "192.0.2.1")],
            [RouteScript.RespondingHop(0, "192.0.2.1"), RouteScript.SilentHop(1)]);
        var channel = new ScriptedProbeChannel(new ProbeCapabilities(PayloadSupport.Supported));
        var tracer = new TracerScript
        {
            Target = target,
            Channel = channel,
            Resolver = new ScriptedNameResolver(),
            Snapshots = snapshots,
        }.BuildTracer();

        var prepared = await tracer.CreateTraceAsync("127.0.0.1", ProbeSettings.Default);
        var seen = new List<Route>();
        await foreach (var snapshot in prepared.Snapshots)
            seen.Add(snapshot);

        Assert.Equal(2, seen.Count);
        Assert.Equal(Start + TimeSpan.FromMilliseconds(250), seen[1].TakenAt);
        Assert.Equal(0, channel.TotalProbes);
    }

    [Fact]
    public void Changing_hop_identity_across_snapshots_is_observable()
    {
        var target = RouteScript.TestTarget();
        var before = RouteScript.RespondingHop(0, "192.0.2.1");
        var after = RouteScript.RespondingHop(0, "192.0.2.2");

        Assert.NotNull(before.Address);
        Assert.NotNull(after.Address);
        Assert.NotEqual(before.Address, after.Address);
        Assert.NotEmpty(before.Label);
    }

    [Fact]
    public void Silent_hop_keeps_host_empty_and_status_in_status_column()
    {
        var silent = RouteScript.SilentHop(1);

        Assert.Equal(string.Empty, silent.Label);
        Assert.Equal("No response.", silent.StatusDescription);
    }
}
