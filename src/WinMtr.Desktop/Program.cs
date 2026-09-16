using Avalonia;
using WinMtr.Desktop.Services;

namespace WinMtr.Desktop;

internal static class Program
{
    public static void Main(string[] args)
    {
        ApplyLinuxScaleBridge();
        _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        PlatformProfile profile = OperatingSystem.IsMacOS() ? PlatformProfile.MacOs : PlatformProfile.Default;
        return AppBuilder.Configure(() => new App(profile)).UsePlatformDetect().WithInterFont().LogToTrace();
    }

    // Avalonia 11 runs on XWayland and ignores GDK_SCALE, so a 200%
    // monitor renders at 100% (tiny UI). Bridge the desktop scale into
    // the variable Avalonia does read, unless the user already overrode
    // scaling via Avalonia or Qt variables.
    internal static void ApplyLinuxScaleBridge()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QT_SCALE_FACTOR"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QT_SCREEN_SCALE_FACTORS")))
            return;

        double gdk = ParseScale(Environment.GetEnvironmentVariable("GDK_SCALE"), 1d)
            * ParseScale(Environment.GetEnvironmentVariable("GDK_DPI_SCALE"), 1d);
        if (gdk <= 0 || Math.Abs(gdk - 1d) < 0.01)
            return;

        // AVALONIA_GLOBAL_SCALE_FACTOR multiplies the Xrdb (Xft.dpi) factor,
        // so divide it out to avoid double-scaling where Xft.dpi is set.
        double xrdb = XftDpiFactor();
        double global = gdk / xrdb;
        if (global > 0 && Math.Abs(global - 1d) >= 0.01)
            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", global.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static double ParseScale(string? text, double fallback) =>
        double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : fallback;

    private static double XftDpiFactor()
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("xrdb", "-query")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Split([':', '\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "Xft.dpi"
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dpi)
                    && dpi > 0)
                    return dpi / 96d;
            }
        }
        catch (Exception)
        {
            // xrdb missing: fall through to 1.
        }

        return 1d;
    }
}
