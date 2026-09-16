using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Styling;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

public class WindowIconThemeTests
{
    [AvaloniaFact]
    public void Theme_Icons_Provide_Pixel_Fitted_Windows_Frames()
    {
        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        string[] resourceNames =
        [
            "winmtr-route-pulse-light.ico",
            "winmtr-route-pulse-dark.ico"
        ];

        foreach (string resourceName in resourceNames)
        {
            using Stream stream = AssetLoader.Open(
                AppAssets.Locate(resourceName));
            using var reader = new BinaryReader(stream);

            Assert.Equal(0, reader.ReadUInt16());
            Assert.Equal(1, reader.ReadUInt16());
            ushort imageCount = reader.ReadUInt16();
            Assert.Equal(expectedSizes.Length, imageCount);

            var actualSizes = new int[imageCount];
            for (var index = 0; index < imageCount; index++)
            {
                byte width = reader.ReadByte();
                byte height = reader.ReadByte();
                actualSizes[index] = width == 0 ? 256 : width;
                Assert.Equal(actualSizes[index], height == 0 ? 256 : height);
                reader.ReadByte();
                reader.ReadByte();
                Assert.Equal(1, reader.ReadUInt16());
                Assert.Equal(32, reader.ReadUInt16());
                stream.Position += 8;
            }

            Assert.Equal(expectedSizes, actualSizes.Order());
        }
    }

    // Windows shows the 16 px frame in the title bar and the 24 px frame on the
    // taskbar at 96 dpi, so those frames decide how the application looks. The
    // first hand-made icons drew them with fully on or fully off pixels, which
    // made every edge a staircase. Real antialiasing leaves pixels part way
    // between clear and solid, so count them. See docs/design/route-pulse.
    [AvaloniaTheory]
    [InlineData("winmtr-route-pulse-light.ico")]
    [InlineData("winmtr-route-pulse-dark.ico")]
    public void Small_Frames_Are_Antialiased(string resourceName)
    {
        int[] smallSizes = [16, 20, 24, 32];

        using Stream stream = AssetLoader.Open(
            AppAssets.Locate(resourceName));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] icon = buffer.ToArray();

        foreach (int size in smallSizes)
        {
            double partial = PartOfFrameWithPartialAlpha(icon, size);

            Assert.True(
                partial > 0.02,
                $"The {size} px frame of {resourceName} has {partial:P1} of its pixels "
                + "part way between clear and solid. It reads as a staircase. "
                + "Rebuild the icons with 'dotnet run scripts/build-icons.cs -- --refresh-ico'.");
        }
    }

    // Reads one uncompressed frame out of an ICO and reports the part of it
    // that antialiasing touched.
    private static double PartOfFrameWithPartialAlpha(byte[] icon, int size)
    {
        int count = BitConverter.ToUInt16(icon, 4);
        for (var index = 0; index < count; index++)
        {
            int entry = 6 + index * 16;
            int width = icon[entry] == 0 ? 256 : icon[entry];
            if (width != size)
            {
                continue;
            }

            int offset = BitConverter.ToInt32(icon, entry + 12);
            int pixels = offset + 40;                    // skip the BITMAPINFOHEADER
            var partial = 0;
            for (var i = 0; i < size * size; i++)
            {
                byte alpha = icon[pixels + i * 4 + 3];   // BGRA order
                if (alpha is > 0 and < 255)
                {
                    partial++;
                }
            }

            return (double)partial / (size * size);
        }

        throw new InvalidOperationException($"The icon has no {size} px frame.");
    }

    [AvaloniaFact]
    public void Every_Window_Changes_Icon_With_Resolved_Theme()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");
        ThemeVariant? originalTheme = application.RequestedThemeVariant;
        var mainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
        var settingsWindow = new SettingsWindow { DataContext = new SettingsViewModel() };

        mainWindow.Show();
        settingsWindow.Show();
        try
        {
            application.RequestedThemeVariant = ThemeVariant.Light;
            var lightMainIcon = mainWindow.Icon;
            var lightSettingsIcon = settingsWindow.Icon;

            Assert.NotNull(lightMainIcon);
            Assert.NotNull(lightSettingsIcon);

            application.RequestedThemeVariant = ThemeVariant.Dark;

            Assert.NotNull(mainWindow.Icon);
            Assert.NotNull(settingsWindow.Icon);
            Assert.NotSame(lightMainIcon, mainWindow.Icon);
            Assert.NotSame(lightSettingsIcon, settingsWindow.Icon);
        }
        finally
        {
            settingsWindow.Close();
            mainWindow.Close();
            application.RequestedThemeVariant = originalTheme;
        }
    }
}
