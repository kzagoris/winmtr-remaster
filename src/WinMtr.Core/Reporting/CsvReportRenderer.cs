using System.Globalization;
using System.Text;

namespace WinMtr.Core.Reporting;

/// <summary>
/// A new format, so it carries none of v0.92's display conventions: it reports the
/// accurate <see cref="HopStatistics.LossPercent"/> rather than the legacy integer
/// form the text and HTML reports are pinned to, and leaves absent measurements empty.
/// </summary>
public sealed class CsvReportRenderer : IReportRenderer
{
    public string Name => "CSV";
    public string Extension => ".csv";

    public string Render(Route route)
    {
        var sb = new StringBuilder(1024);
        sb.Append("Hop,Host,Address,LossPercent,Sent,Received,BestMs,AverageMs,WorstMs,LastMs,Status\r\n");

        foreach (var hop in route.Hops)
        {
            var s = hop.Stats;
            sb.Append(CultureInfo.InvariantCulture,
                $"{hop.Index.DisplayNumber},{Quote(hop.Label)},{Quote(hop.Address?.ToString() ?? "")},{Math.Round(s.LossPercent, 2)},{s.Sent},{s.Received},{Number(s.BestRttMs)},{Number(s.AverageRttMsOrNull)},{Number(s.WorstRttMs)},{Number(s.LastRttMs)},{Quote(hop.StatusDescription)}\r\n");
        }

        return sb.ToString();
    }

    // Deliberately NOT mitigating spreadsheet formula injection: a label beginning
    // '=', '+', '-' or '@' is evaluated as a formula by Excel and Sheets, but the
    // usual fix -- prefixing an apostrophe -- corrupts the value for every
    // programmatic consumer, and no standard CSV writer does it. Escaping here stays
    // RFC 4180; rendering safety belongs to whatever opens the file.
    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    /// <summary>Absent samples are empty, not zero: a hop that never replied
    /// has no measurement, and emitting 0 invites a spreadsheet to average it in.</summary>
    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
