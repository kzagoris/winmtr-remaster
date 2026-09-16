using System.Net;

namespace WinMtr.Core.Tests;

/// <summary>
/// Covers <see cref="PathLength"/> through the extent-based overload, which
/// relies on the state-maintained active extent invariant instead of taking a
/// ceiling parameter. Wording follows the route-length glossary: route length
/// is the number of hops in the current trimmed snapshot, from the first TTL
/// through the last responding hop or confirmed destination; trailing hops
/// repeating the previous responder are trimmed.
/// </summary>
public class PathLengthExtentTests
{
    private static Hop At(int index, string? address) =>
        Hop.Empty(index) with { Address = address is null ? null : IPAddress.Parse(address) };

    [Fact]
    public void Route_length_ends_at_the_confirmed_destination()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, "10.0.0.2"), At(2, "9.9.9.9"), At(3, null)];
        Assert.Equal(3, PathLength.Determine(hops, target, destinationReached: true));
    }

    [Fact]
    public void Route_length_trims_trailing_hops_repeating_the_previous_responder()
    {
        // Glossary: trailing hops repeating the previous responder are trimmed.
        // The target never answers, so the last responding hop's address
        // repeats across the remaining active extent.
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops =
        [
            At(0, "10.0.0.1"), At(1, "10.0.0.2"),
            At(2, "10.0.0.3"), At(3, "10.0.0.3"), At(4, "10.0.0.3")
        ];
        Assert.Equal(3, PathLength.Determine(hops, target, destinationReached: false));
    }

    [Fact]
    public void Route_length_trims_trailing_silence_to_the_last_responding_hop()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, null), At(2, null)];
        Assert.Equal(1, PathLength.Determine(hops, target, destinationReached: false));
    }

    [Fact]
    public void Route_length_keeps_intermittent_silence_between_responders()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, null), At(2, "10.0.0.3")];
        Assert.Equal(3, PathLength.Determine(hops, target, destinationReached: false));
    }

    [Fact]
    public void Route_length_keeps_a_minimum_of_one_waiting_row_on_a_fresh_trace()
    {
        var target = IPAddress.Parse("9.9.9.9");
        Hop[] hops = [At(0, null)];
        Assert.Equal(1, PathLength.Determine(hops, target, destinationReached: false));
    }

    [Fact]
    public void Retained_identity_never_counts_as_destination_evidence()
    {
        // A retained hop row can still carry the target address after the
        // boundary invalidates. Without current destination evidence the
        // target-answer cut must not apply; route length runs through the
        // last responding hop instead.
        var target = IPAddress.Parse("10.0.0.2");
        Hop[] hops = [At(0, "10.0.0.1"), At(1, "10.0.0.2"), At(2, "10.0.0.4")];
        Assert.Equal(3, PathLength.Determine(hops, target, destinationReached: false));
        Assert.Equal(2, PathLength.Determine(hops, target, destinationReached: true));
    }
}
