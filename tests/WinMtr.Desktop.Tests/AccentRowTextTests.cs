using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using WinMtr.Desktop.Services;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The selected hop row uses the system accent as fill.
/// The user can set that accent to any colour.
/// These tests check the rule that keeps the text readable.
/// The rule returns text colour with muted opacity.
/// The muted opacity is for the retained hop row.
/// It measures paint over fill, not colour alone.
/// </summary>
public class AccentRowTextTests
{
    // These are steps from most muted to full strength. The test mirrors the rule steps.
    private static readonly double[] OpacityLadder = [0.70, 0.75, 0.80, 0.85, 0.90, 0.95, 1.0];

    // This is the minimum contrast for normal text. WCAG sets this value.
    private const double MinimumContrast = 4.5;

    // The set has a dark accent and a bright accent. It has the accent where
    // mute changes the choice. It has two ends of the range.
    [Theory]
    [InlineData(0x00, 0x33, 0x99, false)]
    [InlineData(0x4C, 0xC2, 0xFF, true)]
    [InlineData(0x00, 0x78, 0xD4, false)]
    [InlineData(0xFF, 0xF0, 0x00, true)]
    [InlineData(0x00, 0x00, 0x00, false)]
    [InlineData(0xFF, 0xFF, 0xFF, true)]
    public void PickRowText_Chooses_Better_Contrast_At_Full_Strength(byte red, byte green, byte blue, bool expectDark)
    {
        Color accent = Color.FromRgb(red, green, blue);

        AccentTextContrast.AccentRowText row = AccentTextContrast.PickRowText(accent);

        Assert.Equal(expectDark ? AccentTextContrast.OnLightFill : AccentTextContrast.OnDarkFill, row.Text);
        Assert.True(
            AccentTextContrast.ContrastRatio(row.Text, accent)
            >= AccentTextContrast.ContrastRatio(
                row.Text == AccentTextContrast.OnLightFill
                    ? AccentTextContrast.OnDarkFill
                    : AccentTextContrast.OnLightFill,
                accent));
    }

    [Fact]
    public void Returned_Combination_Reaches_Floor_Or_Best_Available()
    {
        // The test paints the text over the fill. Then it measures that paint.
        for (var red = 0; red <= 255; red += 15)
        {
            for (var green = 0; green <= 255; green += 15)
            {
                for (var blue = 0; blue <= 255; blue += 15)
                {
                    Color accent = Color.FromRgb((byte)red, (byte)green, (byte)blue);
                    AccentTextContrast.AccentRowText row = AccentTextContrast.PickRowText(accent);
                    double ratio = AccentTextContrast.ContrastRatio(
                        Composite(row.Text, accent, row.MutedOpacity),
                        accent);

                    if (ratio >= MinimumContrast)
                    {
                        continue;
                    }

                    // Where no step holds 4.5, the rule uses full strength.
                    Assert.Equal(1.0, row.MutedOpacity, 10);
                    Color other = row.Text == AccentTextContrast.OnLightFill
                        ? AccentTextContrast.OnDarkFill
                        : AccentTextContrast.OnLightFill;
                    Assert.True(
                        AccentTextContrast.ContrastRatio(row.Text, accent)
                        >= AccentTextContrast.ContrastRatio(other, accent),
                        $"{accent} gives {ratio:F2} and does not use the best colour.");
                }
            }
        }
    }

    [Fact]
    public void Returned_Opacity_Is_Most_Muted_Step_That_Holds_Floor()
    {
        // The test checks the step below the result. That step must stay below 4.5.
        for (var red = 0; red <= 255; red += 15)
        {
            for (var green = 0; green <= 255; green += 15)
            {
                for (var blue = 0; blue <= 255; blue += 15)
                {
                    Color accent = Color.FromRgb((byte)red, (byte)green, (byte)blue);
                    AccentTextContrast.AccentRowText row = AccentTextContrast.PickRowText(accent);
                    int index = LadderIndex(row.MutedOpacity);
                    double ratio = AccentTextContrast.ContrastRatio(
                        Composite(row.Text, accent, row.MutedOpacity),
                        accent);

                    if (ratio >= MinimumContrast)
                    {
                        if (index > 0)
                        {
                            double below = AccentTextContrast.ContrastRatio(
                                Composite(row.Text, accent, OpacityLadder[index - 1]),
                                accent);
                            Assert.True(
                                below < MinimumContrast,
                                $"{accent} keeps {row.MutedOpacity:F2} but {OpacityLadder[index - 1]:F2} also holds the floor.");
                        }

                        continue;
                    }

                    // Where no step holds 4.5, the rule returns full strength.
                    Assert.Equal(OpacityLadder.Length - 1, index);
                    foreach (double opacity in OpacityLadder)
                    {
                        double stepRatio = AccentTextContrast.ContrastRatio(
                            Composite(row.Text, accent, opacity),
                            accent);
                        Assert.True(
                            stepRatio < MinimumContrast,
                            $"{accent} gives {stepRatio:F2} at {opacity:F2} and should not use muted opacity.");
                    }
                }
            }
        }
    }

    [Fact]
    public void Full_Strength_And_Muted_Results_Agree_On_Colour()
    {
        // One accent changes the choice at mute. White has higher contrast
        // at full strength. Near-black has higher contrast at mute. The rule
        // keeps white.
        Color accent = Color.FromRgb(0x00, 0x78, 0xD4);
        AccentTextContrast.AccentRowText row = AccentTextContrast.PickRowText(accent);

        Assert.Equal(AccentTextContrast.OnDarkFill, row.Text);
        Assert.True(
            AccentTextContrast.ContrastRatio(AccentTextContrast.OnDarkFill, accent)
            >= AccentTextContrast.ContrastRatio(AccentTextContrast.OnLightFill, accent));
        double whiteMuted = AccentTextContrast.ContrastRatio(
            Composite(AccentTextContrast.OnDarkFill, accent, OpacityLadder[0]),
            accent);
        double darkMuted = AccentTextContrast.ContrastRatio(
            Composite(AccentTextContrast.OnLightFill, accent, OpacityLadder[0]),
            accent);
        Assert.True(
            darkMuted > whiteMuted,
            $"The mute inverts the choice but the rule must keep white.");
    }

    // This test uses the accent with the lowest result in the sweep. It also
    // uses the two accents named in the spec. All three stay below 4.5. The
    // code returns full strength for each.
    [Theory]
    [InlineData(0xE1, 0x0F, 0xA5)]
    [InlineData(0xF0, 0x00, 0x00)]
    [InlineData(0xF0, 0x00, 0x4B)]
    public void Accent_Without_Readable_Mute_Returns_Full_Strength(byte red, byte green, byte blue)
    {
        Color accent = Color.FromRgb(red, green, blue);

        AccentTextContrast.AccentRowText row = AccentTextContrast.PickRowText(accent);

        Assert.Equal(1.0, row.MutedOpacity, 10);
        double ratio = AccentTextContrast.ContrastRatio(
            Composite(row.Text, accent, row.MutedOpacity),
            accent);
        Assert.True(
            ratio < MinimumContrast,
            $"{accent} gives {ratio:F2} and should not claim the floor.");
        Color other = row.Text == AccentTextContrast.OnLightFill
            ? AccentTextContrast.OnDarkFill
            : AccentTextContrast.OnLightFill;
        Assert.True(
            AccentTextContrast.ContrastRatio(row.Text, accent)
            >= AccentTextContrast.ContrastRatio(other, accent));
    }

    [Fact]
    public void Contrast_Ratio_Runs_From_One_To_Twenty_One()
    {
        Assert.Equal(1.0, AccentTextContrast.ContrastRatio(Colors.Red, Colors.Red), 3);
        Assert.Equal(21.0, AccentTextContrast.ContrastRatio(Colors.Black, Colors.White), 3);
        // The ratio does not depend on which colour comes first.
        Assert.Equal(
            AccentTextContrast.ContrastRatio(Colors.Black, Colors.White),
            AccentTextContrast.ContrastRatio(Colors.White, Colors.Black),
            10);
    }

    [AvaloniaFact]
    public void Row_Brushes_Follow_The_Accent()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");

        // The test writes both brushes. It puts back the first brushes at the end.
        // Later tests then see the same brushes as before.
        object? originalText = application.Resources["WinMtrRowSelectedTextBrush"];
        object? originalFrozen = application.Resources["WinMtrRowSelectedFrozenTextBrush"];
        try
        {
            Color darkAccent = Color.FromRgb(0x00, 0x33, 0x99);
            AccentRowTextTheme.ApplyAccent(application, darkAccent);
            Assert.Equal(Colors.White, TextColor(application, "WinMtrRowSelectedTextBrush"));
            Assert.Equal(Colors.White, TextColor(application, "WinMtrRowSelectedFrozenTextBrush"));
            double expectedOpacity = AccentTextContrast.PickRowText(darkAccent).MutedOpacity;
            Assert.Equal(expectedOpacity, Brush(application, "WinMtrRowSelectedFrozenTextBrush").Opacity, 3);
            Assert.Equal(1.0, Brush(application, "WinMtrRowSelectedTextBrush").Opacity, 3);

            AccentRowTextTheme.ApplyAccent(application, Color.FromRgb(0x4C, 0xC2, 0xFF));
            Assert.Equal(AccentTextContrast.OnLightFill, TextColor(application, "WinMtrRowSelectedTextBrush"));
            Assert.Equal(AccentTextContrast.OnLightFill, TextColor(application, "WinMtrRowSelectedFrozenTextBrush"));
        }
        finally
        {
            application.Resources["WinMtrRowSelectedTextBrush"] = originalText!;
            application.Resources["WinMtrRowSelectedFrozenTextBrush"] = originalFrozen!;
        }
    }

    [AvaloniaFact]
    public void Attach_Writes_Both_Brushes()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");

        // The test sets one accent in the resources. Then it starts the service.
        // The service must write both brushes from that accent.
        object? originalText = application.Resources["WinMtrRowSelectedTextBrush"];
        object? originalFrozen = application.Resources["WinMtrRowSelectedFrozenTextBrush"];
        bool hadAccent = application.Resources.ContainsKey("SystemAccentColor");
        object? oldAccent = hadAccent ? application.Resources["SystemAccentColor"] : null;
        Color accent = Color.FromRgb(0x4C, 0xC2, 0xFF);
        try
        {
            application.Resources["SystemAccentColor"] = accent;
            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(application);

            AccentTextContrast.AccentRowText expected = AccentTextContrast.PickRowText(accent);
            Assert.Equal(expected.Text, TextColor(application, "WinMtrRowSelectedTextBrush"));
            Assert.Equal(1.0, Brush(application, "WinMtrRowSelectedTextBrush").Opacity, 3);
            Assert.Equal(expected.Text, TextColor(application, "WinMtrRowSelectedFrozenTextBrush"));
            Assert.Equal(expected.MutedOpacity, Brush(application, "WinMtrRowSelectedFrozenTextBrush").Opacity, 3);
        }
        finally
        {
            // The test removes the test accent. Then it restarts the service.
            // It puts back the first brushes. Later tests then see no change.
            if (hadAccent)
            {
                application.Resources["SystemAccentColor"] = oldAccent!;
            }
            else
            {
                application.Resources.Remove("SystemAccentColor");
            }

            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(application);
            application.Resources["WinMtrRowSelectedTextBrush"] = originalText!;
            application.Resources["WinMtrRowSelectedFrozenTextBrush"] = originalFrozen!;
        }
    }

    [AvaloniaFact]
    public void Accent_Change_Rewrites_Both_Brushes()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");

        // The test starts with one accent. Then it changes to a second accent.
        // Both brushes must use the second accent.
        object? originalText = application.Resources["WinMtrRowSelectedTextBrush"];
        object? originalFrozen = application.Resources["WinMtrRowSelectedFrozenTextBrush"];
        bool hadAccent = application.Resources.ContainsKey("SystemAccentColor");
        object? oldAccent = hadAccent ? application.Resources["SystemAccentColor"] : null;
        Color firstAccent = Color.FromRgb(0x00, 0x33, 0x99);
        Color secondAccent = Color.FromRgb(0x4C, 0xC2, 0xFF);
        try
        {
            application.Resources["SystemAccentColor"] = firstAccent;
            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(application);
            Color firstText = TextColor(application, "WinMtrRowSelectedTextBrush");

            application.Resources["SystemAccentColor"] = secondAccent;
            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(application);

            AccentTextContrast.AccentRowText expected = AccentTextContrast.PickRowText(secondAccent);
            Assert.Equal(expected.Text, TextColor(application, "WinMtrRowSelectedTextBrush"));
            Assert.Equal(expected.Text, TextColor(application, "WinMtrRowSelectedFrozenTextBrush"));
            Assert.Equal(expected.MutedOpacity, Brush(application, "WinMtrRowSelectedFrozenTextBrush").Opacity, 3);
            Assert.NotEqual(firstText, TextColor(application, "WinMtrRowSelectedTextBrush"));
        }
        finally
        {
            // The test removes the test accent. Then it restarts the service.
            // It puts back the first brushes. Later tests then see no change.
            if (hadAccent)
            {
                application.Resources["SystemAccentColor"] = oldAccent!;
            }
            else
            {
                application.Resources.Remove("SystemAccentColor");
            }

            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(application);
            application.Resources["WinMtrRowSelectedTextBrush"] = originalText!;
            application.Resources["WinMtrRowSelectedFrozenTextBrush"] = originalFrozen!;
        }
    }

    [AvaloniaFact]
    public void Host_Without_Accent_Keeps_StartUp_Defaults()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");

        // A bare host has no accent to read. The service must keep the start-up brushes.
        // It must throw nothing while it tries to read the accent.
        object? originalText = application.Resources["WinMtrRowSelectedTextBrush"];
        object? originalFrozen = application.Resources["WinMtrRowSelectedFrozenTextBrush"];
        var bare = new Application();
        bare.Resources["WinMtrRowSelectedTextBrush"] = new SolidColorBrush(Colors.White);
        bare.Resources["WinMtrRowSelectedFrozenTextBrush"] = new SolidColorBrush(Colors.White, 0.7);
        try
        {
            Assert.False(bare.TryGetResource("SystemAccentColor", bare.ActualThemeVariant, out object? _));

            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(bare);

            Assert.Equal(Colors.White, TextColor(bare, "WinMtrRowSelectedTextBrush"));
            Assert.Equal(1.0, Brush(bare, "WinMtrRowSelectedTextBrush").Opacity, 3);
            Assert.Equal(Colors.White, TextColor(bare, "WinMtrRowSelectedFrozenTextBrush"));
            Assert.Equal(0.7, Brush(bare, "WinMtrRowSelectedFrozenTextBrush").Opacity, 3);
        }
        finally
        {
            // The test detaches the bare host. Then it restarts the service for the test application.
            // It puts back the first brushes. Later tests then see no change.
            AccentRowTextTheme.Detach();
            AccentRowTextTheme.Attach(application);
            application.Resources["WinMtrRowSelectedTextBrush"] = originalText!;
            application.Resources["WinMtrRowSelectedFrozenTextBrush"] = originalFrozen!;
        }
    }

    [AvaloniaFact]
    public void Brush_Change_Reaches_DynamicResource_Consumer()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia test application is not running.");

        // A control shows the brush through DynamicResource. A new accent must reach the control.
        // The dictionary alone is not enough. The test reads the control back.
        object? originalText = application.Resources["WinMtrRowSelectedTextBrush"];
        object? originalFrozen = application.Resources["WinMtrRowSelectedFrozenTextBrush"];
        var window = new Window();
        window.Show();
        try
        {
            var panel = new StackPanel();
            var textBlock = new TextBlock();
            var frozenBlock = new TextBlock();
            panel.Children.Add(textBlock);
            panel.Children.Add(frozenBlock);
            window.Content = panel;
            textBlock[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("WinMtrRowSelectedTextBrush");
            frozenBlock[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("WinMtrRowSelectedFrozenTextBrush");

            Color accent = Color.FromRgb(0x4C, 0xC2, 0xFF);
            AccentRowTextTheme.ApplyAccent(application, accent);

            AccentTextContrast.AccentRowText expected = AccentTextContrast.PickRowText(accent);
            Assert.Same(application.Resources["WinMtrRowSelectedTextBrush"], textBlock.Foreground);
            Assert.Same(application.Resources["WinMtrRowSelectedFrozenTextBrush"], frozenBlock.Foreground);
            Assert.Equal(expected.Text, ((SolidColorBrush)textBlock.Foreground!).Color);
            Assert.Equal(1.0, ((SolidColorBrush)textBlock.Foreground!).Opacity, 3);
            Assert.Equal(expected.Text, ((SolidColorBrush)frozenBlock.Foreground!).Color);
            Assert.Equal(expected.MutedOpacity, ((SolidColorBrush)frozenBlock.Foreground!).Opacity, 3);
        }
        finally
        {
            // The test closes the window. It puts back the first brushes. Later tests then see no change.
            window.Close();
            application.Resources["WinMtrRowSelectedTextBrush"] = originalText!;
            application.Resources["WinMtrRowSelectedFrozenTextBrush"] = originalFrozen!;
        }
    }

    // This finds one opacity in the ladder. The test fails where the value is absent.
    private static int LadderIndex(double opacity)
    {
        for (int index = 0; index < OpacityLadder.Length; index++)
        {
            if (Math.Abs(OpacityLadder[index] - opacity) < 1e-9)
            {
                return index;
            }
        }

        Assert.Fail($"Opacity {opacity:F2} is not a ladder step.");
        return -1;
    }

    // This mixes text over fill in sRGB bytes. The test mirrors the rule measure.
    private static Color Composite(Color text, Color fill, double opacity) =>
        Color.FromRgb(
            BlendChannel(text.R, fill.R, opacity),
            BlendChannel(text.G, fill.G, opacity),
            BlendChannel(text.B, fill.B, opacity));

    // This mixes one channel. The result is rounded to the nearest byte.
    private static byte BlendChannel(byte text, byte fill, double opacity) =>
        (byte)Math.Round(
            (opacity * text) + ((1.0 - opacity) * fill),
            MidpointRounding.AwayFromZero);

    private static SolidColorBrush Brush(Application application, string key) =>
        Assert.IsType<SolidColorBrush>(application.Resources[key]);

    private static Color TextColor(Application application, string key) => Brush(application, key).Color;
}
