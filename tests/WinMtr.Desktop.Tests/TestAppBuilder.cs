using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(WinMtr.Desktop.Tests.TestAppBuilder))]

namespace WinMtr.Desktop.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<WinMtr.Desktop.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
