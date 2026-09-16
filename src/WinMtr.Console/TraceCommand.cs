using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Core.Reporting;
using WinMtr.Infrastructure;

namespace WinMtr.ConsoleApp;

internal static class TraceCommand
{
    internal static Task<int> RunAsync(
        TargetExpression targetExpression, ProbeSettings settings, CancellationToken cancellationToken)
        => RunAsync(targetExpression, settings, cancellationToken, tracerFactory: null);

    internal static async Task<int> RunAsync(
        TargetExpression targetExpression,
        ProbeSettings settings,
        CancellationToken cancellationToken,
        Func<Tracer>? tracerFactory)
    {
        PreparedTrace trace;
        try
        {
            trace = await (tracerFactory?.Invoke() ?? new Tracer()).CreateTraceAsync(
                targetExpression.Text, settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("trace preparation was cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"trace preparation failed for {targetExpression}: {ex.Message}");
            return 1;
        }

        var renderer = new TextReportRenderer();

        Console.WriteLine($"Tracing {trace.Target.Expression} [{trace.Target.Address}] - press Ctrl+C to stop");
        switch (trace.Capabilities.PayloadSupport)
        {
            case PayloadSupport.Supported:
                break;
            case PayloadSupport.Restricted:
                Console.WriteLine("note: custom payload size unavailable without cap_net_raw; using the default payload");
                break;
            case PayloadSupport.Undetermined:
                Console.WriteLine("note: custom payload support could not be determined; using the default payload");
                break;
        }

        Route? last = null;
        await foreach (var route in trace.Snapshots)
        {
            last = route;
            // Console.Clear() throws when output is redirected or there is no
            // terminal, which would otherwise kill a piped or headless run.
            if (!Console.IsOutputRedirected) Console.Clear();
            Console.WriteLine(renderer.Render(route));
        }

        if (last is not null)
        {
            Console.WriteLine();
            Console.WriteLine("Final report:");
            Console.WriteLine(renderer.Render(last));

            if (last.IsTruncated)
            {
                // v0.92 said nothing here, so a route longer than the hop limit looked
                // like a route that simply ended.
                Console.WriteLine();
                Console.WriteLine($"note: a probe at the configured hop limit ({settings.HopLimit} hops) completed without confirming the destination; "
                                  + "the target may be silent or filtered, or the route may extend farther.");
            }
        }

        return 0;
    }
}
