using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.Sessions;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Core.Reporting;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Copy and export reports, driven through the tracer-factory seam with
/// scripted route snapshots. No Avalonia dependency except for the thin
/// headless binding check at the end; no real network traffic.
/// </summary>
public class CopyExportReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingClipboard : IClipboardService
    {
        public readonly List<string> Texts = new();

        public Task SetTextAsync(string text)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFileSaver : IReportFileSaver
    {
        public readonly List<(string FileName, string Content)> Saves = new();

        public Task<bool> SaveAsync(string suggestedFileName, string content)
        {
            Saves.Add((suggestedFileName, content));
            return Task.FromResult(true);
        }
    }

    // A renderer the view model has never seen: selecting it through the
    // bound pickers must still reach it, proving the format list is
    // renderer-provided rather than format-specific controls.
    private sealed class StubRenderer : IReportRenderer
    {
        public string Name => "Stub";
        public string Extension => ".stub";
        public string Render(Route route) => $"STUB:{route.Hops.Length}";
    }

    private static Hop NamedHop(int index, string address, string? hostName, HopStatistics stats) =>
        RouteScript.RespondingHop(index, address, hostName, stats);

    private static HopStatistics OneProbe(int rttMs = 10) =>
        HopStatistics.Empty.Record(ProbeOutcome.Expired, rttMs);

    private static MainWindowViewModel CreateViewModel(
        Func<Tracer> factory,
        RecordingClipboard clipboard,
        RecordingFileSaver saver) =>
        new(new InMemoryTargetHistoryStore(), factory, clipboard, saver);

    private static Func<Tracer> SnapshotsFactory(Target target, IReadOnlyList<Route> snapshots) =>
        new TracerScript { Target = target, Snapshots = snapshots }.BuildFactory();

    [Fact]
    public void ReportFormats_ListsEveryEngineFormat()
    {
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(RouteScript.TestTarget(), []));

        Assert.Equal(
            ["Text", "HTML", "CSV", "JSON"],
            viewModel.ReportFormats.Select(format => format.Name).ToArray());
        Assert.Equal([".txt", ".html", ".csv", ".json"],
            viewModel.ReportFormats.Select(format => format.Extension).ToArray());
    }

    [Fact]
    public async Task EveryFormat_CopiesAndExports_WhileStopped()
    {
        Target target = RouteScript.TestTarget();
        var first = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        var last = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "router.example", OneProbe(10).Record(ProbeOutcome.Expired, 12)),
            NamedHop(1, "198.51.100.7", null, OneProbe(30)));
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = CreateViewModel(SnapshotsFactory(target, [first, last]), clipboard, saver);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 2);

        Assert.Equal("Idle", viewModel.SessionState);
        foreach (IReportRenderer format in viewModel.ReportFormats)
        {
            string expected = format.Render(last);

            viewModel.CopyFormat = format;
            Assert.Equal(expected, viewModel.RenderCopyContent());
            await viewModel.CopyCommand.ExecuteAsync(null);
            Assert.Equal(expected, clipboard.Texts[^1]);

            viewModel.ExportFormat = format;
            var request = viewModel.BuildExportRequest();
            Assert.NotNull(request);
            Assert.Equal(expected, request.Value.Content);
            Assert.EndsWith(format.Extension, request.Value.FileName, StringComparison.Ordinal);
            await viewModel.ExportCommand.ExecuteAsync(null);
            Assert.Equal(request.Value.FileName, saver.Saves[^1].FileName);
            Assert.Equal(expected, saver.Saves[^1].Content);
        }

        // Evidence survives the captures: rows and state are untouched.
        Assert.Equal("Idle", viewModel.SessionState);
        Assert.Equal(2, viewModel.Rows.Count);
    }

    [Fact]
    public async Task EveryFormat_CopiesAndExports_WhileTracing_WithoutInterrupting()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = CreateViewModel(factory, clipboard, saver);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Tracing" && viewModel.Rows.Count == 1);

        foreach (IReportRenderer format in viewModel.ReportFormats)
        {
            string expected = format.Render(snapshot);

            viewModel.CopyFormat = format;
            Assert.True(viewModel.CopyCommand.CanExecute(null));
            Assert.Equal(expected, viewModel.RenderCopyContent());
            await viewModel.CopyCommand.ExecuteAsync(null);
            Assert.Equal(expected, clipboard.Texts[^1]);

            viewModel.ExportFormat = format;
            Assert.True(viewModel.ExportCommand.CanExecute(null));
            var request = viewModel.BuildExportRequest();
            Assert.NotNull(request);
            Assert.Equal(expected, request.Value.Content);
            Assert.EndsWith(format.Extension, request.Value.FileName, StringComparison.Ordinal);
            await viewModel.ExportCommand.ExecuteAsync(null);
            Assert.Equal(expected, saver.Saves[^1].Content);

            // Each capture leaves the live session alone.
            Assert.Equal("Tracing", viewModel.SessionState);
            Assert.Single(viewModel.Rows);
        }

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle");

        Assert.True(engine.Disposed);
        Assert.Single(viewModel.Rows);
        Assert.True(viewModel.CopyCommand.CanExecute(null));
        Assert.True(viewModel.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task EveryFormat_CopiesAndExports_AfterMidTraceFault_WithControlsUsable()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            Snapshots = [snapshot],
            EnumerationFault = new InvalidOperationException("worker blew up"),
            FaultAfterSnapshots = 1,
        }.BuildFactory();
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = CreateViewModel(factory, clipboard, saver);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        Assert.Single(viewModel.Banners);
        foreach (IReportRenderer format in viewModel.ReportFormats)
        {
            string expected = format.Render(snapshot);

            Assert.True(viewModel.CopyCommand.CanExecute(null));
            viewModel.CopyFormat = format;
            Assert.Equal(expected, viewModel.RenderCopyContent());
            await viewModel.CopyCommand.ExecuteAsync(null);
            Assert.Equal(expected, clipboard.Texts[^1]);

            Assert.True(viewModel.ExportCommand.CanExecute(null));
            viewModel.ExportFormat = format;
            var request = viewModel.BuildExportRequest();
            Assert.NotNull(request);
            Assert.Equal(expected, request.Value.Content);
            Assert.EndsWith(format.Extension, request.Value.FileName, StringComparison.Ordinal);
            await viewModel.ExportCommand.ExecuteAsync(null);
            Assert.Equal(expected, saver.Saves[^1].Content);
        }

        Assert.Equal("Idle", viewModel.SessionState);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public void Reports_FollowTrimmedSnapshot_NotRetainedRows()
    {
        Target target = RouteScript.TestTarget();
        var session = new TraceSession(SnapshotsFactory(target, []));
        session.ApplySnapshot(RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", "first.example", OneProbe(10)),
            NamedHop(1, "198.51.100.7", "second.example", OneProbe(30))));
        var trimmed = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "first.example", OneProbe(11)));
        session.ApplySnapshot(trimmed);

        // The grid retains the trimmed hop as a frozen row; the report source
        // stays the trimmed snapshot.
        Assert.Equal(2, session.Rows.Count);
        Assert.True(session.Rows[1].IsFrozen);
        Assert.Equal("second.example", session.Rows[1].Host);
        Assert.NotNull(session.CurrentSnapshot);
        Assert.Single(session.CurrentSnapshot.Hops);

        IReportRenderer[] formats = [new TextReportRenderer(), new HtmlReportRenderer(), new CsvReportRenderer(), new JsonReportRenderer()];
        foreach (IReportRenderer format in formats)
        {
            string expected = format.Render(trimmed);
            Assert.Equal(expected, session.RenderReport(format));
            Assert.Contains("first.example", session.RenderReport(format), StringComparison.Ordinal);
            Assert.DoesNotContain("second.example", session.RenderReport(format), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ViewModel_Reports_FollowTrimmedSnapshot_NotDisplayedRows()
    {
        Target target = RouteScript.TestTarget();
        var wide = RouteScript.Snapshot(
            target, T0,
            NamedHop(0, "192.0.2.1", "first.example", OneProbe(10)),
            NamedHop(1, "198.51.100.7", "second.example", OneProbe(30)));
        var trimmed = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "first.example", OneProbe(11)));
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = CreateViewModel(SnapshotsFactory(target, [wide, trimmed]), clipboard, saver);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 2);

        // The frozen retained row stays displayed while reports exclude it.
        Assert.True(viewModel.Rows[1].IsFrozen);
        Assert.Equal("second.example", viewModel.Rows[1].Host);
        foreach (IReportRenderer format in viewModel.ReportFormats)
        {
            viewModel.CopyFormat = format;
            Assert.Equal(format.Render(trimmed), viewModel.RenderCopyContent());
            Assert.DoesNotContain("second.example", viewModel.RenderCopyContent(), StringComparison.Ordinal);

            viewModel.ExportFormat = format;
            var request = viewModel.BuildExportRequest();
            Assert.NotNull(request);
            Assert.Equal(format.Render(trimmed), request.Value.Content);
            Assert.DoesNotContain("second.example", request.Value.Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Session_RenderReport_CapturesOneSnapshot_WithoutChangingSession()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        var engine = new GatedTraceEngine([snapshot]);
        Func<Tracer> factory = new TracerScript
        {
            Target = target,
            EngineFactory = (_, _, _) => engine,
        }.BuildFactory();
        var session = new TraceSession(factory);

        Task run = session.StartAsync("example.com", ProbeSettings.Default);
        await WaitUntilAsync(() => session.State == TraceSessionState.Tracing && session.Rows.Count == 1);

        Route? before = session.CurrentSnapshot;
        Assert.NotNull(before);
        foreach (IReportRenderer format in new IReportRenderer[] { new TextReportRenderer(), new HtmlReportRenderer(), new CsvReportRenderer(), new JsonReportRenderer() })
            Assert.Equal(format.Render(snapshot), session.RenderReport(format));

        // Rendering is a read: state, rows, and snapshot stand still.
        Assert.Same(before, session.CurrentSnapshot);
        Assert.Equal(TraceSessionState.Tracing, session.State);
        Assert.Single(session.Rows);

        await session.StopAsync();
        Assert.Equal(TraceSessionState.Idle, session.State);
        Assert.Single(session.Rows);
        Assert.Same(before, session.CurrentSnapshot);
        foreach (IReportRenderer format in new IReportRenderer[] { new TextReportRenderer(), new HtmlReportRenderer(), new CsvReportRenderer(), new JsonReportRenderer() })
            Assert.Equal(format.Render(snapshot), session.RenderReport(format));
        await run;
    }

    [Fact]
    public void Session_RenderReport_ReturnsNull_BeforeFirstSnapshot()
    {
        var session = new TraceSession(SnapshotsFactory(RouteScript.TestTarget(), []));

        Assert.Null(session.CurrentSnapshot);
        Assert.Null(session.RenderReport(new TextReportRenderer()));
        Assert.Null(session.RenderReport(null));
    }

    [Fact]
    public async Task NewSession_ClearsSnapshot_SoReportsWaitForEvidence()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, OneProbe(10)));
        var session = new TraceSession(SnapshotsFactory(target, [snapshot]));

        await session.StartAsync("example.com", ProbeSettings.Default);
        Assert.NotNull(session.CurrentSnapshot);
        Assert.NotNull(session.RenderReport(new TextReportRenderer()));
        Assert.Single(session.Rows);
    }

    [Fact]
    public void Export_SuggestsTargetBasedFilename_WithRendererExtension()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, OneProbe(10)));
        var session = new TraceSession(SnapshotsFactory(target, []));
        session.ApplySnapshot(snapshot);

        Assert.Equal(
            "winmtr-example.com-20260908-120000Z.txt",
            ReportFileName.Build("example.com", T0, ".txt"));
        Assert.Equal(
            "winmtr-__1-20260908-120000Z.json",
            ReportFileName.Build("::1", T0, ".json"));
        Assert.Equal(
            "winmtr-trace-20260908-120000Z.csv",
            ReportFileName.Build("   ", T0, ".csv"));
        Assert.Equal(
            "winmtr-example.com-20260908-120000Z.html",
            ReportFileName.Build("example.com", T0, "html"));
    }

    [Fact]
    public void ExportFilename_StampsUtcWithZMarker_ForEveryFormat()
    {
        // 14:00 at +02:00 is 12:00 UTC. The name carries the UTC instant with
        // the explicit Z marker, and the rule holds for every renderer
        // extension instead of varying by format.
        var takenAt = new DateTimeOffset(2026, 9, 8, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal(
            "winmtr-example.com-20260908-120000Z.txt",
            ReportFileName.Build("example.com", takenAt, ".txt"));
        Assert.Equal(
            "winmtr-example.com-20260908-120000Z.csv",
            ReportFileName.Build("example.com", takenAt, ".csv"));
    }

    [Fact]
    public async Task ExportFilename_FollowsAcceptedTarget_AndRendererExtension()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, OneProbe(10)));
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = CreateViewModel(SnapshotsFactory(target, [snapshot]), clipboard, saver);

        viewModel.Target = "::1";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        foreach (IReportRenderer format in viewModel.ReportFormats)
        {
            viewModel.ExportFormat = format;
            var request = viewModel.BuildExportRequest();
            Assert.NotNull(request);
            Assert.StartsWith("winmtr-__1-", request.Value.FileName, StringComparison.Ordinal);
            Assert.EndsWith(format.Extension, request.Value.FileName, StringComparison.Ordinal);
            Assert.DoesNotContain(":", request.Value.FileName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AddedRenderer_ReachesCopyAndExport_ThroughFormatList()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", null, OneProbe(10)));
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = CreateViewModel(SnapshotsFactory(target, [snapshot]), clipboard, saver);

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        var stub = new StubRenderer();
        viewModel.CopyFormat = stub;
        Assert.Equal("STUB:1", viewModel.RenderCopyContent());
        await viewModel.CopyCommand.ExecuteAsync(null);
        Assert.Equal("STUB:1", clipboard.Texts[^1]);

        viewModel.ExportFormat = stub;
        var request = viewModel.BuildExportRequest();
        Assert.NotNull(request);
        Assert.Equal("STUB:1", request.Value.Content);
        Assert.EndsWith(".stub", request.Value.FileName, StringComparison.Ordinal);
        await viewModel.ExportCommand.ExecuteAsync(null);
        Assert.Equal("STUB:1", saver.Saves[^1].Content);
        Assert.EndsWith(".stub", saver.Saves[^1].FileName, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task FormatChoices_BindToCopyAndExportCommands_AndReachRenderers()
    {
        Target target = RouteScript.TestTarget();
        var first = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        var last = RouteScript.Snapshot(
            target, T0 + TimeSpan.FromSeconds(1),
            NamedHop(0, "192.0.2.1", "router.example", OneProbe(12)));
        var clipboard = new RecordingClipboard();
        var saver = new RecordingFileSaver();
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, [first, last]),
            clipboard,
            saver);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var copyButton = window.FindControl<SplitButton>("CopyButton")!;
            var exportButton = window.FindControl<SplitButton>("ExportButton")!;

            // One split button per action: the primary half runs the shown
            // format, and the drop-down half offers every renderer-provided
            // format through the format-taking commands.
            Assert.Equal(
                ["Text", "HTML", "CSV", "JSON"],
                viewModel.ReportFormats.Select(format => format.Name).ToArray());
            Assert.Same(viewModel.CopyCommand, copyButton.Command);
            Assert.Same(viewModel.ExportCommand, exportButton.Command);

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

            // Selecting each available format reaches its renderer on both paths.
            foreach (IReportRenderer format in viewModel.ReportFormats)
            {
                await viewModel.CopyAsCommand.ExecuteAsync(format);
                Assert.Same(format, viewModel.CopyFormat);
                Assert.Equal(format.Render(last), clipboard.Texts[^1]);

                await viewModel.ExportAsCommand.ExecuteAsync(format);
                Assert.Same(format, viewModel.ExportFormat);
                Assert.Equal(format.Render(last), saver.Saves[^1].Content);
                Assert.EndsWith(format.Extension, saver.Saves[^1].FileName, StringComparison.Ordinal);
            }

            Assert.True(copyButton.IsEffectivelyEnabled);
            Assert.True(exportButton.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }
}
