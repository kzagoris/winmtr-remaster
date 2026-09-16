using System.Collections.Immutable;
using System.Net.Sockets;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Sessions;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;
using WinMtr.Core.Probing;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Behavioural coverage for the graphical trace session, driven through the
/// tracer-factory seam with scripted route snapshots. No Avalonia dependency
/// and no real network traffic are involved.
/// </summary>
public class TraceSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel CreateViewModel(Func<Tracer> factory) =>
        new(new InMemoryTargetHistoryStore(), factory);

    private static Func<Tracer> SnapshotsFactory(Target target, IReadOnlyList<Route> snapshots) =>
        new TracerScript { Target = target, Snapshots = snapshots }.BuildFactory();

    private static Hop NamedHop(int index, string address, string? hostName, HopStatistics stats) =>
        RouteScript.RespondingHop(index, address, hostName, stats);

    [Theory]
    [InlineData("999.999.999.999")]
    [InlineData("not a host!")]
    [InlineData("-bad-.example")]
    public void InvalidTarget_RejectedInline_SessionStaysIdle(string input)
    {
        var viewModel = CreateViewModel(SnapshotsFactory(RouteScript.TestTarget(), []));

        viewModel.Target = input;

        Assert.NotEmpty(viewModel.TargetError);
        Assert.True(viewModel.HasTargetError);
        Assert.False(viewModel.StartStopCommand.CanExecute(null));

        viewModel.StartStopCommand.Execute(null);

        Assert.Equal("Idle", viewModel.SessionState);
        Assert.Empty(viewModel.TargetHistory);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public void BlankTarget_DisablesStart_WithoutInlineError()
    {
        var viewModel = CreateViewModel(SnapshotsFactory(RouteScript.TestTarget(), []));

        viewModel.Target = "   ";

        Assert.False(viewModel.StartStopCommand.CanExecute(null));
        Assert.False(viewModel.HasTargetError);
        Assert.Equal("Idle", viewModel.SessionState);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("192.168.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public void ValidTarget_CanStart(string input)
    {
        var viewModel = CreateViewModel(SnapshotsFactory(RouteScript.TestTarget(), []));

        viewModel.Target = input;

        Assert.True(viewModel.StartStopCommand.CanExecute(null));
        Assert.False(viewModel.HasTargetError);
        Assert.Empty(viewModel.TargetError);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("192.168.0.1")]
    [InlineData("::1")]
    public async Task ValidTarget_StartsSessionWithEmptyRowsAndStatistics(string input)
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var viewModel = CreateViewModel(SnapshotsFactory(target, [snapshot]));

        viewModel.Target = input;
        viewModel.StartStopCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        Assert.Equal([input], viewModel.TargetHistory.ToArray());
        Assert.Equal(input, viewModel.Target);
    }

    [Fact]
    public async Task FullLifecycle_Preparing_Tracing_Stopping_Idle()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]) { ReleaseDispose = new TaskCompletionSource() };
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        try
        {
            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);

            // Rows arrive with the first snapshot, just after the state flip.
            await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);
            Assert.Equal("Stop", viewModel.StartStopLabel);
            Assert.False(viewModel.IsSessionIdle);
            Assert.Single(viewModel.Rows);

            viewModel.StartStopCommand.Execute(null);

            await WaitUntilAsync(() => viewModel.SessionState == "Stopping...");
            Assert.Equal("Stopping...", viewModel.StartStopLabel);
            Assert.False(viewModel.StartStopCommand.CanExecute(null));

            engine.ReleaseDispose!.SetResult();
            // The start invocation settles just after the session reaches
            // idle; wait for it too before asserting command availability.
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);

            Assert.Equal("Start", viewModel.StartStopLabel);
            Assert.True(viewModel.IsSessionIdle);
            Assert.True(viewModel.StartStopCommand.CanExecute(null));
            Assert.True(engine.Disposed);
            Assert.Single(viewModel.Rows);
        }
        finally
        {
            // Never strand the parked enumeration on failure.
            engine.ReleaseDispose!.TrySetResult();
        }
    }

    [Fact]
    public async Task StopDuringPreparing_CancelsPromptly_ToIdleWithoutFault()
    {
        var (factory, gate) = GatedPreparation([]);
        var session = new TraceSession(factory);

        Task run = session.StartAsync("example.com", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Preparing);

        await session.StopAsync();

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Empty(session.Rows);
        Assert.False(session.HasFault);
        Assert.Equal(string.Empty, session.Diagnostic);
        await run;
    }

    [Fact]
    public async Task StopDuringTracing_DrainsToIdle_KeepingRows()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var session = new TraceSession(factory);

        Task run = session.StartAsync("example.com", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Tracing && session.Rows.Count == 1);
        Assert.Single(session.Rows);

        await session.StopAsync();

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.True(engine.Disposed);
        Assert.Single(session.Rows);
        Assert.False(session.HasFault);
        await run;
    }

    [Fact]
    public async Task RepeatedStopWhileStopping_IsIgnored()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]) { ReleaseDispose = new TaskCompletionSource() };
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var session = new TraceSession(factory);

        try
        {
            Task run = session.StartAsync("example.com", ProbeSettings.Default);
            await WaitUntilAsync(() => session.State == TraceSessionState.Tracing);

            Task firstStop = session.StopAsync();
            Assert.Equal(TraceSessionState.Stopping, session.State);

            Task secondStop = session.StopAsync();
            Assert.True(secondStop.IsCompletedSuccessfully);
            Assert.Equal(TraceSessionState.Stopping, session.State);

            engine.ReleaseDispose!.SetResult();
            await firstStop;

            Assert.Equal(TraceSessionState.Idle, session.State);
            await run;
        }
        finally
        {
            engine.ReleaseDispose!.TrySetResult();
        }
    }

    [Fact]
    public async Task StartWhileRunning_IsIgnored()
    {
        var (factory, gate) = GatedPreparation([]);
        var session = new TraceSession(factory);

        Task run = session.StartAsync("first.example", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Preparing);

        Task ignored = session.StartAsync("second.example", ProbeSettings.Default);

        Assert.True(ignored.IsCompletedSuccessfully);
        // Preparation has not settled, so no target is accepted yet.
        Assert.Equal(string.Empty, session.AcceptedTarget);

        gate.SetResult();
        await run;
        Assert.Equal(TraceSessionState.Idle, session.State);
        // The ignored start never replaced the running session's target.
        Assert.Equal("first.example", session.AcceptedTarget);
    }

    [Fact]
    public async Task RestartAfterStop_StartsNewSessionWithEmptyRows()
    {
        Target target = RouteScript.TestTarget();
        var first = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        // Each preparation round parks on its own gate, so the cleared rows
        // at the start of a new session are observable deterministically:
        // consumption cannot run before preparation completes.
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparations = 0;
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            ResolveAsync = async (_, ct) =>
            {
                int round = Interlocked.Increment(ref preparations);
                await (round == 1 ? firstGate.Task : secondGate.Task).WaitAsync(ct);
                return target;
            },
            Snapshots = [first],
        }.BuildFactory();
        var session = new TraceSession(factory);

        Task run = session.StartAsync("first.example", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Preparing);
        Assert.Empty(session.Rows);

        firstGate.SetResult();
        await run;
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Single(session.Rows);
        Assert.Equal("first.example", session.AcceptedTarget);

        // A stopped session starts again for another target, beginning empty.
        Task restart = session.StartAsync("second.example", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Preparing);
        Assert.Empty(session.Rows);
        // The previous target is released at the start and the new one is not
        // accepted until its preparation succeeds.
        Assert.Equal(string.Empty, session.AcceptedTarget);

        secondGate.SetResult();
        await restart;
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Single(session.Rows);
        Assert.Equal("second.example", session.AcceptedTarget);
    }

    [Fact]
    public async Task StatusTarget_AfterStop_SaysLastRun_AndClearingTheInputKeepsIt()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);

        // While tracing, the accepted target names the current run.
        Assert.Equal("Target: example.com", viewModel.AcceptedTargetText);

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);

        // The bar keeps the session's numbers, so it says they are the last
        // run; the elapsed time and the destination stay with them.
        Assert.Equal("Last run: example.com", viewModel.AcceptedTargetText);
        Assert.Equal("00:00", viewModel.ElapsedText);
        Assert.Equal("Destination: —", viewModel.DestinationText);

        // Clearing the input never touches the bar: it names the accepted
        // target, not the text in the box.
        viewModel.Target = string.Empty;
        Assert.Equal("Last run: example.com", viewModel.AcceptedTargetText);
    }

    [Fact]
    public async Task StatusTarget_NewStart_ClearsToNone_ThenNamesTheNewRun()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        // Each preparation round parks on its own gate, so the cleared status
        // text at the start of a round is observable deterministically.
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparations = 0;
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            ResolveAsync = async (_, ct) =>
            {
                int round = Interlocked.Increment(ref preparations);
                await (round == 1 ? firstGate.Task : secondGate.Task).WaitAsync(ct);
                return target;
            },
            EngineFactory = (_, _, _) => new GatedTraceEngine([snapshot]),
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        viewModel.Target = "first.example";
        viewModel.StartStopCommand.Execute(null);
        firstGate.SetResult();
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);
        Assert.Equal("Last run: first.example", viewModel.AcceptedTargetText);

        // A new start clears all three session fields together, before the
        // new target is accepted.
        viewModel.Target = "second.example";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Preparing...");
        Assert.Equal("Target: —", viewModel.AcceptedTargetText);
        Assert.Equal("00:00", viewModel.ElapsedText);
        Assert.Equal("Destination: —", viewModel.DestinationText);

        secondGate.SetResult();
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);
        Assert.Equal("Target: second.example", viewModel.AcceptedTargetText);

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);
        Assert.Equal("Last run: second.example", viewModel.AcceptedTargetText);
    }

    [Fact]
    public async Task DnsFailure_ReturnsToIdle_WithActionableDiagnostic()
    {
        Func<Tracer> factory = new TracerScript
        {
            ResolveFailure = new SocketException((int)SocketError.HostNotFound),
        }.BuildFactory();
        var session = new TraceSession(factory);

        await session.StartAsync("nosuchhost.invalid", ProbeSettings.Default);

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Contains("Could not resolve", session.Diagnostic);
        Assert.Contains("nosuchhost.invalid", session.Diagnostic);
        Assert.Empty(session.Rows);
        Assert.False(session.HasFault);
    }

    [Fact]
    public async Task InitializationDeadline_ReturnsToIdle_WithActionableDiagnostic()
    {
        Func<Tracer> factory = new TracerScript
        {
            Target = RouteScript.TestTarget(),
            // A detection that never settles on its own but honors
            // cancellation, so the initialization deadline fires.
            InitializeAsync = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ProbeCapabilities(PayloadSupport.Supported);
            },
            InitTimeout = TimeSpan.FromMilliseconds(50),
            Snapshots = [],
        }.BuildFactory();
        var session = new TraceSession(factory);

        await session.StartAsync("example.com", ProbeSettings.Default);

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Contains("timed out", session.Diagnostic);
        Assert.Empty(session.Rows);
        Assert.False(session.HasFault);
    }

    [Fact]
    public async Task OtherPreparationFailure_ReturnsToIdle_WithDiagnostic()
    {
        Func<Tracer> factory = new TracerScript
        {
            Target = RouteScript.TestTarget(),
            InitializeFailure = new InvalidOperationException("capability probe exploded"),
            Snapshots = [],
        }.BuildFactory();
        var session = new TraceSession(factory);

        await session.StartAsync("example.com", ProbeSettings.Default);

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Contains("capability probe exploded", session.Diagnostic);
        Assert.Empty(session.Rows);
        Assert.False(session.HasFault);
    }

    [Fact]
    public async Task CancelDuringPreparing_ShowsNoBanner_AndKeepsStartAvailable()
    {
        var (factory, gate) = GatedPreparation([]);
        var viewModel = CreateViewModel(factory);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Preparing...");
        Assert.Equal("Cancel", viewModel.StartStopLabel);

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);

        Assert.Empty(viewModel.Banners);
        Assert.Empty(viewModel.Rows);
        Assert.Equal(string.Empty, viewModel.DiagnosticText);
        Assert.Equal("Start", viewModel.StartStopLabel);
        Assert.True(viewModel.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task RuntimeFault_ReturnsToIdle_PreservingRowsAndMeasurements()
    {
        Target target = RouteScript.TestTarget();
        var stats = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10).Record(ProbeOutcome.Expired, 20);
        var first = RouteScript.Snapshot(target, T0, NamedHop(0, "192.0.2.1", "router.example", stats));
        var later = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "router.example", stats),
            NamedHop(1, "198.51.100.7", null, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0)));
        var tail = RouteScript.Snapshot(target, T0 + TimeSpan.FromSeconds(2), NamedHop(0, "192.0.2.1", null, stats));
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Snapshots = [first, later, tail],
            EnumerationFault = new InvalidOperationException("worker blew up"),
            FaultAfterSnapshots = 2,
        }.BuildFactory();
        var session = new TraceSession(factory);

        await session.StartAsync("example.com", ProbeSettings.Default);

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.True(session.HasFault);
        Assert.Contains("worker blew up", session.Diagnostic);
        Assert.Contains("kept", session.Diagnostic);
        Assert.Equal(2, session.Rows.Count);
        Assert.Equal(2, session.Rows[0].Sent);
        Assert.Equal(2, session.Rows[0].Received);
        Assert.Equal("router.example", session.Rows[0].Host);
    }

    [Fact]
    public async Task MidTraceFault_ShowsBanner_AndKeepsCopyEnabled()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Snapshots = [snapshot],
            EnumerationFault = new InvalidOperationException("worker blew up"),
            FaultAfterSnapshots = 1,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        Assert.Single(viewModel.Banners);
        Assert.Contains("kept", viewModel.Banners[0].Message);
        Assert.Contains("worker blew up", viewModel.DiagnosticText);
        Assert.True(viewModel.CopyCommand.CanExecute(null));
        Assert.True(viewModel.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task NormalCompletion_ReturnsToIdle_KeepingRows()
    {
        Target target = RouteScript.TestTarget();
        var first = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var second = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)),
            NamedHop(1, "198.51.100.7", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 30)));
        var session = new TraceSession(SnapshotsFactory(target, [first, second]));

        await session.StartAsync("example.com", ProbeSettings.Default);

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.False(session.HasFault);
        Assert.Equal(string.Empty, session.Diagnostic);
        Assert.Equal(2, session.Rows.Count);
    }

    [Fact]
    public void SkippedIntermediateSnapshot_RetainsRows_AndKeepsRowIdentity()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var first = RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", "first.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)),
            NamedHop(1, "198.51.100.7", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 30)));
        var skipped = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "first.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)),
            NamedHop(1, "198.51.100.7", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 30)),
            NamedHop(2, "203.0.113.9", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 40)));
        var latest = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(2),
            NamedHop(0, "192.0.2.99", "moved.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 11)));

        session.ApplySnapshot(first);
        HopRowViewModel originalFirst = session.Rows[0];
        HopRowViewModel originalSecond = session.Rows[1];

        // The middle publication is skipped, as a slow consumer of the lossy
        // engine publication would: only the latest snapshot may show.
        session.ApplySnapshot(latest);

        // Retention (ADR 0003): the trimmed hop stays as a frozen row with its
        // last true values instead of vanishing, and the surviving row keeps
        // its object identity so selection and sorting survive the update.
        Assert.Equal(2, session.Rows.Count);
        Assert.Same(originalFirst, session.Rows[0]);
        Assert.Same(originalSecond, session.Rows[1]);
        Assert.Equal("moved.example", session.Rows[0].Host);
        Assert.False(session.Rows[0].IsFrozen);
        Assert.True(session.Rows[1].IsFrozen);
        Assert.Equal("198.51.100.7", session.Rows[1].Host);
        Assert.Equal(string.Empty, session.Rows[1].Status);
        Assert.Equal("(frozen)", session.Rows[1].DisplayStatus);
        Assert.Equal("00:02", session.ElapsedText);
    }

    [Fact]
    public void RowFields_MapHopIdentity_Status_Loss_AndWaitingState()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var measured = HopStatistics.Empty
            .Record(ProbeOutcome.Expired, 10)
            .Record(ProbeOutcome.Expired, 20)
            .Record(ProbeOutcome.TimedOut, 0);
        var snapshot = RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", "router.example", measured),
            RouteScript.SilentHop(1, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0)),
            RouteScript.RespondingHop(
                2, "198.51.100.7", null,
                HopStatistics.Empty.Record(ProbeOutcome.Unreachable, 0),
                ProbeOutcome.Unreachable, ProbeErrorReason.HostUnreachable),
            new Hop(new HopIndex(3), null, null, HopStatistics.Empty, ProbeOutcome.TimedOut, null));

        session.ApplySnapshot(snapshot);

        Assert.Equal(4, session.Rows.Count);
        Assert.Equal([1, 2, 3, 4], session.Rows.Select(row => row.Hop).ToArray());

        HopRowViewModel responding = session.Rows[0];
        Assert.Equal("router.example", responding.Host);
        Assert.Equal("192.0.2.1", responding.Address);
        Assert.False(responding.IsFrozen);
        Assert.False(responding.HasIdentityChange);
        Assert.Equal(3, responding.Sent);
        Assert.Equal(2, responding.Received);
        // Accurate loss from the counts: 1 lost of 3 sent, not legacy rounding.
        Assert.Equal(100.0 / 3.0, responding.LossPercent, 6);
        Assert.Equal(10, responding.BestMs);
        Assert.Equal(15, responding.AverageMs);
        Assert.Equal(20, responding.WorstMs);
        Assert.Equal(20, responding.LastMs);
        Assert.Equal(string.Empty, responding.Status);

        HopRowViewModel silent = session.Rows[1];
        Assert.Equal(string.Empty, silent.Host);
        Assert.Equal(string.Empty, silent.Address);
        Assert.Equal("No response.", silent.Status);
        Assert.Equal("No response.", silent.DisplayStatus);

        HopRowViewModel errored = session.Rows[2];
        Assert.Equal("198.51.100.7", errored.Host);
        Assert.Equal("Destination host unreachable.", errored.Status);

        HopRowViewModel waiting = session.Rows[3];
        Assert.Equal(string.Empty, waiting.Host);
        Assert.Equal("Waiting for first result.", waiting.Status);
        Assert.Equal(0, waiting.Sent);
        Assert.Equal(0, waiting.Received);
        Assert.Null(waiting.BestMs);
        Assert.Null(waiting.AverageMs);
        Assert.Null(waiting.WorstMs);
        Assert.Null(waiting.LastMs);
    }

    [Fact]
    public void RowSeverity_Follows_Snapshot_Statistics()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var spiky = HopStatistics.Empty
            .Record(ProbeOutcome.Expired, 20)
            .Record(ProbeOutcome.Expired, 20)
            .Record(ProbeOutcome.Expired, 20)
            .Record(ProbeOutcome.Expired, 100);

        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, spiky)));

        // Average 40 ms, one 100 ms spike on the last and worst samples; the
        // derivation reads the current statistics only.
        HopRowViewModel row = session.Rows[0];
        Assert.Equal(LossLevel.None, row.Severity.Level);
        Assert.False(row.Severity.HasLoss);
        Assert.True(row.Severity.LastIsOutlier);
        Assert.True(row.Severity.WorstIsOutlier);
        Assert.False(row.Severity.BestIsOutlier);
        Assert.False(row.Severity.AverageIsOutlier);

        // A lost probe moves the loss grade without touching the outlier rule.
        var losing = spiky.Record(ProbeOutcome.TimedOut, 0);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1), NamedHop(0, "192.0.2.1", null, losing)));

        Assert.Equal(LossLevel.Moderate, session.Rows[0].Severity.Level);
        Assert.True(session.Rows[0].Severity.HasLoss);
        Assert.True(session.Rows[0].Severity.LastIsOutlier);
    }

    [Fact]
    public void Discovery_AppendsRows_WithoutFillerForNeverDiscoveredHops()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        Assert.Empty(session.Rows);

        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10))));
        Assert.Single(session.Rows);

        // Discovery grows the grid; hops never seen produce no filler rows.
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)),
            NamedHop(1, "198.51.100.7", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 30)),
            NamedHop(2, "203.0.113.9", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 40))));
        Assert.Equal([1, 2, 3], session.Rows.Select(row => row.Hop).ToArray());
        Assert.DoesNotContain(session.Rows, row => row.IsFrozen);
    }

    [Fact]
    public void TrailingSilenceTrim_FreezesRetainedRows_AtLastTrueValues()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var live = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10);
        var secondStats = HopStatistics.Empty
            .Record(ProbeOutcome.TimedOut, 0)
            .Record(ProbeOutcome.Expired, 10)
            .Record(ProbeOutcome.Expired, 30);
        var silentStats = HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", "first.example", live),
            NamedHop(1, "198.51.100.7", "second.example", secondStats),
            RouteScript.SilentHop(2, silentStats)));

        var resumed = HopStatistics.Empty.Record(ProbeOutcome.Expired, 12);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "first.example", resumed)));

        // No filler and no removal: the trimmed rows stay with their last
        // true values, visibly frozen.
        Assert.Equal(3, session.Rows.Count);
        Assert.False(session.Rows[0].IsFrozen);
        Assert.Equal(12, session.Rows[0].LastMs);

        HopRowViewModel frozen = session.Rows[1];
        Assert.True(frozen.IsFrozen);
        Assert.Equal("second.example", frozen.Host);
        Assert.Equal("198.51.100.7", frozen.Address);
        Assert.Equal(3, frozen.Sent);
        Assert.Equal(2, frozen.Received);
        Assert.Equal(100.0 / 3.0, frozen.LossPercent, 6);
        Assert.Equal(10, frozen.BestMs);
        Assert.Equal(20, frozen.AverageMs);
        Assert.Equal(30, frozen.WorstMs);
        Assert.Equal(30, frozen.LastMs);
        Assert.Equal(string.Empty, frozen.Status);
        Assert.Equal("(frozen)", frozen.DisplayStatus);

        HopRowViewModel frozenSilent = session.Rows[2];
        Assert.True(frozenSilent.IsFrozen);
        Assert.Equal(string.Empty, frozenSilent.Host);
        Assert.Equal(1, frozenSilent.Sent);
        Assert.Equal(0, frozenSilent.Received);
        Assert.Equal("No response.", frozenSilent.Status);
        Assert.Equal("No response. (frozen)", frozenSilent.DisplayStatus);
    }

    [Fact]
    public void FrozenRow_ResumesLiveUpdates_WhenHopReappears()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var before = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", null, before),
            NamedHop(1, "198.51.100.7", null, before)));
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", null, before)));

        HopRowViewModel retained = session.Rows[1];
        Assert.True(retained.IsFrozen);

        var after = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10).Record(ProbeOutcome.Expired, 14);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(2),
            NamedHop(0, "192.0.2.1", null, before),
            NamedHop(1, "198.51.100.7", null, after)));

        // The marker clears and the row resumes live updates on the same
        // stable row object.
        Assert.Same(retained, session.Rows[1]);
        Assert.False(session.Rows[1].IsFrozen);
        Assert.Equal(2, session.Rows[1].Sent);
        Assert.Equal(2, session.Rows[1].Received);
        Assert.Equal(14, session.Rows[1].LastMs);
        Assert.Equal(session.Rows[1].Status, session.Rows[1].DisplayStatus);
    }

    [Fact]
    public async Task Retention_IsBoundedByHopLimit()
    {
        Target target = RouteScript.TestTarget();
        var stats = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10);
        var wide = RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", null, stats),
            NamedHop(1, "198.51.100.7", null, stats),
            NamedHop(2, "203.0.113.9", null, stats));
        var narrow = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", null, stats));
        var session = new TraceSession(SnapshotsFactory(target, [wide, narrow]));

        await session.StartAsync("example.com", ProbeSettings.Default with { HopLimit = 2 });

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Equal(2, session.Rows.Count);
        Assert.Equal([1, 2], session.Rows.Select(row => row.Hop).ToArray());
    }

    [Fact]
    public void RepeatedSnapshots_ReplaceValues_WithoutAccumulatingRows()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var stats = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", null, stats),
            NamedHop(1, "198.51.100.7", null, stats),
            NamedHop(2, "203.0.113.9", null, stats)));
        HopRowViewModel[] stable = session.Rows.ToArray();

        // A long session of growing and shrinking snapshots: row count stays
        // bounded, values track the latest snapshot, and no snapshot, probe,
        // or error history accumulates on the rows.
        for (int i = 1; i <= 50; i++)
        {
            int length = (i % 3) + 1;
            var hops = Enumerable.Range(0, length)
                .Select(index => NamedHop(
                    index,
                    index == 0 ? "192.0.2.1" : index == 1 ? "198.51.100.7" : "203.0.113.9",
                    null,
                    HopStatistics.Empty.Record(ProbeOutcome.Expired, 10 + i)))
                .ToArray();
            session.ApplySnapshot(RouteScript.Snapshot(target, T0 + TimeSpan.FromSeconds(i), hops));
            Assert.True(session.Rows.Count <= 3);
        }

        Assert.Equal(3, session.Rows.Count);
        Assert.Equal(stable, session.Rows.ToArray());
        Assert.Equal(60, session.Rows[0].LastMs);
        Assert.DoesNotContain(session.Rows, row => row.IsFrozen);
    }

    [Fact]
    public void FirstDiscovery_UnknownToKnown_RaisesNoHighlight()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        session.ApplySnapshot(RouteScript.Snapshot(target, T0, RouteScript.SilentHop(0)));
        Assert.Equal(string.Empty, session.Rows[0].Address);
        Assert.False(session.Rows[0].HasIdentityChange);

        var discovered = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "router.example", discovered)));

        Assert.False(session.Rows[0].HasIdentityChange);
        Assert.Equal(string.Empty, session.Rows[0].IdentityNotice);
        Assert.Equal("192.0.2.1", session.Rows[0].Address);
        Assert.Equal("router.example", session.Rows[0].Host);
        Assert.Equal(1, session.Rows[0].Sent);
        Assert.False(session.Rows[0].IsFrozen);
    }

    [Fact]
    public void SameAddressUpdate_RaisesNoHighlight_AndRefreshesValues()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10))));

        var refreshed = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10).Record(ProbeOutcome.Expired, 14);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "router.example", refreshed)));

        Assert.False(session.Rows[0].HasIdentityChange);
        Assert.Equal("router.example", session.Rows[0].Host);
        Assert.Equal(2, session.Rows[0].Sent);
        Assert.Equal(14, session.Rows[0].LastMs);
    }

    [Fact]
    public void SilentTransitions_RaiseNoHighlight()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10))));

        // A known address going silent is not an address replacement.
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            RouteScript.SilentHop(0, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0))));
        Assert.False(session.Rows[0].HasIdentityChange);
        Assert.Equal(string.Empty, session.Rows[0].Host);
        Assert.Equal(string.Empty, session.Rows[0].Address);
        Assert.Equal("No response.", session.Rows[0].Status);
        Assert.False(session.Rows[0].IsFrozen);

        // Null identities staying null raise no highlight either.
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(2),
            RouteScript.SilentHop(0, HopStatistics.Empty.Record(ProbeOutcome.TimedOut, 0))));
        Assert.False(session.Rows[0].HasIdentityChange);
    }

    [Fact]
    public void AddressReplacement_HighlightsOnce_AndShowsOnlyNewStatistics()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var oldStats = HopStatistics.Empty.Record(ProbeOutcome.Expired, 10).Record(ProbeOutcome.Expired, 12);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "first.example", oldStats)));

        var newStats = HopStatistics.Empty.Record(ProbeOutcome.Expired, 50);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            RouteScript.RespondingHop(
                0, "198.51.100.7", "second.example", newStats,
                ProbeOutcome.Unreachable, ProbeErrorReason.HostUnreachable)));

        // Two different non-null addresses: the replacement highlights, the
        // row takes the new identity, and statistics describe only the new
        // hop. Diagnostic text stays in the status, never in the host.
        HopRowViewModel row = session.Rows[0];
        Assert.True(row.HasIdentityChange);
        Assert.Equal("Hop identity changed; statistics describe only the new hop.", row.IdentityNotice);
        Assert.Equal("198.51.100.7", row.Address);
        Assert.Equal("second.example", row.Host);
        Assert.Equal("Destination host unreachable.", row.Status);
        Assert.Equal(1, row.Sent);
        Assert.Equal(1, row.Received);
        Assert.Equal(50, row.BestMs);
        Assert.Equal(50, row.LastMs);
        Assert.Equal(0, row.LossPercent);
        Assert.False(row.IsFrozen);

        // The highlight is transient: the next ordinary update clears it.
        var newerStats = HopStatistics.Empty.Record(ProbeOutcome.Expired, 50).Record(ProbeOutcome.Expired, 60);
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(2),
            NamedHop(0, "198.51.100.7", "second.example", newerStats)));

        Assert.False(session.Rows[0].HasIdentityChange);
        Assert.Equal(string.Empty, session.Rows[0].IdentityNotice);
        Assert.Equal("second.example", session.Rows[0].Host);
        Assert.Equal(2, session.Rows[0].Sent);
        Assert.Equal(60, session.Rows[0].LastMs);
    }

    [Fact]
    public void StatusArea_ShowsElapsed_FromFirstSnapshotTimestamp()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var first = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var second = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(10), NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));

        session.ApplySnapshot(first);
        Assert.Equal("00:00", session.ElapsedText);

        session.ApplySnapshot(second);
        Assert.Equal("00:10", session.ElapsedText);
    }

    [Fact]
    public void StatusArea_ShowsElapsed_PastTheHour()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var first = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var later = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(3723), NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));

        session.ApplySnapshot(first);
        session.ApplySnapshot(later);

        Assert.Equal("01:02:03", session.ElapsedText);
    }

    [Fact]
    public void StatusArea_ShowsDestinationConfirmation()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var hops = ImmutableArray.Create(
            NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));

        session.ApplySnapshot(RouteScript.Snapshot(target, T0, hops));
        Assert.Equal("Destination: —", session.DestinationText);

        session.ApplySnapshot(RouteScript.Snapshot(target, T0, hops, destinationReached: true, hopLimitObserved: false));
        Assert.Equal("Destination confirmed", session.DestinationText);
    }

    [Fact]
    public void StatusArea_ExplainsUnconfirmedDestination_WithinHopLimit()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        var hops = ImmutableArray.Create(
            NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));

        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0, hops, destinationReached: false, hopLimitObserved: true));

        Assert.Contains("within hop limit of 30", session.DestinationText);
    }

    [Fact]
    public async Task DisposeDuringTracing_WaitsForDrain_AndDisposes()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var session = new TraceSession(factory);

        Task run = session.StartAsync("example.com", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Tracing);

        await session.DisposeAsync();

        Assert.True(engine.Disposed);
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Single(session.Rows);
        await run;
    }

    [Fact]
    public async Task DisposeDuringPreparing_CancelsToIdle()
    {
        var (factory, gate) = GatedPreparation([]);
        var session = new TraceSession(factory);

        Task run = session.StartAsync("example.com", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Preparing);

        await session.DisposeAsync();

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Empty(session.Rows);
        await run;
    }

    [Fact]
    public async Task ShutdownAsync_SettlesActiveSession_ToIdle()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing");

        await viewModel.ShutdownAsync();

        Assert.Equal("Idle", viewModel.SessionState);
        Assert.True(engine.Disposed);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public async Task ShutdownAsync_RepeatedCallers_WaitForTheSameDrain()
    {
        var engine = new GatedTraceEngine([])
        {
            ReleaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var viewModel = CreateViewModel(new TracerScript
        {
            Target = RouteScript.TestTarget(),
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory());

        try
        {
            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);
            await engine.EnteredMoveNext.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Task first = viewModel.ShutdownAsync();
            Task second = viewModel.ShutdownAsync();
            Task third = viewModel.ShutdownAsync();

            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
            Assert.False(engine.Disposed);

            engine.ReleaseDispose.SetResult();
            await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(engine.Disposed);
            Assert.Equal("Idle", viewModel.SessionState);
            await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            engine.ReleaseDispose.TrySetResult();
        }
    }

    [Fact]
    public async Task ShutdownAsync_ConcurrentCloseAndQuitCallers_BothWaitForDrain()
    {
        var engine = new GatedTraceEngine([])
        {
            ReleaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var viewModel = CreateViewModel(new TracerScript
        {
            Target = RouteScript.TestTarget(),
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeRequested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quitRequested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RequestShutdownAsync(TaskCompletionSource<Task> requested)
        {
            await start.Task;
            Task drain = viewModel.ShutdownAsync();
            requested.SetResult(drain);
            await drain;
        }

        try
        {
            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);
            await engine.EnteredMoveNext.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Task close = Task.Run(() => RequestShutdownAsync(closeRequested), TestContext.Current.CancellationToken);
            Task quit = Task.Run(() => RequestShutdownAsync(quitRequested), TestContext.Current.CancellationToken);
            start.SetResult();
            Task[] drains = await Task.WhenAll(closeRequested.Task, quitRequested.Task)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.All(drains, drain => Assert.False(drain.IsCompleted));
            Assert.False(close.IsCompleted);
            Assert.False(quit.IsCompleted);
            Assert.False(engine.Disposed);

            engine.ReleaseDispose.SetResult();
            await Task.WhenAll(close, quit).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(engine.Disposed);
            Assert.Equal("Idle", viewModel.SessionState);
        }
        finally
        {
            start.TrySetResult();
            engine.ReleaseDispose.TrySetResult();
        }
    }
}
