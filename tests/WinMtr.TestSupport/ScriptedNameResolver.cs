// tests/WinMtr.TestSupport/ScriptedNameResolver.cs
using System.Net;
using WinMtr.Core.Probing;

namespace WinMtr.TestSupport;

/// <summary>
/// Deterministic offline <see cref="INameResolver"/> substitute. Maps
/// responding-hop addresses to hostnames and answers null where no name is
/// available, so a hop falls back to its address and never goes anonymous
/// merely because reverse DNS failed.
/// </summary>
public sealed class ScriptedNameResolver : INameResolver
{
    private readonly Func<IPAddress, CancellationToken, Task<string?>>? _resolveAsync;
    private readonly IReadOnlyDictionary<IPAddress, string?>? _names;
    private readonly string? _fallback;
    private readonly List<IPAddress> _calls = [];

    /// <summary>
    /// A resolver with no names: every address resolves to null.
    /// </summary>
    public ScriptedNameResolver()
    {
    }

    /// <summary>
    /// A resolver answering from a fixed map. Addresses absent from the map
    /// answer <paramref name="fallback"/> (null by default).
    /// </summary>
    public ScriptedNameResolver(IReadOnlyDictionary<IPAddress, string?> names, string? fallback = null)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _fallback = fallback;
    }

    /// <summary>
    /// A resolver answering via a delegate, for changing answers or failures.
    /// A throwing delegate reads as advisory DNS: the engine keeps the numeric
    /// identity, so tests asserting resilience can fail lookups on demand.
    /// </summary>
    public ScriptedNameResolver(Func<IPAddress, CancellationToken, Task<string?>> resolveAsync)
    {
        _resolveAsync = resolveAsync ?? throw new ArgumentNullException(nameof(resolveAsync));
    }

    /// <summary>Each resolved address in order.</summary>
    public IReadOnlyList<IPAddress> Calls
    {
        get
        {
            lock (_calls)
                return _calls.ToArray();
        }
    }

    public Task<string?> ResolveAsync(IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        ct.ThrowIfCancellationRequested();

        lock (_calls)
            _calls.Add(address);

        if (_resolveAsync is not null)
            return _resolveAsync(address, ct);

        if (_names is not null && _names.TryGetValue(address, out string? name))
            return Task.FromResult(name);

        return Task.FromResult(_fallback);
    }
}
