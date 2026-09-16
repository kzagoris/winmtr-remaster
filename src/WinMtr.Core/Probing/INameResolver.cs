using System.Net;

namespace WinMtr.Core.Probing;

public interface INameResolver
{
    /// <summary>Reverse-resolves an address, or returns null if it cannot.</summary>
    Task<string?> ResolveAsync(IPAddress address, CancellationToken ct);
}
