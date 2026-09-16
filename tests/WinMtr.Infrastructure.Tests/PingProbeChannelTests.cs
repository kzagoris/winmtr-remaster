using System.Net;
using System.Net.NetworkInformation;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Infrastructure;

namespace WinMtr.Infrastructure.Tests;

/// <summary>
/// Deterministic lifecycle and mapping tests. The ping transmission is faked,
/// so no test here touches the network. The integration class below uses
/// real loopback and external ICMP probes.
/// </summary>
public class PingProbeChannelLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Recorder
    {
        public readonly List<(IPAddress Dest, int Ttl, byte[]? Buffer, TimeSpan Timeout)> Calls = new();
    }

    private static PingProbeChannel WithSender(
        Recorder recorder,
        Func<byte[]?, PingSendResult> respond)
    {
        return new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            recorder.Calls.Add((dest, ttl, buffer, timeout));
            return Task.FromResult(respond(buffer));
        });
    }

    [Fact]
    public async Task Initialize_reports_supported_when_loopback_accepts_custom_payload()
    {
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(IPStatus.Success, IPAddress.Loopback));

        var caps = await ch.InitializeAsync(CancellationToken.None);

        Assert.Equal(PayloadSupport.Supported, caps.PayloadSupport);
        Assert.True(caps.SupportsCustomPayloadSize);
        Assert.True(ch.Capabilities.SupportsCustomPayloadSize);
        Assert.Single(recorder.Calls);
        Assert.NotNull(recorder.Calls[0].Buffer);

        // The capability probe is bounded, not an open-ended wait.
        Assert.Equal(TimeSpan.FromSeconds(2), recorder.Calls[0].Timeout);

        // Success is sticky: no re-probe.
        var again = await ch.InitializeAsync(CancellationToken.None);
        Assert.Equal(PayloadSupport.Supported, again.PayloadSupport);
        Assert.True(again.SupportsCustomPayloadSize);
        Assert.Single(recorder.Calls);
    }

    [Fact]
    public async Task Initialize_reports_undetermined_when_loopback_probe_times_out()
    {
        // A filtered loopback returns no exception, but a timeout proves
        // nothing about payload support. It must not be mistaken for proof of
        // support, but it is a valid probe answer, so preparation completes as
        // undetermined instead of failing.
        var recorder = new Recorder();
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            recorder.Calls.Add((dest, ttl, buffer, timeout));
            return Task.FromResult(new PingSendResult(IPStatus.TimedOut, null));
        });

        var caps = await ch.InitializeAsync(CancellationToken.None);

        Assert.Equal(PayloadSupport.Undetermined, caps.PayloadSupport);
        Assert.False(caps.SupportsCustomPayloadSize);
        Assert.Equal(PayloadSupport.Undetermined, ch.Capabilities.PayloadSupport);
        Assert.False(ch.Capabilities.SupportsCustomPayloadSize);
        Assert.Single(recorder.Calls);
        Assert.NotNull(recorder.Calls[0].Buffer);

        // Undetermined is settled: no re-probe.
        var again = await ch.InitializeAsync(CancellationToken.None);
        Assert.Equal(PayloadSupport.Undetermined, again.PayloadSupport);
        Assert.Single(recorder.Calls);
    }

    [Fact]
    public async Task Undetermined_channel_sends_the_default_payload()
    {
        var recorder = new Recorder();
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            recorder.Calls.Add((dest, ttl, buffer, timeout));
            if (dest.Equals(IPAddress.Loopback))
                return Task.FromResult(new PingSendResult(IPStatus.TimedOut, null));
            return Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Parse("1.1.1.1")));
        });
        var caps = await ch.InitializeAsync(CancellationToken.None);
        Assert.Equal(PayloadSupport.Undetermined, caps.PayloadSupport);
        recorder.Calls.Clear();

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 64, payloadBytes: 128, Timeout,
            CancellationToken.None);

        Assert.Equal(ProbeOutcome.Reached, r.Outcome);
        var sent = Assert.Single(recorder.Calls);
        Assert.Null(sent.Buffer);
    }

    [Fact]
    public async Task Concurrent_initializations_probe_only_once()
    {
        int sends = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ch = new PingProbeChannel(async (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref sends);
            await gate.Task;
            return new PingSendResult(IPStatus.Success, IPAddress.Loopback);
        });

        Task<ProbeCapabilities> first = ch.InitializeAsync(CancellationToken.None);
        Task<ProbeCapabilities> second = ch.InitializeAsync(CancellationToken.None);
        gate.TrySetResult();
        ProbeCapabilities[] settled = await Task.WhenAll(first, second);

        Assert.Equal(PayloadSupport.Supported, settled[0].PayloadSupport);
        Assert.Equal(PayloadSupport.Supported, settled[1].PayloadSupport);
        Assert.True(settled[0].SupportsCustomPayloadSize);
        Assert.True(settled[1].SupportsCustomPayloadSize);
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task Initialize_reports_restricted_and_verifies_fallback()
    {
        var recorder = new Recorder();
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            recorder.Calls.Add((dest, ttl, buffer, timeout));
            if (buffer is not null)
            {
                throw new PlatformNotSupportedException(
                    "Unable to send custom ping payload.");
            }

            return Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback));
        });

        var caps = await ch.InitializeAsync(CancellationToken.None);

        Assert.Equal(PayloadSupport.Restricted, caps.PayloadSupport);
        Assert.False(caps.SupportsCustomPayloadSize);
        Assert.Equal(PayloadSupport.Restricted, ch.Capabilities.PayloadSupport);
        Assert.False(ch.Capabilities.SupportsCustomPayloadSize);

        // Restriction triggered the fallback, and the fallback used the
        // default (null) payload.
        Assert.Equal(2, recorder.Calls.Count);
        Assert.NotNull(recorder.Calls[0].Buffer);
        Assert.Null(recorder.Calls[1].Buffer);
        Assert.Equal(IPAddress.Loopback, recorder.Calls[0].Dest);

        // Settled: no re-probe.
        await ch.InitializeAsync(CancellationToken.None);
        Assert.Equal(2, recorder.Calls.Count);
    }

    [Fact]
    public async Task Initialize_propagates_cancellation()
    {
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(IPStatus.Success, IPAddress.Loopback));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ch.InitializeAsync(cts.Token));

        Assert.Empty(recorder.Calls);

        // Cancellation settles nothing: capabilities stay uninitialized and a
        // later call may retry.
        Assert.Throws<InvalidOperationException>(() => ch.Capabilities);
        var caps = await ch.InitializeAsync(CancellationToken.None);
        Assert.True(caps.SupportsCustomPayloadSize);
    }

    [Fact]
    public async Task Initialize_fails_with_diagnostic_on_unrelated_failure()
    {
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
            Task.FromException<PingSendResult>(
                new PingException("An exception occurred during a Ping request.")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ch.InitializeAsync(CancellationToken.None));

        // Neither confirmed support nor confirmed restriction: the failure is
        // reported, and capabilities stay uninitialized.
        Assert.IsType<PingException>(ex.InnerException);
        Assert.Throws<InvalidOperationException>(() => ch.Capabilities);
    }

    [Fact]
    public async Task Initialize_fails_when_the_verified_fallback_fails()
    {
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
            buffer is not null
                ? Task.FromException<PingSendResult>(
                    new PlatformNotSupportedException("Unable to send custom ping payload."))
                : Task.FromException<PingSendResult>(
                    new PingException("An exception occurred during a Ping request.")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ch.InitializeAsync(CancellationToken.None));

        Assert.IsType<PingException>(ex.InnerException);
        Assert.Throws<InvalidOperationException>(() => ch.Capabilities);
    }

    [Fact]
    public async Task Send_requires_initialization()
    {
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(IPStatus.Success, IPAddress.Loopback));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ch.SendAsync(IPAddress.Loopback, 1, 64, Timeout, CancellationToken.None));
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public void Capabilities_throws_before_initialization()
    {
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(IPStatus.Success, IPAddress.Loopback));

        Assert.Throws<InvalidOperationException>(() => ch.Capabilities);
    }

    [Fact]
    public async Task Send_measures_rtt_instead_of_reading_the_reply()
    {
        // A fake sender that takes ~50ms of wall time: a returned value near
        // the wall clock cannot have come from PingReply.RoundtripTime, which
        // is zeroed for non-Success statuses on both platforms.
        var ch = new PingProbeChannel(async (dest, ttl, buffer, timeout, ct) =>
        {
            if (dest.Equals(IPAddress.Loopback))
                return new PingSendResult(IPStatus.Success, IPAddress.Loopback);
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            return new PingSendResult(IPStatus.TimeExceeded, IPAddress.Parse("10.0.0.1"));
        });
        await ch.InitializeAsync(CancellationToken.None);

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Expired, r.Outcome);
        Assert.InRange(r.RttMs, 40, 5000);
    }

    [Theory]
    [InlineData(IPStatus.TtlExpired)]
    [InlineData(IPStatus.TimeExceeded)]
    public async Task Send_maps_expired_status_and_responder(IPStatus status)
    {
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(status, IPAddress.Parse("10.0.0.1")));
        await ch.InitializeAsync(CancellationToken.None);
        recorder.Calls.Clear();

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Expired, r.Outcome);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), r.Responder);
        Assert.True(r.RttMs >= 0);
    }

    [Fact]
    public async Task Send_zeroes_rtt_and_nulls_any_for_timeouts()
    {
        var recorder = new Recorder();
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            recorder.Calls.Add((dest, ttl, buffer, timeout));
            // Initialization loopback succeeds; real sends time out.
            if (dest.Equals(IPAddress.Loopback))
                return Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback));
            return Task.FromResult(new PingSendResult(IPStatus.TimedOut, IPAddress.Any));
        });
        await ch.InitializeAsync(CancellationToken.None);

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.TimedOut, r.Outcome);
        Assert.Null(r.Responder);
        Assert.Equal(0, r.RttMs);
    }

    [Fact]
    public async Task Send_observes_a_precanceled_token_before_anything_else()
    {
        // Uninitialized channel + canceled token must surface cancellation,
        // proving ThrowIfCancellationRequested runs before the init guard.
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(IPStatus.Success, IPAddress.Loopback));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ch.SendAsync(IPAddress.Parse("1.1.1.1"), 1, 64, Timeout, cts.Token));
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task Send_reports_an_unexpected_runtime_restriction_explicitly()
    {
        var calls = 0;
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            calls++;
            // Initialization probe succeeds; every later custom send is
            // unexpectedly restricted.
            if (calls == 1)
            {
                return Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback));
            }

            throw new PlatformNotSupportedException("Unable to send custom ping payload.");
        });
        var caps = await ch.InitializeAsync(CancellationToken.None);
        Assert.True(caps.SupportsCustomPayloadSize);

        // Reported explicitly, not silently retried with the default payload.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ch.SendAsync(
                IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout,
                CancellationToken.None));
        Assert.IsType<PlatformNotSupportedException>(ex.InnerException);

        // Capabilities stay as settled: no silent flip.
        Assert.True(ch.Capabilities.SupportsCustomPayloadSize);
    }

    [Fact]
    public async Task Supported_channel_sends_ascii_space_payload_of_the_requested_size()
    {
        var recorder = new Recorder();
        var ch = WithSender(recorder,
            _ => new PingSendResult(IPStatus.Success, IPAddress.Parse("1.1.1.1")));
        await ch.InitializeAsync(CancellationToken.None);
        recorder.Calls.Clear();

        await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 64, payloadBytes: 128, Timeout,
            CancellationToken.None);

        var sent = Assert.Single(recorder.Calls);
        Assert.NotNull(sent.Buffer);
        Assert.Equal(128, sent.Buffer.Length);
        Assert.All(sent.Buffer, b => Assert.Equal(32, b));
    }

    [Fact]
    public async Task Restricted_channel_sends_the_default_payload()
    {
        var recorder = new Recorder();
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
        {
            recorder.Calls.Add((dest, ttl, buffer, timeout));
            if (buffer is not null)
            {
                throw new PlatformNotSupportedException("Unable to send custom ping payload.");
            }

            return Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Parse("1.1.1.1")));
        });
        await ch.InitializeAsync(CancellationToken.None);
        recorder.Calls.Clear();

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 64, payloadBytes: 128, Timeout,
            CancellationToken.None);

        Assert.Equal(ProbeOutcome.Reached, r.Outcome);
        var sent = Assert.Single(recorder.Calls);
        Assert.Null(sent.Buffer);
    }

    [Theory]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeOutcome.Unreachable, ProbeErrorReason.NetworkUnreachable)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeOutcome.Unreachable, ProbeErrorReason.HostUnreachable)]
    [InlineData(IPStatus.DestinationProtocolUnreachable, ProbeOutcome.Unreachable, ProbeErrorReason.ProtocolUnreachable)]
    [InlineData(IPStatus.DestinationPortUnreachable, ProbeOutcome.Unreachable, ProbeErrorReason.PortUnreachable)]
    [InlineData(IPStatus.NoResources, ProbeOutcome.Failed, ProbeErrorReason.InsufficientResources)]
    [InlineData(IPStatus.BadOption, ProbeOutcome.Failed, ProbeErrorReason.BadOption)]
    [InlineData(IPStatus.HardwareError, ProbeOutcome.Failed, ProbeErrorReason.HardwareError)]
    [InlineData(IPStatus.PacketTooBig, ProbeOutcome.Failed, ProbeErrorReason.PacketTooBig)]
    [InlineData(IPStatus.BadRoute, ProbeOutcome.Failed, ProbeErrorReason.BadRoute)]
    [InlineData(IPStatus.TtlReassemblyTimeExceeded, ProbeOutcome.Failed, ProbeErrorReason.ReassemblyTimeExceeded)]
    [InlineData(IPStatus.ParameterProblem, ProbeOutcome.Failed, ProbeErrorReason.ParameterProblem)]
    [InlineData(IPStatus.SourceQuench, ProbeOutcome.Failed, ProbeErrorReason.SourceQuench)]
    [InlineData(IPStatus.BadDestination, ProbeOutcome.Failed, ProbeErrorReason.BadDestination)]
    public async Task Send_preserves_the_specific_error_reason(
        IPStatus status, ProbeOutcome outcome, ProbeErrorReason reason)
    {
        var responder = IPAddress.Parse("10.0.0.9");
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
            dest.Equals(IPAddress.Loopback)
                ? Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback))
                : Task.FromResult(new PingSendResult(status, responder)));
        await ch.InitializeAsync(CancellationToken.None);

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

        Assert.Equal(outcome, r.Outcome);
        Assert.Equal(reason, r.ErrorReason);
        // An error reply still identifies its responder.
        Assert.Equal(responder, r.Responder);
        Assert.Equal(0, r.RttMs);
    }

    [Theory]
    [InlineData(IPStatus.Success, ProbeOutcome.Reached)]
    [InlineData(IPStatus.TtlExpired, ProbeOutcome.Expired)]
    [InlineData(IPStatus.TimeExceeded, ProbeOutcome.Expired)]
    [InlineData(IPStatus.TimedOut, ProbeOutcome.TimedOut)]
    public async Task Send_carries_no_error_reason_for_non_error_statuses(
        IPStatus status, ProbeOutcome outcome)
    {
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
            dest.Equals(IPAddress.Loopback)
                ? Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback))
                : Task.FromResult(new PingSendResult(status, IPAddress.Parse("10.0.0.9"))));
        await ch.InitializeAsync(CancellationToken.None);

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

        Assert.Equal(outcome, r.Outcome);
        Assert.Null(r.ErrorReason);
    }

    [Theory]
    [InlineData(IPStatus.Unknown)]
    [InlineData(IPStatus.DestinationUnreachable)]
    [InlineData(IPStatus.BadHeader)]
    [InlineData(IPStatus.UnrecognizedNextHeader)]
    [InlineData(IPStatus.IcmpError)]
    [InlineData(IPStatus.DestinationScopeMismatch)]
    public async Task Send_maps_unrecognized_status_to_the_unknown_reason(IPStatus status)
    {
        var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
            dest.Equals(IPAddress.Loopback)
                ? Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback))
                : Task.FromResult(new PingSendResult(status, IPAddress.Parse("10.0.0.9"))));
        await ch.InitializeAsync(CancellationToken.None);

        var r = await ch.SendAsync(
            IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Failed, r.Outcome);
        Assert.Equal(ProbeErrorReason.Unknown, r.ErrorReason);
    }

    [Fact]
    public async Task Send_maps_local_stack_rejections_to_local_failure_without_internals()
    {
        foreach (Exception failure in new Exception[]
                 {
                     new PingException("An exception occurred during a Ping request."),
                     new ArgumentException("ttl"),
                 })
        {
            var ch = new PingProbeChannel((dest, ttl, buffer, timeout, ct) =>
                dest.Equals(IPAddress.Loopback)
                    ? Task.FromResult(new PingSendResult(IPStatus.Success, IPAddress.Loopback))
                    : Task.FromException<PingSendResult>(failure));
            await ch.InitializeAsync(CancellationToken.None);

            var r = await ch.SendAsync(
                IPAddress.Parse("1.1.1.1"), ttl: 1, payloadBytes: 64, Timeout, CancellationToken.None);

            Assert.Equal(ProbeOutcome.Failed, r.Outcome);
            Assert.Equal(ProbeErrorReason.LocalFailure, r.ErrorReason);
            Assert.Null(r.Responder);
            Assert.Equal(0, r.RttMs);
        }
    }
}

[Trait("Category", "Integration")]
public class PingProbeChannelTests
{
    private static readonly IPAddress Dest = IPAddress.Parse("1.1.1.1");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task<PingProbeChannel> ReadyChannelAsync()
    {
        var ch = new PingProbeChannel();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ch.InitializeAsync(cts.Token);
        return ch;
    }

    [Fact]
    public async Task Ttl_one_is_answered_by_a_router_that_is_not_the_target()
    {
        var ch = await ReadyChannelAsync();
        var r = await ch.SendAsync(Dest, ttl: 1, payloadBytes: 64, Timeout, default);

        Assert.Equal(ProbeOutcome.Expired, r.Outcome);
        Assert.NotNull(r.Responder);
        Assert.NotEqual(Dest, r.Responder);
    }

    [Fact]
    public async Task Rtt_is_measured_not_read_from_reply()
    {
        // Spike finding 2: PingReply.RoundtripTime is 0 for every non-Success
        // status on both Windows and Linux. If this channel returned that
        // value, every intermediate hop would report 0 ms.
        //
        // `RttMs >= 0` would be vacuously true and would pass against exactly
        // the broken implementation this guards. Bound it against an
        // independently measured elapsed time instead: a returned value that
        // tracks the wall clock cannot have come from the zeroed reply field
        // unless the probe genuinely took under a millisecond, which a remote
        // hop does not.
        var ch = await ReadyChannelAsync();

        var outer = System.Diagnostics.Stopwatch.StartNew();
        var r = await ch.SendAsync(Dest, ttl: 8, payloadBytes: 64, Timeout, default);
        outer.Stop();

        Assert.True(r.Outcome is ProbeOutcome.Expired or ProbeOutcome.Reached,
                    $"expected a reply from a hop 8 away, got {r.Outcome}");
        Assert.InRange(r.RttMs, 0, (int)outer.ElapsedMilliseconds + 50);
        Assert.True(r.RttMs > 0,
                    "a remote hop reported 0 ms, which means RoundtripTime was used instead of a measurement");
    }

    [Fact]
    public async Task High_ttl_reaches_the_target()
    {
        var ch = await ReadyChannelAsync();
        var r = await ch.SendAsync(Dest, ttl: 64, payloadBytes: 64, Timeout, default);

        Assert.Equal(ProbeOutcome.Reached, r.Outcome);
        Assert.Equal(Dest, r.Responder);
    }

    [Fact]
    public async Task Custom_payload_is_used_when_supported_and_skipped_otherwise()
    {
        // Spike finding 3: an unprivileged Linux process cannot send a custom
        // payload. Initialization settles this up front; the probe must
        // degrade to the default payload rather than throw
        // PlatformNotSupportedException at probe time.
        var ch = await ReadyChannelAsync();
        var r = await ch.SendAsync(Dest, ttl: 64, payloadBytes: 128, Timeout, default);

        Assert.Equal(ProbeOutcome.Reached, r.Outcome);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(ch.Capabilities.SupportsCustomPayloadSize);
        }
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        var ch = await ReadyChannelAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ch.SendAsync(Dest, 1, 64, Timeout, cts.Token));
    }
}
