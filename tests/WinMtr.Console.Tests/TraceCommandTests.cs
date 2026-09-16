using System.Net;
using WinMtr.ConsoleApp;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;

namespace WinMtr.Console.Tests;

public sealed class TraceCommandTests
{
    [Fact]
    public async Task Preparation_failure_returns_one_and_names_the_target()
    {
        var expression = TargetExpression.Parse("example.com");
        var failure = new InvalidOperationException("dns down");
        Func<Tracer> factory = new TracerScript
        {
            ResolveFailure = failure,
            ChannelFactory = () => throw new InvalidOperationException("channel must not be created"),
            ResolverFactory = () => throw new InvalidOperationException("resolver must not be created"),
        }.BuildFactory();

        var (exit, _, error) = await RunWithRedirectedConsole(
            () => TraceCommand.RunAsync(expression, ProbeSettings.Default, CancellationToken.None, factory));

        Assert.Equal(1, exit);
        Assert.Contains("example.com", error);
        Assert.Contains("dns down", error);
    }

    [Fact]
    public async Task Precancelled_token_returns_one()
    {
        var expression = TargetExpression.Parse("127.0.0.1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (exit, _, _) = await RunWithRedirectedConsole(
            () => TraceCommand.RunAsync(expression, ProbeSettings.Default, cts.Token));

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Successful_empty_trace_prints_banner_and_returns_zero()
    {
        var expression = TargetExpression.Parse("127.0.0.1");
        Func<Tracer> factory = new TracerScript
        {
            Target = RouteScript.TestTarget("127.0.0.1", "127.0.0.1", null),
            Capabilities = new ProbeCapabilities(PayloadSupport.Supported),
            Snapshots = [],
        }.BuildFactory();

        var (exit, output, _) = await RunWithRedirectedConsole(
            () => TraceCommand.RunAsync(expression, ProbeSettings.Default, CancellationToken.None, factory));

        Assert.Equal(0, exit);
        Assert.Contains("Tracing 127.0.0.1 [127.0.0.1]", output);
    }

    [Fact]
    public async Task Restricted_payload_prints_warning_and_final_report()
    {
        var expression = TargetExpression.Parse("127.0.0.1");
        var target = RouteScript.TestTarget("127.0.0.1", "127.0.0.1", null);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Restricted),
            Snapshots = [RouteScript.Snapshot(target, DateTimeOffset.UnixEpoch)],
        }.BuildFactory();

        var (exit, output, _) = await RunWithRedirectedConsole(
            () => TraceCommand.RunAsync(expression, ProbeSettings.Default, CancellationToken.None, factory));

        Assert.Equal(0, exit);
        Assert.Contains("custom payload size unavailable", output);
        Assert.Contains("Final report:", output);
    }

    [Fact]
    public async Task Undetermined_payload_prints_warning_and_final_report()
    {
        var expression = TargetExpression.Parse("127.0.0.1");
        var target = RouteScript.TestTarget("127.0.0.1", "127.0.0.1", null);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Undetermined),
            Snapshots = [RouteScript.Snapshot(target, DateTimeOffset.UnixEpoch)],
        }.BuildFactory();

        var (exit, output, _) = await RunWithRedirectedConsole(
            () => TraceCommand.RunAsync(expression, ProbeSettings.Default, CancellationToken.None, factory));

        Assert.Equal(0, exit);
        Assert.Contains("could not be determined", output);
        Assert.Contains("Final report:", output);
    }

    [Fact]
    public async Task Live_and_final_output_show_a_status_column_with_error_reasons()
    {
        var expression = TargetExpression.Parse("127.0.0.1");
        var target = RouteScript.TestTarget("127.0.0.1", "127.0.0.1", null);
        var errorHop = RouteScript.RespondingHop(
            0,
            "192.0.2.1",
            null,
            HopStatistics.Empty.Record(ProbeOutcome.Unreachable, 0),
            ProbeOutcome.Unreachable,
            ProbeErrorReason.HostUnreachable);
        var timeoutHop = RouteScript.SilentHop(
            1,
            HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0));
        var route = RouteScript.Snapshot(target, DateTimeOffset.UnixEpoch, errorHop, timeoutHop);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Supported),
            Snapshots = [route],
        }.BuildFactory();

        var (exit, output, _) = await RunWithRedirectedConsole(
            () => TraceCommand.RunAsync(expression, ProbeSettings.Default, CancellationToken.None, factory));

        Assert.Equal(0, exit);
        // One status-aware table per live snapshot plus one final report.
        Assert.Equal(2, output.Split("Status", StringSplitOptions.None).Length - 1);
        Assert.Contains("Destination host unreachable.", output);
        Assert.Contains("No response.", output);
        // Identity is untouched: the address still labels the errored hop.
        Assert.Contains("192.0.2.1", output);
    }

    private static async Task<(int Exit, string Output, string Error)> RunWithRedirectedConsole(Func<Task<int>> run)
    {
        var originalOut = System.Console.Out;
        var originalError = System.Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        System.Console.SetOut(output);
        System.Console.SetError(error);
        try
        {
            int exit = await run();
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            System.Console.SetOut(originalOut);
            System.Console.SetError(originalError);
        }
    }
}
