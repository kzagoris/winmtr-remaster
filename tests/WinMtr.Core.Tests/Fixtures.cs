using System.Collections.Immutable;
using System.Net;

namespace WinMtr.Core.Tests;

public static class Fixtures
{
    private static Hop Row(int index, string? address, string? hostName,
                           int sent, int received, long total,
                           int? best, int? worst, int? last) =>
        new(new HopIndex(index),
            address is null ? null : IPAddress.Parse(address),
            hostName,
            new HopStatistics(sent, received, total, last, best, worst),
            received > 0 ? ProbeOutcome.Expired : ProbeOutcome.TimedOut,
            null);

    /// <summary>
    /// Six hops chosen so the derived columns exercise integer truncation:
    /// avg is total/received and loss is 100 - 100*received/sent, both
    /// integer division, so rows 5 and 6 render 3 and 1 rather than 2 and 0.
    /// </summary>
    public static Route SixHopRoute { get; } = new(
        new Target(TargetExpression.Parse("github.com"), IPAddress.Parse("140.82.121.4"), "github.com"),
        [
            Row(0, "192.168.1.1", null, 214, 214, 500, 1, 19, 1),
            Row(1, "100.64.0.1", null, 214, 214, 1300, 4, 41, 5),
            Row(2, "198.51.100.231", "ae1.core.example.net", 214, 214, 2000, 7, 63, 8),
            Row(3, null, null, 214, 0, 0, null, null, null),
            Row(4, "203.0.113.38", "ae-3.r20.frnkft13.de.bb.example.net", 214, 209, 7200, 31, 118, 32),
            Row(5, "140.82.121.4", "github.com", 214, 213, 7500, 33, 96, 34)
        ],
        DateTimeOffset.UnixEpoch);
}
