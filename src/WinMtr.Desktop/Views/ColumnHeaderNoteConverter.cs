using System.Globalization;
using Avalonia.Data.Converters;
using WinMtr.Core;

namespace WinMtr.Desktop.Views;

/// <summary>
/// Gives a grid column header the note that belongs to it, keyed by the
/// header's own title. Only the Sent column has one today: it carries the
/// probe interval rule, which says why the counts of two hops can be
/// different (issue WM-11). Every other header converts to null, so it shows
/// no tool tip.
/// </summary>
/// <remarks>
/// The note belongs to the column and not to a row. Every hop obeys the same
/// rule, and a row-level mark would read as a probe outcome, which this is
/// not.
/// </remarks>
public sealed class ColumnHeaderNoteConverter : IValueConverter
{
    public static ColumnHeaderNoteConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value as string == "Sent" ? ProbeIntervalNote.Text : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Column header notes are read-only.");
}
