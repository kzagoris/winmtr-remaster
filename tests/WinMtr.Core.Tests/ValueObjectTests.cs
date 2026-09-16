using System.Collections.Immutable;
using System.Net;

namespace WinMtr.Core.Tests;

public class ValueObjectTests
{
    [Fact]
    public void HopIndex_owns_the_plus_one()
    {
        var i = new HopIndex(0);
        Assert.Equal(1, i.Ttl);
        Assert.Equal(1, i.DisplayNumber);
        Assert.Equal(30, new HopIndex(29).Ttl);
    }

    [Fact]
    public void ProbeSettings_defaults_match_v092()
    {
        var d = ProbeSettings.Default;
        Assert.Equal(TimeSpan.FromSeconds(1), d.Cadence);
        Assert.Equal(64, d.PayloadBytes);
        Assert.Equal(30, d.HopLimit);
        Assert.Equal(TimeSpan.FromSeconds(5), d.ReplyTimeout);
        Assert.True(d.ResolveNames);
    }

    [Theory]
    [InlineData(0, 64, 1, 5, 250)]        // HopLimit too low
    [InlineData(256, 64, 1, 5, 250)]      // HopLimit above the TTL octet ceiling
    [InlineData(30, -1, 1, 5, 250)]       // negative payload
    [InlineData(30, 65501, 1, 5, 250)]    // payload above Ping's limit
    [InlineData(30, 64, 0, 5, 250)]       // zero cadence spins the probe loop
    [InlineData(30, 64, 1, 0, 250)]       // zero reply timeout
    [InlineData(30, 64, 1, 5, 0)]         // zero snapshot interval spins publication
    public void Validate_rejects_settings_that_would_misbehave(
        int hopLimit, int payloadBytes, int cadenceSeconds, int timeoutSeconds, int snapshotMs)
    {
        var settings = new ProbeSettings(
            TimeSpan.FromSeconds(cadenceSeconds), payloadBytes, hopLimit,
            TimeSpan.FromSeconds(timeoutSeconds), true, TimeSpan.FromMilliseconds(snapshotMs));

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }

    [Fact]
    public void Validate_accepts_the_defaults()
    {
        ProbeSettings.Default.Validate();   // must not throw
    }

    [Fact]
    public void Empty_hop_has_not_responded_and_shows_empty_host()
    {
        var h = Hop.Empty(3);
        Assert.False(h.HasResponded);
        Assert.Equal(string.Empty, h.Label);
        Assert.Equal(4, h.Index.DisplayNumber);
    }

    [Fact]
    public void Label_prefers_hostname_then_address()
    {
        var addr = IPAddress.Parse("10.0.0.1");
        var noName = Hop.Empty(0) with { Address = addr };
        Assert.Equal("10.0.0.1", noName.Label);

        var named = noName with { HostName = "gw.local" };
        Assert.Equal("gw.local", named.Label);
    }

    [Fact]
    public void Error_text_does_not_overwrite_hostname()
    {
        // Regression guard for v0.92 defect D3, where the single `name[255]`
        // field held hostname, dotted quad AND error text, so one transient
        // timeout permanently replaced the hostname in the table and reports.
        var hop = Hop.Empty(0) with
        {
            Address = IPAddress.Parse("10.0.0.1"),
            HostName = "gw.local"
        };

        var afterError = hop with
        {
            Address = IPAddress.Parse("10.0.0.1"),
            HostName = "gw.local",
            LastOutcome = ProbeOutcome.Unreachable,
            LastErrorReason = ProbeErrorReason.HostUnreachable,
            Stats = HopStatistics.Empty.Record(ProbeOutcome.Unreachable, 0)
        };

        Assert.Equal("gw.local", afterError.Label);
        Assert.Equal("Destination host unreachable.", afterError.StatusDescription);
    }

    [Theory]
    [InlineData("github.com")]              // ordinary name
    [InlineData("a.b.c.d.example.com")]     // many labels
    [InlineData("xn--bcher-kva.example")]   // punycode IDN
    [InlineData("localhost")]               // single label, no dot
    public void TargetExpression_classifies_names_as_hostnames(string input)
    {
        var expression = TargetExpression.Parse(input);
        var hostname = Assert.IsType<TargetExpression.Hostname>(expression);
        Assert.Equal(input, hostname.Value);
        Assert.Equal(input, expression.Text);
    }

    [Theory]
    [InlineData("8.8.8.8")]                 // dotted quad
    [InlineData("127.0.0.1")]               // loopback
    [InlineData("255.255.255.255")]         // broadcast, valid when typed deliberately
    public void TargetExpression_classifies_dotted_quads_as_IPv4(string input)
    {
        var v4 = Assert.IsType<TargetExpression.IPv4Literal>(TargetExpression.Parse(input));
        Assert.Equal(IPAddress.Parse(input), v4.Value);
    }

    [Theory]
    [InlineData("::1", "::1")]                          // loopback
    [InlineData("[::1]", "::1")]                        // URL-authority brackets are punctuation
    [InlineData("fe80::1%eth0", "fe80::1%eth0")]        // scope id survives
    [InlineData("2001:db8::dead:beef", "2001:db8::dead:beef")]
    public void TargetExpression_classifies_IPv6_including_scope(string input, string expected)
    {
        // v0.92 could not represent these at all: it read the resolved address as
        // *(int *)host->h_addr (WinMTRDialog.cpp:1009), four bytes, so only AF_INET survived.
        var v6 = Assert.IsType<TargetExpression.IPv6Literal>(TargetExpression.Parse(input));
        Assert.Equal(IPAddress.Parse(expected), v6.Value);
    }

    [Theory]
    [InlineData("999.999.999.999")]     // octets out of range
    [InlineData("1.2.3.4.5.6")]         // too many octets
    [InlineData("...")]                 // dots alone
    [InlineData("1.2.3")]               // partial dotted quad
    [InlineData("01.2.3.4")]            // leading zero, ambiguous octal
    [InlineData("123")]                 // .NET would read this as 0.0.0.123
    [InlineData("1.2.3.4.")]            // trailing dot on an address
    public void TargetExpression_rejects_digits_and_dots_that_are_not_addresses(string input)
    {
        // Regression guard for v0.92 defect D-A. InitMTRNet classified a target as an
        // IP with `while(*t) if(!isdigit(*t) && *t!='.') isIP=0;` (WinMTRDialog.cpp:952),
        // so each of these reached inet_addr, which answers INADDR_NONE -- and WinMTR
        // silently traced 255.255.255.255 rather than reporting a bad target.
        // Rejected outright -- so there is no address at all to be wrong about,
        // and in particular none that silently became 255.255.255.255.
        Assert.False(TargetExpression.TryParse(input, out var expression, out var error));
        Assert.Null(expression);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]              // no target at all
    [InlineData("")]                // empty
    [InlineData("   ")]             // whitespace only
    [InlineData("http://x")]        // a URL is not a host
    [InlineData("-bad.com")]        // label starts with a hyphen
    [InlineData("bad-.com")]        // label ends with a hyphen
    [InlineData("has space.com")]   // embedded space
    public void TargetExpression_rejects_input_that_is_not_a_host(string? input)
    {
        Assert.False(TargetExpression.TryParse(input, out var expression, out var error));
        Assert.Null(expression);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TargetExpression_rejects_a_name_longer_than_DNS_allows()
    {
        // v0.92 read the target into `char strtmp[255]` (WinMTRDialog.cpp:944); the
        // bound is now the protocol's, not a buffer's.
        var tooLong = string.Join('.', Enumerable.Repeat("label", 60));
        Assert.True(tooLong.Length > 253);
        Assert.False(TargetExpression.TryParse(tooLong, out _, out _));
    }

    [Theory]
    [InlineData("  github.com  ")]
    [InlineData("github.com ")]
    [InlineData("\tgithub.com")]
    public void TargetExpression_trims_surrounding_whitespace(string input)
    {
        // Regression guard for v0.92 defect D-E: OnRestart called TrimLeft() twice
        // (WinMTRDialog.cpp:555), a typo for TrimRight, so a trailing space made a
        // perfectly good hostname unresolvable.
        Assert.Equal("github.com", TargetExpression.Parse(input).Text);
    }

    [Fact]
    public void TargetExpression_match_is_exhaustive_over_the_closed_cases()
    {
        static string Describe(TargetExpression e) =>
            e.Match(h => $"name:{h.Value}", v4 => $"v4:{v4.Value}", v6 => $"v6:{v6.Value}");

        Assert.Equal("name:github.com", Describe(TargetExpression.Parse("github.com")));
        Assert.Equal("v4:8.8.8.8", Describe(TargetExpression.Parse("8.8.8.8")));
        Assert.Equal("v6:::1", Describe(TargetExpression.Parse("::1")));
    }

    [Fact]
    public void TargetExpression_parse_throws_on_rejection()
    {
        Assert.Throws<ArgumentException>(() => TargetExpression.Parse("999.999.999.999"));
    }

    [Fact]
    public void Target_keeps_the_typed_expression_separate_from_the_address()
    {
        var t = new Target(TargetExpression.Parse("github.com"), IPAddress.Parse("140.82.121.4"), "github.com");
        Assert.Equal("github.com", t.Expression.Text);
        Assert.IsType<TargetExpression.Hostname>(t.Expression);
        Assert.Equal(IPAddress.Parse("140.82.121.4"), t.Address);
    }

    [Fact]
    public void Route_carries_hops_and_a_timestamp()
    {
        var t = new Target(TargetExpression.Parse("h"), IPAddress.Loopback, null);
        var when = DateTimeOffset.UnixEpoch;
        var r = new Route(t, [Hop.Empty(0), Hop.Empty(1)], when);
        Assert.Equal(2, r.Hops.Length);
        Assert.Equal(when, r.TakenAt);
    }

    [Fact]
    public void Route_started_at_defaults_to_taken_at_and_accepts_a_session_start()
    {
        var t = new Target(TargetExpression.Parse("h"), IPAddress.Loopback, null);
        var startedAt = DateTimeOffset.UnixEpoch;
        var takenAt = startedAt + TimeSpan.FromMinutes(5);

        // Every shorter constructor keeps working and reports zero duration.
        Assert.Equal(takenAt, new Route(t, [], takenAt).StartedAt);
        Assert.Equal(takenAt, new Route(t, [], takenAt, IsTruncated: true).StartedAt);
        Assert.Equal(takenAt, new Route(
            t, [], takenAt, destinationReached: false, hopLimitObserved: false).StartedAt);

        var withStart = new Route(
            t, [], takenAt, destinationReached: false, hopLimitObserved: false, startedAt: startedAt);
        Assert.Equal(startedAt, withStart.StartedAt);
        Assert.Equal(takenAt, withStart.TakenAt);
    }

    [Fact]
    public void Route_discovery_observations_drive_the_compatibility_flag()
    {
        var t = new Target(TargetExpression.Parse("h"), IPAddress.Loopback, null);
        var when = DateTimeOffset.UnixEpoch;

        var initial = new Route(t, [Hop.Empty(0)], when);
        Assert.False(initial.DestinationReached);
        Assert.False(initial.HopLimitObserved);
        Assert.False(initial.IsTruncated);

        var uncertain = new Route(t, [Hop.Empty(0)], when, destinationReached: false, hopLimitObserved: true);
        Assert.True(uncertain.IsTruncated);

        var reached = new Route(t, [Hop.Empty(0)], when, destinationReached: true, hopLimitObserved: true);
        Assert.False(reached.IsTruncated);

        // Original 4-arg positional form: (target, hops, takenAt, IsTruncated).
        var legacy = new Route(t, ImmutableArray<Hop>.Empty, when, IsTruncated: true);
        Assert.True(legacy.IsTruncated);
        Assert.False(legacy.DestinationReached);
        Assert.True(legacy.HopLimitObserved);

        var (dt, dh, dw, truncated) = uncertain;
        Assert.Equal(t, dt);
        Assert.Equal(when, dw);
        Assert.True(truncated);

        var (_, _, _, destReached, hopLimit) = uncertain;
        Assert.False(destReached);
        Assert.True(hopLimit);
    }
}
