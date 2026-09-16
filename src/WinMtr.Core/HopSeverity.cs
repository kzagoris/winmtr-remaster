namespace WinMtr.Core;

/// <summary>
/// The loss grade of one hop row. Four ascending steps and a separate total
/// step: a hop that lost every probe is not merely worse than a hop that lost
/// many of them, so the grid keeps the two apart at a glance.
/// </summary>
public enum LossLevel
{
    None,
    Low,
    Moderate,
    High,
    Total,
}

/// <summary>
/// The severity facets of one hop row, derived from its statistics alone: the
/// loss level that drives the Loss ramp, and one outlier flag per latency cell.
/// This is the single place where "a spike" is defined. Pure, so the rule runs
/// in tests away from the grid, and built only from values a route snapshot
/// already carries: no probe logic lives here.
/// </summary>
public readonly record struct HopSeverity(
    LossLevel Level,
    bool LastIsOutlier,
    bool AverageIsOutlier,
    bool BestIsOutlier,
    bool WorstIsOutlier)
{
    /// <summary>A spike must beat the hop's own average by this factor.</summary>
    public const double OutlierRatio = 2.5;

    /// <summary>A spike must also clear the average by this many milliseconds,
    /// so a fast hop's noise floor never counts as a spike.</summary>
    public const int OutlierFloorMs = 20;

    /// <summary>A spike needs this many completed probes, so the first samples
    /// of a hop cannot mark themselves.</summary>
    public const int MinimumSamplesForOutlier = 3;

    /// <summary>True when the Loss cell carries the ramp and the bar.</summary>
    public bool HasLoss => Level != LossLevel.None;

    public bool IsLossLow => Level == LossLevel.Low;

    public bool IsLossModerate => Level == LossLevel.Moderate;

    public bool IsLossHigh => Level == LossLevel.High;

    public bool IsLossTotal => Level == LossLevel.Total;

    /// <summary>
    /// Derives every facet from the hop's accumulated statistics. The
    /// average is compared with itself, so it can never pass the ratio test.
    /// The best sample can never exceed the average either. So only the last
    /// sample and the worst sample can carry the mark. Every snapshot
    /// replaces the derivation, so a spike stops being special when the
    /// average catches up. This is a view of the statistics, not a history.
    /// </summary>
    public static HopSeverity From(HopStatistics stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new HopSeverity(
            ToLossLevel(stats.LossPercent),
            IsLatencyOutlier(stats.LastRttMs, stats.AverageRttMs, stats.Received),
            IsLatencyOutlier(stats.AverageRttMs, stats.AverageRttMs, stats.Received),
            IsLatencyOutlier(stats.BestRttMs, stats.AverageRttMs, stats.Received),
            IsLatencyOutlier(stats.WorstRttMs, stats.AverageRttMs, stats.Received));
    }

    /// <summary>
    /// Maps an accurate loss percentage to its grade. Zero loss is quiet;
    /// everything below 10 percent is noise on a live path, below 50 is
    /// degraded, below 100 is barely alive, and a full 100 is a dead hop.
    /// </summary>
    public static LossLevel ToLossLevel(double lossPercent)
    {
        if (lossPercent <= 0)
            return LossLevel.None;
        if (lossPercent < 10)
            return LossLevel.Low;
        if (lossPercent < 50)
            return LossLevel.Moderate;
        if (lossPercent < 100)
            return LossLevel.High;
        return LossLevel.Total;
    }

    /// <summary>
    /// The one outlier rule: a measurement is a spike when it beats the hop's
    /// average by the ratio and the floor together, on enough completed probes.
    /// Both thresholds must hold: the ratio alone would mark milliseconds of
    /// noise on a fast hop, and the floor alone would mark every hop that is
    /// simply far away.
    /// </summary>
    public static bool IsLatencyOutlier(int? value, int averageRttMs, int received)
    {
        if (value is not { } sample
            || received < MinimumSamplesForOutlier
            || averageRttMs <= 0)
        {
            return false;
        }

        return sample >= averageRttMs * OutlierRatio
            && sample - averageRttMs >= OutlierFloorMs;
    }
}
