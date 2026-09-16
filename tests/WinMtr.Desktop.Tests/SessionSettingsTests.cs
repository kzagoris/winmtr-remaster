using System.Collections.Immutable;
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
/// Behavioural coverage for session settings and payload capabilities,
/// driven through the tracer-factory seam with scripted route snapshots.
/// No Avalonia dependency and no real network traffic are involved:
/// capabilities arrive through <see cref="TracerScript.Capabilities"/>,
/// a never-settling detection plus <see cref="TracerScript.InitTimeout"/>
/// scripts a genuine detection timeout, and cancellation stays caller-side.
/// </summary>
public class SessionSettingsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel CreateViewModel(Func<Tracer> factory) =>
        new(new InMemoryTargetHistoryStore(), factory);

    private static Hop NamedHop(int index, string address, string? hostName, HopStatistics stats) =>
        RouteScript.RespondingHop(index, address, hostName, stats);

    private static Route OneHop(Target target, DateTimeOffset takenAt) =>
        RouteScript.Snapshot(
            target, takenAt,
            NamedHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));

    private static Route HopLimitObserved(Target target, DateTimeOffset takenAt)
    {
        var hops = ImmutableArray.Create(
            NamedHop(0, "192.0.2.1", null, HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        return RouteScript.Snapshot(target, takenAt, hops, destinationReached: false, hopLimitObserved: true);
    }

    private static ProbeSettings CustomSettings(
        double intervalSeconds = 0.5,
        int payloadBytes = 1200,
        int hopLimit = 5,
        bool resolveNames = false,
        double replyTimeoutSeconds = 5) => new(
            Cadence: TimeSpan.FromSeconds(intervalSeconds),
            PayloadBytes: payloadBytes,
            HopLimit: hopLimit,
            ReplyTimeout: TimeSpan.FromSeconds(replyTimeoutSeconds),
            ResolveNames: resolveNames,
            SnapshotInterval: TimeSpan.FromMilliseconds(250));

    private static Task StartViewModelAsync(MainWindowViewModel viewModel, string target = "example.com")
    {
        // Awaiting the command awaits the whole session run to idle: the
        // session task completes once preparation, tracing, or failure has
        // settled. State polling would race a fast scripted run that is
        // already idle before the first check.
        viewModel.Target = target;
        return viewModel.StartStopCommand.ExecuteAsync(null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Interval_AtOrBelowZero_IsRejected(double interval)
    {
        var editor = new SettingsViewModel { ProbeIntervalSeconds = interval };

        Assert.False(editor.IsValid);
        Assert.Contains("greater than zero", editor.ErrorText);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65501)]
    public void Payload_OutsideRange_IsRejected(int payload)
    {
        var editor = new SettingsViewModel { PayloadSizeBytes = payload };

        Assert.False(editor.IsValid);
        Assert.Contains("0 and 65500", editor.ErrorText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void HopLimit_OutsideRange_IsRejected(int hopLimit)
    {
        var editor = new SettingsViewModel { HopLimit = hopLimit };

        Assert.False(editor.IsValid);
        Assert.Contains("1 and 255", editor.ErrorText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.05)]
    [InlineData(61)]
    public void ReplyTimeout_OutsideRange_IsRejected(double timeout)
    {
        var editor = new SettingsViewModel { ReplyTimeoutSeconds = timeout };

        Assert.False(editor.IsValid);
        Assert.True(editor.HasReplyTimeoutError);
        Assert.Equal("Reply timeout must be between 0.1 and 60.", editor.ReplyTimeoutError);
        Assert.Equal(editor.ReplyTimeoutError, editor.ErrorText);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ReplyTimeout_NotFinite_Reports_Enter_A_Number(double timeout)
    {
        var editor = new SettingsViewModel { ReplyTimeoutSeconds = timeout };

        Assert.False(editor.IsValid);
        Assert.Equal("Enter a number between 0.1 and 60.", editor.ReplyTimeoutError);
        Assert.Equal(editor.ReplyTimeoutError, editor.ErrorText);
    }

    [Fact]
    public void ReplyTimeout_Empty_Box_Reports_Enter_A_Number()
    {
        var editor = new SettingsViewModel { ReplyTimeoutSeconds = null };

        Assert.False(editor.IsValid);
        Assert.True(editor.HasReplyTimeoutError);
        Assert.Equal("Enter a number between 0.1 and 60.", editor.ReplyTimeoutError);
    }

    [Fact]
    public void ReplyTimeout_Unparsable_Text_Blocks_Until_Reparsed()
    {
        var editor = new SettingsViewModel();

        editor.MarkUnparsable(nameof(SettingsViewModel.ReplyTimeoutSeconds));

        Assert.False(editor.IsValid);
        Assert.Equal("Enter a number between 0.1 and 60.", editor.ReplyTimeoutError);

        editor.ClearUnparsable(nameof(SettingsViewModel.ReplyTimeoutSeconds));

        Assert.True(editor.IsValid);
        Assert.Equal(ProbeSettings.Default.ReplyTimeout.TotalSeconds, editor.ReplyTimeoutSeconds);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(5.0)]
    [InlineData(60.0)]
    public void ReplyTimeout_BoundaryValues_AreAccepted(double timeout)
    {
        var editor = new SettingsViewModel { ReplyTimeoutSeconds = timeout };

        Assert.True(editor.IsValid);
        Assert.Equal(string.Empty, editor.ErrorText);
    }

    [Theory]
    [InlineData(0.5, 0, 1)]
    [InlineData(1.0, 64, 30)]
    [InlineData(10.0, 65500, 255)]
    public void BoundaryValues_AreAccepted(double interval, int payload, int hopLimit)
    {
        var editor = new SettingsViewModel
        {
            ProbeIntervalSeconds = interval,
            PayloadSizeBytes = payload,
            HopLimit = hopLimit,
        };

        Assert.True(editor.IsValid);
        Assert.Equal(string.Empty, editor.ErrorText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NameResolution_Choice_RoundTrips(bool resolve)
    {
        var editor = new SettingsViewModel { ResolveHostNames = resolve };

        Assert.True(editor.IsValid);
        Assert.Equal(resolve, editor.ToProbeSettings().ResolveNames);
    }

    [Fact]
    public void Settings_RoundTrip_ThroughProbeSettings()
    {
        var editor = new SettingsViewModel();
        editor.FromProbeSettings(CustomSettings(replyTimeoutSeconds: 12.5));

        Assert.Equal(0.5, editor.ProbeIntervalSeconds);
        Assert.Equal(1200, editor.PayloadSizeBytes);
        Assert.Equal(5, editor.HopLimit);
        Assert.Equal(12.5, editor.ReplyTimeoutSeconds);
        Assert.False(editor.ResolveHostNames);

        ProbeSettings roundTripped = editor.ToProbeSettings();
        Assert.Equal(TimeSpan.FromSeconds(0.5), roundTripped.Cadence);
        Assert.Equal(1200, roundTripped.PayloadBytes);
        Assert.Equal(5, roundTripped.HopLimit);
        Assert.Equal(TimeSpan.FromSeconds(12.5), roundTripped.ReplyTimeout);
        Assert.False(roundTripped.ResolveNames);
        // Snapshot cadence stays at the engine default: the dialog does not
        // expose it.
        Assert.Equal(ProbeSettings.Default.SnapshotInterval, roundTripped.SnapshotInterval);
    }

    [Fact]
    public void ApplySettings_Valid_PublishesAcceptedSettings()
    {
        var viewModel = CreateViewModel(new TracerScript().BuildFactory());
        var editor = viewModel.CreateSettingsEditor();
        editor.FromProbeSettings(CustomSettings());

        Assert.True(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(CustomSettings(), viewModel.AcceptedSettings);
    }

    [Fact]
    public void ApplySettings_CarriesTheEditedReplyTimeout()
    {
        var viewModel = CreateViewModel(new TracerScript().BuildFactory());
        var editor = viewModel.CreateSettingsEditor();
        editor.ReplyTimeoutSeconds = 12.5;

        Assert.True(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(TimeSpan.FromSeconds(12.5), viewModel.AcceptedSettings.ReplyTimeout);
    }

    [Fact]
    public void RestoreDefaults_ResetsEveryField_AndWithdrawsTheHistoryClear()
    {
        var editor = new SettingsViewModel
        {
            ProbeIntervalSeconds = 2.5,
            PayloadSizeBytes = 128,
            HopLimit = 42,
            ReplyTimeoutSeconds = 12.5,
            ResolveHostNames = false,
            HistorySize = 25,
            Theme = AppTheme.Dark,
            IsHistoryCleared = true,
        };
        // The payload capability is a restriction, not a field: restore
        // leaves it in place.
        editor.ApplyCapability(PayloadSupport.Restricted);

        editor.RestoreDefaultsCommand.Execute(null);

        Assert.Equal(ProbeSettings.Default.Cadence.TotalSeconds, editor.ProbeIntervalSeconds);
        Assert.Equal(ProbeSettings.Default.PayloadBytes, editor.PayloadSizeBytes);
        Assert.Equal(ProbeSettings.Default.HopLimit, editor.HopLimit);
        Assert.Equal(ProbeSettings.Default.ReplyTimeout.TotalSeconds, editor.ReplyTimeoutSeconds);
        Assert.True(editor.ResolveHostNames);
        Assert.Equal(AcceptedSettings.DefaultHistorySize, editor.HistorySize);
        Assert.Equal(AppTheme.System, editor.Theme);
        Assert.False(editor.IsHistoryCleared);
        Assert.False(editor.IsPayloadEnabled);
        Assert.NotEmpty(editor.PayloadDisabledReason);
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void ApplySettings_Invalid_ReturnsFalse_AndKeepsAccepted()
    {
        var viewModel = CreateViewModel(new TracerScript().BuildFactory());
        ProbeSettings before = viewModel.AcceptedSettings;
        var editor = viewModel.CreateSettingsEditor();
        editor.PayloadSizeBytes = -1;

        Assert.False(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(before, viewModel.AcceptedSettings);
    }

    [Fact]
    public void EditorMutated_WithoutApply_LeavesAcceptedUnchanged()
    {
        // Cancelling the dialog discards the editor copy: the previously
        // accepted settings govern the next session untouched.
        var viewModel = CreateViewModel(new TracerScript().BuildFactory());
        ProbeSettings before = viewModel.AcceptedSettings;

        var editor = viewModel.CreateSettingsEditor();
        editor.FromProbeSettings(CustomSettings());

        Assert.Equal(before, viewModel.AcceptedSettings);
    }

    [Fact]
    public async Task ConfirmedSettings_GovernTheNextSession()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Snapshots = [HopLimitObserved(target, T0)],
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        var editor = viewModel.CreateSettingsEditor();
        editor.HopLimit = 7;
        Assert.True(viewModel.ApplySettingsEditor(editor));

        await StartViewModelAsync(viewModel);

        Assert.Contains("within hop limit of 7", viewModel.DestinationText);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public async Task ConfirmedReplyTimeout_ReachesTheTraceSession()
    {
        Target target = RouteScript.TestTarget();
        var engine = new GatedTraceEngine([OneHop(target, T0)]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        var editor = viewModel.CreateSettingsEditor();
        editor.ReplyTimeoutSeconds = 12.5;
        Assert.True(viewModel.ApplySettingsEditor(editor));

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => engine.RequestedSettings is not null);

        // The session runs with exactly the confirmed timeout.
        Assert.Equal(TimeSpan.FromSeconds(12.5), engine.RequestedSettings!.ReplyTimeout);

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);
    }

    [Fact]
    public async Task ActiveSession_IgnoresLaterAcceptedSettings()
    {
        Target target = RouteScript.TestTarget();
        var engine = new GatedTraceEngine([HopLimitObserved(target, T0)]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        var first = viewModel.CreateSettingsEditor();
        first.HopLimit = 5;
        Assert.True(viewModel.ApplySettingsEditor(first));

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);
        Assert.Contains("within hop limit of 5", viewModel.DestinationText);

        // Confirming new settings mid-session publishes them for the next
        // session only: nothing changes underneath the running one.
        var second = viewModel.CreateSettingsEditor();
        second.HopLimit = 60;
        Assert.True(viewModel.ApplySettingsEditor(second));
        Assert.Equal(60, viewModel.AcceptedSettings.HopLimit);
        Assert.Contains("within hop limit of 5", viewModel.DestinationText);

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task SessionSettings_AreImmutable_WhileTracing()
    {
        Target target = RouteScript.TestTarget();
        var engine = new GatedTraceEngine([OneHop(target, T0)]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var session = new TraceSession(factory);
        ProbeSettings requested = CustomSettings();

        Task run = session.StartAsync("example.com", requested);
        await WaitUntilAsync(() => session.State == TraceSessionState.Tracing);

        // The record is immutable and captured at Start: the running session
        // reports exactly what it started with.
        Assert.Equal(requested, session.ActiveSettings);

        await session.StopAsync();
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Equal(requested, session.ActiveSettings);
        await run;
    }

    [Fact]
    public async Task Capabilities_AreNullWhilePreparing_AndSettledWhenTracing()
    {
        var (factory, gate) = GatedPreparation([]);
        var session = new TraceSession(factory);
        Task run = session.StartAsync("example.com", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Preparing);
        Assert.Null(session.Capabilities);

        gate.SetResult();
        await run;

        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.NotNull(session.Capabilities);
        Assert.Equal(PayloadSupport.Supported, session.Capabilities.PayloadSupport);
    }

    [Fact]
    public async Task SupportedCapability_Traces_WithRequestedPayload_AndNoBanner()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Supported),
            Snapshots = [OneHop(target, T0)],
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        var editor = viewModel.CreateSettingsEditor();
        editor.PayloadSizeBytes = 1200;
        Assert.True(viewModel.ApplySettingsEditor(editor));

        await StartViewModelAsync(viewModel);

        Assert.Single(viewModel.Rows);
        Assert.Empty(viewModel.Banners);
        Assert.Equal(string.Empty, viewModel.DiagnosticText);

        // Supported leaves the custom payload setting available.
        SettingsViewModel next = viewModel.CreateSettingsEditor();
        Assert.True(next.IsPayloadEnabled);
        Assert.False(next.HasPayloadDisabledReason);
    }

    [Fact]
    public async Task RestrictedCapability_Traces_WithDefaultPayload_Banner_AndDisabledControl()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Restricted),
            Snapshots = [OneHop(target, T0)],
        }.BuildFactory();
        var session = new TraceSession(factory);
        ProbeSettings requested = CustomSettings(payloadBytes: 1200);

        await session.StartAsync("example.com", requested);

        // Tracing proceeds despite the restriction, with the default payload.
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.False(session.HasFault);
        Assert.Equal(string.Empty, session.Diagnostic);
        Assert.Single(session.Rows);
        Assert.Equal(PayloadSupport.Restricted, session.Capabilities?.PayloadSupport);
        Assert.Equal(ProbeSettings.Default.PayloadBytes, session.ActiveSettings.PayloadBytes);
        Assert.Equal(requested.HopLimit, session.ActiveSettings.HopLimit);
        Assert.Equal(requested.Cadence, session.ActiveSettings.Cadence);
    }

    [Fact]
    public async Task RestrictedCapability_Shows_ActionablePrivilegeBanner()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Restricted),
            Snapshots = [OneHop(target, T0)],
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        await StartViewModelAsync(viewModel);

        Assert.Equal("Idle", viewModel.SessionState);
        Assert.Single(viewModel.Rows);
        var banner = Assert.Single(viewModel.Banners);
        Assert.Equal(BannerSeverity.Warning, banner.Severity);
        Assert.Contains("restricted", banner.Message);
        // Actionable privilege guidance grounded in the repo's own hint.
        Assert.Contains("setcap cap_net_raw+ep", banner.Message);
        Assert.Contains("Partial results", banner.Message);

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        Assert.False(editor.IsPayloadEnabled);
        Assert.True(editor.HasPayloadDisabledReason);
        Assert.Contains("default payload", editor.PayloadDisabledReason);

        banner.DismissCommand.Execute(null);
        Assert.Empty(viewModel.Banners);
    }

    [Fact]
    public async Task UndeterminedCapability_Traces_WithDefaultPayload_Banner_AndDisabledControl()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Undetermined),
            Snapshots = [OneHop(target, T0)],
        }.BuildFactory();
        var session = new TraceSession(factory);

        await session.StartAsync("example.com", CustomSettings(payloadBytes: 1200));

        // A valid inconclusive probe is not a failure: the session traces
        // with the default payload and returns idle normally.
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.False(session.HasFault);
        Assert.Equal(string.Empty, session.Diagnostic);
        Assert.Single(session.Rows);
        Assert.Equal(PayloadSupport.Undetermined, session.Capabilities?.PayloadSupport);
        Assert.Equal(ProbeSettings.Default.PayloadBytes, session.ActiveSettings.PayloadBytes);
    }

    [Fact]
    public async Task UndeterminedCapability_Shows_InconclusiveBanner_AndStillTraces()
    {
        Target target = RouteScript.TestTarget();
        var engine = new GatedTraceEngine([OneHop(target, T0)]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Undetermined),
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);

        // The session genuinely reaches tracing rather than failing.
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);

        var banner = Assert.Single(viewModel.Banners);
        Assert.Contains("could not be determined", banner.Message);
        Assert.Contains("default payload", banner.Message);

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        Assert.False(editor.IsPayloadEnabled);
        Assert.Contains("could not be determined", editor.PayloadDisabledReason);

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);
        Assert.True(engine.Disposed);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public async Task RestrictedCapability_ReachesTracing_BeforeSettling()
    {
        Target target = RouteScript.TestTarget();
        var engine = new GatedTraceEngine([OneHop(target, T0)]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Restricted),
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var session = new TraceSession(factory);

        Task run = session.StartAsync("example.com", CustomSettings());
        await WaitUntilAsync(() => session.State == TraceSessionState.Tracing);

        Assert.Equal(PayloadSupport.Restricted, session.Capabilities?.PayloadSupport);
        Assert.Equal(ProbeSettings.Default.PayloadBytes, session.ActiveSettings.PayloadBytes);

        await session.StopAsync();
        Assert.Equal(TraceSessionState.Idle, session.State);
        await run;
    }

    [Fact]
    public async Task DetectionTimeout_RemainsAGenuineFailure_WithActionableDiagnostic()
    {
        Func<Tracer> factory = new TracerScript
        {
            Target = RouteScript.TestTarget(),
            // A detection that never settles on its own but honors
            // cancellation, so the initialization deadline fires: the probe
            // failed to complete, which is nothing like an inconclusive
            // answer that settles as undetermined.
            InitializeAsync = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ProbeCapabilities(PayloadSupport.Supported);
            },
            InitTimeout = TimeSpan.FromMilliseconds(50),
            Snapshots = [],
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        await StartViewModelAsync(viewModel);

        Assert.Equal("Idle", viewModel.SessionState);
        Assert.Empty(viewModel.Rows);
        Assert.Contains("timed out", viewModel.DiagnosticText);
        Assert.Contains("did not start", viewModel.DiagnosticText);
    }

    [Fact]
    public async Task UnrelatedPreparationFailure_RemainsAGenuineFailure()
    {
        Func<Tracer> factory = new TracerScript
        {
            Target = RouteScript.TestTarget(),
            InitializeFailure = new InvalidOperationException("capability probe exploded"),
            Snapshots = [],
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        await StartViewModelAsync(viewModel);

        Assert.Equal("Idle", viewModel.SessionState);
        Assert.Empty(viewModel.Rows);
        Assert.Contains("capability probe exploded", viewModel.DiagnosticText);
        Assert.True(viewModel.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewSession_RetiresStaleCapabilityBanners()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = OneHop(target, T0);
        int preparations = 0;
        Func<Tracer> factory = () => new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(
                Interlocked.Increment(ref preparations) == 1
                    ? PayloadSupport.Restricted
                    : PayloadSupport.Supported),
            Snapshots = [snapshot],
        }.BuildTracer();
        var viewModel = CreateViewModel(factory);

        await StartViewModelAsync(viewModel);
        Assert.Single(viewModel.Banners);

        // The next session settles supported: the stale restriction notice
        // is retired and no new banner takes its place.
        await StartViewModelAsync(viewModel);
        Assert.Empty(viewModel.Banners);
        Assert.True(viewModel.CreateSettingsEditor().IsPayloadEnabled);
    }

    [Fact]
    public async Task RepeatedRestrictedSessions_DoNotStackBanners()
    {
        Target target = RouteScript.TestTarget();
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Capabilities = new ProbeCapabilities(PayloadSupport.Restricted),
            Snapshots = [OneHop(target, T0)],
        }.BuildFactory();
        var viewModel = CreateViewModel(factory);

        await StartViewModelAsync(viewModel);
        await StartViewModelAsync(viewModel);

        Assert.Single(viewModel.Banners);
    }

    [Fact]
    public void TransitCheck_StartsPending_AndNeverPassesWithoutObservation()
    {
        var check = new UnprivilegedTransitCheck();

        Assert.Equal(TransitAddressCheckStatus.Pending, check.Status);
        Assert.NotEqual(TransitAddressCheckStatus.AddressExposed, check.Status);

        check.RecordUnavailable();
        Assert.Equal(TransitAddressCheckStatus.Unavailable, check.Status);
        Assert.NotEqual(TransitAddressCheckStatus.AddressExposed, check.Status);
    }

    [Fact]
    public void TransitCheck_RecordsObservedBehavior()
    {
        var exposed = new UnprivilegedTransitCheck();
        exposed.RecordObservation(true);
        Assert.Equal(TransitAddressCheckStatus.AddressExposed, exposed.Status);

        var hidden = new UnprivilegedTransitCheck();
        hidden.RecordObservation(false);
        Assert.Equal(TransitAddressCheckStatus.AddressHidden, hidden.Status);
    }

    [Fact]
    public void TransitCheck_SettledObservation_WinsOverUnavailable()
    {
        var check = new UnprivilegedTransitCheck();
        check.RecordObservation(true);
        check.RecordUnavailable();

        Assert.Equal(TransitAddressCheckStatus.AddressExposed, check.Status);
    }

    [Fact]
    public void ViewModel_TransitCheck_IsNeverReportedAsPassed()
    {
        var viewModel = CreateViewModel(new TracerScript().BuildFactory());

        Assert.NotEqual(TransitAddressCheckStatus.AddressExposed, viewModel.TransitCheck.Status);
    }

    [Theory]
    [InlineData(TransitAddressCheckStatus.Pending)]
    [InlineData(TransitAddressCheckStatus.Unavailable)]
    [InlineData(TransitAddressCheckStatus.AddressHidden)]
    [InlineData(TransitAddressCheckStatus.AddressExposed)]
    public void PrivilegeBanner_DistinguishesCheckUncertainty_FromPayloadVerdict(TransitAddressCheckStatus transit)
    {
        string banner = CapabilityMessages.PrivilegeBanner(transit);

        // The payload verdict is told in every case.
        Assert.Contains("restricted", banner);
        Assert.Contains("setcap cap_net_raw+ep", banner);

        if (transit == TransitAddressCheckStatus.AddressExposed)
        {
            Assert.DoesNotContain("could not be verified", banner);
            Assert.DoesNotContain("not yet been verified", banner);
        }
        else
        {
            // The open weaker-result question is reported as its own
            // uncertainty, never folded into the restriction itself.
            Assert.Contains("intermediate router addresses", banner);
        }
    }

    [Fact]
    public void UndeterminedBanner_IsDistinct_FromFailureDiagnostics()
    {
        string banner = CapabilityMessages.UndeterminedPayloadBanner;

        Assert.Contains("could not be determined", banner);
        Assert.Contains("Tracing with the default payload", banner);
        Assert.DoesNotContain("did not start", banner);
    }
}
