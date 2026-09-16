using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Sessions;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// A target becomes the accepted target only when a trace session accepts it
/// (CONTEXT.md, Trace session). A name that does not resolve is rejected, so
/// no status surface may name it, and the rejection must read in the same
/// inline slot as a validation error.
/// </summary>
public class RejectedTargetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static Route OneHop(Target target, DateTimeOffset takenAt) =>
        RouteScript.Snapshot(
            target, takenAt,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));

    private static Func<Tracer> ResolveFailureFactory() =>
        new TracerScript { ResolveFailure = new SocketException((int)SocketError.HostNotFound) }.BuildFactory();

    private static Func<Tracer> LiveFactory(out Target target)
    {
        target = RouteScript.TestTarget();
        return new TracerScript { Target = target, Snapshots = [OneHop(target, T0)] }.BuildFactory();
    }

    [Fact]
    public async Task FailedResolution_LeavesNoAcceptedTarget()
    {
        var session = new TraceSession(ResolveFailureFactory());

        await session.StartAsync("nosuchhost.invalid", ProbeSettings.Default);

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Equal(string.Empty, session.AcceptedTarget);
    }

    [Fact]
    public async Task SuccessfulPreparation_AcceptsTheTarget()
    {
        var session = new TraceSession(LiveFactory(out _));

        await session.StartAsync(" example.com ", ProbeSettings.Default);

        Assert.Equal("example.com", session.AcceptedTarget);
    }

    [Fact]
    public async Task TypingANewTarget_DoesNotRenameThePreviousResult()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory(out _));
        viewModel.Target = "8.8.8.8";
        await viewModel.StartStopCommand.ExecuteAsync(null);
        Assert.Equal("Last run: 8.8.8.8", viewModel.AcceptedTargetText);

        viewModel.Target = "this-host-does-not-exist-zz9.example";

        // The rows, the elapsed time, and the destination still describe the
        // finished run, so the named target must too, labelled as the last run.
        Assert.NotEmpty(viewModel.Rows);
        Assert.Equal("Last run: 8.8.8.8", viewModel.AcceptedTargetText);
    }

    [Fact]
    public void BeforeTheFirstSession_NoTargetIsNamed()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory(out _));

        Assert.Equal("Target: —", viewModel.AcceptedTargetText);
    }

    [AvaloniaFact]
    public async Task StatusBar_NamesTheAcceptedTarget_NotTheInputText()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory(out _));
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        var statusTarget = window.FindControl<TextBlock>("StatusTargetText")!;

        viewModel.Target = "8.8.8.8";
        await viewModel.StartStopCommand.ExecuteAsync(null);
        Assert.Equal("Last run: 8.8.8.8", statusTarget.Text);

        viewModel.Target = "this-host-does-not-exist-zz9.example";

        Assert.Equal("Last run: 8.8.8.8", statusTarget.Text);
    }

    [Fact]
    public async Task FailedLookup_ReadsInTheInlineValidatorSlot()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), ResolveFailureFactory());
        viewModel.Target = "this-host-does-not-exist-zz9.example";

        await viewModel.StartStopCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasTargetError);
        Assert.Contains("Could not resolve", viewModel.TargetError);
        Assert.Equal("Target: —", viewModel.AcceptedTargetText);
    }

    [Fact]
    public async Task EditingTheTarget_ClearsTheInlineLookupFailure()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), ResolveFailureFactory());
        viewModel.Target = "this-host-does-not-exist-zz9.example";
        await viewModel.StartStopCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasTargetError);

        viewModel.Target = "example.com";

        Assert.False(viewModel.HasTargetError);
        Assert.Equal(string.Empty, viewModel.TargetError);
    }

    [Fact]
    public async Task EditingIntoAnInvalidTarget_KeepsTheValidationError()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), ResolveFailureFactory());
        viewModel.Target = "this-host-does-not-exist-zz9.example";
        await viewModel.StartStopCommand.ExecuteAsync(null);

        viewModel.Target = "-bad-.example";

        // The stale rejection retires, but the text in the box is invalid
        // now: the inline slot must describe what the person typed.
        Assert.True(viewModel.HasTargetError);
        Assert.DoesNotContain("Could not resolve", viewModel.TargetError);
    }
}
