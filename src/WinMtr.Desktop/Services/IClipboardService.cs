namespace WinMtr.Desktop.Services;

/// <summary>
/// Writes report text to the clipboard. Behind an interface so view model
/// behaviour stays testable without a user interface: tests inject a
/// recording substitute while the window wires the Avalonia clipboard.
/// </summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}
