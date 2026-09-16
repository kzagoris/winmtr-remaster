using Avalonia.Input.Platform;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The production clipboard behind <see cref="IClipboardService"/>, wrapping
/// the window's Avalonia clipboard. No behaviour lives here: formatting comes
/// from the selected report renderer via the view model.
/// </summary>
public sealed class AvaloniaClipboardService(IClipboard clipboard) : IClipboardService
{
    private readonly IClipboard _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    public Task SetTextAsync(string text) => _clipboard.SetTextAsync(text);
}
