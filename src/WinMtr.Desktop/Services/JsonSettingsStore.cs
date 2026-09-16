using System.Text.Json;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;

namespace WinMtr.Desktop.Services;

/// <summary>
/// Reads and writes the accepted settings (ADR 0005). A damaged, unreadable,
/// or unknown-version file yields the defaults with no message and is replaced
/// by the next normal write; a failed write is reported by the return value of
/// <see cref="Save"/>, because a confirmed choice would otherwise be lost
/// without a sign.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";

    private const int SchemaVersion = 1;

    private readonly ITextStore _text;
    private readonly ILegacyStore? _legacy;

    public JsonSettingsStore(ITextStore text, ILegacyStore? legacy = null)
    {
        _text = text;
        _legacy = legacy;
    }

    public AcceptedSettings Load()
    {
        string? content = _text.Read(FileName);
        if (content is null)
            return ImportedOrDefault();

        SettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(content, PersistenceJsonContext.Default.SettingsDocument);
        }
        catch (JsonException)
        {
            return AcceptedSettings.Default;
        }

        if (document is null || document.SchemaVersion != SchemaVersion)
            return AcceptedSettings.Default;

        ProbeSettings defaults = ProbeSettings.Default;
        var loaded = new AcceptedSettings(
            defaults with
            {
                Cadence = document.ProbeIntervalSeconds is { } seconds
                    ? SecondsToCadence(seconds)
                    : defaults.Cadence,
                PayloadBytes = document.PayloadBytes ?? defaults.PayloadBytes,
                HopLimit = document.HopLimit ?? defaults.HopLimit,
                ReplyTimeout = document.ReplyTimeoutSeconds is { } timeout
                    ? SecondsToReplyTimeout(timeout)
                    : defaults.ReplyTimeout,
                ResolveNames = document.ResolveNames ?? defaults.ResolveNames,
            },
            ParseTheme(document.Theme),
            document.HistorySize ?? AcceptedSettings.DefaultHistorySize);

        return loaded.Clamped();
    }

    public bool Save(AcceptedSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = new SettingsDocument
        {
            SchemaVersion = SchemaVersion,
            ProbeIntervalSeconds = settings.Probe.Cadence.TotalSeconds,
            PayloadBytes = settings.Probe.PayloadBytes,
            HopLimit = settings.Probe.HopLimit,
            ReplyTimeoutSeconds = settings.Probe.ReplyTimeout.TotalSeconds,
            ResolveNames = settings.Probe.ResolveNames,
            HistorySize = settings.HistorySize,
            Theme = settings.Theme.ToString(),
        };

        string content = JsonSerializer.Serialize(document, PersistenceJsonContext.Default.SettingsDocument);
        return _text.TryWrite(FileName, content);
    }

    // The previous version is read only while this application owns no file,
    // and never written to.
    private AcceptedSettings ImportedOrDefault()
    {
        if (_legacy?.ReadSettings() is not { } legacy)
            return AcceptedSettings.Default;

        ProbeSettings defaults = ProbeSettings.Default;
        return new AcceptedSettings(
            defaults with
            {
                Cadence = SecondsToCadence(legacy.ProbeIntervalSeconds),
                PayloadBytes = legacy.PayloadBytes,
                ResolveNames = legacy.ResolveNames,
            },
            AppTheme.System,
            legacy.HistorySize).Clamped();
    }

    // A number that cannot become a TimeSpan at all is handed on as the
    // fallback; AcceptedSettings.Clamped keeps whatever survives.
    private static TimeSpan SecondsToTimeSpan(double seconds, TimeSpan fallback) =>
        double.IsFinite(seconds)
        && seconds > TimeSpan.MinValue.TotalSeconds
        && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : fallback;

    private static TimeSpan SecondsToCadence(double seconds) =>
        SecondsToTimeSpan(seconds, ProbeSettings.Default.Cadence);

    private static TimeSpan SecondsToReplyTimeout(double seconds) =>
        SecondsToTimeSpan(seconds, ProbeSettings.Default.ReplyTimeout);

    private static AppTheme ParseTheme(string? stored) =>
        Enum.TryParse(stored, ignoreCase: true, out AppTheme theme) && Enum.IsDefined(theme)
            ? theme
            : AppTheme.System;
}
