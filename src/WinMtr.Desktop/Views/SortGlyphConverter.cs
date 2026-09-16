using System.ComponentModel;
using System.Globalization;
using Avalonia.Data.Converters;
using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Views;

/// <summary>
/// Gives one grid column header its sort glyph. The values are the shell's
/// sort descriptor — the active column and its direction — followed by the
/// header's own title, which names the column the header stands for. The
/// active column gets an upward glyph while ascending and a downward glyph
/// while descending; every other column gets empty text, so only one header
/// shows a glyph at a time.
/// </summary>
public sealed class SortGlyphConverter : IMultiValueConverter
{
    public const string Ascending = "▲";

    public const string Descending = "▼";

    public static SortGlyphConverter Instance { get; } = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 3
            || values[0] is not HopSortColumn active
            || values[1] is not ListSortDirection direction
            || values[2] is not string title)
            return string.Empty;

        // The title identifies its column through the one shared mapping:
        // the latency titles state their millisecond unit, which the mapping
        // strips before the column name is read.
        return HopSortColumns.FromHeaderTitle(title) == active
            ? direction == ListSortDirection.Descending ? Descending : Ascending
            : string.Empty;
    }
}
