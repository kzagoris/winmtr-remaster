using WinMtr.Core.Probing;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The user-facing wording for payload capability states. Kept in one place
/// so the settings dialog, the banner stack, and the tests describe
/// supported, restricted, and undetermined support with the same words.
/// The privilege-grant hint below repeats the approved interface design word
/// for word: it matches the console's cap_net_raw note and the probe
/// channel's own comments, so no new grant command is invented here.
/// </summary>
public static class CapabilityMessages
{
    /// <summary>
    /// Why the custom payload control is unavailable when a custom payload
    /// is restricted on this machine. Tracing still proceeds with the
    /// default payload.
    /// </summary>
    public const string RestrictedPayloadReason =
        "Custom payloads are restricted on this machine; tracing uses the default payload.";

    /// <summary>
    /// Why the custom payload control is unavailable when payload support
    /// could not be determined at all. Tracing still proceeds with the
    /// default payload.
    /// </summary>
    public const string UndeterminedPayloadReason =
        "Payload support could not be determined; tracing uses the default payload.";

    /// <summary>
    /// The dismissible banner for restricted payload support: the operating
    /// system refused custom payloads, with the privilege-grant hint telling
    /// the operator how to restore full function. Partial results are still
    /// shown. Callers append the transit-address uncertainty for the recorded
    /// check status via <see cref="PrivilegeBanner"/>.
    /// </summary>
    public const string RestrictedPayloadBannerBase =
        "Probing is restricted: the OS refused custom payloads. For full function, run elevated " +
        "(Windows: administrator; Linux: root or grant the capability, e.g. setcap cap_net_raw+ep <binary>). " +
        "Partial results shown.";

    /// <summary>
    /// The dismissible banner for undetermined payload support: the
    /// capability probe was inconclusive (often a filtered loopback), so the
    /// application traces with the default payload rather than refusing to
    /// start. Includes the timeout action text.
    /// </summary>
    public const string UndeterminedPayloadBanner =
        "Payload support could not be determined (the capability probe was inconclusive \u2014 often a " +
        "filtered loopback). Tracing with the default payload; check local firewall/loopback filtering if this persists.";

    /// <summary>
    /// The reason the custom payload control is unavailable for
    /// <paramref name="support"/>, or empty when custom payloads are
    /// available. Unknown (never probed) reads as available: the control is
    /// only disabled on observed restriction or observed uncertainty.
    /// </summary>
    public static string PayloadDisabledReason(PayloadSupport? support) => support switch
    {
        PayloadSupport.Restricted => RestrictedPayloadReason,
        PayloadSupport.Undetermined => UndeterminedPayloadReason,
        _ => string.Empty,
    };

    /// <summary>
    /// The banner for a settled capability that limits payloads, or null
    /// when no banner is owed. Supported capabilities and never-probed
    /// platforms stay silent; restricted and undetermined both trace with
    /// the default payload and say so. The restricted banner carries the
    /// privilege hint plus whatever the transit-address check recorded, kept
    /// distinct from the payload verdict itself.
    /// </summary>
    public static string? CapabilityBanner(PayloadSupport? support, TransitAddressCheckStatus transit) => support switch
    {
        PayloadSupport.Restricted => PrivilegeBanner(transit),
        PayloadSupport.Undetermined => UndeterminedPayloadBanner,
        _ => null,
    };

    /// <summary>
    /// The restricted-payload banner with the privilege hint, extended with
    /// what the early transit-address check recorded. An observed exposed
    /// address needs no extra words; anything else says plainly that the
    /// weaker-result question is still open, so the uncertainty is never
    /// mistaken for the payload verdict.
    /// </summary>
    public static string PrivilegeBanner(TransitAddressCheckStatus transit) =>
        RestrictedPayloadBannerBase + transit switch
        {
            TransitAddressCheckStatus.AddressExposed => string.Empty,
            TransitAddressCheckStatus.AddressHidden =>
                " Without privilege, intermediate router addresses may also be hidden, so expect weaker results.",
            TransitAddressCheckStatus.Pending =>
                " It has not yet been verified on this machine whether intermediate router addresses stay visible without privilege.",
            _ =>
                " Whether intermediate router addresses stay visible without privilege could not be verified on this machine.",
        };
}
