using WinMtr.Desktop.Services;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The text seam under the stores, held in memory: the persistence tests stay
/// fast and never touch a real folder. Writes can be made to fail so the
/// failed-write path is observable.
/// </summary>
internal sealed class FakeTextStore : ITextStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public bool FailWrites { get; set; }

    public int WriteCount { get; private set; }

    public IReadOnlyDictionary<string, string> Files => _files;

    public void Seed(string name, string content) => _files[name] = content;

    public string? Read(string name) => _files.TryGetValue(name, out string? content) ? content : null;

    public bool TryWrite(string name, string content)
    {
        WriteCount++;
        if (FailWrites)
            return false;
        _files[name] = content;
        return true;
    }
}

/// <summary>
/// A stand-in for the previous version's store, so the legacy import is
/// testable on every platform without a registry.
/// </summary>
internal sealed class FakeLegacyStore : ILegacyStore
{
    public LegacySettings? Settings { get; set; }

    public IReadOnlyList<string> History { get; set; } = [];

    public LegacySettings? ReadSettings() => Settings;

    public IReadOnlyList<string> ReadHistory() => History;
}
