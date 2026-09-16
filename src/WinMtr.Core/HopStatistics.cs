namespace WinMtr.Core;

/// <summary>
/// The accumulator for one hop. Six numbers are stored; average and loss are
/// derived on read. No probe history is retained, so memory is O(hops) rather
/// than O(probes) and a session can run for days.
/// </summary>
public sealed record HopStatistics(
    int Sent,
    int Received,
    long TotalRttMs,
    int? LastRttMs,
    int? BestRttMs,
    int? WorstRttMs)
{
    public static readonly HopStatistics Empty = new(0, 0, 0, null, null, null);

    /// <summary>
    /// The single transition function. Pure: everything a probe teaches the
    /// domain enters here.
    /// </summary>
    public HopStatistics Record(ProbeOutcome outcome, int rttMs) =>
        ProbeStatusMap.CarriesTiming(outcome)
            ? this with
            {
                Sent = Sent + 1,
                Received = Received + 1,
                TotalRttMs = TotalRttMs + rttMs,
                LastRttMs = rttMs,
                // `with` evaluates every initializer against the source record,
                // so all right-hand sides here read the ORIGINAL instance's
                // values. The first-sample test is expressed on the nullable
                // fields rather than on Received, so it stays total for
                // instances built through the positional constructor -- which
                // can hold Received > 0 with no best -- and not just for
                // instances Record itself produced. This is the v0.92 defect D1
                // fix: a lost first probe no longer pins Best at 0.
                BestRttMs = BestRttMs is null ? rttMs : Math.Min(BestRttMs.Value, rttMs),
                WorstRttMs = WorstRttMs is null ? rttMs : Math.Max(WorstRttMs.Value, rttMs)
            }
            : this with { Sent = Sent + 1 };

    /// <summary>Mean round-trip time, integer division as v0.92 did.</summary>
    public int AverageRttMs => Received == 0 ? 0 : (int)(TotalRttMs / Received);

    /// <summary>
    /// The mean round-trip time, or null when no probe replied. AverageRttMs
    /// reads 0 for a hop with no reply, which a report must not show as a
    /// measurement. Every report renderer asks here instead of testing
    /// Received itself, so the rule has one home.
    /// </summary>
    public int? AverageRttMsOrNull => Received == 0 ? null : AverageRttMs;

    /// <summary>Accurate loss percentage, for the UI and the new report formats.</summary>
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    /// <summary>
    /// v0.92's integer loss arithmetic, used only by the legacy text and HTML
    /// reports to preserve their loss arithmetic. Rounds differently from LossPercent.
    /// </summary>
    public int LegacyLossPercent => Sent == 0 ? 0 : 100 - (int)(100L * Received / Sent);
}
