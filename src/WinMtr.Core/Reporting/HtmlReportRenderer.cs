using System.Globalization;
using System.Net;
using System.Text;

namespace WinMtr.Core.Reporting;

/// <summary>
/// v0.92's HTML table, with two deliberate changes: host labels are HTML-escaped
/// (the original interpolated PTR records straight into the document), and a
/// Status column carries the latest probe observation. Byte-to-byte
/// compatibility with the legacy report is explicitly not required; improving
/// the report takes precedence over preserving its exact bytes.
/// </summary>
public sealed class HtmlReportRenderer : IReportRenderer
{
    public string Name => "HTML";
    public string Extension => ".html";

    public string Render(Route route)
    {
        var sb = new StringBuilder(2048);
        sb.Append("<html><head><title>WinMTR Statistics</title></head><body bgcolor=\"white\">\r\n");
        sb.Append("<center><h2>WinMTR statistics</h2></center>\r\n");
        sb.Append("<p align=\"center\"> <table border=\"1\" align=\"center\">\r\n");
        sb.Append("<tr><td>Host</td> <td>%</td> <td>Sent</td> <td>Recv</td> <td>Best</td> <td>Avrg</td> <td>Wrst</td> <td>Last</td> <td>Status</td></tr>\r\n");

        foreach (var hop in route.Hops)
        {
            var s = hop.Stats;
            sb.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{WebUtility.HtmlEncode(hop.Label)}</td> <td>{s.LegacyLossPercent,4}</td> <td>{s.Sent,4}</td> <td>{s.Received,4}</td> <td>{(s.BestRttMs ?? 0),4}</td> <td>{s.AverageRttMs,4}</td> <td>{(s.WorstRttMs ?? 0),4}</td> <td>{(s.LastRttMs ?? 0),4}</td> <td>{WebUtility.HtmlEncode(hop.StatusDescription)}</td></tr>\r\n");
        }

        sb.Append("</table>\r\n");
        // Why two hops can show different Sent counts.
        sb.Append(CultureInfo.InvariantCulture,
            $"<p align=\"center\">{WebUtility.HtmlEncode(ProbeIntervalNote.Text)}</p>\r\n");
        sb.Append("</body></html>\r\n");
        return sb.ToString();
    }
}
