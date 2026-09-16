using System.Net.NetworkInformation;

namespace WinMtr.Core.Tests;

public class ProbeStatusMapTests
{
    [Theory]
    [InlineData(IPStatus.Success, ProbeOutcome.Reached)]
    [InlineData(IPStatus.TtlExpired, ProbeOutcome.Expired)]       // Windows
    [InlineData(IPStatus.TimeExceeded, ProbeOutcome.Expired)]     // Linux and macOS
    [InlineData(IPStatus.TimedOut, ProbeOutcome.TimedOut)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.DestinationPortUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.DestinationProtocolUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.BadRoute, ProbeOutcome.Failed)]
    [InlineData(IPStatus.PacketTooBig, ProbeOutcome.Failed)]
    public void Classify_maps_status_to_outcome(IPStatus status, ProbeOutcome expected)
    {
        Assert.Equal(expected, ProbeStatusMap.Classify(status));
    }

    [Fact]
    public void Reached_and_Expired_are_the_only_outcomes_that_carry_timing()
    {
        Assert.True(ProbeStatusMap.CarriesTiming(ProbeOutcome.Reached));
        Assert.True(ProbeStatusMap.CarriesTiming(ProbeOutcome.Expired));
        Assert.False(ProbeStatusMap.CarriesTiming(ProbeOutcome.TimedOut));
        Assert.False(ProbeStatusMap.CarriesTiming(ProbeOutcome.Unreachable));
        Assert.False(ProbeStatusMap.CarriesTiming(ProbeOutcome.Failed));
    }
}
