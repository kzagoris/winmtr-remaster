namespace WinMtr.Core;

/// <summary>
/// Settings for one trace session. These are passed INTO the engine. In v0.92
/// the engine held a raw pointer to the dialog and read these off the live
/// window from worker threads on every iteration, unsynchronised.
/// </summary>
public sealed record ProbeSettings(
    TimeSpan Cadence,
    int PayloadBytes,
    int HopLimit,
    TimeSpan ReplyTimeout,
    bool ResolveNames,
    TimeSpan SnapshotInterval)
{
    public static ProbeSettings Default { get; } = new(
        Cadence: TimeSpan.FromSeconds(1),
        PayloadBytes: 64,
        HopLimit: 30,
        ReplyTimeout: TimeSpan.FromSeconds(5),
        ResolveNames: true,
        SnapshotInterval: TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Compatibility forwarder for the previous property name.
    /// New code should use <see cref="HopLimit"/>.
    /// </summary>
    [Obsolete("Use HopLimit instead.")]
    public int MaxHops { get => HopLimit; init => HopLimit = value; }

    /// <summary>
    /// Rejects settings that would misbehave rather than fail: HopLimit 0 ends
    /// the trace instantly, a negative value throws deep inside array
    /// construction, a zero cadence spins a probe loop as fast as the network
    /// allows, a zero snapshot interval spins the publication loop, and a
    /// payload above 65500 is rejected by Ping itself.
    /// TTL is a single octet, so 255 is the hard ceiling for HopLimit.
    /// </summary>
    public void Validate()
    {
        if (HopLimit is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(HopLimit), HopLimit, "HopLimit must be between 1 and 255.");
        if (PayloadBytes is < 0 or > 65500)
            throw new ArgumentOutOfRangeException(nameof(PayloadBytes), PayloadBytes, "PayloadBytes must be between 0 and 65500.");
        if (Cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Cadence), Cadence, "Cadence must be greater than zero.");
        if (ReplyTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ReplyTimeout), ReplyTimeout, "ReplyTimeout must be greater than zero.");
        if (SnapshotInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SnapshotInterval), SnapshotInterval, "SnapshotInterval must be greater than zero.");
    }
}
