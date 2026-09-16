using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinMtr.Desktop.Services;
using WinMtr.Core;
using WinMtr.Core.Probing;

namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// The trace settings dialog model. Values snapshot into the session at
/// Start: the dialog edits a copy seeded from the accepted settings, and
/// only confirmation publishes a new snapshot, so cancel leaves the
/// previously accepted settings unchanged and no live dialog is ever read
/// by a worker. History size and theme are app-level, never part of
/// ProbeSettings, and follow the same copy-edit-publish rule via
/// MainWindowViewModel, which stores them with the accepted settings.
///
/// The dialog edits these numbers with NumericUpDown, which clamps a typed
/// overflow to its own range on commit (ClipValueToMinMax) and reports an
/// empty edit as a null value, so the view model keeps one nullable
/// property per setting and no shadow text copy. Text that parses to no
/// number keeps the old value in the control and never reaches the
/// binding, so SettingsWindow reports it through MarkUnparsable instead
/// of letting confirmation silently revert it.
///
/// Errors are held in the standard <see cref="INotifyDataErrorInfo"/> store,
/// which Avalonia reads without help and shows beside the field. Each range
/// is stated once, here, and the control repeats it as its own bounds. The
/// interface is implemented directly rather than inherited from
/// ObservableValidator because that base class validates by reflection and
/// is marked unsafe for trimming, while this app builds with
/// IsAotCompatible.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, INotifyDataErrorInfo
{
    public const int MinPayloadSizeBytes = 0;

    public const int MaxPayloadSizeBytes = 65500;

    public const int MinHopLimit = 1;

    public const int MaxHopLimit = 255;

    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    private double? _probeIntervalSeconds = 1.0;
    private int? _payloadSizeBytes = 64;
    private int? _hopLimit = 30;
    private double? _replyTimeoutSeconds = ProbeSettings.Default.ReplyTimeout.TotalSeconds;
    private int? _historySize = AcceptedSettings.DefaultHistorySize;

    public SettingsViewModel() => ValidateAllFields();

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>
    /// The probe interval in seconds. Must be greater than zero: a trace
    /// that waits no time between probes floods the path. Null means the
    /// dialog box is empty or holds text that is not a number, so it reports
    /// the dialog's own 0.1-3600 range instead of the binding failure.
    /// A stored value under the dialog floor (for example 0.05) still runs,
    /// so only null and non-positive numbers fail here.
    /// </summary>
    public double? ProbeIntervalSeconds
    {
        get => _probeIntervalSeconds;
        set
        {
            if (SetProperty(ref _probeIntervalSeconds, value))
                ValidateProbeInterval();
        }
    }

    /// <summary>
    /// The probe payload size in bytes. Null means the dialog box is empty
    /// or holds text that is not a number.
    /// </summary>
    public int? PayloadSizeBytes
    {
        get => _payloadSizeBytes;
        set
        {
            if (SetProperty(ref _payloadSizeBytes, value))
                ValidatePayloadSize();
        }
    }

    /// <summary>
    /// The largest hop number the trace will probe. Null means the dialog
    /// box is empty or holds text that is not a number.
    /// </summary>
    public int? HopLimit
    {
        get => _hopLimit;
        set
        {
            if (SetProperty(ref _hopLimit, value))
                ValidateHopLimit();
        }
    }

    /// <summary>
    /// The reply timeout in seconds: how long one probe waits for a reply
    /// before the attempt counts as lost. Null means the dialog box is empty
    /// or holds text that is not a number. The dialog holds the value to its
    /// own 0.1-60 range, and the engine rejects zero or less.
    /// </summary>
    public double? ReplyTimeoutSeconds
    {
        get => _replyTimeoutSeconds;
        set
        {
            if (SetProperty(ref _replyTimeoutSeconds, value))
                ValidateReplyTimeout();
        }
    }

    [ObservableProperty]
    private bool _resolveHostNames = true;

    /// <summary>
    /// The number of traced targets the history keeps. Null means the dialog
    /// box is empty or holds text that is not a number.
    /// </summary>
    public int? HistorySize
    {
        get => _historySize;
        set
        {
            if (SetProperty(ref _historySize, value))
                ValidateHistorySize();
        }
    }

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    /// <summary>
    /// Whether OK must clear the target history. Set by Clear, unset by
    /// Undo. A request only: the store stays untouched until confirmation,
    /// so cancel needs no restore.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand), nameof(UndoClearHistoryCommand))]
    private bool _isHistoryCleared;

    /// <summary>
    /// How many entries the target history held when the dialog opened.
    /// Seeded by <see cref="MainWindowViewModel.CreateSettingsEditor"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryClearPendingText))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    private int _historyEntryCount;

    [ObservableProperty]
    private int _themeIndex;

    /// <summary>
    /// The theme picker entries. Values carry their display text so the
    /// view needs no converter. Order matches <see cref="AppTheme"/> numeric
    /// order so <see cref="ThemeIndex"/> selects the same entry.
    /// </summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new ThemeOption(AppTheme.System, "System (follow OS)"),
        new ThemeOption(AppTheme.Light, "Light"),
        new ThemeOption(AppTheme.Dark, "Dark"),
    ];

    /// <summary>
    /// Whether the custom payload control is available. False once a
    /// capability probe has observed restriction or inconclusive support;
    /// the session then traces with the default payload and the reason
    /// below says so.
    /// </summary>
    [ObservableProperty]
    private bool _isPayloadEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPayloadDisabledReason))]
    private string _payloadDisabledReason = string.Empty;

    public bool HasErrors => _errors.Count > 0;

    /// <summary>
    /// The message the dialog shows at the foot of the form. Fields are read
    /// top to bottom, so the message names the field the user meets first.
    /// Empty when every field is valid.
    /// </summary>
    public string ErrorText
    {
        get
        {
            if (ProbeIntervalError.Length > 0)
                return ProbeIntervalError;
            if (PayloadSizeError.Length > 0)
                return PayloadSizeError;
            if (HopLimitError.Length > 0)
                return HopLimitError;
            if (ReplyTimeoutError.Length > 0)
                return ReplyTimeoutError;
            return HistorySizeError;
        }
    }

    /// <summary>
    /// Whether the edited values may be confirmed. Derived from the error
    /// store, so the dialog's confirm button and
    /// <see cref="MainWindowViewModel.ApplySettingsEditor"/> can never
    /// disagree about validity.
    /// </summary>
    public bool IsValid => !HasErrors;

    public string ProbeIntervalError => ErrorFor(nameof(ProbeIntervalSeconds));

    public string PayloadSizeError => ErrorFor(nameof(PayloadSizeBytes));

    public string HopLimitError => ErrorFor(nameof(HopLimit));

    public string ReplyTimeoutError => ErrorFor(nameof(ReplyTimeoutSeconds));

    public string HistorySizeError => ErrorFor(nameof(HistorySize));

    public bool HasProbeIntervalError => ProbeIntervalError.Length > 0;

    public bool HasPayloadSizeError => PayloadSizeError.Length > 0;

    public bool HasHopLimitError => HopLimitError.Length > 0;

    public bool HasReplyTimeoutError => ReplyTimeoutError.Length > 0;

    public bool HasHistorySizeError => HistorySizeError.Length > 0;

    public bool HasPayloadDisabledReason => PayloadDisabledReason.Length > 0;

    /// <summary>
    /// The errors for one property, as <see cref="INotifyDataErrorInfo"/>
    /// defines it. A field holds at most one message: the rule that fails is
    /// the one the user must act on.
    /// </summary>
    public IEnumerable GetErrors(string? propertyName)
    {
        if (propertyName is not null && _errors.TryGetValue(propertyName, out string? message))
            return new[] { message };
        return Array.Empty<string>();
    }

    /// <summary>
    /// Seeds this editor copy from the accepted settings. The accepted
    /// snapshot itself is untouched: only applying this editor publishes.
    /// </summary>
    public void FromProbeSettings(ProbeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProbeIntervalSeconds = settings.Cadence.TotalSeconds;
        PayloadSizeBytes = settings.PayloadBytes;
        HopLimit = settings.HopLimit;
        ReplyTimeoutSeconds = settings.ReplyTimeout.TotalSeconds;
        ResolveHostNames = settings.ResolveNames;
    }

    /// <summary>
    /// Snapshots these editor values as the settings for one trace session.
    /// The snapshot cadence stays at the engine default and is not exposed
    /// in the dialog. Callers check <see cref="IsValid"/> first (as
    /// <see cref="MainWindowViewModel.ApplySettingsEditor"/> does), so every
    /// value below is present here.
    /// </summary>
    public ProbeSettings ToProbeSettings() => new(
        Cadence: TimeSpan.FromSeconds(ProbeIntervalSeconds!.Value),
        PayloadBytes: PayloadSizeBytes!.Value,
        HopLimit: HopLimit!.Value,
        ReplyTimeout: TimeSpan.FromSeconds(ReplyTimeoutSeconds!.Value),
        ResolveNames: ResolveHostNames,
        SnapshotInterval: ProbeSettings.Default.SnapshotInterval);

    /// <summary>
    /// Applies the observed capability to the payload control: supported
    /// (or never probed) leaves custom payloads available, while restricted
    /// and undetermined disable the control with the reason the session
    /// will trace with the default payload.
    /// </summary>
    public void ApplyCapability(PayloadSupport? support)
    {
        if (support is PayloadSupport.Restricted or PayloadSupport.Undetermined)
        {
            IsPayloadEnabled = false;
            PayloadDisabledReason = CapabilityMessages.PayloadDisabledReason(support);
        }
        else
        {
            IsPayloadEnabled = true;
            PayloadDisabledReason = string.Empty;
        }
    }

    /// <summary>
    /// What the pending clear will remove. Future tense: the store still
    /// holds the entries until OK confirms the request. The outcome text
    /// belongs to <see cref="MainWindowViewModel"/>, which applies it.
    /// </summary>
    public string HistoryClearPendingText => $"Will clear {HistoryEntryCount} targets.";

    private bool CanClearHistory() => !IsHistoryCleared && HistoryEntryCount > 0;

    /// <summary>
    /// Asks OK to remove every entry from the target history. The store is
    /// untouched until then, so Undo and cancel both cost nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearHistory))]
    private void ClearHistory() => IsHistoryCleared = true;

    /// <summary>
    /// Withdraws the clear request while the dialog stays open.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsHistoryCleared))]
    private void UndoClearHistory() => IsHistoryCleared = false;

    /// <summary>
    /// Returns every dialog field to its default and withdraws a pending
    /// history clear. A request only: nothing is published until OK. The
    /// payload capability state is not a field, so it stays as it is.
    /// </summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        ProbeSettings defaults = ProbeSettings.Default;
        ProbeIntervalSeconds = defaults.Cadence.TotalSeconds;
        PayloadSizeBytes = defaults.PayloadBytes;
        HopLimit = defaults.HopLimit;
        ReplyTimeoutSeconds = defaults.ReplyTimeout.TotalSeconds;
        ResolveHostNames = defaults.ResolveNames;
        HistorySize = AcceptedSettings.DefaultHistorySize;
        Theme = AppTheme.System;
        IsHistoryCleared = false;
    }

    partial void OnThemeChanged(AppTheme value)
    {
        int index = (int)value;
        if (ThemeIndex != index)
            ThemeIndex = index;
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (value >= 0 && value < ThemeOptions.Count)
        {
            AppTheme mapped = ThemeOptions[value].Value;
            if (Theme != mapped)
                Theme = mapped;
        }
    }

    /// <summary>
    /// Validates every field once, so a freshly built editor reports the
    /// same state as one the user has already edited.
    /// </summary>
    private void ValidateAllFields()
    {
        ValidateProbeInterval();
        ValidatePayloadSize();
        ValidateHopLimit();
        ValidateReplyTimeout();
        ValidateHistorySize();
    }

    /// <summary>
    /// Marks one field as holding text that parses to no number (for
    /// example "abc"). The control keeps its old value, so the binding
    /// never updates and IsValid would stay true; this error blocks
    /// confirmation and shows beside the field instead of silently
    /// reverting on commit. Clearing re-validates the kept value.
    /// </summary>
    public void MarkUnparsable(string propertyName)
    {
        string? message = propertyName switch
        {
            nameof(ProbeIntervalSeconds) => "Enter a number between 0.1 and 3600.",
            nameof(PayloadSizeBytes) => "Enter a number between 0 and 65500.",
            nameof(HopLimit) => "Enter a number between 1 and 255.",
            nameof(ReplyTimeoutSeconds) => "Enter a number between 0.1 and 60.",
            nameof(HistorySize) => "Enter a number between 1 and 100.",
            _ => null,
        };
        if (message is not null)
            SetError(propertyName, message);
    }

    /// <summary>
    /// Clears a <see cref="MarkUnparsable"/> error once the text parses
    /// again (or is empty, which the null rule already covers). It
    /// re-validates the kept value so a value set in code keeps its own
    /// message instead of being cleared by mistake.
    /// </summary>
    public void ClearUnparsable(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(ProbeIntervalSeconds):
                ValidateProbeInterval();
                break;
            case nameof(PayloadSizeBytes):
                ValidatePayloadSize();
                break;
            case nameof(HopLimit):
                ValidateHopLimit();
                break;
            case nameof(ReplyTimeoutSeconds):
                ValidateReplyTimeout();
                break;
            case nameof(HistorySize):
                ValidateHistorySize();
                break;
        }
    }

    private void ValidateProbeInterval() => SetError(
        nameof(ProbeIntervalSeconds),
        _probeIntervalSeconds is null
            ? "Enter a number between 0.1 and 3600."
            : double.IsFinite(_probeIntervalSeconds.Value) && _probeIntervalSeconds.Value > 0
                ? null
                : "Probe interval must be greater than zero.");

    private void ValidatePayloadSize()
    {
        if (_payloadSizeBytes is null)
        {
            SetError(nameof(PayloadSizeBytes), "Enter a number between 0 and 65500.");
            return;
        }

        ValidateRange(
            nameof(PayloadSizeBytes),
            _payloadSizeBytes.Value,
            "Payload size",
            MinPayloadSizeBytes,
            MaxPayloadSizeBytes);
    }

    private void ValidateHopLimit()
    {
        if (_hopLimit is null)
        {
            SetError(nameof(HopLimit), "Enter a number between 1 and 255.");
            return;
        }

        ValidateRange(
            nameof(HopLimit), _hopLimit.Value, "Hop limit", MinHopLimit, MaxHopLimit);
    }

    private void ValidateReplyTimeout()
    {
        if (_replyTimeoutSeconds is null || !double.IsFinite(_replyTimeoutSeconds.Value))
        {
            SetError(nameof(ReplyTimeoutSeconds), "Enter a number between 0.1 and 60.");
            return;
        }

        double timeout = _replyTimeoutSeconds.Value;
        SetError(
            nameof(ReplyTimeoutSeconds),
            timeout >= AcceptedSettings.MinReplyTimeoutSeconds
            && timeout <= AcceptedSettings.MaxReplyTimeoutSeconds
                ? null
                : "Reply timeout must be between 0.1 and 60.");
    }

    private void ValidateHistorySize()
    {
        if (_historySize is null)
        {
            SetError(nameof(HistorySize), "Enter a number between 1 and 100.");
            return;
        }

        ValidateRange(
            nameof(HistorySize),
            _historySize.Value,
            "History size",
            AcceptedSettings.MinHistorySize,
            AcceptedSettings.MaxHistorySize);
    }

    /// <summary>
    /// The one rule the three whole-number fields share.
    /// </summary>
    private void ValidateRange(
        string propertyName, int value, string subject, int minimum, int maximum) => SetError(
        propertyName,
        value >= minimum && value <= maximum
            ? null
            : $"{subject} must be between {minimum} and {maximum}.");

    /// <summary>
    /// Records or clears one field's message. A message that has not changed
    /// raises nothing, so editing inside a valid range stays quiet.
    /// </summary>
    private void SetError(string propertyName, string? message)
    {
        _errors.TryGetValue(propertyName, out string? current);
        if (current == message)
            return;

        if (message is null)
            _errors.Remove(propertyName);
        else
            _errors[propertyName] = message;

        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        RaiseErrorProperties(propertyName);
    }

    /// <summary>
    /// The error store raises only ErrorsChanged. The dialog binds the
    /// derived error properties, so republish the pair for the changed field
    /// and the form-level properties.
    /// </summary>
    private void RaiseErrorProperties(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(ProbeIntervalSeconds):
                OnPropertyChanged(nameof(ProbeIntervalError));
                OnPropertyChanged(nameof(HasProbeIntervalError));
                break;
            case nameof(PayloadSizeBytes):
                OnPropertyChanged(nameof(PayloadSizeError));
                OnPropertyChanged(nameof(HasPayloadSizeError));
                break;
            case nameof(HopLimit):
                OnPropertyChanged(nameof(HopLimitError));
                OnPropertyChanged(nameof(HasHopLimitError));
                break;
            case nameof(ReplyTimeoutSeconds):
                OnPropertyChanged(nameof(ReplyTimeoutError));
                OnPropertyChanged(nameof(HasReplyTimeoutError));
                break;
            case nameof(HistorySize):
                OnPropertyChanged(nameof(HistorySizeError));
                OnPropertyChanged(nameof(HasHistorySizeError));
                break;
        }

        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(IsValid));
    }

    private string ErrorFor(string propertyName) =>
        _errors.TryGetValue(propertyName, out string? message) ? message : string.Empty;
}
