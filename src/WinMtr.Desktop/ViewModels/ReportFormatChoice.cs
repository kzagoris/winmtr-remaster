using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WinMtr.Core.Reporting;

namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// One row of a copy or export format menu. The row carries its own name,
/// its own active mark and the command that picks it, so the menu binds to
/// the row alone. A menu flyout opens in a popup outside the window's visual
/// tree, where a binding that walks up to the window finds no window and
/// silently gives nothing; a self-contained row keeps the check mark and the
/// command working there.
/// </summary>
public sealed partial class ReportFormatChoice(IReportRenderer format, ICommand command) : ObservableObject
{
    /// <summary>The renderer this row picks.</summary>
    public IReportRenderer Format { get; } = format;

    /// <summary>The format name shown as the menu row header, e.g. CSV.</summary>
    public string Name => Format.Name;

    /// <summary>The command that picks this format and runs the action.</summary>
    public ICommand Command { get; } = command;

    /// <summary>
    /// True while this row is the shown format of its menu. The menu marks
    /// exactly one row, so the menu reads as a picker as well as an action.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;
}
