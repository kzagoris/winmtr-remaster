using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using WinMtr.Core.Reporting;

namespace WinMtr.Core.Tests;

public class TextReportRendererTests
{
    // The renderer converts the start time with its time provider's local
    // zone. A fixed UTC zone keeps the rendered bytes independent of the
    // machine that runs the tests, so the golden file stays byte-stable.
    private static readonly FakeTimeProvider UtcClock = CreateUtcClock();

    private static FakeTimeProvider CreateUtcClock()
    {
        var clock = new FakeTimeProvider();
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    private static TextReportRenderer Renderer(string? footer = null) => new(footer, UtcClock);

    private static string[] Lines(Route route) => Renderer().Render(route).Split("\r\n");

    [Fact]
    public void TextReport_matches_the_pinned_golden_file()
    {
        // Compare BYTES, not decoded text. File.ReadAllText strips a BOM and
        // normalises encodings, so a golden file saved as UTF-8-with-BOM or
        // UTF-16 would compare equal while having entirely the wrong bytes --
        // which defeats the point of a byte-exact characterisation test.
        // The golden file pins the current layout (hop and address columns,
        // report context header, statistics plus Status); byte-to-byte
        // fidelity to v0.92 itself is not required.
        var expected = File.ReadAllBytes(Path.Combine("golden", "report-6hops.txt"));
        var actual = Encoding.UTF8.GetBytes(Renderer().Render(Fixtures.SixHopRoute));

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Exported_text_uses_CRLF_not_CRCRLF()
    {
        // Regression guard for v0.92 defect D4: the buffer already held \r\n
        // and was then written with fopen(path, "wt"), whose text-mode
        // translation turned every \n into \r\n, producing \r\r\n on disk.
        var text = Renderer().Render(Fixtures.SixHopRoute);
        Assert.DoesNotContain("\r\r\n", text);
        // 13 table lines (rule, title, target, started, header, header rule,
        // six rows, footer rule), one blank line, and the four lines of the
        // probe interval note. The footer line itself carries no terminator.
        Assert.Equal(18, text.Count(c => c == '\n'));
        Assert.Equal(18, text.Count(c => c == '\r'));
    }

    [Fact]
    public void Report_does_not_end_with_a_newline()
    {
        var text = Renderer().Render(Fixtures.SixHopRoute);
        Assert.False(text.EndsWith('\n'));
        Assert.EndsWith(TextReportRenderer.DefaultFooter, text);
    }

    [Fact]
    public void Header_names_target_address_start_time_and_duration()
    {
        var text = Renderer().Render(Fixtures.SixHopRoute);

        Assert.Contains("Target: github.com [140.82.121.4]", text);
        // SixHopRoute is taken at the Unix epoch and never started before it,
        // so the report starts and ends at the same instant.
        Assert.Contains("Started: 1970-01-01 00:00:00 local   Duration: 00:00:00", text);
    }

    [Fact]
    public void Header_duration_is_taken_at_minus_started_at()
    {
        var startedAt = new DateTimeOffset(2024, 3, 5, 22, 30, 0, TimeSpan.Zero);
        var route = Fixtures.SixHopRoute with
        {
            StartedAt = startedAt,
            TakenAt = startedAt + new TimeSpan(1, 2, 3)
        };

        var text = Renderer().Render(route);

        Assert.Contains("Started: 2024-03-05 22:30:00 local", text);
        Assert.Contains("Duration: 01:02:03", text);
    }

    [Fact]
    public void Header_duration_never_goes_negative()
    {
        var route = Fixtures.SixHopRoute with
        {
            StartedAt = DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(1),
            TakenAt = DateTimeOffset.UnixEpoch
        };

        Assert.Contains("Duration: 00:00:00", Renderer().Render(route));
    }

    [Fact]
    public void Header_start_time_uses_the_time_providers_local_zone()
    {
        var clock = new FakeTimeProvider();
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone(
            "utc-plus-two", TimeSpan.FromHours(2), "utc-plus-two", "utc-plus-two"));

        var text = new TextReportRenderer(timeProvider: clock).Render(Fixtures.SixHopRoute);

        Assert.Contains("Started: 1970-01-01 02:00:00 local", text);
    }

    [Fact]
    public void Silent_hop_renders_dash_latency_cells_never_zero()
    {
        var lines = Lines(Fixtures.SixHopRoute);
        var silent = lines[9];

        // Hop 4 has 214 sent and 0 received: an empty host and address, and
        // four absent measurements that must not read as zero milliseconds.
        // The fixture's widest address is 14 characters, so that is the
        // computed address column width.
        Assert.Contains("|   4 | " + new string(' ', 14) + " |", silent);
        Assert.Contains("|   -- |   -- |   -- |   -- |", silent);
        Assert.Contains("No response.", silent);
        Assert.DoesNotContain("|    0 |    0 |    0 |    0 |", silent);
    }

    [Fact]
    public void Loss_prints_with_one_decimal_place()
    {
        var lines = Lines(Fixtures.SixHopRoute);

        // Hop 4 is 214 sent / 0 received, the widest loss value.
        Assert.Contains("- 100.0 |", lines[9]);
        // Hop 5 is 214 sent / 209 received: the accurate formula gives 2.34,
        // the legacy integer form gave 3.
        Assert.Contains("-   2.3 |", lines[10]);
        // Hop 6 is 214 sent / 213 received: 0.47, not the legacy 1.
        Assert.Contains("-   0.5 |", lines[11]);
    }

    [Fact]
    public void Rows_show_hop_number_and_responder_address()
    {
        var lines = Lines(Fixtures.SixHopRoute);

        // The address column is as wide as the fixture's longest address,
        // 198.51.100.231 (14 characters), so addresses are right-aligned
        // inside it.
        Assert.Contains("|   1 |    192.168.1.1 |", lines[6]);
        Assert.Contains("|   2 |     100.64.0.1 |", lines[7]);
        Assert.Contains("|   3 | 198.51.100.231 |", lines[8]);
        Assert.Contains("ae1.core.example.net", lines[8]);
    }

    [Fact]
    public void Ipv6_addresses_widen_the_address_column_and_keep_the_table_aligned()
    {
        var ipv6 = IPAddress.Parse("2001:db8:1234:5678::1");
        var hops = Fixtures.SixHopRoute.Hops.SetItem(
            0, Fixtures.SixHopRoute.Hops[0] with { Address = ipv6, HostName = null });
        var route = Fixtures.SixHopRoute with { Hops = hops };

        var lines = Lines(route);

        // The address column fits the 21-character IPv6 address, so the
        // table is wider than the IPv4 table and every table line keeps one
        // width.
        Assert.True(lines[0].Length > Lines(Fixtures.SixHopRoute)[0].Length);
        Assert.All(lines[..13], line => Assert.Equal(lines[0].Length, line.Length));

        Assert.Contains("|   1 | " + ipv6 + " |", lines[6]);
        // The statistics columns still line up under their headers.
        int statsStart = lines[4].IndexOf("| Sent |", StringComparison.Ordinal);
        Assert.True(statsStart > 0);
        Assert.Equal("|  214 |", lines[6].Substring(statsStart, "|  214 |".Length));
    }

    [Fact]
    public void Long_target_wraps_inside_the_table_and_stays_complete()
    {
        // 135 characters, longer than the inner width, so the target must
        // wrap. It is one DNS name, so the wrap has to break the word.
        string hostname = new string('a', 63) + "." + new string('b', 63) + ".example";
        var target = new Target(
            TargetExpression.Parse(hostname), IPAddress.Parse("2001:db8::1"), null);
        var route = Fixtures.SixHopRoute with { Target = target };

        var lines = Lines(route);
        int width = lines[0].Length;

        // No line escapes the table border, including the wrapped target.
        Assert.All(lines, line => Assert.True(line.Length <= width, line));

        int header = Array.FindIndex(lines, l => l.StartsWith("| Hop |", StringComparison.Ordinal));
        Assert.True(header > 4, "the long target must wrap to more than one line");

        // Joining the metadata lines rebuilds the full target text and the
        // resolved address.
        string metadata = string.Concat(lines[2..header].Select(l => l.Trim('|').TrimEnd()));
        Assert.Contains(hostname, metadata);
        Assert.Contains("[2001:db8::1]", metadata);
    }

    [Fact]
    public void Table_lines_share_one_width()
    {
        var lines = Lines(Fixtures.SixHopRoute);

        // Rule, title, target, started, header, header rule, six rows, footer.
        Assert.All(lines[..13], line => Assert.Equal(lines[0].Length, line.Length));
    }

    [Fact]
    public void Footer_is_overridable()
    {
        var text = Renderer("custom footer").Render(Fixtures.SixHopRoute);
        Assert.EndsWith("   custom footer", text);
    }

    [Fact]
    public void Default_footer_is_remaster_branding_without_legacy_sponsor()
    {
        var text = Renderer().Render(Fixtures.SixHopRoute);
        Assert.EndsWith("   " + TextReportRenderer.DefaultFooter, text);
        Assert.DoesNotContain("Appnor", text);
        Assert.DoesNotContain("v0.92", text);
    }

    [Fact]
    public void Renderer_declares_its_name_and_extension()
    {
        var r = new TextReportRenderer();
        Assert.Equal("Text", r.Name);
        Assert.Equal(".txt", r.Extension);
    }
}
