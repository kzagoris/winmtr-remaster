// tests/WinMtr.Infrastructure.Tests/CachingNameResolverTests.cs
// These are deterministic unit tests: the DNS lookup is injected, so no test
// here touches the network. Only the tagged smoke test at the bottom does.
using System.Net;
using System.Net.Sockets;
using WinMtr.Infrastructure;

namespace WinMtr.Infrastructure.Tests;

public class CachingNameResolverTests
{
    private static readonly IPAddress Addr = IPAddress.Parse("192.0.2.1");  // TEST-NET-1

    [Fact]
    public async Task A_lookup_that_finds_nothing_returns_null_rather_than_throwing()
    {
        var r = new CachingNameResolver((_, _) => Task.FromResult<string?>(null));
        Assert.Null(await r.ResolveAsync(Addr, default));
    }

    [Fact]
    public async Task Repeated_lookups_of_the_same_address_hit_the_cache()
    {
        var calls = 0;
        var r = new CachingNameResolver((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("gw.local");
        });

        Assert.Equal("gw.local", await r.ResolveAsync(Addr, default));
        Assert.Equal("gw.local", await r.ResolveAsync(Addr, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Concurrent_lookups_of_the_same_address_are_single_flight()
    {
        // A plain check-then-resolve cache lets every concurrent caller start
        // its own lookup. With 30 workers converging on one router that is 30
        // identical DNS queries.
        var calls = 0;
        var gate = new TaskCompletionSource();

        var r = new CachingNameResolver(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return "gw.local";
        });

        var inFlight = Enumerable.Range(0, 20)
                                 .Select(_ => r.ResolveAsync(Addr, default))
                                 .ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(inFlight);

        Assert.All(results, n => Assert.Equal("gw.local", n));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancelling_one_caller_does_not_poison_the_shared_lookup()
    {
        var gate = new TaskCompletionSource();
        var r = new CachingNameResolver(async (_, _) => { await gate.Task; return "gw.local"; });

        using var cts = new CancellationTokenSource();
        var cancelled = r.ResolveAsync(Addr, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        Assert.Equal("gw.local", await r.ResolveAsync(Addr, default));
    }

    [Fact]
    public async Task Cancellation_is_observed_before_any_lookup_starts()
    {
        var r = new CachingNameResolver((_, _) => Task.FromResult<string?>("never"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => r.ResolveAsync(Addr, cts.Token));
    }

    [Fact]
    public async Task A_failing_lookup_is_cached_as_null_with_no_retry()
    {
        // Pins the documented negative-caching decision: a transient failure
        // at discovery time blanks the hostname for the session rather than
        // re-hitting DNS.
        var calls = 0;
        var r = new CachingNameResolver((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new SocketException(11001);
        });

        Assert.Null(await r.ResolveAsync(Addr, default));
        Assert.Null(await r.ResolveAsync(Addr, default));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task The_least_recently_used_entry_is_evicted_once_capacity_is_exceeded()
    {
        // Bounds the session cache: a route that churns for days introduces a
        // new address at an existing hop index indefinitely, and every entry
        // was previously retained for the life of the session.
        var calls = new Dictionary<string, int>();
        var r = new CachingNameResolver(
            (a, _) =>
            {
                lock (calls)
                {
                    calls[a.ToString()] = calls.GetValueOrDefault(a.ToString()) + 1;
                }

                return Task.FromResult<string?>("gw.local");
            },
            capacity: 2);

        var a = IPAddress.Parse("192.0.2.1");
        var b = IPAddress.Parse("192.0.2.2");
        var c = IPAddress.Parse("192.0.2.3");

        await r.ResolveAsync(a, default);
        await r.ResolveAsync(b, default);
        await r.ResolveAsync(a, default);   // a is now the more recently used of the two
        await r.ResolveAsync(c, default);   // evicts b

        await r.ResolveAsync(a, default);   // still cached
        await r.ResolveAsync(b, default);   // evicted, so re-resolved

        Assert.Equal(1, calls["192.0.2.1"]);
        Assert.Equal(2, calls["192.0.2.2"]);
    }

    [Fact]
    public async Task An_entry_still_awaiting_DNS_is_never_evicted()
    {
        // Eviction must not weaken the single-flight guarantee: dropping an
        // in-flight entry would let the next caller start a second query for
        // the address already being looked up.
        var calls = 0;
        var gate = new TaskCompletionSource();

        var r = new CachingNameResolver(
            async (address, _) =>
            {
                if (!address.Equals(Addr))
                {
                    return "other.local";
                }

                Interlocked.Increment(ref calls);
                await gate.Task;
                return "gw.local";
            },
            capacity: 1);

        var first = r.ResolveAsync(Addr, default);                              // in flight, holds the only slot
        await r.ResolveAsync(IPAddress.Parse("192.0.2.9"), default);            // over capacity: must not evict the in-flight entry
        var second = r.ResolveAsync(Addr, default);

        gate.SetResult();
        Assert.Equal("gw.local", await first);
        Assert.Equal("gw.local", await second);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void A_capacity_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CachingNameResolver(capacity: 0));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_resolver_returns_null_for_an_unresolvable_address()
    {
        var r = new CachingNameResolver();
        Assert.Null(await r.ResolveAsync(Addr, default));
    }
}
