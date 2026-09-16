using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WinMtr.Core;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// WM-11: the Sent column says why the counts of two hops can be different.
/// The statement belongs to the column, not to a row, because a responding
/// hop with intermittent loss slows down in the same way a silent hop does.
/// </summary>
public class ProbeIntervalNoteHeaderTests
{
    [AvaloniaFact]
    public void Sent_header_carries_the_probe_interval_note()
    {
        Assert.Equal(ProbeIntervalNote.Text, NoteOfHeader("Sent"));
    }

    [AvaloniaFact]
    public void Other_headers_carry_no_note()
    {
        Assert.Null(NoteOfHeader("Received"));
        Assert.Null(NoteOfHeader("Loss"));
    }

    private static string? NoteOfHeader(string title)
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var grid = window.FindControl<DataGrid>("HopGrid")!;
            grid.Measure(new Size(1200, 800));
            grid.Arrange(new Rect(0, 0, 1200, 800));

            var header = grid.GetVisualDescendants()
                .OfType<DataGridColumnHeader>()
                .Single(h => (h.Content as string) == title);

            return header.GetVisualDescendants()
                .OfType<Control>()
                .Select(ToolTip.GetTip)
                .OfType<string>()
                .FirstOrDefault();
        }
        finally
        {
            window.Close();
        }
    }
}
