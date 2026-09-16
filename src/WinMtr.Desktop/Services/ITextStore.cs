namespace WinMtr.Desktop.Services;

/// <summary>
/// The seam under the persistent stores: named text in, named text out. It
/// keeps the file system out of the store logic, so ordering, trimming,
/// clamping, and the damaged-file rules are all testable in memory.
/// </summary>
public interface ITextStore
{
    /// <summary>
    /// The stored text, or null when nothing is stored under this name or the
    /// content cannot be read. A caller cannot tell the two apart, and does
    /// not need to: both mean "use the defaults".
    /// </summary>
    string? Read(string name);

    /// <summary>
    /// Stores the text under this name, replacing what was there. Returns
    /// false when the write failed; a failed write is reported to the
    /// operator once per session rather than thrown.
    /// </summary>
    bool TryWrite(string name, string content);
}
