using System.Globalization;
using System.Text;

namespace WinMtr.Desktop.Services;

/// <summary>
/// Suggests the export filename for a captured report:
/// <c>winmtr-&lt;sanitised-target&gt;-&lt;timestamp&gt;.&lt;ext&gt;</c>, where the
/// extension is the selected renderer's own extension. The target is sanitised
/// so IPv6 colons, spaces, and other filename-hostile characters cannot escape
/// the suggested name. The timestamp is the route snapshot taken-at instant in
/// UTC, written as <c>yyyyMMdd-HHmmss</c> with a literal <c>Z</c> marker (for
/// example <c>20260909-093524Z</c>). The name never varies by renderer: the
/// instant is always UTC, always marked with <c>Z</c>, and never converted to
/// local time.
/// </summary>
public static class ReportFileName
{
    /// <summary>
    /// Builds the suggested export filename for <paramref name="target"/> at
    /// <paramref name="takenAt"/> with the renderer's <paramref name="extension"/>.
    /// </summary>
    public static string Build(string target, DateTimeOffset takenAt, string extension)
    {
        string sanitised = Sanitize(target);
        string timestamp = takenAt.UtcDateTime.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture);
        string ext = string.IsNullOrEmpty(extension)
            ? ".txt"
            : extension.StartsWith('.') ? extension : "." + extension;
        return $"winmtr-{sanitised}-{timestamp}{ext}";
    }

    private static string Sanitize(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "trace";

        string trimmed = target.Trim();
        var sanitised = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
            sanitised.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');

        return sanitised.Length == 0 ? "trace" : sanitised.ToString();
    }
}
