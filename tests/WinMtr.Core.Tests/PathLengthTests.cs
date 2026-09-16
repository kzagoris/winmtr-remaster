using System.Net;

// Exercises the obsolete ceiling-parameter shim; assertions unchanged (issue 03).
#pragma warning disable CS0618
namespace WinMtr.Core.Tests;

public class PathLengthTests
{
    private static Hop At(int index, string? address) =>
        Hop.Empty(index) with { Address = address is null ? null : IPAddress.Parse(address) };

    [Fact]
    public void Rule1_stops_at_the_first_hop_that_is_the_target()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, "10.0.0.2"), At(2, "9.9.9.9"), At(3, null)];
        Assert.Equal(3, PathLength.Determine(hops, target, 30));
    }

    [Fact]
    public void Rule2_trims_trailing_hops_that_repeat_the_same_responder()
    {
        // The target never answers pings, so the last real router's address
        // repeats across the remaining slots.
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops =
        [
            At(0, "10.0.0.1"), At(1, "10.0.0.2"),
            At(2, "10.0.0.3"), At(3, "10.0.0.3"), At(4, "10.0.0.3")
        ];
        Assert.Equal(3, PathLength.Determine(hops, target, 5));
    }

    [Fact]
    public void Organic_growth_trims_trailing_silent_hops_to_last_responder()
    {
        // Remaster break from v0.92: v0.92 kept null tails via its
        // `addr != 0` guard and padded unreached routes to the hop limit.
        // The remaster grows rows organically to the last responding hop.
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, null), At(2, null)];
        Assert.Equal(1, PathLength.Determine(hops, target, 3));
    }

    [Fact]
    public void Organic_growth_keeps_intermittent_silence()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, null), At(2, "10.0.0.3")];
        Assert.Equal(3, PathLength.Determine(hops, target, 3));
    }

    [Fact]
    public void Organic_growth_trims_many_trailing_silent_hops()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops =
        [
            At(0, "10.0.0.1"), At(1, "10.0.0.2"), At(2, "10.0.0.3"),
            At(3, null), At(4, null), At(5, null), At(6, null), At(7, null)
        ];
        Assert.Equal(3, PathLength.Determine(hops, target, 30));
    }

    [Fact]
    public void Never_returns_less_than_one()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, null)];
        Assert.Equal(1, PathLength.Determine(hops, target, 30));
    }

    [Fact]
    public void Never_exceeds_hop_limit_or_the_hop_count()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, "10.0.0.2"), At(2, "10.0.0.3")];
        Assert.Equal(2, PathLength.Determine(hops, target, 2));
        Assert.Equal(3, PathLength.Determine(hops, target, 99));
    }

    [Fact]
    public void Rule1_wins_over_rule2()
    {
        var target = IPAddress.Parse("10.0.0.3");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, "10.0.0.3"), At(2, "10.0.0.3")];
        Assert.Equal(2, PathLength.Determine(hops, target, 30));
    }
}
#pragma warning restore CS0618
