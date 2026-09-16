namespace WinMtr.Core.Tests;

public class HopStatisticsTests
{
    [Fact]
    public void Empty_has_no_samples()
    {
        var s = HopStatistics.Empty;
        Assert.Equal(0, s.Sent);
        Assert.Equal(0, s.Received);
        Assert.Null(s.BestRttMs);
        Assert.Null(s.WorstRttMs);
        Assert.Null(s.LastRttMs);
        Assert.Equal(0, s.AverageRttMs);
        Assert.Equal(0, s.LossPercent);
    }

    [Fact]
    public void First_good_probe_sets_best_worst_and_last()
    {
        var s = HopStatistics.Empty.Record(ProbeOutcome.Expired, 25);
        Assert.Equal(1, s.Sent);
        Assert.Equal(1, s.Received);
        Assert.Equal(25, s.BestRttMs);
        Assert.Equal(25, s.WorstRttMs);
        Assert.Equal(25, s.LastRttMs);
        Assert.Equal(25, s.AverageRttMs);
    }

    [Fact]
    public void Best_tracks_the_minimum_and_worst_the_maximum()
    {
        var s = HopStatistics.Empty
            .Record(ProbeOutcome.Expired, 30)
            .Record(ProbeOutcome.Expired, 10)
            .Record(ProbeOutcome.Expired, 50);
        Assert.Equal(10, s.BestRttMs);
        Assert.Equal(50, s.WorstRttMs);
        Assert.Equal(50, s.LastRttMs);
        Assert.Equal(30, s.AverageRttMs);   // (30+10+50)/3
    }

    [Fact]
    public void Best_is_correct_when_first_probe_is_lost()
    {
        // Regression guard for v0.92 defect D1. There, AddXmit ran on every
        // attempt but SetBest only on a good reply, so after a lost first probe
        // the `xmit == 1` branch never fired and best stayed 0 forever.
        var s = HopStatistics.Empty
            .Record(ProbeOutcome.TimedOut, 0)
            .Record(ProbeOutcome.Expired, 42);

        Assert.Equal(2, s.Sent);
        Assert.Equal(1, s.Received);
        Assert.Equal(42, s.BestRttMs);      // v0.92 reported 0 here
        Assert.Equal(42, s.WorstRttMs);
    }

    [Fact]
    public void Failed_outcomes_count_as_sent_but_not_received()
    {
        var s = HopStatistics.Empty
            .Record(ProbeOutcome.Expired, 10)
            .Record(ProbeOutcome.TimedOut, 0)
            .Record(ProbeOutcome.Unreachable, 0)
            .Record(ProbeOutcome.Failed, 0);

        Assert.Equal(4, s.Sent);
        Assert.Equal(1, s.Received);
        Assert.Equal(10, s.LastRttMs);      // unchanged by the failures
        Assert.Equal(10, s.BestRttMs);
    }

    [Fact]
    public void Reached_counts_as_received()
    {
        var s = HopStatistics.Empty.Record(ProbeOutcome.Reached, 7);
        Assert.Equal(1, s.Received);
        Assert.Equal(7, s.BestRttMs);
    }

    [Fact]
    public void Average_is_integer_division_of_total_by_received()
    {
        var s = HopStatistics.Empty
            .Record(ProbeOutcome.Expired, 10)
            .Record(ProbeOutcome.Expired, 11);
        Assert.Equal(10, s.AverageRttMs);   // 21 / 2 == 10
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(214, 214, 0)]
    [InlineData(214, 0, 100)]
    [InlineData(214, 209, 3)]     // 100 - (100*209/214) == 100 - 97
    [InlineData(214, 213, 1)]     // 100 - (100*213/214) == 100 - 99
    public void LegacyLossPercent_uses_v092_integer_arithmetic(int sent, int received, int expected)
    {
        var s = new HopStatistics(sent, received, 0, null, null, null);
        Assert.Equal(expected, s.LegacyLossPercent);
    }

    [Fact]
    public void LegacyLossPercent_differs_from_the_accurate_formula()
    {
        // Documents a real discrepancy: the legacy integer form rounds
        // differently from the true percentage. Both are kept on purpose --
        // legacy for text and HTML reports, accurate for new formats.
        var s = new HopStatistics(214, 209, 0, null, null, null);
        Assert.Equal(3, s.LegacyLossPercent);
        Assert.Equal(2, (int)s.LossPercent);
    }

    [Fact]
    public void A_later_larger_sample_does_not_displace_an_established_best()
    {
        var first = HopStatistics.Empty.Record(ProbeOutcome.Expired, 99);
        Assert.Equal(99, first.BestRttMs);
        var second = first.Record(ProbeOutcome.Expired, 150);
        Assert.Equal(99, second.BestRttMs);
    }

    [Fact]
    public void Record_on_a_hand_constructed_instance_with_no_best_does_not_throw()
    {
        // The positional constructor can produce Received > 0 with BestRttMs
        // null; Record must be total over that, not just over its own output.
        var s = new HopStatistics(214, 209, 0, null, null, null).Record(ProbeOutcome.Expired, 5);
        Assert.Equal(5, s.BestRttMs);
        Assert.Equal(5, s.WorstRttMs);
        Assert.Equal(210, s.Received);
    }
}
