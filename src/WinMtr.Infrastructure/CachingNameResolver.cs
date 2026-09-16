// src/WinMtr.Infrastructure/CachingNameResolver.cs
using System.Net;
using WinMtr.Core.Probing;

namespace WinMtr.Infrastructure;

/// <summary>
/// Reverse DNS with a per-session cache. v0.92 started an unbounded, uncached
/// OS thread per discovered address and blocked it in gethostbyaddr with no
/// timeout.
/// </summary>
/// <remarks>
/// <para>
/// Failures (including no PTR record) are cached as null until eviction or the
/// end of the session. There is no timed retry, keeping DNS off the hot path.
/// </para>
/// <para>
/// The capacity defaults to <see cref="DefaultCapacity"/> entries. On each
/// resolve, the least recently used completed lookups are evicted if needed.
/// In-flight entries are retained even above capacity; completion alone does
/// not trigger eviction. Eviction is size-based, so retained failures do not
/// expire and cause timed retries.
/// </para>
/// </remarks>
public sealed class CachingNameResolver : INameResolver
{
    /// <summary>
    /// Covers the worst legitimate session — the 255-hop ceiling of an IP TTL,
    /// each hop load-balanced across some sixteen addresses — in roughly a
    /// megabyte, so an ordinary trace never evicts anything.
    /// </summary>
    public const int DefaultCapacity = 4096;

    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly Func<IPAddress, CancellationToken, Task<string?>> _lookup;
    private readonly int _capacity;

    // Protects the cache, recency updates, and eviction. DNS runs outside it.
    private readonly Lock _cacheGate = new();
    private long _clock;

    /// <param name="lookup">
    /// Injected so unit tests are deterministic and never touch the network.
    /// Defaults to real reverse DNS.
    /// </param>
    /// <param name="capacity">
    /// Target retained entries; in-flight lookups may exceed it. Injected like
    /// <paramref name="lookup"/>: eviction is otherwise only reachable by
    /// manufacturing thousands of entries. No production call site sets it.
    /// </param>
    public CachingNameResolver(
        Func<IPAddress, CancellationToken, Task<string?>>? lookup = null,
        int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        _lookup = lookup ?? DefaultLookupAsync;
        _capacity = capacity;
    }

    public async Task<string?> ResolveAsync(IPAddress address, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var key = address.ToString();
        CacheEntry entry;
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out entry!))
            {
                // Callers share one lookup, started outside the cache lock.
                entry = new CacheEntry(new Lazy<Task<string?>>(
                    () => SafeLookupAsync(address),
                    LazyThreadSafetyMode.ExecutionAndPublication));
                _cache.Add(key, entry);
            }

            entry.LastUsed = ++_clock;
            Evict();
        }

        // The shared lookup deliberately does NOT take the caller's token --
        // otherwise one caller cancelling would poison the cached task for
        // every other caller. Each caller instead abandons its own wait.
        return await entry.Lookup.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops least-recently-used entries until the cache is back within
    /// capacity. Entries whose lookup has not settled are never candidates:
    /// evicting one would let the next caller start a second query for an
    /// address already being resolved, weakening single-flight to best-effort.
    /// </summary>
    private void Evict()
    {
        // Called with the cache lock held.
        while (_cache.Count > _capacity)
        {
            KeyValuePair<string, CacheEntry> victim = default;
            var found = false;

            // Scan only on overflow; no separate recency list to maintain.
            foreach (var candidate in _cache)
            {
                if (!candidate.Value.HasSettled)
                {
                    continue;
                }

                if (!found || candidate.Value.LastUsed < victim.Value.LastUsed)
                {
                    victim = candidate;
                    found = true;
                }
            }

            // Everything still in flight: the next resolve reconsiders it.
            if (!found)
            {
                return;
            }

            _cache.Remove(victim.Key);
        }
    }

    private async Task<string?> SafeLookupAsync(IPAddress address)
    {
        try
        {
            return await _lookup(address, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No PTR record, or DNS is unavailable. The dotted quad stands.
            return null;
        }
    }

    private static async Task<string?> DefaultLookupAsync(IPAddress address, CancellationToken ct)
    {
        // The string overload accepts cancellation and reverse-resolves IP literals.
        var entry = await Dns.GetHostEntryAsync(address.ToString(), ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == address.ToString()
            ? null
            : entry.HostName;
    }

    private sealed class CacheEntry(Lazy<Task<string?>> lookup)
    {
        public Lazy<Task<string?>> Lookup { get; } = lookup;

        public long LastUsed { get; set; }

        /// <summary>
        /// False while the lookup is still running, and also during the brief
        /// window where another thread is inside the Lazy factory: reading
        /// <c>Value</c> then would block on that thread while holding the
        /// cache lock.
        /// </summary>
        public bool HasSettled => Lookup.IsValueCreated && Lookup.Value.IsCompleted;
    }
}
