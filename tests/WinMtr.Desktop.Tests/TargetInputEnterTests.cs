using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Thin headless check that Enter in the target box does what the Start
/// button does: a valid target starts a trace session, and an empty target
/// starts nothing. Session behaviour lives elsewhere; this asserts the key.
/// </summary>
public class TargetInputEnterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateShell()
    {
        Target target = RouteScript.TestTarget();
        Route snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = [snapshot] }.BuildFactory();
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), factory);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        return (window, viewModel);
    }

    private static void PressEnter(AutoCompleteBox input) =>
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    [AvaloniaFact]
    public async Task Enter_On_Valid_Target_Starts_The_Session()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var input = window.FindControl<AutoCompleteBox>("TargetInput")!;
            input.Text = "example.com";

            PressEnter(input);

            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
            Assert.Equal(["example.com"], viewModel.TargetHistory.ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Enter_On_Empty_Target_Starts_Nothing()
    {
        var (window, viewModel) = CreateShell();
        try
        {
            var input = window.FindControl<AutoCompleteBox>("TargetInput")!;

            PressEnter(input);

            Assert.Equal("Idle", viewModel.SessionState);
            Assert.Empty(viewModel.Rows);
        }
        finally
        {
            window.Close();
        }
    }
}
