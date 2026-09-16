using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinMtr.Desktop.Services;

/// <summary>
/// Reads the previous version's registry store under
/// <c>HKCU\Software\WinMTR</c>. Read-only by design (ADR 0005): v0.92 keeps
/// working beside this one. On any platform but Windows it reports nothing.
/// </summary>
public sealed class WindowsLegacyStore : ILegacyStore
{
    private const string ConfigPath = @"Software\WinMTR\Config";
    private const string HistoryPath = @"Software\WinMTR\LRU";

    public LegacySettings? ReadSettings()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return ReadSettingsFromRegistry();
    }

    public IReadOnlyList<string> ReadHistory()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        return ReadHistoryFromRegistry();
    }

    [SupportedOSPlatform("windows")]
    private static LegacySettings? ReadSettingsFromRegistry()
    {
        try
        {
            using RegistryKey? config = Registry.CurrentUser.OpenSubKey(ConfigPath);
            if (config is null)
                return null;

            // The previous version stored the interval in milliseconds.
            int intervalMilliseconds = ReadInt(config, "Interval", 1000);
            return new LegacySettings(
                ProbeIntervalSeconds: intervalMilliseconds / 1000.0,
                PayloadBytes: ReadInt(config, "PingSize", 64),
                ResolveNames: ReadInt(config, "UseDNS", 1) != 0,
                HistorySize: ReadInt(config, "MaxLRU", AcceptedSettings.DefaultHistorySize));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadHistoryFromRegistry()
    {
        try
        {
            using RegistryKey? history = Registry.CurrentUser.OpenSubKey(HistoryPath);
            if (history is null)
                return [];

            var slots = new Dictionary<int, string>();
            foreach (string name in history.GetValueNames())
            {
                if (!name.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                    continue;
                if (history.GetValue(name) is string host)
                    slots[slot] = host;
            }

            return LegacyRingOrder.NewestFirst(slots, ReadInt(history, "NrLRU", 0)).ToArray();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ReadInt(RegistryKey key, string name, int fallback) =>
        key.GetValue(name) is int stored ? stored : fallback;
}
