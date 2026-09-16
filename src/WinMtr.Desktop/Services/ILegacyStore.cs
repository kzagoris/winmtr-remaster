namespace WinMtr.Desktop.Services;

/// <summary>
/// The settings the previous WinMTR version knew. It had no hop limit and no
/// theme, so those two keep their defaults after a legacy import.
/// </summary>
/// <param name="ProbeIntervalSeconds">The stored interval, held there in milliseconds.</param>
/// <param name="PayloadBytes">The stored ping size.</param>
/// <param name="ResolveNames">The stored UseDNS choice.</param>
/// <param name="HistorySize">The stored MaxLRU value.</param>
public sealed record LegacySettings(
    double ProbeIntervalSeconds,
    int PayloadBytes,
    bool ResolveNames,
    int HistorySize);

/// <summary>
/// Read-only access to the previous version's store, used once per absent file
/// (ADR 0005). Nothing is ever written back, so both versions can be installed
/// together.
/// </summary>
public interface ILegacyStore
{
    /// <summary>Null when the previous version stored nothing readable.</summary>
    LegacySettings? ReadSettings();

    /// <summary>The stored hosts, newest first; empty when there are none.</summary>
    IReadOnlyList<string> ReadHistory();
}
