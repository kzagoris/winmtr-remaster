using System.Net;
using WinMtr.Core;
using WinMtr.Infrastructure;

namespace WinMtr.Infrastructure.Tests;

public class DnsTargetResolverTests
{
    private readonly DnsTargetResolver resolver = new();

    [Theory]
    [InlineData("8.8.8.8")]                 // IPv4 literal
    [InlineData("127.0.0.1")]               // loopback
    [InlineData("::1")]                     // IPv6 literal
    [InlineData("2001:db8::dead:beef")]     // documentation range, never resolvable
    public async Task Literal_addresses_resolve_without_touching_DNS(string input)
    {
        // No lookup means this passes with the network unplugged, and means a trace
        // to an IP cannot fail on a name service it never needed.
        var expression = TargetExpression.Parse(input);

        using var noTimeToResolve = new CancellationTokenSource();
        noTimeToResolve.Cancel();

        var target = await resolver.ResolveAsync(expression, noTimeToResolve.Token);

        Assert.Equal(IPAddress.Parse(input), target.Address);
        Assert.Same(expression, target.Expression);
        Assert.Null(target.CanonicalName);
    }

    [Fact]
    public async Task Null_expression_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => resolver.ResolveAsync(null!, CancellationToken.None));
    }
}
