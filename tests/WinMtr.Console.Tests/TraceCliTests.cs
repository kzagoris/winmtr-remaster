using System.CommandLine;
using WinMtr.ConsoleApp;
using WinMtr.Core;

namespace WinMtr.Console.Tests;

public class TraceCliTests
{
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private InvocationConfiguration Configuration => new() { Output = output, Error = error, EnableDefaultExceptionHandler = false };

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_succeeds_without_starting_a_trace(string flag)
    {
        var exit = await TraceCli.InvokeAsync([flag], UnexpectedTrace, Configuration);
        Assert.Equal(0, exit);
        Assert.Contains("--interval", output.ToString());
        Assert.Contains("--hop-limit", output.ToString());
        // The deprecated alias stays accepted but hidden: it may appear
        // inside the --hop-limit description, never as its own option entry.
        Assert.DoesNotContain(
            output.ToString().Split('\n'),
            line => line.TrimStart().StartsWith("--max-hops", StringComparison.Ordinal));
        Assert.Contains("host", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Bare_target_keeps_defaults_and_handler_exit_code()
    {
        var called = false;
        var exit = await TraceCli.InvokeAsync(["example.com"], (host, settings, _) =>
        {
            called = true;
            Assert.Equal("example.com", host.Text);
            Assert.Equal(ProbeSettings.Default, settings);
            return Task.FromResult(1);
        }, Configuration);
        Assert.True(called);
        Assert.Equal(1, exit);
    }

    [Theory]
    [InlineData(false, "--hop-limit")]
    [InlineData(false, "--max-hops")]
    [InlineData(true, "--hop-limit")]
    [InlineData(true, "--max-hops")]
    public async Task Options_can_precede_or_follow_the_host(bool hostFirst, string hopLimitOption)
    {
        string[] options = ["--interval", "0.5", "--size", "128", hopLimitOption, "12", "--numeric"];
        string[] args = hostFirst ? ["example.com", .. options] : [.. options, "example.com"];
        var called = false;
        Assert.Equal(0, await TraceCli.InvokeAsync(args, (host, settings, _) =>
        {
            called = true;
            Assert.Equal("example.com", host.Text);
            Assert.Equal(TimeSpan.FromSeconds(0.5), settings.Cadence);
            Assert.Equal(128, settings.PayloadBytes);
            Assert.Equal(12, settings.HopLimit);
            Assert.False(settings.ResolveNames);
            return Task.FromResult(0);
        }, Configuration));
        Assert.True(called);
    }

    [Theory]
    [InlineData()]
    [InlineData("--numeric")]
    [InlineData("example.com", "extra-host")]
    [InlineData("example.com", "--frob")]
    [InlineData("example.com", "--interval")]
    [InlineData("example.com", "--size")]
    [InlineData("example.com", "--hop-limit")]
    [InlineData("example.com", "--interval", "bogus")]
    [InlineData("example.com", "--interval", "NaN")]
    [InlineData("example.com", "--interval", "Infinity")]
    [InlineData("example.com", "--interval", "1e30")]
    [InlineData("example.com", "--interval", "1e-20")]
    [InlineData("example.com", "--interval", "0")]
    [InlineData("example.com", "--size", "-1")]
    [InlineData("example.com", "--size", "65501")]
    [InlineData("example.com", "--size", "bogus")]
    [InlineData("example.com", "--hop-limit", "0")]
    [InlineData("example.com", "--hop-limit", "256")]
    [InlineData("example.com", "--max-hops", "0")]
    [InlineData("example.com", "--max-hops", "256")]
    [InlineData("999.999.999.999")]     // D-A: v0.92 traced this as 255.255.255.255
    [InlineData("1.2.3.4.5.6")]         // D-A: too many octets, legacy called it an IP
    [InlineData("...")]                 // D-A: dots alone passed the digits-and-dots test
    [InlineData("http://x")]            // a URL is not a host
    [InlineData("-bad.com")]            // label starts with a hyphen
    public async Task Invalid_arguments_exit_two_without_starting_a_trace(params string[] args)
    {
        Assert.Equal(2, await TraceCli.InvokeAsync(args, UnexpectedTrace, Configuration));
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData("8.8.8.8", typeof(TargetExpression.IPv4Literal))]
    [InlineData("::1", typeof(TargetExpression.IPv6Literal))]
    [InlineData("example.com", typeof(TargetExpression.Hostname))]
    public async Task Host_argument_reaches_the_handler_already_classified(string input, Type expected)
    {
        var called = false;
        Assert.Equal(0, await TraceCli.InvokeAsync([input], (host, _, _) =>
        {
            called = true;
            Assert.IsType(expected, host);
            Assert.Equal(input, host.Text);
            return Task.FromResult(0);
        }, Configuration));
        Assert.True(called);
    }

    [Fact]
    public async Task Cancellation_reaches_the_handler_and_allows_cleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanedUp = false;
        var exit = await TraceCli.InvokeAsync(["example.com"], async (_, _, token) =>
        {
            cancellation.Cancel();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cleanedUp = true;
            }
            return 0;
        }, Configuration, cancellation.Token);
        Assert.True(cleanedUp);
        Assert.Equal(0, exit);
    }

    private static Task<int> UnexpectedTrace(TargetExpression host, ProbeSettings settings, CancellationToken token)
        => throw new InvalidOperationException("Invalid arguments or help must not start tracing.");
}
