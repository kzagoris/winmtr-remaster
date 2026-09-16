using System.Net;

namespace WinMtr.Core;

/// <summary>
/// One hop. Identity (<see cref="Address"/>, <see cref="HostName"/>) is kept
/// structurally separate from state (<see cref="LastOutcome"/>,
/// <see cref="LastErrorReason"/>) -- v0.92 stored all four in one char[255].
/// </summary>
public sealed record Hop(
    HopIndex Index,
    IPAddress? Address,
    string? HostName,
    HopStatistics Stats,
    ProbeOutcome LastOutcome,
    ProbeErrorReason? LastErrorReason)
{
    public static Hop Empty(int index) =>
        new(new HopIndex(index), null, null, HopStatistics.Empty, ProbeOutcome.TimedOut, null);

    public bool HasResponded => Address is not null;

    /// <summary>
    /// Display identity, host only. Empty when the hop never replied
    /// (unknown address); the "No response." message belongs to
    /// <see cref="StatusDescription"/>, not here, so the Host column
    /// never carries status text. Error text never appears here either.
    /// </summary>
    public string Label => HostName ?? Address?.ToString() ?? string.Empty;

    /// <summary>
    /// The latest observation in readable form: the waiting message before the
    /// first completed attempt, "No response." for timeouts, the error
    /// description for unreachable/failed attempts, and empty for normal
    /// replies. Shared with text, CSV and JSON output.
    /// </summary>
    public string StatusDescription =>
        ProbeStatusPresentation.Describe(LastOutcome, LastErrorReason, Stats.Sent > 0);
}
