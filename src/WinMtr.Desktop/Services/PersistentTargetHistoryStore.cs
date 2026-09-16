using System.Text.Json;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The target history in its own file (ADR 0005). Ordering stays the one
/// ticket 10 established — newest first, reuse promotes, no duplicates — and
/// the file is written whenever the history changes. A damaged or
/// unknown-version file loads as an empty history with no message and is
/// replaced by the next write; clearing writes an empty list rather than
/// deleting the file, so a cleared history cannot be re-imported.
/// </summary>
public sealed class PersistentTargetHistoryStore : ITargetHistoryStore
{
    public const string FileName = "history.json";

    private const int SchemaVersion = 1;

    private readonly ITextStore _text;
    private readonly TargetHistoryEntries _entries = new();

    public PersistentTargetHistoryStore(
        ITextStore text,
        int historySize = AcceptedSettings.DefaultHistorySize,
        ILegacyStore? legacy = null)
    {
        _text = text;
        _entries.SetHistorySize(historySize);
        _entries.Load(LoadEntries(legacy));
    }

    /// <summary>
    /// Told once when a write does not reach the disk. Set after the reporter
    /// exists, because this store writes on its own.
    /// </summary>
    public Action? WriteFailed { get; set; }

    public IReadOnlyList<string> Entries => _entries.Entries;

    /// <summary>
    /// The maximum number of entries kept. Lowering it drops the oldest
    /// entries at once and stores the shorter history.
    /// </summary>
    public int HistorySize
    {
        get => _entries.HistorySize;
        set
        {
            if (_entries.SetHistorySize(value))
                Save();
        }
    }

    public void Add(string target)
    {
        if (_entries.Add(target))
            Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private IEnumerable<string> LoadEntries(ILegacyStore? legacy)
    {
        string? content = _text.Read(FileName);
        if (content is null)
            return legacy?.ReadHistory() ?? [];

        try
        {
            HistoryDocument? document =
                JsonSerializer.Deserialize(content, PersistenceJsonContext.Default.HistoryDocument);
            if (document is null || document.SchemaVersion != SchemaVersion)
                return [];
            return document.Entries ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save()
    {
        var document = new HistoryDocument
        {
            SchemaVersion = SchemaVersion,
            Entries = [.. _entries.Entries],
        };

        string content = JsonSerializer.Serialize(document, PersistenceJsonContext.Default.HistoryDocument);
        if (!_text.TryWrite(FileName, content))
            WriteFailed?.Invoke();
    }
}
