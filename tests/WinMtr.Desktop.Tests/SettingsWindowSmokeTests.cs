using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;

namespace WinMtr.Desktop.Tests;

public class SettingsWindowSmokeTests
{
    /// <summary>
    /// Range overflow clamps on commit instead of keeping the last valid
    /// prefix: typing 999 into the hop limit shows 255, not 99.
    /// </summary>
    [AvaloniaFact]
    public void Hop_Limit_Overflow_Clamps_To_Maximum()
    {
        var viewModel = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            var hopLimit = dialog.FindControl<NumericUpDown>("HopLimitBox")!;

            Assert.True(hopLimit.ClipValueToMinMax);

            hopLimit.Text = "999";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(255m, hopLimit.Value);
            Assert.Equal(255, viewModel.HopLimit);
            Assert.True(viewModel.IsValid);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// Text that parses to no number never reaches the binding, so the
    /// dialog watches it directly: "abc" blocks OK with a visible error
    /// instead of closing and silently reverting to the kept value.
    /// </summary>
    [AvaloniaFact]
    public void Unparsable_Interval_Blocks_Confirmation()
    {
        var viewModel = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            var interval = dialog.FindControl<NumericUpDown>("ProbeIntervalBox")!;
            var ok = dialog.FindControl<Button>("OkButton")!;

            interval.Text = "abc";
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewModel.IsValid);
            Assert.NotEmpty(viewModel.ErrorText);
            Assert.True(DataValidationErrors.GetHasErrors(interval));
            Assert.False(ok.IsEnabled);

            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(closed);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_Dialog_Shows_Approved_Fields_And_Validates()
    {
        var viewModel = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            var interval = dialog.FindControl<NumericUpDown>("ProbeIntervalBox");
            var payload = dialog.FindControl<NumericUpDown>("PayloadSizeBox");
            var hopLimit = dialog.FindControl<NumericUpDown>("HopLimitBox");
            var replyTimeout = dialog.FindControl<NumericUpDown>("ReplyTimeoutBox");
            var resolve = dialog.FindControl<CheckBox>("ResolveNamesBox");
            var history = dialog.FindControl<NumericUpDown>("HistorySizeBox");
            var clear = dialog.FindControl<Button>("ClearHistoryButton");
            var undo = dialog.FindControl<Button>("UndoClearHistoryButton");
            var clearedText = dialog.FindControl<TextBlock>("HistoryClearPendingText");
            var error = dialog.FindControl<TextBlock>("SettingsErrorText");
            var ok = dialog.FindControl<Button>("OkButton");
            var cancel = dialog.FindControl<Button>("CancelButton");
            var restore = dialog.FindControl<Button>("RestoreDefaultsButton");

            Assert.All(
                new object?[] { interval, payload, hopLimit, replyTimeout, resolve, history, clear, undo, clearedText, error, ok, cancel, restore },
                Assert.NotNull);

            // Bindings resolved to the approved defaults.
            Assert.Equal(1m, interval!.Value);
            Assert.Equal(64m, payload!.Value);
            Assert.Equal(30m, hopLimit!.Value);
            Assert.Equal(5m, replyTimeout!.Value);
            Assert.True(resolve!.IsChecked);

            // History size is a live setting now that the history is stored.
            Assert.True(history!.IsEnabled);
            Assert.Equal(10m, history.Value);

            Assert.True(ok!.IsEnabled);

            viewModel.ProbeIntervalSeconds = -1;
            Assert.NotEmpty(viewModel.ErrorText);
            Assert.False(string.IsNullOrEmpty(error!.Text));
            Assert.False(ok.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// The dialog grows to fit the form and opens centred on its owner:
    /// the height follows the content, the starting width holds the help
    /// lines, and the user can resize from there. The owned show also proves
    /// that the CenterOwner placement runs without a throw, and every help line
    /// sits visible in the tree. Headless uses a stub screen, so the test
    /// cannot check clipping or positions. The OK/Cancel pair keeps its
    /// order and the OK gate follows IsValid in both directions.
    /// </summary>
    [AvaloniaFact]
    public void Settings_Dialog_Sizes_To_Content_And_Centres_On_Owner()
    {
        string[] helpLines =
        {
            "Trade responsiveness against load on the path. Allowed range 0.1-3600 seconds; a shorter interval already stored still runs.",
            "Allowed range 0-65500.",
            "Bounds discovery even when the destination stays silent. Allowed range 1-255.",
            "Off shows addresses only; a slow or broken resolver then cannot obscure the path.",
            "How many traced targets the input keeps, newest first. Allowed range 1-100; a smaller number drops the oldest at once.",
            "System follows your OS light/dark preference; Light and Dark pin the app. Applies on OK.",
        };

        var viewModel = new SettingsViewModel();
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        try
        {
            var dialog = new SettingsWindow { DataContext = viewModel };
            dialog.Show(owner);
            try
            {
                // An owned show gives CenterOwner a window to centre on.
                Assert.Same(owner, dialog.Owner);
                Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
                Assert.Equal(440d, dialog.Width);
                Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
                Assert.True(dialog.CanResize);
                // MinWidth pins the wrapped help lines as the narrowest the
                // user can drag the dialog.
                Assert.Equal(440d, dialog.MinWidth);

                var buttonBar = dialog.FindControl<Grid>("ButtonBar")!;
                var confirmButtons = dialog.FindControl<StackPanel>("ConfirmButtons")!;
                var restore = dialog.FindControl<Button>("RestoreDefaultsButton")!;
                var ok = dialog.FindControl<Button>("OkButton")!;
                var cancel = dialog.FindControl<Button>("CancelButton")!;

                // Restore defaults sits on the left; OK and Cancel keep their
                // order, their roles, and their right alignment.
                Assert.Equal(0, Grid.GetColumn(restore));
                Assert.Equal(2, Grid.GetColumn(confirmButtons));
                Assert.Equal(HorizontalAlignment.Right, confirmButtons.HorizontalAlignment);
                Assert.Equal("OK", (string?)ok.Content);
                Assert.Equal("Cancel", (string?)cancel.Content);
                Assert.True(ok.IsDefault);
                Assert.True(cancel.IsCancel);
                Assert.Same(viewModel.RestoreDefaultsCommand, restore.Command);

                // Every help line is present, visible, and wraps, so the
                // fixed width cannot clip it horizontally.
                var helpBlocks = dialog.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(block => block.IsVisible && block.Text is not null)
                    .ToDictionary(block => block.Text!, block => block);
                Assert.All(helpLines, line =>
                {
                    Assert.True(helpBlocks.ContainsKey(line), $"Help line is missing or hidden: {line}");
                    Assert.Equal(TextWrapping.Wrap, helpBlocks[line].TextWrapping);
                });

                // OK follows IsValid: an invalid value disables it, and a
                // valid value enables it again.
                viewModel.ProbeIntervalSeconds = -1;
                Dispatcher.UIThread.RunJobs();
                Assert.False(viewModel.IsValid);
                Assert.False(ok.IsEnabled);

                viewModel.ProbeIntervalSeconds = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.True(viewModel.IsValid);
                Assert.True(ok.IsEnabled);
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>
    /// The view model reports errors through INotifyDataErrorInfo, so
    /// Avalonia prints the message beside the field by itself. The dialog
    /// once carried a message TextBlock under each field as well, which
    /// printed the same sentence twice. The field must therefore report a
    /// failed rule to the framework, and the view must add no message block
    /// of its own.
    /// </summary>
    [AvaloniaFact]
    public void Field_Error_Is_Left_To_The_Framework()
    {
        var viewModel = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            viewModel.ProbeIntervalSeconds = -1;
            Assert.NotEmpty(viewModel.ProbeIntervalError);

            // The framework knows the field failed, so it prints the message.
            var interval = dialog.FindControl<NumericUpDown>("ProbeIntervalBox")!;
            Assert.True(DataValidationErrors.GetHasErrors(interval));

            // No hand-built block repeats it. Only the footer summarises.
            Assert.Null(dialog.FindControl<TextBlock>("ProbeIntervalErrorText"));
            Assert.Null(dialog.FindControl<TextBlock>("PayloadSizeErrorText"));
            Assert.Null(dialog.FindControl<TextBlock>("HopLimitErrorText"));
            Assert.Null(dialog.FindControl<TextBlock>("HistorySizeErrorText"));
            Assert.Equal(viewModel.ProbeIntervalError, viewModel.ErrorText);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// The probe interval is a decimal number of seconds. The dialog holds
    /// the user to a floor of a tenth of a second, because a probe loop that
    /// waits almost no time floods the path, and it steps by that same tenth
    /// so the spinner lands on round values.
    /// </summary>
    [AvaloniaFact]
    public void The_Probe_Interval_Steps_In_Tenths_Of_A_Second()
    {
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel() };
        dialog.Show();
        try
        {
            var interval = dialog.FindControl<NumericUpDown>("ProbeIntervalBox")!;

            Assert.Equal(0.1m, interval.Minimum);
            Assert.Equal(0.1m, interval.Increment);
            Assert.Equal(3600m, interval.Maximum);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// The floor belongs to the dialog, not to the setting. A shorter
    /// interval stored by an earlier version, or written by hand, still
    /// loads and still runs; the dialog only stops the user choosing one.
    /// </summary>
    [AvaloniaFact]
    public void A_Stored_Interval_Under_The_Floor_Survives_The_Dialog()
    {
        var viewModel = new SettingsViewModel { ProbeIntervalSeconds = 0.05 };
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0.05, viewModel.ProbeIntervalSeconds);
            Assert.True(viewModel.IsValid);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// Text that parses to no number never reaches the binding, so the
    /// dialog watches the reply-timeout text directly: "abc" blocks OK with
    /// the field's own message instead of closing and silently reverting.
    /// </summary>
    [AvaloniaFact]
    public void Unparsable_ReplyTimeout_Blocks_Confirmation()
    {
        var viewModel = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            var timeout = dialog.FindControl<NumericUpDown>("ReplyTimeoutBox")!;
            var ok = dialog.FindControl<Button>("OkButton")!;

            timeout.Text = "abc";
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewModel.IsValid);
            Assert.Equal("Enter a number between 0.1 and 60.", viewModel.ReplyTimeoutError);
            Assert.True(DataValidationErrors.GetHasErrors(timeout));
            Assert.False(ok.IsEnabled);

            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(closed);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// The reply-timeout box carries the dialog's 0.1-60 range and clamps a
    /// typed overflow on commit. Restore defaults is bound to the view model
    /// command and returns the changed field to its default.
    /// </summary>
    [AvaloniaFact]
    public void Reply_Timeout_Box_Clamps_And_RestoreDefaults_Resets()
    {
        var viewModel = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = viewModel };
        dialog.Show();
        try
        {
            var timeout = dialog.FindControl<NumericUpDown>("ReplyTimeoutBox")!;
            var restore = dialog.FindControl<Button>("RestoreDefaultsButton")!;

            Assert.Equal(0.1m, timeout.Minimum);
            Assert.Equal(60m, timeout.Maximum);
            Assert.Equal(0.1m, timeout.Increment);
            Assert.Equal(5m, timeout.Value);
            Assert.True(timeout.ClipValueToMinMax);

            timeout.Text = "999";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(60m, timeout.Value);
            Assert.Equal(60.0, viewModel.ReplyTimeoutSeconds);
            Assert.True(viewModel.IsValid);

            Assert.Same(viewModel.RestoreDefaultsCommand, restore.Command);
            restore.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(5m, timeout.Value);
            Assert.Equal(ProbeSettings.Default.ReplyTimeout.TotalSeconds, viewModel.ReplyTimeoutSeconds);
            Assert.True(viewModel.IsValid);
        }
        finally
        {
            dialog.Close();
        }
    }
}
