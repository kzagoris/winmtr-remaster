using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Core.Reporting;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Copy and export confirmations: transient popups anchored to the toolbar,
/// never the status bar, plus a check on the active menu format.
/// </summary>
public class CopyConfirmationTests
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

    private sealed class ThrowingClipboard : IClipboardService
    {
        public Task SetTextAsync(string text) => throw new InvalidOperationException("clipboard busy");
    }

    private sealed class RecordingFileSaver(bool result) : IReportFileSaver
    {
        public readonly List<(string FileName, string Content)> Saves = new();
        public Task<bool> SaveAsync(string suggestedFileName, string content)
        {
            Saves.Add((suggestedFileName, content));
            return Task.FromResult(result);
        }
    }

    private static Hop NamedHop(int index, string address, string? hostName, HopStatistics stats) =>
        RouteScript.RespondingHop(index, address, hostName, stats);

    private static HopStatistics OneProbe(int rttMs = 10) =>
        HopStatistics.Empty.Record(ProbeOutcome.Expired, rttMs);

    private static Func<Tracer> SnapshotsFactory(Target target, IReadOnlyList<Route> snapshots) =>
        new TracerScript { Target = target, Snapshots = snapshots }.BuildFactory();

    [Fact]
    public void ConfirmationWording_UsesRouteLengthAndFormatName()
    {
        Assert.Equal("Copied 10 hops as CSV", MainWindowViewModel.BuildCopyConfirmation(10, "CSV"));
        Assert.Equal("Saved 10 hops as CSV", MainWindowViewModel.BuildExportConfirmation(10, "CSV"));
    }

    [Fact]
    public void FormatMenus_MarkOnlyTheShownFormat()
    {
        Target target = RouteScript.TestTarget();
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, []),
            new RecordingClipboard(),
            new RecordingFileSaver(true));

        // Each menu marks its own shown format, and only that one, so the
        // copy menu never follows a change of the export format.
        Assert.Equal(viewModel.ReportFormats.Count, viewModel.CopyFormatChoices.Count);
        Assert.Single(viewModel.CopyFormatChoices, choice => choice.IsActive);
        Assert.True(viewModel.CopyFormatChoices[0].IsActive);

        IReportRenderer csv = viewModel.ReportFormats.Single(format => format.Name == "CSV");
        viewModel.ExportFormat = csv;

        Assert.True(viewModel.CopyFormatChoices[0].IsActive);
        Assert.Single(viewModel.ExportFormatChoices, choice => choice.IsActive);
        Assert.True(viewModel.ExportFormatChoices.Single(choice => choice.IsActive).Format == csv);
    }

    [Fact]
    public async Task Copy_ShowsPopupWithTrimmedLength_ThenAutoCloses()
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
        var saver = new RecordingFileSaver(true);
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, [wide, trimmed]),
            clipboard,
            saver)
        {
            ConfirmationDuration = TimeSpan.FromMilliseconds(30),
        };

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 2);

        viewModel.CopyFormat = viewModel.ReportFormats.First(f => f.Name == "CSV");
        await viewModel.CopyCommand.ExecuteAsync(null);

        Assert.Equal("Copied 1 hops as CSV", viewModel.CopyConfirmation.Text);
        Assert.True(viewModel.CopyConfirmation.IsOpen);
        await WaitUntilAsync(() => !viewModel.CopyConfirmation.IsOpen);
    }

    [Fact]
    public async Task Export_ShowsPopupOnlyWhenFileSaved()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));

        var savedClipboard = new RecordingClipboard();
        var savedSaver = new RecordingFileSaver(true);
        var savedModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, [snapshot]),
            savedClipboard,
            savedSaver)
        {
            ConfirmationDuration = TimeSpan.FromMilliseconds(30),
        };
        savedModel.Target = "example.com";
        savedModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => savedModel.SessionState == "Idle" && savedModel.Rows.Count == 1);
        savedModel.ExportFormat = savedModel.ReportFormats.First(f => f.Name == "CSV");
        await savedModel.ExportCommand.ExecuteAsync(null);
        Assert.Equal("Saved 1 hops as CSV", savedModel.ExportConfirmation.Text);
        Assert.True(savedModel.ExportConfirmation.IsOpen);

        var cancelledSaver = new RecordingFileSaver(false);
        var cancelledModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, [snapshot]),
            new RecordingClipboard(),
            cancelledSaver);
        cancelledModel.Target = "example.com";
        cancelledModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => cancelledModel.SessionState == "Idle" && cancelledModel.Rows.Count == 1);
        cancelledModel.ExportFormat = cancelledModel.ReportFormats.First(f => f.Name == "CSV");
        await cancelledModel.ExportCommand.ExecuteAsync(null);
        Assert.False(cancelledModel.ExportConfirmation.IsOpen);
        Assert.Empty(cancelledModel.ExportConfirmation.Text);
    }

    [Fact]
    public async Task CopyFailure_ShowsBanner_NotPopup()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, [snapshot]),
            new ThrowingClipboard(),
            new RecordingFileSaver(true));
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        await viewModel.CopyCommand.ExecuteAsync(null);

        Assert.False(viewModel.CopyConfirmation.IsOpen);
        Assert.Single(viewModel.Banners);
    }

    [AvaloniaFact]
    public async Task Popups_BindToConfirmation_AndAnchorToButtons()
    {
        Target target = RouteScript.TestTarget();
        var snapshot = RouteScript.Snapshot(
            target, T0, NamedHop(0, "192.0.2.1", "router.example", OneProbe(10)));
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            SnapshotsFactory(target, [snapshot]),
            new RecordingClipboard(),
            new RecordingFileSaver(true))
        {
            ConfirmationDuration = TimeSpan.FromSeconds(30),
        };
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var copyButton = window.FindControl<SplitButton>("CopyButton")!;
            var exportButton = window.FindControl<SplitButton>("ExportButton")!;
            var copyPopup = window.FindControl<Popup>("CopyConfirmationPopup")!;
            var exportPopup = window.FindControl<Popup>("ExportConfirmationPopup")!;

            Assert.Same(copyButton, copyPopup.PlacementTarget);
            Assert.Same(exportButton, exportPopup.PlacementTarget);
            Assert.False(copyPopup.IsOpen);

            viewModel.Target = "example.com";
            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

            viewModel.CopyFormat = viewModel.ReportFormats.First(f => f.Name == "CSV");
            await viewModel.CopyAsCommand.ExecuteAsync(viewModel.CopyFormat);
            Assert.True(copyPopup.IsOpen);
            Assert.Equal("Copied 1 hops as CSV", viewModel.CopyConfirmation.Text);

            viewModel.ExportFormat = viewModel.ReportFormats.First(f => f.Name == "CSV");
            await viewModel.ExportAsCommand.ExecuteAsync(viewModel.ExportFormat);
            Assert.True(exportPopup.IsOpen);
            Assert.Equal("Saved 1 hops as CSV", viewModel.ExportConfirmation.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
