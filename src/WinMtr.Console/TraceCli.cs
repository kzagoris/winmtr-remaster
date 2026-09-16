using System.CommandLine;
using System.Globalization;
using System.CommandLine.Invocation;
using WinMtr.Core;

namespace WinMtr.ConsoleApp;

internal static class TraceCli
{
    internal static async Task<int> InvokeAsync(
        string[] args,
        Func<TargetExpression, ProbeSettings, CancellationToken, Task<int>>? run = null,
        InvocationConfiguration? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var host = new Argument<TargetExpression>("host")
        {
            Description = "Hostname or IP address to trace",
            // Classify at the parse boundary so a nonsense target is a usage error
            // rather than something we discover mid-resolution, or -- as in v0.92 --
            // never discover at all and trace as 255.255.255.255.
            CustomParser = result =>
            {
                if (!TargetExpression.TryParse(result.Tokens.Single().Value, out var expression, out var error))
                {
                    result.AddError(error);
                    return null;
                }
                return expression;
            }
        };
        var interval = new Option<double>("--interval")
        {
            Description = "Seconds between probes",
            DefaultValueFactory = _ => ProbeSettings.Default.Cadence.TotalSeconds,
            CustomParser = result =>
            {
                if (!double.TryParse(result.Tokens.Single().Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds)
                    || !double.IsFinite(seconds) || seconds <= 0
                    || seconds >= TimeSpan.MaxValue.TotalSeconds
                    || TimeSpan.FromSeconds(seconds) == TimeSpan.Zero)
                    result.AddError("--interval must be a finite positive duration of at least 100 ns within the TimeSpan range.");
                return seconds;
            }
        };
        var size = new Option<int>("--size")
        {
            Description = "Payload size in bytes",
            DefaultValueFactory = _ => ProbeSettings.Default.PayloadBytes,
            CustomParser = result => ParseInteger(result, 0, 65500)
        };
        var hopLimit = new Option<int>("--hop-limit")
        {
            Description = "Maximum hop limit (deprecated alias --max-hops is still accepted)",
            DefaultValueFactory = _ => ProbeSettings.Default.HopLimit,
            CustomParser = result => ParseInteger(result, 1, 255)
        };
        var legacyHopLimit = new Option<int?>("--max-hops")
        {
            Hidden = true,
            CustomParser = result => ParseInteger(result, 1, 255)
        };
        var numeric = new Option<bool>("--numeric") { Description = "Skip reverse DNS lookups" };
        var command = new RootCommand("Trace a network route with repeated probes")
        {
            host, interval, size, hopLimit, legacyHopLimit, numeric
        };
        command.SetAction((result, token) => (run ?? TraceCommand.RunAsync)(
            result.GetValue(host)!,
            ProbeSettings.Default with
            {
                Cadence = TimeSpan.FromSeconds(result.GetValue(interval)),
                PayloadBytes = result.GetValue(size),
                HopLimit = result.GetValue(legacyHopLimit) ?? result.GetValue(hopLimit),
                ResolveNames = !result.GetValue(numeric)
            }, token));

        var parsed = command.Parse(args);
        var exitCode = await parsed.InvokeAsync(configuration, cancellationToken);
        // Preserve the CLI's usage-error exit code; help and version remain successful.
        return parsed.Action is ParseErrorAction ? 2 : exitCode;
    }
    private static int ParseInteger(System.CommandLine.Parsing.ArgumentResult result, int min, int max)
    {
        if (!int.TryParse(result.Tokens.Single().Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            result.AddError($"{result.Argument.Name} must be an integer between {min} and {max}.");
        return value;
    }
}

