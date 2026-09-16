// tests/WinMtr.TestSupport/RouteScript.cs
using System.Collections.Immutable;
using System.Net;
using WinMtr.Core;

namespace WinMtr.TestSupport;

/// <summary>
/// Builders for deterministic route snapshots. Shared so the console and
/// front-end suites describe the same paths with one vocabulary and no
/// duplicated construction.
/// </summary>
/// <remarks>
/// Glossary: route snapshot, responding hop, silent hop, trailing silence,
/// hop limit, route length, latest probe state. Route snapshots trim trailing
/// silence, so a sequence whose later entries are shorter exercises retained
/// hop rows; intermittent silence between responders is kept.
/// </remarks>
public static class RouteScript
{
    /// <summary>
    /// A target for scripted traces. Defaults to the documentation range so
    /// no scripted address can be mistaken for real traffic.
    /// </summary>
    public static Target TestTarget(
        string host = "example.com",
        string address = "203.0.113.9",
        string? canonicalName = "example.com") =>
        new(TargetExpression.Parse(host), IPAddress.Parse(address), canonicalName);

    /// <summary>
    /// A responding hop: a hop with a known address because it replied at
    /// least once. Carries the latest probe state for the status column and
    /// the optional probe error description for unreachable or failed probes.
    /// </summary>
    public static Hop RespondingHop(
        int index,
        string address,
        string? hostName = null,
        HopStatistics? stats = null,
        ProbeOutcome outcome = ProbeOutcome.Expired,
        ProbeErrorReason? reason = null) =>
        new(
            new HopIndex(index),
            IPAddress.Parse(address),
            hostName,
            stats ?? HopStatistics.Empty.Record(ProbeOutcome.Expired, 10),
            outcome,
            reason);

    /// <summary>
    /// A silent hop: a hop with no known address because it never replied.
    /// The host column stays empty for these rows; the latest probe state
    /// (ordinarily a timeout) belongs to the status column.
    /// </summary>
    public static Hop SilentHop(
        int index,
        HopStatistics? stats = null,
        ProbeOutcome outcome = ProbeOutcome.TimedOut,
        ProbeErrorReason? reason = null) =>
        new(
            new HopIndex(index),
            null,
            null,
            stats ?? HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0),
            outcome,
            reason);

    /// <summary>
    /// One immutable route snapshot at the given instant. Pass fewer hops
    /// than before to describe trailing silence trimming; the route length is
    /// the trimmed hop count, never padded to the hop limit.
    /// </summary>
    public static Route Snapshot(
        Target target,
        DateTimeOffset takenAt,
        ImmutableArray<Hop> hops,
        bool destinationReached = false,
        bool hopLimitObserved = false) =>
        new(target, hops, takenAt, destinationReached, hopLimitObserved);

    /// <summary>
    /// One immutable route snapshot at the given instant, from individual hops.
    /// </summary>
    public static Route Snapshot(
        Target target,
        DateTimeOffset takenAt,
        params Hop[] hops) =>
        new(target, ImmutableArray.Create(hops), takenAt);

    /// <summary>
    /// A controlled timestamped sequence: one route snapshot per hop set, one
    /// <paramref name="step"/> apart from <paramref name="start"/>. Elapsed
    /// session time derives from these timestamps, so tests control it without
    /// a second clock seam.
    /// </summary>
    public static IReadOnlyList<Route> TimestampedSequence(
        Target target,
        DateTimeOffset start,
        TimeSpan step,
        params Hop[][] perSnapshotHops)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(perSnapshotHops);

        var snapshots = new List<Route>(perSnapshotHops.Length);
        for (int i = 0; i < perSnapshotHops.Length; i++)
            snapshots.Add(new Route(target, ImmutableArray.Create(perSnapshotHops[i]), start + step * i));
        return snapshots;
    }
}
