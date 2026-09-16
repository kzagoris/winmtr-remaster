using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Thin headless checks that the settings controls, the capability banner,
/// the payload-control state, cancel behavior, validation messages, and the
/// active-session disabled state are connected to the session surface.
/// Behaviour lives in <see cref="SessionSettingsTests"/>; these assert
/// connection, not appearance.
/// </summary>
public class SessionSettingsBindingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateShell(Func<Tracer> factory)
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), factory);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        return (window, viewModel);
    }

    private static Func<Tracer> OneHopFactory(PayloadSupport support, out Target target)
    {
        target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        return new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(support),
            Snapshots = [snapshot],
        }.BuildFactory();
    }

    [AvaloniaFact]
    public async Task RestrictedSession_ShowsBanner_AndDisablesPayloadControl()
    {
        var (window, viewModel) = CreateShell(OneHopFactory(PayloadSupport.Restricted, out _));
        try
        {
            var banners = window.FindControl<ItemsControl>("BannerStack")!;

            viewModel.Target = "example.com";
            await viewModel.StartStopCommand.ExecuteAsync(null);

            // The capability banner reaches the dismissible stack.
            Assert.Equal(1, banners.ItemCount);
            Assert.Contains("restricted", viewModel.Banners[0].Message);

            // The payload control state follows the settled capability.
            var dialog = new SettingsWindow { DataContext = viewModel.CreateSettingsEditor() };
            dialog.Show();
            try
            {
                var payload = dialog.FindControl<NumericUpDown>("PayloadSizeBox")!;
                var reason = dialog.FindControl<TextBlock>("PayloadDisabledReasonText")!;
                var history = dialog.FindControl<NumericUpDown>("HistorySizeBox")!;

                Assert.False(payload.IsEnabled);
                Assert.True(reason.IsVisible);
                Assert.False(string.IsNullOrEmpty(reason.Text));
                // Payload restriction says nothing about the history size,
                // which stays editable.
                Assert.True(history.IsEnabled);
            }
            finally
            {
                dialog.Close();
            }

            // Dismissing clears the stack through the bound command.
            viewModel.Banners[0].DismissCommand.Execute(null);
            Assert.Equal(0, banners.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UndeterminedSession_Banner_SaysSupportCouldNotBeDetermined()
    {
        var (window, viewModel) = CreateShell(OneHopFactory(PayloadSupport.Undetermined, out _));
        try
        {
            var banners = window.FindControl<ItemsControl>("BannerStack")!;

            viewModel.Target = "example.com";
            await viewModel.StartStopCommand.ExecuteAsync(null);

            Assert.Equal(1, banners.ItemCount);
            Assert.Contains("could not be determined", viewModel.Banners[0].Message);

            var dialog = new SettingsWindow { DataContext = viewModel.CreateSettingsEditor() };
            dialog.Show();
            try
            {
                Assert.False(dialog.FindControl<NumericUpDown>("PayloadSizeBox")!.IsEnabled);
                Assert.Contains(
                    "could not be determined",
                    dialog.FindControl<TextBlock>("PayloadDisabledReasonText")!.Text);
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_ValidationMessages_GateConfirmation()
    {
        var editor = new SettingsViewModel();
        var dialog = new SettingsWindow { DataContext = editor };
        dialog.Show();
        try
        {
            var interval = dialog.FindControl<NumericUpDown>("ProbeIntervalBox")!;
            var payload = dialog.FindControl<NumericUpDown>("PayloadSizeBox")!;
            var hopLimit = dialog.FindControl<NumericUpDown>("HopLimitBox")!;
            var resolve = dialog.FindControl<CheckBox>("ResolveNamesBox")!;
            var error = dialog.FindControl<TextBlock>("SettingsErrorText")!;
            var ok = dialog.FindControl<Button>("OkButton")!;
            var cancel = dialog.FindControl<Button>("CancelButton")!;

            // The approved controls are all present and bound to the defaults.
            Assert.Equal(1m, interval.Value);
            Assert.Equal(64m, payload.Value);
            Assert.Equal(30m, hopLimit.Value);
            Assert.True(resolve.IsChecked);
            Assert.True(ok.IsEnabled);

            // An invalid value surfaces its message and gates confirmation.
            editor.ProbeIntervalSeconds = -1;
            Assert.False(string.IsNullOrEmpty(error.Text));
            Assert.Contains("greater than zero", error.Text);
            Assert.False(ok.IsEnabled);

            // Repairing the value clears the message and re-enables confirmation.
            editor.ProbeIntervalSeconds = 1;
            Assert.Equal(string.Empty, error.Text);
            Assert.True(ok.IsEnabled);

            // Confirming an invalid editor leaves the dialog open; cancelling
            // closes it. Closed fires synchronously with Close, so no wait is
            // needed either way.
            editor.HopLimit = 0;
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(closed);

            editor.HopLimit = 30;
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(closed);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void CancelledDialog_LeavesAcceptedSettingsUnchanged()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), new TracerScript().BuildFactory());
        ProbeSettings before = viewModel.AcceptedSettings;

        var editor = viewModel.CreateSettingsEditor();
        editor.FromProbeSettings(new ProbeSettings(
            Cadence: TimeSpan.FromSeconds(0.5),
            PayloadBytes: 1200,
            HopLimit: 5,
            ReplyTimeout: ProbeSettings.Default.ReplyTimeout,
            ResolveNames: false,
            SnapshotInterval: ProbeSettings.Default.SnapshotInterval));

        var dialog = new SettingsWindow { DataContext = editor };
        dialog.Show();
        try
        {
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Cancel closes without publishing: the code-behind only applies on
            // confirmation, and the view model only publishes an applied editor.
            Assert.True(closed);
            Assert.Equal(before, viewModel.AcceptedSettings);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task ActiveSession_Disables_SettingsSurface()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var (window, viewModel) = CreateShell(factory);
        try
        {
            var settings = window.FindControl<Button>("SettingsButton")!;
            var targetInput = window.FindControl<AutoCompleteBox>("TargetInput")!;
            var startStop = window.FindControl<Button>("StartStopButton")!;

            Assert.True(settings.IsEnabled);

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);

            // No setting can change underneath the running session: the
            // dialog cannot be opened and the target cannot be edited.
            Assert.Equal("Stop", startStop.Content);
            Assert.False(settings.IsEnabled);
            Assert.False(targetInput.IsEnabled);

            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);

            Assert.True(settings.IsEnabled);
            Assert.True(targetInput.IsEnabled);
            Assert.True(engine.Disposed);
        }
        finally
        {
            window.Close();
        }
    }
}
