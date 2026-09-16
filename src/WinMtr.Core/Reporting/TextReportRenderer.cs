using System.Globalization;
using System.Text;

namespace WinMtr.Core.Reporting;

/// <summary>
/// The fixed-width clipboard/table format: v0.92's statistics columns plus a
/// Status column carrying the latest probe observation. The report opens with
/// the target, the resolved address, the local start time and the duration, so
/// a pasted report is self-contained evidence. Each row carries its hop number
/// and the responder address. A latency cell with no measurement prints "--"
/// instead of "0", which would read as a real zero-millisecond reply. The
/// Status column intentionally extends the legacy layout and the loss column
/// prints the accurate one-decimal value, so this report is no longer
/// byte-identical to v0.92; the host and latency columns keep their legacy
/// widths and arithmetic. Row count also breaks v0.92: trailing silence is
/// trimmed so rows grow organically to the last responding hop. People paste
/// this into ISP tickets, so column widths and arithmetic beyond those
/// intentional changes are treated as a fixed contract.
/// </summary>
public sealed class TextReportRenderer : IReportRenderer
{
    /// <summary>
    /// Footer for new reports, including the shared remaster release version.
    /// </summary>
    public static readonly string DefaultFooter =
        $"WinMTR Remaster v{AppVersion.Display} GPL V2 by Konstantinos Zagoris";

    // The hop and address columns lead the table. The address column widens
    // to the longest rendered address because an IPv6 address is longer than
    // the "Address" header. The loss column is one cell wider than v0.92's
    // because loss now prints one decimal and "100.0" needs five cells. The
    // long Host field (40) and the numeric fields (4) keep their legacy
    // widths.
    private const string StatsHeader =
        "                       Host              -     % | Sent | Recv | Best | Avrg | Wrst | Last |";
    private const string StatsHeaderRule =
        "-------------------------------------------------|------|------|------|------|------|------|";
    private const string StatsFooter =
        "_________________________________________________|______|______|______|______|______|______|";

    private readonly string _footerText;
    private readonly TimeProvider _timeProvider;

    /// <param name="timeProvider">
    /// Supplies the local time zone for the start time. Tests pass a fake with
    /// a fixed zone so the golden file does not depend on the machine.
    /// </param>
    public TextReportRenderer(string? footer = null, TimeProvider? timeProvider = null)
    {
        _footerText = footer ?? DefaultFooter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => "Text";
    public string Extension => ".txt";

    public string Render(Route route)
    {
        var statuses = route.Hops.Select(h => h.StatusDescription).ToList();
        int statusWidth = Math.Max(
            "Status".Length,
            statuses.Count == 0 ? 0 : statuses.Max(s => s.Length));

        int addressWidth = Math.Max(
            "Address".Length,
            route.Hops.Length == 0 ? 0 : route.Hops.Max(h => h.Address?.ToString().Length ?? 0));
        string addressHeader = $"| Hop | {"Address".PadRight(addressWidth)} |";
        string addressRule = $"|-----|{new string('-', addressWidth + 2)}|";
        string addressFooter = $"|_____|{new string('_', addressWidth + 2)}|";

        int innerWidth = TotalWidth(addressWidth, statusWidth) - 2;
        string Rule(char fill) => "|" + new string(fill, innerWidth) + "|";
        string title = Center("WinMTR statistics", innerWidth);

        var sb = new StringBuilder(1024);
        sb.Append(CultureInfo.InvariantCulture, $"{Rule('-')}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"|{title}|\r\n");
        AppendMetadataLines(sb, TargetLine(route), innerWidth);
        AppendMetadataLines(sb, StartedLine(route), innerWidth);
        sb.Append(CultureInfo.InvariantCulture, $"{addressHeader}{StatsHeader} {"Status".PadRight(statusWidth)} |\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"{addressRule}{StatsHeaderRule} {new string('-', statusWidth)} |\r\n");

        for (int i = 0; i < route.Hops.Length; i++)
        {
            var hop = route.Hops[i];
            var s = hop.Stats;
            // Interpolation alignment must be a constant, so the address is
            // padded in code and right-aligned like the legacy columns.
            string address = (hop.Address?.ToString() ?? string.Empty).PadLeft(addressWidth);
            sb.Append(CultureInfo.InvariantCulture,
                $"| {hop.Index.DisplayNumber,3} | {address} |{hop.Label,40} - {LossText(s),5} | {s.Sent,4} | {s.Received,4} | {RttText(s.BestRttMs),4} | {RttText(s.AverageRttMsOrNull),4} | {RttText(s.WorstRttMs),4} | {RttText(s.LastRttMs),4} | {statuses[i].PadRight(statusWidth)} |\r\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"{addressFooter}{StatsFooter} {new string('_', statusWidth)} |\r\n");
        // Why two hops can show different Sent counts. It sits between the
        // table and the footer, so the footer stays the last thing read.
        sb.Append("\r\n");
        foreach (var line in ProbeIntervalNote.Lines)
            sb.Append("   ").Append(line).Append("\r\n");
        sb.Append("   ").Append(_footerText);
        return sb.ToString();
    }

    /// <summary>The target as the user typed it, and the address that was probed.</summary>
    private static string TargetLine(Route route) =>
        $"Target: {route.Target.Expression.Text} [{route.Target.Address}]";

    /// <summary>
    /// The local start time and the elapsed session time. The conversion uses
    /// the renderer's time provider so the result is deterministic under test.
    /// </summary>
    private string StartedLine(Route route)
    {
        DateTimeOffset started = TimeZoneInfo.ConvertTime(route.StartedAt, _timeProvider.LocalTimeZone);
        return $"Started: {started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} local"
            + $"   Duration: {FormatDuration(route.TakenAt - route.StartedAt)}";
    }

    /// <summary>
    /// Formats an elapsed time as HH:mm:ss. Total hours are used so a session
    /// longer than a day does not wrap, and a negative span clamps to zero.
    /// </summary>
    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return FormattableString.Invariant(
            $"{(long)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}");
    }

    /// <summary>One decimal place, as CSV and JSON print it. "100.0" is the widest value.</summary>
    private static string LossText(HopStatistics stats) =>
        stats.LossPercent.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>A missing measurement prints "--"; a real zero still prints "0".</summary>
    private static string RttText(int? rttMs) =>
        rttMs?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>
    /// Wraps one metadata line inside the table and pads every produced line
    /// to the inner width, so a long target cannot escape the border.
    /// </summary>
    private static void AppendMetadataLines(StringBuilder sb, string text, int innerWidth)
    {
        foreach (var line in Wrap(text, innerWidth))
            sb.Append(CultureInfo.InvariantCulture, $"|{line.PadRight(innerWidth)}|\r\n");
    }

    /// <summary>
    /// Wraps text inside the given width. Whole words are kept when they fit;
    /// a word wider than one line, such as a long hostname, is split at the
    /// width so no text is lost.
    /// </summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            if (text.Length - start <= width)
            {
                lines.Add(text[start..]);
                break;
            }

            // Prefer the last space that keeps the line within the width.
            int space = text.LastIndexOf(' ', start + width - 1, width);
            if (space > start)
            {
                lines.Add(text[start..space]);
                // The break space is not printed; it only separated words.
                start = space + 1;
            }
            else
            {
                lines.Add(text[start..(start + width)]);
                start += width;
            }
        }

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }

    private static int TotalWidth(int addressWidth, int statusWidth) =>
        // The statistics segment ends with the status cell's left border; the
        // status segment is a blank, the padded text, a blank, and a border.
        AddressPrefixWidth(addressWidth) + StatsHeader.Length + 1 + statusWidth + 1 + 1;

    /// <summary>The "| Hop | Address... |" prefix that leads every table line.</summary>
    private static int AddressPrefixWidth(int addressWidth) =>
        "| Hop | ".Length + addressWidth + " |".Length;

    private static string Center(string text, int width)
    {
        int left = (width - text.Length) / 2;
        int right = width - text.Length - left;
        return new string(' ', left) + text + new string(' ', right);
    }
}
