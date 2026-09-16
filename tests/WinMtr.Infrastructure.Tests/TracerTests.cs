using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Core.Tracing;
using WinMtr.Infrastructure;

namespace WinMtr.Infrastructure.Tests;

public sealed class TracerTests
{
    private static readonly IPAddress TargetAddr = IPAddress.Parse("203.0.113.9");

    private static Target MakeTarget(string host = "example.com")
    {
        var expression = TargetExpression.Parse(host);
        return new Target(expression, TargetAddr, "example.com");
    }

    private static ProbeSettings TestSettings(int hopLimit = 2) => new(
        TimeSpan.FromSeconds(1),
        PayloadBytes: 64,
        hopLimit,
        ReplyTimeout: TimeSpan.FromSeconds(1),
        ResolveNames: true,
        SnapshotInterval: TimeSpan.FromMilliseconds(10));

    [Theory]
    [InlineData("999.999.999.999")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_host_fails_before_any_network_activity(string host)
    {
        var resolveCalled = false;
        var initCalled = false;
        var tracer = new Tracer(
            (expr, ct) => { resolveCalled = true; return Task.FromResult(MakeTarget()); },
            () => new CountingChannel(),
            (ch, ct) => { initCalled = true; return Task.FromResult(ch.Capabilities); },
            () => new NullNameResolver(),
            null, null, null);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => tracer.CreateTraceAsync(host, TestSettings()));
        Assert.Equal("host", ex.ParamName);
        Assert.False(resolveCalled);
        Assert.False(initCalled);
    }

    [Fact]
    public async Task Invalid_settings_fail_before_resolution_or_initialization()
    {
        var resolveCalled = false;
        var initCalled = false;
        var tracer = new Tracer(
            (expr, ct) => { resolveCalled = true; return Task.FromResult(MakeTarget()); },
            () => new CountingChannel(),
            (ch, ct) => { initCalled = true; return Task.FromResult(ch.Capabilities); },
            () => new NullNameResolver(),
            null, null, null);

        var bad = ProbeSettings.Default with { HopLimit = 0 };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tracer.CreateTraceAsync("example.com", bad));
        Assert.False(resolveCalled);
        Assert.False(initCalled);
    }

    [Fact]
    public async Task Default_settings_reach_the_engine_when_omitted()
    {
        Target? seenTarget = null;
        ProbeSettings? seenSettings = null;
        CancellationToken seenToken = default;
        var expectedTarget = MakeTarget();
        var caps = new ProbeCapabilities(PayloadSupport.Supported);
        var channel = new CountingChannel();

        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(expectedTarget),
            () => channel,
            (ch, ct) => Task.FromResult(caps),
            () => new NullNameResolver(),
            null, null,
            (ch, resolver, clock) => new CapturingEngine((t, s, ct) =>
            {
                seenTarget = t;
                seenSettings = s;
                seenToken = ct;
                return EmptyStream();
            }));

        using var cts = new CancellationTokenSource();
        var prepared = await tracer.CreateTraceAsync("example.com", null, cts.Token);

        // CapturingEngine records during CreateTraceAsync when Tracer calls
        // RunAsync to obtain the stream; no enumeration needed.
        Assert.Equal(ProbeSettings.Default, seenSettings);
        Assert.Same(expectedTarget, seenTarget);
        Assert.Equal(cts.Token, seenToken);
        Assert.Same(expectedTarget, prepared.Target);
        Assert.Same(caps, prepared.Capabilities);
    }

    [Fact]
    public async Task Explicit_settings_reach_the_engine_unchanged()
    {
        ProbeSettings? seen = null;
        var expectedTarget = MakeTarget();
        var settings = TestSettings(hopLimit: 5) with { PayloadBytes = 128 };
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(expectedTarget),
            () => new CountingChannel(),
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            null, null,
            (ch, resolver, clock) => new CapturingEngine((t, s, ct) =>
            {
                seen = s;
                return EmptyStream();
            }));

        await tracer.CreateTraceAsync("example.com", settings);
        Assert.Same(settings, seen);
    }

    [Fact]
    public async Task Resolution_precedes_initialization_and_metadata_matches()
    {
        var order = new List<string>();
        var expectedTarget = MakeTarget();
        var caps = new ProbeCapabilities(PayloadSupport.Restricted);
        var channel = new CountingChannel();

        var tracer = new Tracer(
            async (expr, ct) =>
            {
                order.Add("resolve");
                await Task.Yield();
                return expectedTarget;
            },
            () =>
            {
                order.Add("create-channel");
                return channel;
            },
            async (ch, ct) =>
            {
                order.Add("init");
                await Task.Yield();
                Assert.Same(channel, ch);
                return caps;
            },
            () => new NullNameResolver(),
            null, null,
            (ch, resolver, clock) => new CapturingEngine((t, s, ct) => EmptyStream()));

        var prepared = await tracer.CreateTraceAsync("example.com", TestSettings());

        Assert.Equal(["resolve", "create-channel", "init"], order);
        Assert.Same(expectedTarget, prepared.Target);
        Assert.Same(caps, prepared.Capabilities);
    }

    [Fact]
    public async Task Resolution_failure_starts_no_workers_and_calls_no_init()
    {
        var initCalled = false;
        var channel = new CountingChannel();
        var failure = new InvalidOperationException("dns down");
        var tracer = new Tracer(
            (expr, ct) => Task.FromException<Target>(failure),
            () => channel,
            (ch, ct) => { initCalled = true; return Task.FromResult(ch.Capabilities); },
            () => new NullNameResolver(),
            null, null, null);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracer.CreateTraceAsync("example.com", TestSettings()));
        Assert.Same(failure, thrown);
        Assert.False(initCalled);
        Assert.Equal(0, channel.TotalProbes);
    }

    [Fact]
    public async Task Initialization_failure_returns_no_result_and_starts_no_workers()
    {
        var channel = new CountingChannel();
        var failure = new InvalidOperationException("init failed");
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget()),
            () => channel,
            (ch, ct) => Task.FromException<ProbeCapabilities>(failure),
            () => new NullNameResolver(),
            null, null, null);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracer.CreateTraceAsync("example.com", TestSettings()));
        Assert.Same(failure, thrown);
        Assert.Equal(0, channel.TotalProbes);
    }

    [Fact]
    public async Task Precancelled_caller_token_remains_cancellation()
    {
        var resolveCalled = false;
        var tracer = new Tracer(
            (expr, ct) => { resolveCalled = true; return Task.FromResult(MakeTarget()); },
            () => new CountingChannel(),
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            null, null, null);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracer.CreateTraceAsync("example.com", TestSettings(), cts.Token));
        Assert.False(resolveCalled);
    }

    [Fact]
    public async Task Caller_cancellation_during_resolution_propagates()
    {
        using var cts = new CancellationTokenSource();
        var tracer = new Tracer(
            async (expr, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                return MakeTarget();
            },
            () => new CountingChannel(),
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            null, null, null);

        Task<PreparedTrace> pending = tracer.CreateTraceAsync("example.com", TestSettings(), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Initialization_timeout_becomes_TimeoutException_with_inner_cancellation()
    {
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget()),
            () => new CountingChannel(),
            async (ch, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ch.Capabilities;
            },
            () => new NullNameResolver(),
            null,
            TimeSpan.FromMilliseconds(20),
            null);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => tracer.CreateTraceAsync("example.com", TestSettings()));
        Assert.Contains("probe initialization", ex.Message);
        Assert.Contains("seconds", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_takes_precedence_over_init_deadline()
    {
        using var cts = new CancellationTokenSource();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget()),
            () => new CountingChannel(),
            async (ch, ct) =>
            {
                // Observe the linked token: it fires for both caller cancel and deadline.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ch.Capabilities;
            },
            () => new NullNameResolver(),
            null,
            TimeSpan.FromMilliseconds(20),
            null);

        Task<PreparedTrace> pending = tracer.CreateTraceAsync("example.com", TestSettings(), cts.Token);
        await cts.CancelAsync();
        // Caller cancellation must propagate through the linked initialization token.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Init_deadline_is_detached_after_successful_preparation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var channel = new CountingChannel();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget()),
            () => channel,
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            clock,
            TimeSpan.FromMilliseconds(50),
            null);

        var prepared = await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 1));
        // Wait past the init deadline using real time; the running trace must survive it.
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        await using var enumerator = prepared.Snapshots.GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await channel.WaitForCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(channel.TotalProbes > 0);
    }

    [Fact]
    public async Task Cancellation_after_successful_init_still_fails_preparation()
    {
        using var cts = new CancellationTokenSource();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget()),
            () => new CountingChannel(),
            (ch, ct) =>
            {
                // Simulate Ctrl+C landing in the window after init succeeds
                // but before the engine is handed the caller token.
                cts.Cancel();
                return Task.FromResult(ch.Capabilities);
            },
            () => new NullNameResolver(),
            null, null, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracer.CreateTraceAsync("example.com", TestSettings(), cts.Token));
    }

    [Fact]
    public async Task Unrelated_init_cancellation_is_not_relabeled_as_timeout()
    {
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget()),
            () => new CountingChannel(),
            (ch, ct) => Task.FromException<ProbeCapabilities>(
                new OperationCanceledException("unrelated cancel")),
            () => new NullNameResolver(),
            null,
            TimeSpan.FromMilliseconds(50),
            null);

        // The init token never fired (its CTS is not canceled); the OCE must
        // propagate as-is, not as TimeoutException.
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => tracer.CreateTraceAsync("example.com", TestSettings()));
        Assert.Equal("unrelated cancel", ex.Message);
    }

    [Fact]
    public async Task Preparation_sends_no_route_probes_enumeration_does()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var channel = new CountingChannel();
        var expected = MakeTarget("127.0.0.1");
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(expected),
            () => channel,
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            clock, null, null);

        var prepared = await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 2));
        Assert.Equal(0, channel.TotalProbes);
        Assert.Same(expected, prepared.Target);

        await using var enumerator = prepared.Snapshots.GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await channel.WaitForCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(expected, enumerator.Current.Target);
        Assert.True(channel.TotalProbes > 0);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_engine_cleanup()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var channel = new CountingChannel();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget("127.0.0.1")),
            () => channel,
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            clock, null, null);

        using var cts = new CancellationTokenSource();
        var prepared = await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 1), cts.Token);
        await using var enumerator = prepared.Snapshots.GetAsyncEnumerator();

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await channel.WaitForCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));

        int countBefore = channel.TotalProbes;
        Task<bool> final = enumerator.MoveNextAsync().AsTask();
        await cts.CancelAsync();
        Assert.True(await final.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(countBefore, channel.TotalProbes);
    }

    [Fact]
    public async Task WithCancellation_reaches_engine_cleanup()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var channel = new CountingChannel();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget("127.0.0.1")),
            () => channel,
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            clock, null, null);

        var prepared = await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 1));
        using var cts = new CancellationTokenSource();
        // Enumerator token path: the same token WithCancellation forwards to
        // GetAsyncEnumerator. Cancelling it must reach engine cleanup.
        await using var enumerator = prepared.Snapshots.GetAsyncEnumerator(cts.Token);

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await channel.WaitForCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));

        Task<bool> pending = enumerator.MoveNextAsync().AsTask();
        await cts.CancelAsync();
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        int countAfterCancel = channel.TotalProbes;
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        Assert.Equal(countAfterCancel, channel.TotalProbes);
    }

    [Fact]
    public async Task Early_enumeration_exit_cleans_up_workers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var channel = new CountingChannel();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget("127.0.0.1")),
            () => channel,
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () => new NullNameResolver(),
            clock, null, null);

        var prepared = await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 1));
        var enumerator = prepared.Snapshots.GetAsyncEnumerator();
        Task<bool> pending = enumerator.MoveNextAsync().AsTask();
        await channel.WaitForCountAsync(1);
        await enumerator.DisposeAsync();
        int countAfterDispose = channel.TotalProbes;
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(countAfterDispose, channel.TotalProbes);
    }

    [Fact]
    public async Task Each_call_creates_its_own_channel_and_resolver()
    {
        var channels = new ConcurrentBag<IProbeChannel>();
        var resolvers = new ConcurrentBag<INameResolver>();
        var tracer = new Tracer(
            (expr, ct) => Task.FromResult(MakeTarget("127.0.0.1")),
            () =>
            {
                var ch = new CountingChannel();
                channels.Add(ch);
                return ch;
            },
            (ch, ct) => Task.FromResult(ch.Capabilities),
            () =>
            {
                var r = new NullNameResolver();
                resolvers.Add(r);
                return r;
            },
            new FakeTimeProvider(DateTimeOffset.UnixEpoch), null, null);

        await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 1));
        await tracer.CreateTraceAsync("127.0.0.1", TestSettings(hopLimit: 1));

        Assert.Equal(2, channels.Count);
        Assert.Equal(2, resolvers.Count);
        Assert.Distinct(channels);
        Assert.Distinct(resolvers);
    }

    private static async IAsyncEnumerable<Route> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class CountingChannel : IProbeChannel
    {
        private int _count;

        public ProbeCapabilities Capabilities { get; } = new(PayloadSupport.Supported);

        public int TotalProbes => Volatile.Read(ref _count);

        public Task<ProbeResult> SendAsync(IPAddress dest, int ttl, int payloadBytes, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            return Task.FromResult(new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        }

        public async Task WaitForCountAsync(int count)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (TotalProbes < count)
                await Task.Delay(TimeSpan.FromMilliseconds(1), budget.Token);
        }
    }

    private sealed class NullNameResolver : INameResolver
    {
        public Task<string?> ResolveAsync(IPAddress address, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class CapturingEngine(Func<Target, ProbeSettings, CancellationToken, IAsyncEnumerable<Route>> run) : ITraceEngine
    {
        public IAsyncEnumerable<Route> RunAsync(Target target, ProbeSettings settings, CancellationToken ct) =>
            run(target, settings, ct);
    }
}
