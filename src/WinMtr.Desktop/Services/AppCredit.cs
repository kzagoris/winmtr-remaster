using WinMtr.Core;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The footer credit for the shell status bar: the release version from the
/// shared <see cref="AppVersion.Display"/> plus the author and repository.
/// A single default lives here so forks change one place; callers needing a
/// different credit pass their own record the way report renderers accept an
/// overriding footer.
/// </summary>
public sealed record AppCredit(string Author, string RepositoryUrl)
{
    internal const string ProductName = "WinMTR";

    public static AppCredit Default { get; } = new(
        Author: "Konstantinos Zagoris",
        RepositoryUrl: "https://github.com/kzagoris/winmtr-remaster");

    /// <summary>Short release label, e.g. v0.1.0 (prerelease kept, build metadata omitted).</summary>
    public string VersionLabel => $"v{AppVersion.Display}";

    /// <summary>Short footer form for the cramped status bar; the long GPL form stays in reports.</summary>
    public string CreditLabel => $"{VersionLabel} | by {Author}";
}
