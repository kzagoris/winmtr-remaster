using CommunityToolkit.Mvvm.ComponentModel;

namespace WinMtr.Desktop.ViewModels;

/// <summary>
/// A transient confirmation popup anchored to one action control, e.g.
/// Copied 10 hops as CSV. Showing it again restarts the close timer; closing
/// it, by the timer or by light dismiss through the two-way binding, cancels
/// the timer.
/// </summary>
public sealed partial class ConfirmationFlyout : ObservableObject
{
    private CancellationTokenSource? _closeTimer;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isOpen;

    public void Show(string text, TimeSpan duration)
    {
        _closeTimer?.Cancel();
        _closeTimer?.Dispose();
        var timer = new CancellationTokenSource();
        _closeTimer = timer;
        Text = text;
        IsOpen = true;
        _ = CloseAfterAsync(timer, duration);
    }

    // A later Show replaces the timer. The identity check also covers a delay
    // that finished just before that Show cancelled it.
    private async Task CloseAfterAsync(CancellationTokenSource timer, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, timer.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ReferenceEquals(timer, _closeTimer))
            IsOpen = false;
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
            _closeTimer?.Cancel();
    }
}
