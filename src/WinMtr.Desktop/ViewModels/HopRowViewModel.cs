using CommunityToolkit.Mvvm.ComponentModel;
using WinMtr.Core;

namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// One typed row per hop in the live grid. The trace session reconciles these
/// rows against each published route snapshot: rows are keyed by hop index,
/// updated in place as snapshots arrive, and newly discovered hops append, so
/// sorting and selection observe stable row objects. A row that has appeared
/// stays for the rest of the trace session: when trailing silence trims its
/// hop from the current snapshot the row freezes at its last true values
/// instead of vanishing, and when one address replaces another at the same hop
/// index the row takes a transient highlight while showing only the new hop's
/// statistics.
/// </summary>
public sealed partial class HopRowViewModel : ObservableObject
{
    [ObservableProperty]
    private int _hop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddress))]
    [NotifyPropertyChangedFor(nameof(HostToolTip))]
    [NotifyPropertyChangedFor(nameof(HostLabel))]
    private string _host = string.Empty;

    [ObservableProperty]
    private double _lossPercent;

    [ObservableProperty]
    private long _sent;

    [ObservableProperty]
    private long _received;

    [ObservableProperty]
    private double? _bestMs;

    [ObservableProperty]
    private double? _averageMs;

    [ObservableProperty]
    private double? _worstMs;

    [ObservableProperty]
    private double? _lastMs;

    /// <summary>
    /// The severity facets derived from the hop's statistics: the loss grade
    /// that drives the Loss ramp and the per-cell latency outlier flags. The
    /// trace session recomputes it wholesale on every update, so it always
    /// describes the statistics currently shown and never accumulates state
    /// of its own. Presentation reads it; sorting and reports do not.
    /// </summary>
    [ObservableProperty]
    private HopSeverity _severity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private string _status = string.Empty;

    /// <summary>
    /// The responding address as text, empty while unknown. Identity lives
    /// here and in <see cref="Host"/> for display; probe state lives in
    /// <see cref="Status"/> only, so the host column never carries status or
    /// diagnostic text. Only the current address is held: no per-probe
    /// history, no per-error history, so a row stays O(1) however long the
    /// session runs.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddress))]
    [NotifyPropertyChangedFor(nameof(HostToolTip))]
    [NotifyPropertyChangedFor(nameof(HostLabel))]
    private string _address = string.Empty;

    /// <summary>
    /// The one line the Host cell shows on a single-line profile: the resolved
    /// host name when there is one, else the responding address, else empty
    /// for a silent hop.
    /// </summary>
    public string HostLabel => string.IsNullOrEmpty(Host) ? Address : Host;

    /// <summary>
    /// True when the Host cell must show a second line with the numeric
    /// address: the row has a known address that differs from the displayed
    /// label. With resolution off <see cref="Host"/> is the address itself,
    /// and a silent hop leaves <see cref="Address"/> empty, so the second
    /// line stays hidden in both cases.
    /// </summary>
    public bool ShowAddress =>
        !string.IsNullOrEmpty(Address) && !string.Equals(Address, Host, StringComparison.Ordinal);

    /// <summary>
    /// The hover text of the Host cell: the displayed label, with the numeric
    /// address in brackets when it differs. Null while the row has no
    /// identity: a tooltip opens for an empty string, so null is the only
    /// value that keeps a silent hop free of an empty tip.
    /// </summary>
    public string? HostToolTip
    {
        get
        {
            if (string.IsNullOrEmpty(Host))
                return string.IsNullOrEmpty(Address) ? null : Address;
            return ShowAddress ? $"{Host} ({Address})" : Host;
        }
    }

    /// <summary>
    /// True while the hop is absent from the current route snapshot. The row
    /// is then frozen at its last true values and says so on its face through
    /// <see cref="DisplayStatus"/>; the marker clears the moment the hop
    /// reappears in a snapshot.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private bool _isFrozen;

    /// <summary>
    /// True while the most recent live update for this hop replaced one
    /// responding address with a different one. Transient: the next live
    /// update for the hop without a replacement clears it again, which at the
    /// engine's snapshot cadence reads as a brief highlight explaining why
    /// the statistics reset.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityNotice))]
    private bool _hasIdentityChange;

    /// <summary>
    /// The status cell text: the latest probe state on its own while live,
    /// with the frozen marker appended while frozen, so the two stay
    /// independently readable. Frozen rows sort on their real values; only
    /// the label changes here.
    /// </summary>
    public string DisplayStatus =>
        IsFrozen
            ? (string.IsNullOrEmpty(Status) ? "(frozen)" : $"{Status} (frozen)")
            : Status;

    /// <summary>
    /// The brief explanation shown while <see cref="HasIdentityChange"/> is
    /// set. Empty otherwise, so ordinary rows carry no diagnostic text.
    /// </summary>
    public string IdentityNotice =>
        HasIdentityChange ? "Hop identity changed; statistics describe only the new hop." : string.Empty;

    /// <summary>
    /// Decides whether an incoming address replaces the held one. Detection
    /// requires two different non-null addresses: the engine preserves
    /// attempts already made across the initial unknown-to-known transition,
    /// so first discovery of a hop is not a reset and must not raise the
    /// highlight, and neither are null-to-null values or a repeated identical
    /// address. Written as an address comparison so a future core change
    /// exposing the discovery epoch on the published hop can replace it; the
    /// known limitation is an address changing and changing back within one
    /// snapshot interval, which reads as no change.
    /// </summary>
    public static bool IsIdentityReplacement(string previousAddress, string incomingAddress) =>
        previousAddress.Length != 0
        && incomingAddress.Length != 0
        && !string.Equals(previousAddress, incomingAddress, StringComparison.Ordinal);
}
