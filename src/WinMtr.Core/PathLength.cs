using System.Net;

namespace WinMtr.Core;

/// <summary>
/// Decides the route length: the number of hops in the current trimmed route
/// snapshot, from the first TTL through the last responding hop or confirmed
/// destination. This started as v0.92's GetMax() heuristic, which was
/// previously unnamed and inlined, with one deliberate break: trailing
/// silence is trimmed to the last responding hop so rows grow organically
/// instead of padding to the hop limit.
/// </summary>
public static class PathLength
{
    /// <summary>
    /// Determines the route length over the session's hop rows. Rows that
    /// were never probed carry no address, so the trailing-silence rule
    /// already bounds the result — no ceiling parameter is needed.
    /// </summary>
    /// <param name="destinationReached">
    /// Current destination evidence. A null value keeps the original
    /// row-inference behaviour for existing callers; pass false explicitly so
    /// a retained hop row carrying the target address is never mistaken for a
    /// current successful reply.
    /// </param>
    public static int Determine(
        IReadOnlyList<Hop> hops,
        IPAddress target,
        bool? destinationReached)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentNullException.ThrowIfNull(target);

        int limit = hops.Count;
        if (limit <= 0) return 0;

        // Rule 1: the target itself answered somewhere -- the route ends there.
        if (destinationReached is not false)
        {
            for (int i = 0; i < limit; i++)
            {
                if (target.Equals(hops[i].Address)) return i + 1;
            }
        }

        // Rule 2: the target never answers pings. Trim trailing hops whose
        // responder repeats the one before it.
        // Rule 3 (remaster break from v0.92): trim trailing silence
        // (a silent hop tail) to the last responding hop. v0.92 kept them via
        // its `addr != 0` guard and padded unreached routes to the hop limit;
        // the remaster grows rows organically. Intermittent silence between
        // responding hops is kept.
        // Minimum is 1 so a fresh trace still shows a waiting row.
        int lastResponding = -1;
        for (int i = 0; i < limit; i++)
        {
            if (hops[i].Address is not null)
                lastResponding = i;
        }

        int max = lastResponding >= 0 ? lastResponding + 1 : 1;
        max = Math.Min(max, limit);
        while (max > 1
               && hops[max - 1].Address is { } last
               && last.Equals(hops[max - 2].Address))
        {
            max--;
        }

        return max;
    }

    /// <summary>
    /// Historical overload kept as a shim. New callers should use
    /// <see cref="Determine(IReadOnlyList{Hop}, IPAddress, bool?)"/> instead.
    /// </summary>
    [Obsolete("Use Determine(hops, target, destinationReached) instead; rows are already bounded by the state-maintained active extent.")]
    public static int Determine(IReadOnlyList<Hop> hops, IPAddress target, int maxHops) =>
        Determine(hops, target, maxHops, destinationReached: null);

    /// <summary>
    /// Historical overload kept as a shim. New callers should use
    /// <see cref="Determine(IReadOnlyList{Hop}, IPAddress, bool?)"/> instead.
    /// </summary>
    [Obsolete("Use Determine(hops, target, destinationReached) instead; rows are already bounded by the state-maintained active extent.")]
    public static int Determine(
        IReadOnlyList<Hop> hops,
        IPAddress target,
        int maxHops,
        bool? destinationReached)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentNullException.ThrowIfNull(target);

        int limit = Math.Min(maxHops, hops.Count);
        if (limit == hops.Count)
            return Determine(hops, target, destinationReached);

        if (limit <= 0)
            return 0;

        var slice = new Hop[limit];
        for (int i = 0; i < limit; i++)
            slice[i] = hops[i];
        return Determine(slice, target, destinationReached);
    }
}
