using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The production file saver behind <see cref="IReportFileSaver"/>: suggests
/// the view model's filename in a save dialog filtered to the report's
/// extension, then writes the rendered content. Cancelling the dialog saves
/// nothing. No formatting lives here: content comes from the selected report
/// renderer via the view model.
/// </summary>
public sealed class StorageReportFileSaver(TopLevel topLevel) : IReportFileSaver
{
    private readonly TopLevel _topLevel = topLevel ?? throw new ArgumentNullException(nameof(topLevel));

    public async Task<bool> SaveAsync(string suggestedFileName, string content)
    {
        string ext = Path.GetExtension(suggestedFileName).TrimStart('.');
        IReadOnlyList<FilePickerFileType>? choices = string.IsNullOrEmpty(ext)
            ? null
            : [new FilePickerFileType($"Report (*.{ext})") { Patterns = [$"*.{ext}"] }];
        var options = new FilePickerSaveOptions
        {
            Title = "Export report",
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = choices,
            ShowOverwritePrompt = true,
        };

        IStorageFile? file = await _topLevel.StorageProvider.SaveFilePickerAsync(options);
        if (file is null)
            return false;

        await using Stream stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(content);
        return true;
    }
}
