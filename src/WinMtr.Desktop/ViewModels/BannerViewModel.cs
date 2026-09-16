using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinMtr.Desktop.ViewModels;

public enum BannerSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One non-modal, dismissible banner in the stack between the toolbar and the
/// grid. Banners never take focus and never block input (approved design).
/// A banner's message and severity are fixed at construction: a banner that
/// must say something else is a different banner, so nothing here changes
/// after it is shown.
/// </summary>
public sealed partial class BannerViewModel : ObservableObject
{
    private readonly Action<BannerViewModel> _dismiss;

    public BannerViewModel(string message, BannerSeverity severity, Action<BannerViewModel> dismiss)
    {
        Message = message;
        Severity = severity;
        _dismiss = dismiss;
    }

    public string Message { get; }

    public BannerSeverity Severity { get; }

    public bool IsInfo => Severity == BannerSeverity.Info;

    public bool IsWarning => Severity == BannerSeverity.Warning;

    public bool IsError => Severity == BannerSeverity.Error;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}
