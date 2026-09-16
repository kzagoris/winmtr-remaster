using System.Net;
using System.Net.Sockets;
using WinMtr.Core;
using WinMtr.Core.Probing;

namespace WinMtr.Infrastructure;

/// <summary>
/// Resolves a hostname through DNS; literal addresses are already probeable and
/// never reach the resolver, so a trace to an IP starts without waiting on a
/// lookup that could only fail. One <c>GetHostEntryAsync</c> yields both the
/// address and the canonical name, so the name costs no extra round trip.
/// </summary>
public sealed class DnsTargetResolver : ITargetResolver
{
    public async Task<Target> ResolveAsync(TargetExpression expression, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (expression is TargetExpression.Hostname hostname)
            return await ResolveHostNameAsync(hostname, ct).ConfigureAwait(false);

        var address = expression.Match(
            _ => throw new InvalidOperationException("Hostnames are handled above."),
            v4 => v4.Value,
            v6 => v6.Value);

        return new Target(expression, address, CanonicalName: null);
    }

    private static async Task<Target> ResolveHostNameAsync(
        TargetExpression.Hostname hostname, CancellationToken ct)
    {
        var entry = await Dns.GetHostEntryAsync(hostname.Value, ct).ConfigureAwait(false);

        var address = Array.Find(
            entry.AddressList,
            a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            ?? throw new InvalidOperationException("no address");

        // GetHostEntry can answer with an empty HostName; fall back to what was typed
        // rather than storing a blank canonical name.
        var canonical = string.IsNullOrWhiteSpace(entry.HostName) ? hostname.Value : entry.HostName;

        return new Target(hostname, address, canonical);
    }
}
