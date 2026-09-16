using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly List<(NumericUpDown Box, EventHandler<global::Avalonia.AvaloniaPropertyChangedEventArgs> Handler)> _textWatches = new();
    private SettingsViewModel? _watchedEditor;

    public SettingsWindow()
    {
        InitializeComponent();

        // Set the height bound before the window opens, so the first layout
        // pass and the CenterOwner placement both use the final size. A
        // bound set after opening would leave the dialog at the position
        // calculated for its taller, unclamped size.
        try
        {
            if (Screens.Primary is { } screen)
                ApplyWorkingAreaHeightLimit(screen);
        }
        catch (Exception)
        {
            // No screen service (a windowing backend without screens).
            // Keep the XAML bounds.
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The constructor used the primary screen. The dialog opens on the
        // screen of its owner, which can be shorter. Lower the bound for
        // that screen, then shift the dialog by half of the height change,
        // so it stays centred on the owner. The bound only falls here, so
        // the new height is the old height capped by MaxHeight.
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is not null)
            {
                double oldHeight = ClientSize.Height;
                if (ApplyWorkingAreaHeightLimit(screen))
                {
                    double newHeight = Math.Min(oldHeight, MaxHeight);
                    double shift = (oldHeight - newHeight) / 2 * DesktopScaling;
                    if (shift > 0)
                        Position = new PixelPoint(Position.X, Position.Y + (int)Math.Round(shift));
                }
            }
        }
        catch (Exception)
        {
            // No screen available: keep the current bounds.
        }

        WatchNumericText();
    }

    /// <summary>
    /// Lowers MaxHeight so the window fits the working area, with 48 DIPs
    /// left for the frame and the task bar. Returns true when the bound
    /// changed. The limit only lowers, so an explicit XAML MaxHeight stays.
    /// </summary>
    private bool ApplyWorkingAreaHeightLimit(Screen screen)
    {
        double scale = screen.Scaling;
        if (scale <= 0)
            scale = 1;
        double maxHeight = screen.WorkingArea.Height / scale - 48;
        if (maxHeight < MinHeight || maxHeight >= MaxHeight)
            return false;
        MaxHeight = maxHeight;
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        foreach ((NumericUpDown box, var handler) in _textWatches)
            box.PropertyChanged -= handler;
        _textWatches.Clear();
        _watchedEditor = null;
    }

    // NumericUpDown keeps its old Value when the text parses to no number,
    // so the two-way binding never reaches the editor and IsValid stays
    // true while "abc" is still visible. Watch Text live: a non-empty text
    // that parses to no decimal marks the field through the editor (which
    // disables OK and shows beside the field), while empty lets the null
    // rule speak and parseable text clears back to the kept value's state.
    // This preserves the typed text: the editor error changes no value, so
    // the control never overwrites what the user is still editing. Range
    // overflow needs no watch: ClipValueToMinMax clamps it on commit.
    private void WatchNumericText()
    {
        foreach ((NumericUpDown box, var handler) in _textWatches)
            box.PropertyChanged -= handler;
        _textWatches.Clear();
        _watchedEditor = DataContext as SettingsViewModel;
        if (_watchedEditor is null)
            return;

        WatchBox("ProbeIntervalBox", nameof(SettingsViewModel.ProbeIntervalSeconds));
        WatchBox("PayloadSizeBox", nameof(SettingsViewModel.PayloadSizeBytes));
        WatchBox("HopLimitBox", nameof(SettingsViewModel.HopLimit));
        WatchBox("ReplyTimeoutBox", nameof(SettingsViewModel.ReplyTimeoutSeconds));
        WatchBox("HistorySizeBox", nameof(SettingsViewModel.HistorySize));
    }

    private void WatchBox(string controlName, string propertyName)
    {
        if (this.FindControl<NumericUpDown>(controlName) is not { } box)
            return;
        CheckBoxText(box, propertyName);
        EventHandler<global::Avalonia.AvaloniaPropertyChangedEventArgs> handler = (_, e) =>
        {
            if (e.Property == NumericUpDown.TextProperty)
                CheckBoxText(box, propertyName);
        };
        box.PropertyChanged += handler;
        _textWatches.Add((box, handler));
    }

    private void CheckBoxText(NumericUpDown box, string propertyName)
    {
        if (_watchedEditor is null)
            return;
        string? text = box.Text;
        if (string.IsNullOrWhiteSpace(text)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out _))
            _watchedEditor.ClearUnparsable(propertyName);
        else
            _watchedEditor.MarkUnparsable(propertyName);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings && !settings.IsValid)
        {
            return;
        }

        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
