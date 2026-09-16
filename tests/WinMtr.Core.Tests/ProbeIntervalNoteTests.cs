using WinMtr.Core.Reporting;

namespace WinMtr.Core.Tests;

/// <summary>
/// WM-11: the hops do not share one observation window, so the reports say
/// why the Sent counts of two hops can be different. The statement covers
/// every hop, and it is the same statement everywhere.
/// </summary>
public class ProbeIntervalNoteTests
{
    [Fact]
    public void Note_is_one_paragraph_made_of_its_printable_lines()
    {
        Assert.NotEmpty(ProbeIntervalNote.Lines);
        Assert.Equal(string.Join(" ", ProbeIntervalNote.Lines), ProbeIntervalNote.Text);
        Assert.All(ProbeIntervalNote.Lines, line => Assert.True(line.Length <= 88, line));
    }

    [Fact]
    public void Note_covers_every_hop_and_promises_no_policy()
    {
        // A responding hop with intermittent loss slows down in the same way,
        // so the note must not single out the silent hop, and there is no
        // backoff policy to name.
        Assert.DoesNotContain("silent", ProbeIntervalNote.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backing off", ProbeIntervalNote.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reply timeout", ProbeIntervalNote.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Text_report_carries_the_note_between_the_table_and_the_footer()
    {
        var text = new TextReportRenderer().Render(Fixtures.SixHopRoute);

        foreach (var line in ProbeIntervalNote.Lines)
            Assert.Contains("   " + line + "\r\n", text);

        Assert.True(
            text.IndexOf(ProbeIntervalNote.Lines[0], StringComparison.Ordinal)
                > text.IndexOf('_'),
            "the note must follow the table");
        Assert.EndsWith("   " + TextReportRenderer.DefaultFooter, text);
    }

    [Fact]
    public void Html_report_carries_the_note_after_the_table()
    {
        var html = new HtmlReportRenderer().Render(Fixtures.SixHopRoute);

        Assert.Contains(ProbeIntervalNote.Text, html);
        Assert.True(
            html.IndexOf(ProbeIntervalNote.Text, StringComparison.Ordinal)
                > html.IndexOf("</table>", StringComparison.Ordinal),
            "the note must follow the table");
        Assert.EndsWith("</body></html>\r\n", html);
    }

    [Fact]
    public void Machine_formats_stay_free_of_the_note()
    {
        // CSV and JSON are read by programs; a new line or field breaks them.
        Assert.DoesNotContain(
            ProbeIntervalNote.Lines[0], new CsvReportRenderer().Render(Fixtures.SixHopRoute));
        Assert.DoesNotContain(
            ProbeIntervalNote.Lines[0], new JsonReportRenderer().Render(Fixtures.SixHopRoute));
    }
}
