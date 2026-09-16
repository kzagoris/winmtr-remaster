namespace WinMtr.Desktop.Services;

/// <summary>
/// The text seam over the per-user local application data folder (ADR 0005):
/// <c>%LOCALAPPDATA%\WinMTR\</c> on Windows, <c>~/.local/share/WinMTR/</c> on
/// Linux. Each write goes to a temporary file that is then moved over the
/// target, so an interrupted write cannot damage the previous content.
/// </summary>
public sealed class FileTextStore : ITextStore
{
    private const string FolderName = "WinMTR";

    private readonly string _folder;

    public FileTextStore(string? folder = null) => _folder = folder ?? DefaultFolder();

    /// <summary>
    /// The per-user folder these files live in. The Create option matters on
    /// Linux, where the parent folder can be absent.
    /// </summary>
    public static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create),
        FolderName);

    public string? Read(string name)
    {
        try
        {
            string path = Path.Combine(_folder, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public bool TryWrite(string name, string content)
    {
        string path = Path.Combine(_folder, name);
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporary);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The temporary file stays behind. The next write replaces it.
        }
    }
}
