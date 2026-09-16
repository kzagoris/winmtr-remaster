using WinMtr.Desktop.ViewModels;
using WinMtr.Core;

namespace WinMtr.Desktop.Services;

/// <summary>
/// The settings a confirmation established, held between trace sessions: the
/// per-session <see cref="ProbeSettings"/> plus the two app-level choices that
/// never enter them. This is what the store reads and writes.
/// </summary>
public sealed record AcceptedSettings(ProbeSettings Probe, AppTheme Theme, int HistorySize)
{
    public const int MinHistorySize = 1;
    public const int MaxHistorySize = 100;
    public const int DefaultHistorySize = 10;

    // The dialog range for the reply timeout, applied again when a stored
    // value is read back, so the dialog never opens on an invalid field.
    public const double MinReplyTimeoutSeconds = 0.1;
    public const double MaxReplyTimeoutSeconds = 60.0;

    public static AcceptedSettings Default { get; } =
        new(ProbeSettings.Default, AppTheme.System, DefaultHistorySize);

    /// <summary>
    /// Moves every value outside its allowed range to the nearest allowed
    /// value and keeps the others, so one hand-edited or imported number never
    /// costs the operator the rest of their settings. The allowed probe
    /// interval has no smallest member, so an impossible one takes the default
    /// instead of a nearest value.
    /// </summary>
    public AcceptedSettings Clamped()
    {
        double seconds = Probe.Cadence.TotalSeconds;
        TimeSpan cadence = double.IsFinite(seconds) && seconds > 0
            ? Probe.Cadence
            : ProbeSettings.Default.Cadence;
        TimeSpan replyTimeout = TimeSpan.FromSeconds(
            Math.Clamp(Probe.ReplyTimeout.TotalSeconds, MinReplyTimeoutSeconds, MaxReplyTimeoutSeconds));

        return this with
        {
            Probe = Probe with
            {
                Cadence = cadence,
                PayloadBytes = Math.Clamp(Probe.PayloadBytes, 0, 65500),
                HopLimit = Math.Clamp(Probe.HopLimit, 1, 255),
                ReplyTimeout = replyTimeout,
            },
            HistorySize = ClampHistorySize(HistorySize),
        };
    }

    /// <summary>
    /// The one place the allowed history size is applied, so the stores and
    /// the settings cannot hold different ranges.
    /// </summary>
    public static int ClampHistorySize(int value) =>
        Math.Clamp(value, MinHistorySize, MaxHistorySize);
}
