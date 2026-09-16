using CommunityToolkit.Mvvm.ComponentModel;

namespace WinMtr.Desktop.Services;

/// <summary>
/// What the early real-Linux observation recorded: whether an unprivileged
/// transit-expired reply exposes the intermediate router address. This
/// decides what the privilege guidance may claim. Pending means the check
/// has not run yet; Unavailable means the runtime cannot run it; only a
/// genuine observation settles Exposed or Hidden.
/// </summary>
public enum TransitAddressCheckStatus
{
    Pending,
    Unavailable,
    AddressExposed,
    AddressHidden,
}

/// <summary>
/// Records the early real-Linux check on whether an unprivileged
/// transit-expired reply exposes the intermediate router address, so the
/// privilege guidance reflects observed behavior instead of guessing.
/// The check starts pending. When the runtime is unavailable (anything but
/// Linux, or no live observer wired) it is recorded as unavailable: that
/// uncertainty is a distinct outcome, never a pass. Only
/// <see cref="RecordObservation"/> with a real observed answer settles the
/// check as exposed or hidden, so nothing here can report a pass that was
/// never seen. A settled observation always wins over a later unavailable
/// report.
/// </summary>
public sealed partial class UnprivilegedTransitCheck : ObservableObject
{
    [ObservableProperty]
    private TransitAddressCheckStatus _status = TransitAddressCheckStatus.Pending;

    /// <summary>
    /// Records that the check runtime is unavailable, leaving a settled
    /// observation untouched. Never reports a pass.
    /// </summary>
    public void RecordUnavailable()
    {
        if (Status == TransitAddressCheckStatus.Pending)
            Status = TransitAddressCheckStatus.Unavailable;
    }

    /// <summary>
    /// Records one genuine observation of whether an unprivileged
    /// transit-expired reply exposed the intermediate router address.
    /// This is the only path to <see cref="TransitAddressCheckStatus.AddressExposed"/>.
    /// </summary>
    public void RecordObservation(bool addressExposed) =>
        Status = addressExposed
            ? TransitAddressCheckStatus.AddressExposed
            : TransitAddressCheckStatus.AddressHidden;
}
