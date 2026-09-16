using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using WinMtr.Core.Probing;
using WinMtr.Core.Reporting;
using WinMtr.Core.Tracing;

namespace WinMtr.Core.Tests;

/// <summary>
/// Acceptance coverage for the probe-error-descriptions spec: specific
/// conditions stay distinguishable, the latest attempt always replaces the
/// previous state, identity never carries error text, and every user-visible
/// format reports the same observation.
/// </summary>
public sealed class ProbeErrorTests
{
    private static Target MakeTarget() =>
        new(TargetExpression.Parse("target"), IPAddress.Parse("203.0.113.9"), null);

    private static TraceState NewState(int hopLimit = 3) => new(MakeTarget(), hopLimit);

    [Theory]
    [InlineData(ProbeErrorReason.NetworkUnreachable, "Destination network unreachable.")]
    [InlineData(ProbeErrorReason.HostUnreachable, "Destination host unreachable.")]
    [InlineData(ProbeErrorReason.ProtocolUnreachable, "Destination protocol unreachable.")]
    [InlineData(ProbeErrorReason.PortUnreachable, "Destination port unreachable.")]
    [InlineData(ProbeErrorReason.PacketTooBig, "Packet was too big.")]
    [InlineData(ProbeErrorReason.BadRoute, "Bad route.")]
    [InlineData(ProbeErrorReason.BadOption, "Bad IP option was specified.")]
    [InlineData(ProbeErrorReason.HardwareError, "Hardware error occurred.")]
    [InlineData(ProbeErrorReason.InsufficientResources, "Insufficient IP resources were available.")]
    [InlineData(ProbeErrorReason.ReassemblyTimeExceeded, "The time to live expired during fragment reassembly.")]
    [InlineData(ProbeErrorReason.ParameterProblem, "Parameter problem.")]
    [InlineData(ProbeErrorReason.SourceQuench, "Datagrams are arriving too fast to be processed and datagrams may have been discarded.")]
    [InlineData(ProbeErrorReason.BadDestination, "Bad destination.")]
    [InlineData(ProbeErrorReason.LocalFailure, "Local probe failed.")]
    [InlineData(ProbeErrorReason.Unknown, "Probe failed: unknown reason.")]
    public void Descriptions_preserve_legacy_wording_with_safe_fallbacks(
        ProbeErrorReason reason, string expected)
    {
        Assert.Equal(expected, ProbeErrorDescriptions.ToDescription(reason));
    }

    [Theory]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeErrorReason.NetworkUnreachable)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeErrorReason.HostUnreachable)]
    [InlineData(IPStatus.DestinationProtocolUnreachable, ProbeErrorReason.ProtocolUnreachable)]
    [InlineData(IPStatus.DestinationPortUnreachable, ProbeErrorReason.PortUnreachable)]
    [InlineData(IPStatus.NoResources, ProbeErrorReason.InsufficientResources)]
    [InlineData(IPStatus.BadOption, ProbeErrorReason.BadOption)]
    [InlineData(IPStatus.HardwareError, ProbeErrorReason.HardwareError)]
    [InlineData(IPStatus.PacketTooBig, ProbeErrorReason.PacketTooBig)]
    [InlineData(IPStatus.BadRoute, ProbeErrorReason.BadRoute)]
    [InlineData(IPStatus.TtlReassemblyTimeExceeded, ProbeErrorReason.ReassemblyTimeExceeded)]
    [InlineData(IPStatus.ParameterProblem, ProbeErrorReason.ParameterProblem)]
    [InlineData(IPStatus.SourceQuench, ProbeErrorReason.SourceQuench)]
    [InlineData(IPStatus.BadDestination, ProbeErrorReason.BadDestination)]
    public void Known_platform_statuses_keep_their_specific_reason(
        IPStatus status, ProbeErrorReason expected)
    {
        Assert.Equal(expected, ProbeStatusMap.Reason(status));
    }

    [Theory]
    [InlineData(IPStatus.Success)]
    [InlineData(IPStatus.TtlExpired)]
    [InlineData(IPStatus.TimeExceeded)]
    [InlineData(IPStatus.TimedOut)]
    public void Non_error_statuses_carry_no_reason(IPStatus status)
    {
        Assert.Null(ProbeStatusMap.Reason(status));
    }

    [Theory]
    [InlineData(IPStatus.Unknown)]
    [InlineData(IPStatus.DestinationUnreachable)]
    [InlineData(IPStatus.BadHeader)]
    [InlineData(IPStatus.UnrecognizedNextHeader)]
    [InlineData(IPStatus.IcmpError)]
    [InlineData(IPStatus.DestinationScopeMismatch)]
    public void Unrecognized_statuses_use_the_honest_unknown_reason(IPStatus status)
    {
        Assert.Equal(ProbeErrorReason.Unknown, ProbeStatusMap.Reason(status));
    }

    [Fact]
    public void Unreachable_probe_stores_reason_and_derives_description()
    {
        var state = NewState();
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, IPAddress.Parse("192.0.2.1"), 0,
            ProbeErrorReason.HostUnreachable));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(ProbeOutcome.Unreachable, hop.LastOutcome);
        Assert.Equal(ProbeErrorReason.HostUnreachable, hop.LastErrorReason);
        Assert.Equal("Destination host unreachable.", hop.StatusDescription);
        // Errors count as sent but not received, exactly as before.
        Assert.Equal(1, hop.Stats.Sent);
        Assert.Equal(0, hop.Stats.Received);
    }

    [Fact]
    public void Error_then_timeout_clears_the_reason_and_shows_no_response()
    {
        var state = NewState();
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, IPAddress.Parse("192.0.2.1"), 0,
            ProbeErrorReason.HostUnreachable));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(ProbeOutcome.TimedOut, hop.LastOutcome);
        Assert.Null(hop.LastErrorReason);
        Assert.Equal("No response.", hop.StatusDescription);
        // The responder identity survives the silent probe.
        Assert.Equal(IPAddress.Parse("192.0.2.1"), hop.Address);
    }

    [Fact]
    public void Error_then_normal_reply_clears_the_error()
    {
        var state = NewState();
        var responder = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Failed, responder, 0, ProbeErrorReason.BadRoute));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, responder, 12));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(ProbeOutcome.Expired, hop.LastOutcome);
        Assert.Null(hop.LastErrorReason);
        Assert.Equal("", hop.StatusDescription);
        Assert.Equal(2, hop.Stats.Sent);
        Assert.Equal(1, hop.Stats.Received);
    }

    [Fact]
    public void Error_then_different_error_replaces_the_reason()
    {
        var state = NewState();
        var responder = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, responder, 0, ProbeErrorReason.HostUnreachable));
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Failed, responder, 0, ProbeErrorReason.PacketTooBig));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(ProbeErrorReason.PacketTooBig, hop.LastErrorReason);
        Assert.Equal("Packet was too big.", hop.StatusDescription);
    }

    [Fact]
    public void Error_without_specifics_does_not_reuse_the_earlier_reason()
    {
        var state = NewState();
        var responder = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, responder, 0, ProbeErrorReason.HostUnreachable));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Failed, responder, 0, null));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Null(hop.LastErrorReason);
        Assert.Equal("Probe failed: unknown reason.", hop.StatusDescription);
    }

    [Fact]
    public void Transit_ttl_expiry_is_not_an_error_while_reassembly_expiry_is()
    {
        var state = NewState();
        var responder = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Failed, responder, 0, ProbeErrorReason.HardwareError));
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, responder, 9));

        var transit = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Null(transit.LastErrorReason);
        Assert.Equal("", transit.StatusDescription);

        state.RecordProbe(2, new ProbeResult(
            ProbeOutcome.Failed, IPAddress.Parse("192.0.2.2"), 0,
            ProbeErrorReason.ReassemblyTimeExceeded));
        var reassembly = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[1];
        Assert.Equal(ProbeErrorReason.ReassemblyTimeExceeded, reassembly.LastErrorReason);
        Assert.Equal(
            "The time to live expired during fragment reassembly.",
            reassembly.StatusDescription);
    }

    [Fact]
    public void Error_on_a_discovered_named_hop_keeps_identity()
    {
        var state = NewState();
        var responder = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, responder, 10));
        state.ApplyDnsResult(1, responder, state.GetEpoch(1), "gw.example.net");

        // Same responder reports an error: identity must not move to the text.
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, responder, 0, ProbeErrorReason.PortUnreachable));
        // Absent responder on the next error: remembered identity still stands.
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, null, 0, ProbeErrorReason.PortUnreachable));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(responder, hop.Address);
        Assert.Equal("gw.example.net", hop.HostName);
        Assert.Equal("gw.example.net", hop.Label);
        Assert.Equal("Destination port unreachable.", hop.StatusDescription);
    }

    [Fact]
    public void Error_from_a_new_responder_follows_replacement_and_epoch_rules()
    {
        var state = NewState();
        var first = IPAddress.Parse("192.0.2.1");
        var second = IPAddress.Parse("192.0.2.2");
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, first, 10));
        state.ApplyDnsResult(1, first, state.GetEpoch(1), "first.example");
        long firstEpoch = state.GetEpoch(1);

        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, second, 0, ProbeErrorReason.HostUnreachable));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal(second, hop.Address);
        Assert.Null(hop.HostName);
        // A new epoch starts on responder change: statistics restart.
        Assert.Equal(1, hop.Stats.Sent);
        Assert.Equal(0, hop.Stats.Received);
        Assert.Equal(ProbeErrorReason.HostUnreachable, hop.LastErrorReason);

        // Stale DNS for the previous epoch cannot restore the old identity.
        Assert.False(state.ApplyDnsResult(1, first, firstEpoch, "stale.example"));
        Assert.True(state.ApplyDnsResult(1, second, state.GetEpoch(1), "second.example"));
        Assert.Equal("second.example", state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0].HostName);
    }

    [Fact]
    public void Dns_failure_stays_out_of_probe_errors()
    {
        var state = NewState();
        var responder = IPAddress.Parse("192.0.2.1");
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.Expired, responder, 10));
        Assert.False(state.ApplyDnsResult(1, responder, state.GetEpoch(1), null));

        var hop = state.Snapshot(DateTimeOffset.UnixEpoch).Hops[0];
        Assert.Equal("192.0.2.1", hop.Label);
        Assert.Null(hop.LastErrorReason);
        Assert.Equal("", hop.StatusDescription);
    }

    [Fact]
    public void Fresh_hops_wait_rather_than_report_a_timeout()
    {
        var hop = Hop.Empty(0);
        Assert.Equal("Waiting for first result.", hop.StatusDescription);

        var snapshot = NewState(hopLimit: 2).Snapshot(DateTimeOffset.UnixEpoch);
        Assert.All(snapshot.Hops, h =>
        {
            Assert.Equal(0, h.Stats.Sent);
            Assert.Equal("Waiting for first result.", h.StatusDescription);
        });
    }

    [Fact]
    public void Json_reports_named_diagnostics_and_waiting_nulls()
    {
        // Waiting coverage uses an intermittent gap: trailing silence is
        // trimmed organically, so a trailing waiting hop would disappear.
        var state = NewState(hopLimit: 3);
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, IPAddress.Parse("192.0.2.1"), 0,
            ProbeErrorReason.NetworkUnreachable));
        state.RecordProbe(3, new ProbeResult(
            ProbeOutcome.Expired, IPAddress.Parse("192.0.2.2"), 7));

        using var doc = JsonDocument.Parse(new JsonReportRenderer().Render(
            state.Snapshot(DateTimeOffset.UnixEpoch)));
        var hops = doc.RootElement.GetProperty("hops");
        Assert.Equal(3, hops.GetArrayLength());

        var error = hops[0];
        Assert.Equal("Unreachable", error.GetProperty("outcome").GetString());
        Assert.Equal("NetworkUnreachable", error.GetProperty("errorReason").GetString());
        Assert.Equal("Destination network unreachable.", error.GetProperty("status").GetString());
        // Identity fields keep their existing meaning.
        Assert.Equal("192.0.2.1", error.GetProperty("address").GetString());

        var waiting = hops[1];
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("outcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("errorReason").ValueKind);
        Assert.Equal("Waiting for first result.", waiting.GetProperty("status").GetString());

        var normal = hops[2];
        Assert.Equal("Expired", normal.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, normal.GetProperty("errorReason").ValueKind);
        Assert.Equal("", normal.GetProperty("status").GetString());
    }

    [Fact]
    public void Csv_appends_a_status_column_with_the_shared_presentation()
    {
        // Organic growth trims the trailing waiting hop, so only the probed
        // timeout row remains alongside the header.
        var state = NewState(hopLimit: 2);
        state.RecordProbe(1, new ProbeResult(ProbeOutcome.TimedOut, null, 0));

        var lines = new CsvReportRenderer()
            .Render(state.Snapshot(DateTimeOffset.UnixEpoch))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(",Status", lines[0]);
        Assert.EndsWith(",No response.", lines[1]);
    }

    [Fact]
    public void Csv_status_values_use_the_existing_escaping_rules()
    {
        var route = Fixtures.SixHopRoute with
        {
            Hops = [Hop.Empty(0) with { HostName = "a,b\"c" }]
        };
        var csv = new CsvReportRenderer().Render(route);
        Assert.Contains("\"a,b\"\"c\"", csv);
        Assert.Contains(",Waiting for first result.", csv);
    }

    [Fact]
    public void Text_output_has_a_status_column_alongside_statistics()
    {
        // No-response coverage uses an intermittent gap: trailing silence is
        // trimmed organically, so the silent hop sits between responders.
        var state = NewState(hopLimit: 4);
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, IPAddress.Parse("192.0.2.1"), 0,
            ProbeErrorReason.HostUnreachable));
        state.RecordProbe(2, new ProbeResult(
            ProbeOutcome.Expired, IPAddress.Parse("192.0.2.2"), 7));
        state.RecordProbe(3, new ProbeResult(ProbeOutcome.TimedOut, null, 0));
        state.RecordProbe(4, new ProbeResult(
            ProbeOutcome.Expired, IPAddress.Parse("192.0.2.4"), 9));

        var text = new TextReportRenderer().Render(state.Snapshot(DateTimeOffset.UnixEpoch));
        var lines = text.Split("\r\n");
        // Rule, title, target, started, header, rule, four hop rows, footer
        // rule, a blank line, the four lines of the probe interval note,
        // footer text.
        Assert.Equal(17, lines.Length);
        Assert.Contains("Status", lines[4]);
        Assert.Contains("Destination host unreachable.", lines[6]);
        Assert.Contains("No response.", lines[8]);
        // A normal reply carries no message: the status cell is blank padding.
        Assert.EndsWith("| " + new string(' ', "Destination host unreachable.".Length) + " |", lines[7]);
        Assert.EndsWith("| " + new string(' ', "Destination host unreachable.".Length) + " |", lines[9]);
        // One table: every line but the footer text shares the same width.
        Assert.All(lines[..11], line => Assert.Equal(lines[0].Length, line.Length));
        Assert.EndsWith(TextReportRenderer.DefaultFooter, lines[^1]);
    }

    [Fact]
    public void Text_and_html_reports_carry_status_text_without_touching_identity()
    {
        var state = NewState(hopLimit: 1);
        state.RecordProbe(1, new ProbeResult(
            ProbeOutcome.Unreachable, IPAddress.Parse("192.0.2.1"), 0,
            ProbeErrorReason.HostUnreachable));
        var route = state.Snapshot(DateTimeOffset.UnixEpoch);

        Assert.Contains("Destination host unreachable.", new TextReportRenderer().Render(route));
        Assert.Contains("Destination host unreachable.", new HtmlReportRenderer().Render(route));
        Assert.Contains("Status", new TextReportRenderer().Render(route));
        Assert.Contains("192.0.2.1", new TextReportRenderer().Render(route));
    }
}
