using System.Text.RegularExpressions;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Automated checks behind the compiled-binding and theme-resource decisions:
/// a binding to a missing property fails the build through the XAML compiler,
/// and these tests fail a view that bypasses that net with a plain binding or
/// a hard-coded brush.
/// </summary>
public partial class XamlConventionTests
{
    private static string AppDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinMtr.Remaster.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Could not locate the repository root from the test output directory.")
            : Path.Combine(directory.FullName, "src", "WinMtr.Desktop");
    }

    private static IReadOnlyList<(string File, string Text)> LoadViews() =>
        Directory.GetFiles(AppDirectory(), "*.axaml", SearchOption.AllDirectories)
            .Select(file => (file, File.ReadAllText(file)))
            .ToList();

    [Fact]
    public void Views_With_Bindings_Declare_Data_Type()
    {
        var offenders = LoadViews()
            .Where(view => view.Text.Contains("CompiledBinding", StringComparison.Ordinal)
                && !view.Text.Contains("x:DataType", StringComparison.Ordinal))
            .Select(view => view.File)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Views_Use_Compiled_Bindings_Only()
    {
        var offenders = LoadViews()
            .Where(view => PlainBinding().IsMatch(view.Text))
            .Select(view => view.File)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Views_Use_Theme_Resources_Not_Hard_Coded_Brushes()
    {
        // Only the two palette owners are exempt from brush checks. Binding
        // conventions still apply to both, as they do to every other file.
        string appDirectory = AppDirectory();
        var views = LoadViews()
            .Where(view => !string.Equals(view.File, Path.Combine(appDirectory, "App.axaml"), StringComparison.Ordinal)
                && !string.Equals(view.File, Path.Combine(appDirectory, "Themes", "MacOsThemePack.axaml"), StringComparison.Ordinal))
            .ToList();

        var hex = views
            .Where(view => HexColor().IsMatch(view.Text))
            .Select(view => view.File)
            .ToList();
        Assert.Empty(hex);

        var brushes = views
            .Where(view => view.Text.Contains("<SolidColorBrush", StringComparison.Ordinal))
            .Select(view => view.File)
            .ToList();
        Assert.Empty(brushes);

        var literals = views
            .Where(view => LiteralBrush().IsMatch(view.Text))
            .Select(view => view.File)
            .ToList();
        Assert.Empty(literals);
    }

    [GeneratedRegex(@"\{Binding(?=[\s}])")]
    private static partial Regex PlainBinding();

    [GeneratedRegex("#[0-9A-Fa-f]{3,8}\\b")]
    private static partial Regex HexColor();

    [GeneratedRegex("(Background|Foreground|BorderBrush)\\s*=\\s*\"(?!\\{)[^\"]*\"")]
    private static partial Regex LiteralBrush();
}
