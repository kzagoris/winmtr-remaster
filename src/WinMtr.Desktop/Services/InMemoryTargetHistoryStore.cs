namespace WinMtr.Desktop.Services;

/// <summary>
/// The target history for the run only: nothing is read from or written to
/// disk. It keeps the same ordering and history-size rules as the persistent
/// store, because both hold the same <see cref="TargetHistoryEntries"/>, so a
/// test that swaps one for the other sees one behaviour.
/// </summary>
public sealed class InMemoryTargetHistoryStore : ITargetHistoryStore
{
    private readonly TargetHistoryEntries _entries = new();

    public IReadOnlyList<string> Entries => _entries.Entries;

    public int HistorySize
    {
        get => _entries.HistorySize;
        set => _entries.SetHistorySize(value);
    }

    public void Add(string target) => _entries.Add(target);

    public void Clear() => _entries.Clear();
}
