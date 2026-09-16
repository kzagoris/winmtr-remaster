using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace WinMtr.Core;

/// <summary>
/// What the user typed, classified. v0.92 kept the target as a bare string and
/// decided "is this an IP?" with <c>while(*t) if(!isdigit(*t) && *t!='.') isIP=0;</c>
/// (WinMTRDialog.cpp:952), so "999.999.999.999" and "1.2.3.4.5.6" both reached
/// <c>inet_addr</c>, which answers INADDR_NONE -- and WinMTR silently traced
/// 255.255.255.255 instead of telling the user the target was nonsense.
/// The cases are closed (the constructor is private protected), so a switch over
/// the three is exhaustive and no fourth kind of target can appear.
/// TODO (.NET 11): convert to a C# 15 closed hierarchy. Replace the private
/// protected constructor with the closed modifier and drop the Match fallback
/// so the compiler checks exhaustiveness.
/// </summary>
public abstract record TargetExpression
{
    private protected TargetExpression() { }

    /// <summary>Exactly what the user typed, for the report header and the target history.</summary>
    public abstract string Text { get; }

    public sealed override string ToString() => Text;

    /// <summary>A name that needs resolving before anything can be probed.</summary>
    public sealed record Hostname : TargetExpression
    {
        public Hostname(string Value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Value);
            this.Value = Value;
        }

        public string Value { get; init; }
        public override string Text => Value;
    }

    /// <summary>A dotted quad. Nothing to resolve, so the trace can start immediately.</summary>
    public sealed record IPv4Literal : TargetExpression
    {
        public IPv4Literal(IPAddress Value)
        {
            ArgumentNullException.ThrowIfNull(Value);
            if (Value.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("IPv4Literal requires an InterNetwork address.", nameof(Value));
            this.Value = Value;
        }

        public IPAddress Value { get; init; }
        public override string Text => Value.ToString();
    }

    /// <summary>
    /// An IPv6 address, scope id included. v0.92 had no IPv6 path at all: it read
    /// the resolved address as <c>*(int *)host->h_addr</c> (WinMTRDialog.cpp:1009),
    /// four bytes, so only AF_INET could ever survive the trip.
    /// </summary>
    public sealed record IPv6Literal : TargetExpression
    {
        public IPv6Literal(IPAddress Value)
        {
            ArgumentNullException.ThrowIfNull(Value);
            if (Value.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("IPv6Literal requires an InterNetworkV6 address.", nameof(Value));
            this.Value = Value;
        }

        public IPAddress Value { get; init; }
        public override string Text => Value.ToString();
    }

    /// <summary>The longest name DNS allows, so a target cannot outgrow the wire format.</summary>
    private const int MaxHostNameLength = 253;

    /// <summary>
    /// Classifies user input without throwing, for the CLI parse boundary.
    /// Surrounding whitespace is trimmed from both ends -- v0.92 called
    /// <c>TrimLeft()</c> twice (WinMTRDialog.cpp:555), a typo for TrimRight, so a
    /// trailing space made a perfectly good hostname unresolvable.
    /// </summary>
    public static bool TryParse(
        string? input,
        [NotNullWhen(true)] out TargetExpression? expression,
        [NotNullWhen(false)] out string? error)
    {
        expression = null;
        error = null;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "a target host is required";
            return false;
        }

        if (text.Length > MaxHostNameLength)
        {
            // v0.92 read the target into `char strtmp[255]` (WinMTRDialog.cpp:944);
            // the bound is now the protocol's, not a buffer's.
            error = $"a target host may be at most {MaxHostNameLength} characters";
            return false;
        }

        // Bracketed IPv6 is how a URL authority writes it; accept the form and drop
        // the brackets, which are punctuation rather than part of the address.
        var candidate = text.Length > 2 && text[0] == '[' && text[^1] == ']'
            ? text[1..^1]
            : text;

        // This is v0.92's own classifier -- "every character is a digit or a dot"
        // (WinMTRDialog.cpp:952) -- with its consequence inverted. Legacy took the
        // predicate as proof of an IP and handed the text to inet_addr. We take it
        // only as a statement of intent: the user meant an address, so it must be a
        // well-formed one. That closes the door IPAddress.TryParse leaves open, since
        // .NET still accepts shorthand forms and would read "1.2.3" as 1.2.0.3,
        // "123" as 0.0.0.123 and "01.2.3.4" as 1.2.3.4 -- silently tracing a host
        // the user never asked for, which is the defect in its modern clothes.
        if (LooksLikeDigitsAndDots(candidate))
        {
            if (TryParseDottedQuad(candidate, out var v4))
            {
                expression = new IPv4Literal(v4);
                return true;
            }

            error = $"'{text}' is not a valid IPv4 address";
            return false;
        }

        if (IPAddress.TryParse(candidate, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            expression = new IPv6Literal(address);
            return true;
        }

        if (IsHostName(text))
        {
            expression = new Hostname(text);
            return true;
        }

        error = $"'{text}' is not a valid hostname or IP address";
        return false;
    }

    private static bool LooksLikeDigitsAndDots(string text)
    {
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c != '.')
                return false;
        return true;
    }

    /// <summary>
    /// Exactly four decimal octets: no shorthand, no leading zeros (which invite an
    /// octal reading), nothing out of range.
    /// </summary>
    private static bool TryParseDottedQuad(string text, out IPAddress address)
    {
        // Not IPAddress.None -- that is 255.255.255.255, the very value a bad
        // target must never quietly become.
        address = IPAddress.Any;
        var parts = text.Split('.');
        if (parts.Length != 4)
            return false;

        var octets = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3)
                return false;
            if (part.Length > 1 && part[0] == '0')
                return false;
            if (!byte.TryParse(part, out octets[i]))
                return false;
        }

        address = new IPAddress(octets);
        return true;
    }

    /// <summary>
    /// A syntactically valid DNS name. <see cref="Uri.CheckHostName"/> does the bulk
    /// of the work but accepts a label with a trailing hyphen, so labels are checked
    /// here too.
    /// </summary>
    private static bool IsHostName(string text)
    {
        if (Uri.CheckHostName(text) != UriHostNameType.Dns)
            return false;

        // A single trailing dot is the legal absolute-name form; ignore it.
        var name = text.EndsWith('.') ? text[..^1] : text;
        if (name.Length == 0)
            return false;

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63)
                return false;
            if (label[0] == '-' || label[^1] == '-')
                return false;
        }

        return true;
    }

    /// <summary>Classifies user input, throwing on rejection.</summary>
    public static TargetExpression Parse(string input) =>
        TryParse(input, out var expression, out var error)
            ? expression
            : throw new ArgumentException(error, nameof(input));

    /// <summary>Exhaustive dispatch over the closed set of cases.</summary>
    public T Match<T>(
        Func<Hostname, T> hostname,
        Func<IPv4Literal, T> ipv4,
        Func<IPv6Literal, T> ipv6)
    {
        ArgumentNullException.ThrowIfNull(hostname);
        ArgumentNullException.ThrowIfNull(ipv4);
        ArgumentNullException.ThrowIfNull(ipv6);

        return this switch
        {
            Hostname h => hostname(h),
            IPv4Literal v4 => ipv4(v4),
            IPv6Literal v6 => ipv6(v6),
            _ => throw new InvalidOperationException($"Unhandled target expression: {GetType().Name}.")
        };
    }
}
