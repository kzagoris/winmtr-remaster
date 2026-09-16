namespace WinMtr.Desktop.Services;

using System.Reflection;

/// <summary>
/// Locates a file embedded as an <c>AvaloniaResource</c> in this assembly.
/// </summary>
/// <remarks>
/// The authority of an <c>avares://</c> URI is the assembly name, not the
/// namespace. The assembly ships as <c>winmtr</c> while the namespace is
/// <c>WinMtr.Desktop</c>, so the name is read from the assembly instead of
/// written out. A later rename then needs no edit here.
/// </remarks>
internal static class AppAssets
{
    private static readonly string AssemblyName =
        typeof(AppAssets).GetTypeInfo().Assembly.GetName().Name!;

    internal static Uri Locate(string fileName) =>
        new($"avares://{AssemblyName}/Assets/{fileName}");
}
