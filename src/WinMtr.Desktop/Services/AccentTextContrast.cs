using Avalonia.Media;

namespace WinMtr.Desktop.Services;

/// <summary>
/// Picks the text colour for a row filled with the system accent.
/// The user can set the accent to any colour. So the rule measures contrast.
/// It returns the colour with the muted opacity for the retained hop row as one value.
/// </summary>
public static class AccentTextContrast
{
    /// <summary>Text for a light fill.</summary>
    public static readonly Color OnLightFill = Color.FromRgb(0x10, 0x10, 0x10);

    /// <summary>Text for a dark fill.</summary>
    public static readonly Color OnDarkFill = Colors.White;

    // This is the most muted opacity for the retained hop row. A lower value loses contrast.
    private const double MostMutedOpacity = 0.70;

    // This is the minimum contrast for normal text. WCAG sets this value.
    private const double MinimumContrast = 4.5;

    // These are opacity steps from most muted to full strength. Fixed steps keep results stable.
    private static readonly double[] OpacitySteps =
    [
        MostMutedOpacity,
        0.75,
        0.80,
        0.85,
        0.90,
        0.95,
        1.0,
    ];

    /// <summary>This is the text colour with its muted opacity for the retained hop row.</summary>
    public readonly record struct AccentRowText(Color Text, double MutedOpacity);

    /// <summary>
    /// Returns the text colour and the muted opacity for one fill.
    /// The colour has the highest contrast at full strength. A tie uses the dark text.
    /// The muted opacity is for the retained hop row. It is the most muted step that holds 4.5.
    /// Where no step holds 4.5, the result uses full strength.
    /// </summary>
    public static AccentRowText PickRowText(Color fill)
    {
        Color text = PickTextColor(fill);
        foreach (double opacity in OpacitySteps)
        {
            if (ContrastRatio(Composite(text, fill, opacity), fill) >= MinimumContrast)
            {
                return new AccentRowText(text, opacity);
            }
        }

        return new AccentRowText(text, OpacitySteps[^1]);
    }

    // This chooses the colour at full strength. A tie uses the dark text.
    private static Color PickTextColor(Color fill) =>
        ContrastRatio(OnLightFill, fill) >= ContrastRatio(OnDarkFill, fill)
            ? OnLightFill
            : OnDarkFill;

    // This mixes text over fill in sRGB bytes. This matches how a brush with opacity paints.
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

    /// <summary>
    /// Returns the WCAG contrast ratio of two colours, from 1.0 to 21.0.
    /// Equal colours give 1.0. Black against white gives 21.0.
    /// Alpha is ignored. Both colours are opaque here.
    /// </summary>
    public static double ContrastRatio(Color first, Color second)
    {
        double one = RelativeLuminance(first);
        double other = RelativeLuminance(second);
        (double lighter, double darker) = one >= other ? (one, other) : (other, one);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Returns the WCAG relative luminance of one colour, from 0.0 to 1.0.</summary>
    public static double RelativeLuminance(Color color) =>
        (0.2126 * ToLinear(color.R))
        + (0.7152 * ToLinear(color.G))
        + (0.0722 * ToLinear(color.B));

    // This changes sRGB to linear light, as WCAG defines it.
    private static double ToLinear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.03928
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
