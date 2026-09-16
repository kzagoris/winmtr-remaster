using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using WinMtr.Core;
using WinMtr.Core.Probing;

namespace WinMtr.Infrastructure;

/// <summary>
/// The result of one ping: status plus responder, without the
/// live <see cref="PingReply"/> wrapper (which has no public constructor and
/// therefore cannot be faked in unit tests).
/// </summary>
internal sealed record PingSendResult(IPStatus Status, IPAddress? Address);

/// <summary>
/// One ping transmission. Production code wraps <see cref="Ping"/>; tests
/// inject a fake so capability discovery is deterministic and network-free.
/// </summary>
internal delegate Task<PingSendResult> PingSender(
    IPAddress dest, int ttl, byte[]? buffer, TimeSpan timeout, CancellationToken ct);

/// <summary>
/// The real ICMP probe. Uses System.Net.NetworkInformation.Ping on every
/// platform. Three platform facts are handled here and nowhere else:
/// <list type="number">
/// <item>Windows reports an expired hop as IPStatus.TtlExpired, Linux as
/// IPStatus.TimeExceeded. ProbeStatusMap folds both into Expired.</item>
/// <item>PingReply.RoundtripTime is 0 unless Status == Success, so the round
/// trip is measured here with a Stopwatch.</item>
/// <item>An unprivileged Linux process cannot send a custom payload; it throws
/// PlatformNotSupportedException.</item>
/// </list>
/// <para>
/// Capability lifecycle: call <see cref="InitializeAsync"/> once before
/// reading <see cref="Capabilities"/> or calling <see cref="SendAsync"/>. It
/// runs a bounded loopback probe with a custom payload; a payload restriction
/// triggers and verifies the default-payload fallback, while a valid but
/// unhelpful probe answer settles as undetermined so preparation can complete.
/// Cancellation propagates; any unrelated failure fails initialization with a
/// diagnostic instead of guessing support either way. After success (including
/// undetermined) the capabilities are stable for the session: a later unexpected
/// restriction is reported explicitly rather than silently changing behaviour.
/// </para>
/// </summary>
public sealed class PingProbeChannel : IProbeChannel
{
    private static readonly TimeSpan InitProbeTimeout = TimeSpan.FromSeconds(2);
    private const int InitProbeTtl = 64;
    private const int InitProbePayloadBytes = 32;

    private readonly PingSender _sender;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    // Null until discovery succeeds; publish the immutable result atomically.
    private ProbeCapabilities? _capabilities;

    public PingProbeChannel()
        : this(DefaultSendAsync)
    {
    }

    internal PingProbeChannel(PingSender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Payload support established by <see cref="InitializeAsync"/>. Throws
    /// <see cref="InvalidOperationException"/> before successful
    /// initialization, so consumers only observe a settled capability.
    /// </summary>
    public ProbeCapabilities Capabilities => Volatile.Read(ref _capabilities)
        ?? throw new InvalidOperationException(
            "PingProbeChannel has not been initialized. Call InitializeAsync first.");

    /// <summary>
    /// Establishes payload support with a bounded loopback (127.0.0.1) probe
    /// before any worker starts. A custom-payload attempt that draws
    /// PlatformNotSupportedException verifies the default-payload fallback and
    /// reports restricted support; a valid but unhelpful probe answer (for example
    /// a filtered loopback returning a timeout) reports undetermined support so
    /// preparation can complete and tracing can proceed with the default payload;
    /// cancellation propagates; any unrelated failure throws a diagnostic without
    /// recording support either way.
    /// Success (including undetermined) is sticky: later calls return the settled
    /// capabilities without re-probing. Failure (or cancellation) leaves the channel
    /// uninitialized so a later call can retry.
    /// </summary>
    public async Task<ProbeCapabilities> InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _capabilities) is { } capabilities)
        {
            return capabilities;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_capabilities is { } settled)
            {
                return settled;
            }

            ct.ThrowIfCancellationRequested();

            var payloadSupport = await DiscoverPayloadSupportAsync(ct).ConfigureAwait(false);
            capabilities = new ProbeCapabilities(payloadSupport);
            Volatile.Write(ref _capabilities, capabilities);
            return capabilities;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<PayloadSupport> DiscoverPayloadSupportAsync(CancellationToken ct)
    {
        try
        {
            PingSendResult reply = await _sender(IPAddress.Loopback, InitProbeTtl,
                BuildPayload(InitProbePayloadBytes), InitProbeTimeout, ct).ConfigureAwait(false);
            // A timeout is a valid answer but does not prove payload support.
            return ProbeStatusMap.CarriesTiming(ProbeStatusMap.Classify(reply.Status))
                ? PayloadSupport.Supported
                : PayloadSupport.Undetermined;
        }
        catch (PlatformNotSupportedException)
        {
            await VerifyDefaultPayloadAsync(ct).ConfigureAwait(false);
            return PayloadSupport.Restricted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "PingProbeChannel initialization failed: the loopback capability probe "
                + "did not complete, so payload support is unknown.",
                ex);
        }
    }

    private async Task VerifyDefaultPayloadAsync(CancellationToken ct)
    {
        // After a custom-payload rejection, the default fallback must be proven.
        try
        {
            PingSendResult reply = await _sender(IPAddress.Loopback, InitProbeTtl,
                buffer: null, InitProbeTimeout, ct).ConfigureAwait(false);
            if (!ProbeStatusMap.CarriesTiming(ProbeStatusMap.Classify(reply.Status)))
            {
                throw new InvalidOperationException(
                    "PingProbeChannel initialization failed: the stack rejected a custom "
                    + $"ping payload, and the default-payload fallback returned {reply.Status}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "PingProbeChannel initialization failed: the stack rejected a custom "
                + "ping payload, and the default-payload fallback did not succeed.",
                ex);
        }
    }

    public async Task<ProbeResult> SendAsync(IPAddress dest, int ttl, int payloadBytes,
                                             TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var payload = Capabilities.SupportsCustomPayloadSize ? BuildPayload(payloadBytes) : null;

        var started = Stopwatch.GetTimestamp();
        PingSendResult reply;
        try
        {
            reply = await _sender(dest, ttl, payload, timeout, ct).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidOperationException(
                "PingProbeChannel hit an unexpected ICMP payload restriction after "
                + "initialization settled the session capabilities.", ex);
        }
        catch (Exception ex) when (ex is PingException or ArgumentException)
        {
            // Local stack rejection: report the safe category without exception internals.
            return new ProbeResult(ProbeOutcome.Failed, null, 0, ProbeErrorReason.LocalFailure);
        }

        // Rounded, not truncated: sub-millisecond LAN/loopback hops would
        // otherwise report 0, colliding with the 0 used for non-timing
        // outcomes below (residual <0.5ms still reads 0 by int-ms design).
        var elapsedMs = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var outcome = ProbeStatusMap.Classify(reply.Status);

        var responder = reply.Address is { } a && !a.Equals(IPAddress.Any) ? a : null;

        return new ProbeResult(outcome, responder,
                               ProbeStatusMap.CarriesTiming(outcome) ? elapsedMs : 0,
                               ProbeStatusMap.Reason(reply.Status));
    }

    private static async Task<PingSendResult> DefaultSendAsync(
        IPAddress dest, int ttl, byte[]? buffer, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, true);
        PingReply reply = await ping.SendPingAsync(dest, timeout, buffer, options, ct)
            .ConfigureAwait(false);
        return new PingSendResult(reply.Status, reply.Address);
    }

    /// <summary>v0.92 filled its payload with ASCII spaces; kept for parity.</summary>
    private static byte[] BuildPayload(int bytes)
    {
        var payload = new byte[Math.Max(0, bytes)];
        payload.AsSpan().Fill(32);
        return payload;
    }
}
