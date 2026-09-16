namespace WinMtr.Core.Reporting;

/// <summary>
/// One output format. One implementation per format, so adding a format is a
/// new class rather than an edit -- v0.92 copy-pasted the same loop into four
/// dialog handlers.
/// </summary>
public interface IReportRenderer
{
    string Name { get; }
    string Extension { get; }
    string Render(Route route);
}
