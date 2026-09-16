using System.Net.NetworkInformation;

namespace WinMtr.Core;

/// <summary>What one probe told us about the hop at its TTL.</summary>
public enum ProbeOutcome
{
    /// <summary>The target itself replied.</summary>
    Reached,
    /// <summary>A router on the way replied because the TTL ran out there.</summary>
    Expired,
    /// <summary>No reply within the timeout.</summary>
    TimedOut,
    /// <summary>An ICMP unreachable of some flavour.</summary>
    Unreachable,
    /// <summary>Anything else.</summary>
    Failed
}

public static class ProbeStatusMap
{
    public static ProbeOutcome Classify(IPStatus status) => status switch
    {
        IPStatus.Success => ProbeOutcome.Reached,

        // Windows reports TtlExpired; Linux and macOS report TimeExceeded.
        // Both mean "a router answered because the TTL ran out". Verified by
        // spike on win-x64 (.NET 10.0.11) and ubuntu-x64 (.NET 10.0.8).
        IPStatus.TtlExpired or IPStatus.TimeExceeded => ProbeOutcome.Expired,

        IPStatus.TimedOut => ProbeOutcome.TimedOut,

        IPStatus.DestinationHostUnreachable
            or IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationPortUnreachable
            or IPStatus.DestinationProtocolUnreachable => ProbeOutcome.Unreachable,

        _ => ProbeOutcome.Failed
    };

    /// <summary>True when the outcome came with a usable round-trip measurement.</summary>
    public static bool CarriesTiming(ProbeOutcome outcome) =>
        outcome is ProbeOutcome.Reached or ProbeOutcome.Expired;

    /// <summary>
    /// The specific reason behind one platform status, or null when the status
    /// carries no error (success, transit-TTL expiry, timeout). Transit-TTL
    /// expiry and timeout are ordinary observations, never errors.
    /// </summary>
    /// <remarks>
    /// Only reasons the <see cref="IPStatus"/> enum can actually report are
    /// mapped here. <c>DestinationProhibited</c> shares its numeric value with
    /// <c>DestinationProtocolUnreachable</c> (IPv6 vs IPv4), so both arrive as
    /// <see cref="ProbeErrorReason.ProtocolUnreachable"/>. Anything without a
    /// recognized meaning -- including <c>Unknown</c>, the generic
    /// <c>DestinationUnreachable</c>, and IPv6 header diagnostics -- uses
    /// <see cref="ProbeErrorReason.Unknown"/> rather than an invented cause.
    /// </remarks>
    public static ProbeErrorReason? Reason(IPStatus status) => status switch
    {
        IPStatus.Success => null,

        // A router answering because the TTL ran out (both platform spellings)
        // is ordinary traceroute behavior, not an error.
        IPStatus.TtlExpired or IPStatus.TimeExceeded => null,

        IPStatus.TimedOut => null,

        IPStatus.DestinationNetworkUnreachable => ProbeErrorReason.NetworkUnreachable,
        IPStatus.DestinationHostUnreachable => ProbeErrorReason.HostUnreachable,
        IPStatus.DestinationProtocolUnreachable => ProbeErrorReason.ProtocolUnreachable,
        IPStatus.DestinationPortUnreachable => ProbeErrorReason.PortUnreachable,

        IPStatus.NoResources => ProbeErrorReason.InsufficientResources,
        IPStatus.BadOption => ProbeErrorReason.BadOption,
        IPStatus.HardwareError => ProbeErrorReason.HardwareError,
        IPStatus.PacketTooBig => ProbeErrorReason.PacketTooBig,
        IPStatus.BadRoute => ProbeErrorReason.BadRoute,
        IPStatus.TtlReassemblyTimeExceeded => ProbeErrorReason.ReassemblyTimeExceeded,
        IPStatus.ParameterProblem => ProbeErrorReason.ParameterProblem,
        IPStatus.SourceQuench => ProbeErrorReason.SourceQuench,
        IPStatus.BadDestination => ProbeErrorReason.BadDestination,

        _ => ProbeErrorReason.Unknown
    };
}
