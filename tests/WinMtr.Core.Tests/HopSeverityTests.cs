using WinMtr.Core;

namespace WinMtr.Core.Tests;

/// <summary>
/// The two severity facets of one hop row: the loss level that drives the Loss
/// ramp, and the per-cell latency outlier rule that marks a spike above the
/// hop's own average. Pure rules, no probe history, no Avalonia dependency.
/// </summary>
public class HopSeverityTests
{
    [Theory]
    [InlineData(0.0, LossLevel.None)]
    [InlineData(0.1, LossLevel.Low)]
    [InlineData(9.9, LossLevel.Low)]
    [InlineData(10.0, LossLevel.Moderate)]
    [InlineData(49.9, LossLevel.Moderate)]
    [InlineData(50.0, LossLevel.High)]
    [InlineData(99.9, LossLevel.High)]
    [InlineData(100.0, LossLevel.Total)]
    public void LossLevel_Maps_Percent_Boundaries(double lossPercent, LossLevel expected)
    {
        Assert.Equal(expected, HopSeverity.ToLossLevel(lossPercent));
    }

    [Theory]
    [InlineData(null, 20, 5, false)] // no measurement to mark
    [InlineData(100, 20, 2, false)] // fewer than three completed probes
    [InlineData(100, 20, 3, true)] // three completed probes are enough
    [InlineData(100, 0, 5, false)] // no average to spike above
    [InlineData(49, 20, 5, false)] // ratio below 2.5x
    [InlineData(50, 20, 5, true)] // ratio at 2.5x, clear of the floor
    [InlineData(20, 8, 5, false)] // ratio at 2.5x but the floor is not met
    [InlineData(30, 10, 5, true)] // floor at 20 ms, above the ratio
    [InlineData(99, 40, 5, false)] // ratio just below 2.5x
    [InlineData(100, 40, 5, true)] // ratio exactly 2.5x
    public void LatencyOutlier_Requires_Ratio_Floor_And_Samples(
        int? value, int averageRttMs, int received, bool expected)
    {
        Assert.Equal(expected, HopSeverity.IsLatencyOutlier(value, averageRttMs, received));
    }

    [Fact]
    public void From_Derives_Loss_And_PerCell_Outliers_From_Statistics()
    {
        // 10 replies, average 20 ms, one 250 ms spike carried by the last
        // sample and the worst sample; best and average stay quiet.
        var stats = new HopStatistics(12, 10, 200, 250, 20, 250);

        HopSeverity severity = HopSeverity.From(stats);

        Assert.Equal(LossLevel.Moderate, severity.Level); // 2 lost of 12 sent: 16.7%
        Assert.True(severity.HasLoss);
        Assert.True(severity.IsLossModerate);
        Assert.False(severity.IsLossLow);
        Assert.True(severity.LastIsOutlier);
        Assert.True(severity.WorstIsOutlier);
        Assert.False(severity.BestIsOutlier);
        Assert.False(severity.AverageIsOutlier);
    }

    [Fact]
    public void From_WaitingHop_Is_Quiet_And_TotalLoss_Is_Total()
    {
        HopSeverity waiting = HopSeverity.From(HopStatistics.Empty);
        Assert.Equal(LossLevel.None, waiting.Level);
        Assert.False(waiting.HasLoss);
        Assert.False(waiting.IsLossTotal);
        Assert.False(waiting.LastIsOutlier);
        Assert.False(waiting.WorstIsOutlier);

        HopSeverity total = HopSeverity.From(
            HopStatistics.Empty
                .Record(ProbeOutcome.TimedOut, 0)
                .Record(ProbeOutcome.TimedOut, 0)
                .Record(ProbeOutcome.TimedOut, 0));
        Assert.Equal(LossLevel.Total, total.Level);
        Assert.True(total.HasLoss);
        Assert.True(total.IsLossTotal);
        Assert.False(total.BestIsOutlier);
        Assert.False(total.AverageIsOutlier);
        Assert.False(total.LastIsOutlier);
        Assert.False(total.WorstIsOutlier);
    }
}
