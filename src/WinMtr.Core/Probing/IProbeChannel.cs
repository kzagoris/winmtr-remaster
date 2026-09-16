using System.Net;

namespace WinMtr.Core.Probing;

/// <summary>
/// The result of one probe. <paramref name="RttMs"/> is MEASURED by the
/// channel, not read from the platform reply: PingReply.RoundtripTime is 0
/// for every non-Success status on both Windows and Linux (spike finding 2).
/// <paramref name="ErrorReason"/> carries the specific condition behind an
/// unreachable or failed attempt when the platform reported one; it is null
/// for reached, transit-TTL and timeout observations.
/// </summary>
public sealed record ProbeResult(ProbeOutcome Outcome, IPAddress? Responder, int RttMs, ProbeErrorReason? ErrorReason = null);

/// <summary>
/// Whether the channel can send a custom ICMP payload on this machine.
/// Custom payload size needs cap_net_raw or root on Linux (spike finding 3).
/// <see cref="Supported"/> means the loopback capability probe proved a custom
/// payload transmits. <see cref="Restricted"/> means the stack rejected a custom
/// payload and the default-payload fallback was verified. <see cref="Undetermined"/>
/// means the capability probe returned a valid but unhelpful answer (for example
/// a filtered loopback returning a timeout), which proves nothing either way.
/// Both <see cref="Restricted"/> and <see cref="Undetermined"/> trace with the
/// default payload; callers distinguish them only to explain why.
/// </summary>
public enum PayloadSupport
{
    /// <summary>A reply proved custom payloads transmit.</summary>
    Supported,

    /// <summary>The stack rejected a custom payload; default payload verified.</summary>
    Restricted,

    /// <summary>The probe settled nothing; support could not be determined.</summary>
    Undetermined,
}

/// <summary>
/// What this channel can actually do on this machine. Custom payload size
/// needs cap_net_raw or root on Linux (spike finding 3); surfacing it lets the
/// UI explain the limit instead of throwing at probe time.
/// <see cref="SupportsCustomPayloadSize"/> is true only for
/// <see cref="PayloadSupport.Supported"/>; both restricted and undetermined
/// use the default payload.
/// </summary>
public sealed record ProbeCapabilities(PayloadSupport PayloadSupport)
{
    /// <summary>
    /// True only when custom payloads are known to work. False covers both
    /// restricted and undetermined, which both trace with the default payload.
    /// Use <see cref="PayloadSupport"/> when the distinction matters for messaging.
    /// </summary>
    public bool SupportsCustomPayloadSize => PayloadSupport == PayloadSupport.Supported;
}

/// <summary>
/// A deep module's narrow seam: one method hides platform detection, two
/// status-enum mappings, stopwatch timing, privilege fallback and payload
/// construction.
/// </summary>
public interface IProbeChannel
{
    ProbeCapabilities Capabilities { get; }

    Task<ProbeResult> SendAsync(IPAddress dest, int ttl, int payloadBytes,
                                TimeSpan timeout, CancellationToken ct);
}
