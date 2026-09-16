namespace WinMtr.Desktop.Services;

/// <summary>
/// Saves a rendered report under a suggested filename. Behind an interface so
/// view model behaviour stays testable without a user interface: tests inject
/// a recording substitute while the window wires the storage provider.
/// Returns true when content reaches a file, false when the operator cancels
/// the dialog, so the caller confirms only real saves.
/// </summary>
public interface IReportFileSaver
{
    Task<bool> SaveAsync(string suggestedFileName, string content);
}
