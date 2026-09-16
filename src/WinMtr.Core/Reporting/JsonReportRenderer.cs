using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMtr.Core.Reporting;

public sealed record ReportHopDto(
    [property: JsonPropertyName("hop")] int Hop,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("lossPercent")] double LossPercent,
    [property: JsonPropertyName("sent")] int Sent,
    [property: JsonPropertyName("received")] int Received,
    [property: JsonPropertyName("bestMs")] int? BestMs,
    [property: JsonPropertyName("averageMs")] int? AverageMs,
    [property: JsonPropertyName("worstMs")] int? WorstMs,
    [property: JsonPropertyName("lastMs")] int? LastMs,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("errorReason")] string? ErrorReason,
    [property: JsonPropertyName("status")] string Status);

public sealed record ReportDto(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("targetAddress")] string TargetAddress,
    [property: JsonPropertyName("canonicalName")] string? CanonicalName,
    [property: JsonPropertyName("takenAt")] DateTimeOffset TakenAt,
    [property: JsonPropertyName("hops")] IReadOnlyList<ReportHopDto> Hops);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReportDto))]
public partial class ReportJsonContext : JsonSerializerContext;

/// <summary>
/// A new format, so it carries none of v0.92's display conventions: it reports the
/// accurate <see cref="HopStatistics.LossPercent"/> rather than the legacy integer
/// form the text and HTML reports are pinned to, and absent measurements are null.
/// The serializer context is source-generated -- reflection-based serialization
/// warns under the trim and AOT analyzers and would break Native AOT.
/// </summary>
public sealed class JsonReportRenderer : IReportRenderer
{
    public string Name => "JSON";
    public string Extension => ".json";

    public string Render(Route route)
    {
        var dto = new ReportDto(
            route.Target.Expression.Text,
            route.Target.Address.ToString(),
            route.Target.CanonicalName,
            route.TakenAt,
            [.. route.Hops.Select(h =>
            {
                // Before the first completed attempt there is no observation:
                // outcome and reason stay null and the waiting message stands
                // in, rather than the empty-hop timeout placeholder.
                bool observed = h.Stats.Sent > 0;
                return new ReportHopDto(
                    h.Index.DisplayNumber,
                    h.Label,
                    h.Address?.ToString(),
                    Math.Round(h.Stats.LossPercent, 2),
                    h.Stats.Sent,
                    h.Stats.Received,
                    h.Stats.BestRttMs,
                    h.Stats.AverageRttMsOrNull,
                    h.Stats.WorstRttMs,
                    h.Stats.LastRttMs,
                    observed ? h.LastOutcome.ToString() : null,
                    observed ? h.LastErrorReason?.ToString() : null,
                    h.StatusDescription);
            })]);

        return JsonSerializer.Serialize(dto, ReportJsonContext.Default.ReportDto);
    }
}
