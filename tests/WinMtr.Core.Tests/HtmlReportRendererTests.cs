using WinMtr.Core.Reporting;

namespace WinMtr.Core.Tests;

public class HtmlReportRendererTests
{
    private static string Render() => new HtmlReportRenderer().Render(Fixtures.SixHopRoute);

    [Fact]
    public void Emits_the_v092_document_shell()
    {
        var html = Render();
        Assert.StartsWith("<html><head><title>WinMTR Statistics</title></head><body bgcolor=\"white\">\r\n", html);
        Assert.Contains("<center><h2>WinMTR statistics</h2></center>\r\n", html);
        Assert.Contains("</table>\r\n", html);
        Assert.EndsWith("</body></html>\r\n", html);
    }

    [Fact]
    public void Emits_one_row_per_hop_plus_a_header_row()
    {
        var html = Render();
        Assert.Equal(7, html.Split("<tr>").Length - 1);
    }

    [Fact]
    public void Uses_the_same_legacy_loss_arithmetic_as_the_text_report()
    {
        var html = Render();
        Assert.Contains("<td>ae-3.r20.frnkft13.de.bb.example.net</td> <td>   3</td>", html);
    }

    [Fact]
    public void Escapes_html_special_characters_in_host_labels()
    {
        // v0.92 did not escape, which allowed a hostile PTR record to inject
        // markup into a report someone pastes into a ticket system.
        var route = Fixtures.SixHopRoute with
        {
            Hops = [Hop.Empty(0) with { HostName = "<script>alert(1)</script>" }]
        };
        var html = new HtmlReportRenderer().Render(route);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Renderer_declares_its_name_and_extension()
    {
        var r = new HtmlReportRenderer();
        Assert.Equal("HTML", r.Name);
        Assert.Equal(".html", r.Extension);
    }
}
