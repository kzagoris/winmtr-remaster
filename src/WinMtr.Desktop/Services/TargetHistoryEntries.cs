namespace WinMtr.Desktop.Services;

/// <summary>
/// The ordering rules <see cref="ITargetHistoryStore"/> promises, held in one
/// place: entries newest first, reuse promotes rather than duplicates, and no
/// more than <see cref="HistorySize"/> entries. Each store holds one of these,
/// so the persistent store adds only the write and the two cannot drift apart.
/// Each operation reports whether the entries changed, which is what tells the
/// persistent store when to write.
/// </summary>
internal sealed class TargetHistoryEntries
{
    private readonly List<string> _entries = new();
    private int _historySize = AcceptedSettings.DefaultHistorySize;

    public IReadOnlyList<string> Entries => _entries;

    public int HistorySize => _historySize;

    /// <summary>
    /// Moves the size into the allowed range and drops the oldest entries at
    /// once when the new size is smaller. Returns whether entries were lost.
    /// </summary>
    public bool SetHistorySize(int value)
    {
        int wanted = AcceptedSettings.ClampHistorySize(value);
        if (wanted == _historySize)
            return false;
        _historySize = wanted;
        return Trim();
    }

    /// <summary>
    /// Puts the stored entries in as they are, then applies the size. Used
    /// once at load, where the entries are already in order.
    /// </summary>
    public void Load(IEnumerable<string> entries)
    {
        _entries.AddRange(entries);
        Trim();
    }

    /// <summary>Returns false for an empty target, which is not kept.</summary>
    public bool Add(string target)
    {
        string candidate = target.Trim();
        if (candidate.Length == 0)
            return false;

        _entries.RemoveAll(entry => string.Equals(entry, candidate, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, candidate);
        Trim();
        return true;
    }

    public void Clear() => _entries.Clear();

    private bool Trim()
    {
        if (_entries.Count <= _historySize)
            return false;
        _entries.RemoveRange(_historySize, _entries.Count - _historySize);
        return true;
    }
}
