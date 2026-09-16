using System.Text.Json;
using WinMtr.Core.Reporting;

namespace WinMtr.Core.Tests;

public class NewFormatRendererTests
{
    [Fact]
    public void Csv_has_a_header_and_one_line_per_hop()
    {
        var csv = new CsvReportRenderer().Render(Fixtures.SixHopRoute);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, lines.Length);
        Assert.Equal("Hop,Host,Address,LossPercent,Sent,Received,BestMs,AverageMs,WorstMs,LastMs,Status", lines[0]);
        Assert.StartsWith("1,192.168.1.1,192.168.1.1,0", lines[1]);
    }

    [Fact]
    public void Csv_quotes_and_escapes_labels_containing_commas_or_quotes()
    {
        var route = Fixtures.SixHopRoute with
        {
            Hops = [Hop.Empty(0) with { HostName = "a,b\"c" }]
        };
        var csv = new CsvReportRenderer().Render(route);
        Assert.Contains("\"a,b\"\"c\"", csv);
    }

    [Fact]
    public void Csv_uses_the_accurate_loss_percentage()
    {
        var csv = new CsvReportRenderer().Render(Fixtures.SixHopRoute);
        // 214 sent, 209 received => 2.34%, not the legacy integer 3
        Assert.Contains(",2.34,214,209,", csv);
    }

    [Fact]
    public void Json_emits_target_hops_and_accurate_loss()
    {
        var json = new JsonReportRenderer().Render(Fixtures.SixHopRoute);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("github.com", root.GetProperty("target").GetString());
        Assert.Equal(6, root.GetProperty("hops").GetArrayLength());
        var first = root.GetProperty("hops")[0];
        Assert.Equal(1, first.GetProperty("hop").GetInt32());
        Assert.Equal(214, first.GetProperty("sent").GetInt32());

        // Row 5 is 214 sent / 209 received: the accurate formula gives 2.34,
        // the legacy integer form would give 3. This is what distinguishes a
        // new format from a legacy one.
        Assert.Equal(2.34, root.GetProperty("hops")[4].GetProperty("lossPercent").GetDouble());
    }

    [Fact]
    public void Renderers_declare_their_names_and_extensions()
    {
        Assert.Equal((".csv", "CSV"), (new CsvReportRenderer().Extension, new CsvReportRenderer().Name));
        Assert.Equal((".json", "JSON"), (new JsonReportRenderer().Extension, new JsonReportRenderer().Name));
    }
}
