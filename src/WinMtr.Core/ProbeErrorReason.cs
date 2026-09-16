using System.Net.NetworkInformation;

namespace WinMtr.Core;

/// <summary>
/// The specific condition explaining an <see cref="ProbeOutcome.Unreachable"/>
/// or <see cref="ProbeOutcome.Failed"/> probe. One flat set: reasons describe a
/// single attempt, not a proven fault in the remote hop, so no hierarchy or
/// history subsystem is needed.
/// </summary>
/// <remarks>
/// Only reasons the <see cref="IPStatus"/> enum can actually report exist
/// here. Legacy ICMP.DLL codes with no <see cref="IPStatus"/> counterpart
/// (buffer too small, bad request, oversized option, general failure) are
/// deliberately absent: the adapter reports only what the platform supplied
/// and never infers them, and an unrecognized status uses
/// <see cref="Unknown"/>.
/// </remarks>
public enum ProbeErrorReason
{
    NetworkUnreachable,
    HostUnreachable,
    ProtocolUnreachable,
    PortUnreachable,
    PacketTooBig,
    BadRoute,
    BadOption,
    HardwareError,
    InsufficientResources,
    ReassemblyTimeExceeded,
    ParameterProblem,
    SourceQuench,
    BadDestination,
    LocalFailure,
    Unknown
}

/// <summary>
/// The single source of human-readable probe error text. Descriptions are
/// derived from the stored reason; reason and text are never two independently
/// mutable fields. Wording for recognized conditions is v0.92's verbatim.
/// </summary>
public static class ProbeErrorDescriptions
{
    public static string ToDescription(ProbeErrorReason reason) => reason switch
    {
        ProbeErrorReason.NetworkUnreachable => "Destination network unreachable.",
        ProbeErrorReason.HostUnreachable => "Destination host unreachable.",
        ProbeErrorReason.ProtocolUnreachable => "Destination protocol unreachable.",
        ProbeErrorReason.PortUnreachable => "Destination port unreachable.",
        ProbeErrorReason.PacketTooBig => "Packet was too big.",
        ProbeErrorReason.BadRoute => "Bad route.",
        ProbeErrorReason.BadOption => "Bad IP option was specified.",
        ProbeErrorReason.HardwareError => "Hardware error occurred.",
        ProbeErrorReason.InsufficientResources => "Insufficient IP resources were available.",
        ProbeErrorReason.ReassemblyTimeExceeded => "The time to live expired during fragment reassembly.",
        ProbeErrorReason.ParameterProblem => "Parameter problem.",
        ProbeErrorReason.SourceQuench => "Datagrams are arriving too fast to be processed and datagrams may have been discarded.",
        ProbeErrorReason.BadDestination => "Bad destination.",
        ProbeErrorReason.LocalFailure => "Local probe failed.",
        ProbeErrorReason.Unknown => "Probe failed: unknown reason.",
        // No valid reason reaches here; an out-of-range cast degrades to the
        // honest unknown fallback rather than throwing in a renderer.
        _ => "Probe failed: unknown reason.",
    };
}

/// <summary>
/// The shared readable presentation of a hop's latest probe state. Console,
/// CSV and JSON renderers all use this, so the same observation never reads
/// differently per format. Identity (<see cref="Hop.Address"/>,
/// <see cref="Hop.HostName"/>) is never part of the status: error text cannot
/// become a hostname.
/// </summary>
public static class ProbeStatusPresentation
{
    public const string WaitingForFirstResult = "Waiting for first result.";
    public const string NoResponse = "No response.";

    /// <summary>
    /// Describes one outcome plus its optional reason. No completed attempt
    /// (<paramref name="hasCompletedAttempt"/> false) is waiting, not timed
    /// out: the empty-hop <see cref="ProbeOutcome.TimedOut"/> placeholder is
    /// never presented as an observation. Normal replies carry no message;
    /// transit-TTL expiry is ordinary traceroute behavior, not a failure. An
    /// unreachable or failed attempt without specifics falls back to the
    /// honest unknown reason rather than reusing an earlier observation.
    /// </summary>
    public static string Describe(ProbeOutcome outcome, ProbeErrorReason? reason, bool hasCompletedAttempt)
    {
        if (!hasCompletedAttempt)
            return WaitingForFirstResult;

        return outcome switch
        {
            ProbeOutcome.Reached => "",
            ProbeOutcome.Expired => "",
            ProbeOutcome.TimedOut => NoResponse,
            ProbeOutcome.Unreachable => ProbeErrorDescriptions.ToDescription(reason ?? ProbeErrorReason.Unknown),
            ProbeOutcome.Failed => ProbeErrorDescriptions.ToDescription(reason ?? ProbeErrorReason.Unknown),
            // No valid outcome reaches here; degrade like an error without
            // specifics rather than throwing in a renderer.
            _ => ProbeErrorDescriptions.ToDescription(reason ?? ProbeErrorReason.Unknown),
        };
    }
}
