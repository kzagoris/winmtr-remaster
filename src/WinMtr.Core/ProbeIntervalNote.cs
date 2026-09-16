namespace WinMtr.Core;

/// <summary>
/// The one statement of the probe interval rule, written once and shown
/// wherever a reader meets the per-hop counts.
/// </summary>
/// <remarks>
/// A hop sends its next probe only after the last probe settles, so the
/// interval is a minimum and not a schedule. A probe that gets no answer
/// costs the reply timeout, thus the hops of one route do not share an
/// observation window and their Sent counts can be different. The counts are
/// correct; only the shared window is missing, so this text supplies it
/// (issue WM-11).
/// <para>
/// The statement covers every hop on purpose. A responding hop that loses an
/// answer now and then falls behind in the same way a silent hop does, so
/// naming only the silent hop would leave the smaller difference unexplained.
/// It also names no policy: there is no backoff, and the reply timeout
/// governs only a probe that gets no answer at all.
/// </para>
/// </remarks>
public static class ProbeIntervalNote
{
    /// <summary>
    /// The statement in printable lines, for the fixed-width text report.
    /// </summary>
    public static IReadOnlyList<string> Lines { get; } =
    [
        "Each hop sends the next probe only after the last probe gives an answer or",
        "times out. The interval is thus a minimum. A lost answer delays that hop, so",
        "the Sent counts of two hops can be different. Sent counts only the probes",
        "that completed."
    ];

    /// <summary>
    /// The same statement as one paragraph, for a tool tip or a document.
    /// </summary>
    public static string Text { get; } = string.Join(" ", Lines);
}
