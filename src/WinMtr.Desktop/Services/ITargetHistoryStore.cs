namespace WinMtr.Desktop.Services;

/// <summary>
/// The target history behind an interface: entries are ordered most recent
/// first, reuse promotes rather than duplicates, clearing removes every entry,
/// and no more than <see cref="HistorySize"/> entries are kept. Whether the
/// entries outlive the run is the implementation's business, so the in-memory
/// store and the persistent one substitute for each other.
/// </summary>
public interface ITargetHistoryStore
{
    IReadOnlyList<string> Entries { get; }

    /// <summary>
    /// The maximum number of entries kept. Lowering it drops the oldest
    /// entries at once.
    /// </summary>
    int HistorySize { get; set; }

    void Add(string target);

    void Clear();
}
