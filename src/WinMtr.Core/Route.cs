using System.Collections.Immutable;

namespace WinMtr.Core;

/// <summary>An immutable snapshot of the whole route at one instant.</summary>
/// <remarks>
/// <see cref="DestinationReached"/> and <see cref="HopLimitObserved"/> are
/// observations from the current discovery generation. They deliberately stay
/// separate from retained hop identity: an address can remain useful for
/// display after it stops answering. <see cref="IsTruncated"/> is retained for
/// callers of the original API and is derived from those observations.
/// Equality compares <see cref="Hops"/> by underlying-array reference, so two
/// structurally identical routes are unequal; compare hop-by-hop instead.
/// </remarks>
public sealed record Route
{
    public Target Target { get; init; }
    public ImmutableArray<Hop> Hops { get; init; }
    public DateTimeOffset TakenAt { get; init; }

    /// <summary>
    /// When the trace session started. <see cref="TakenAt"/> minus
    /// <see cref="StartedAt"/> is the session duration shown in reports. It
    /// defaults to <see cref="TakenAt"/> so routes built from one instant
    /// report a zero duration.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    public bool DestinationReached { get; init; }
    public bool HopLimitObserved { get; init; }

    /// <summary>
    /// Compatibility indicator. It means that a completed probe reached the
    /// configured hop limit while no destination success is current; it does
    /// not prove that the destination is farther away.
    /// </summary>
    public bool IsTruncated => !DestinationReached && HopLimitObserved;

    /// <summary>Preserves the original three-argument Route construction.</summary>
    public Route(Target target, ImmutableArray<Hop> hops, DateTimeOffset takenAt)
        : this(target, hops, takenAt, destinationReached: false, hopLimitObserved: false)
    {
    }

    /// <summary>
    /// Compatibility constructor for the original positional IsTruncated
    /// argument. New code should provide the two explicit observations.
    /// </summary>
    // Note: parameter is PascalCase (IsTruncated) to preserve the
    // `IsTruncated:` named-argument call sites from the original record.
    public Route(Target target, ImmutableArray<Hop> hops, DateTimeOffset takenAt, bool IsTruncated)
        : this(target, hops, takenAt, destinationReached: false, hopLimitObserved: IsTruncated)
    {
    }

    public Route(
        Target target,
        ImmutableArray<Hop> hops,
        DateTimeOffset takenAt,
        bool destinationReached,
        bool hopLimitObserved)
        : this(target, hops, takenAt, destinationReached, hopLimitObserved, startedAt: takenAt)
    {
    }

    /// <summary>
    /// The full constructor, with the session start that reports use for the
    /// duration. The shorter overloads pass <paramref name="takenAt"/> as the
    /// start, so their routes report a zero duration.
    /// </summary>
    public Route(
        Target target,
        ImmutableArray<Hop> hops,
        DateTimeOffset takenAt,
        bool destinationReached,
        bool hopLimitObserved,
        DateTimeOffset startedAt)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Hops = hops;
        TakenAt = takenAt;
        StartedAt = startedAt;
        DestinationReached = destinationReached;
        HopLimitObserved = hopLimitObserved;
    }

    /// <summary>Preserves deconstruction of the original four-value record.</summary>
    public void Deconstruct(
        out Target target,
        out ImmutableArray<Hop> hops,
        out DateTimeOffset takenAt,
        out bool isTruncated)
    {
        target = Target;
        hops = Hops;
        takenAt = TakenAt;
        isTruncated = IsTruncated;
    }

    public void Deconstruct(
        out Target target,
        out ImmutableArray<Hop> hops,
        out DateTimeOffset takenAt,
        out bool destinationReached,
        out bool hopLimitObserved)
    {
        target = Target;
        hops = Hops;
        takenAt = TakenAt;
        destinationReached = DestinationReached;
        hopLimitObserved = HopLimitObserved;
    }
}
