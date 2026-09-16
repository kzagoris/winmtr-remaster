using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Desktop.Views;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Thin headless check that the target history surface is connected to the
/// view: the dropdown lists the in-memory history, typed text reaches the
/// selected target, reuse promotes through a trace session, and the settings
/// dialog clear action empties the dropdown. Behaviour lives in
/// <see cref="TargetHistoryTests"/>; this asserts connection, not appearance.
/// </summary>
public class TargetHistoryBindingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [AvaloniaFact]
    public async Task HistoryDropdown_SelectedTarget_Reuse_And_Clear_AreBound()
    {
        Target target = RouteScript.TestTarget();
        Route snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = [snapshot] }.BuildFactory();
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), factory);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var input = window.FindControl<AutoCompleteBox>("TargetInput")!;

            // The toolbar no longer carries a clear action: it lives beside History size.
            Assert.Null(window.FindControl<Button>("ClearHistoryButton"));

            // The history dropdown lists the in-memory history surface.
            Assert.Same(viewModel.TargetHistory, input.ItemsSource);

            // Typed text reaches the selected target.
            input.Text = "example.com";
            Assert.Equal("example.com", viewModel.Target);

            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
            Assert.Equal(["example.com"], input.ItemsSource!.Cast<string>().ToArray());

            // Reusing the entry through a later session promotes it without a duplicate.
            input.Text = "other.example";
            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
            input.Text = "example.com";
            Assert.Equal("example.com", viewModel.Target);
            viewModel.StartStopCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
            Assert.Equal(["example.com", "other.example"], input.ItemsSource!.Cast<string>().ToArray());

            // The settings dialog owns the one clear action with inline Undo.
            SettingsViewModel editor = viewModel.CreateSettingsEditor();
            var dialog = new SettingsWindow { DataContext = editor };
            dialog.Show();
            try
            {
                var clear = dialog.FindControl<Button>("ClearHistoryButton")!;
                var undo = dialog.FindControl<Button>("UndoClearHistoryButton")!;

                Assert.Same(editor.ClearHistoryCommand, clear.Command);
                Assert.Same(editor.UndoClearHistoryCommand, undo.Command);

                // Confirming the clear removes every entry, including earlier sessions'.
                clear.Command!.Execute(null);
                Assert.True(viewModel.ApplySettingsEditor(editor));
                Assert.Empty(viewModel.TargetHistory);
                Assert.Empty(input.ItemsSource!.Cast<string>());
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
    public void HistoryChevron_WithEmptyText_OpensDropdownWithAllEntries()
    {
        // The chevron focuses before opening: focusing an open AutoCompleteBox
        // closes its popup, so open-then-focus leaves an empty textbox with
        // no options. Behaviour, not appearance.
        var history = new InMemoryTargetHistoryStore();
        history.Add("b.example");
        history.Add("a.example");
        var viewModel = new MainWindowViewModel(history);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var input = window.FindControl<AutoCompleteBox>("TargetInput")!;
            var chevron = window.FindControl<Button>("TargetHistoryButton")!;
            input.Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.False(input.IsDropDownOpen);

            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(input.IsDropDownOpen);
            Assert.Equal(2, TopLevel.GetTopLevel(window)!.GetVisualDescendants().OfType<ListBoxItem>().Count());

            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(input.IsDropDownOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PickedHistoryEntry_StaysInInput_WhenTraceStarts()
    {
        // A mouse pick from the dropdown makes the entry the box's selected
        // item while its search text stays empty. Tracing promotes the target
        // in the history; if that refresh removes the selected item, the box
        // falls back to the empty search text and the target disappears.
        Target target = RouteScript.TestTarget();
        Route snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        Func<Tracer> factory = new TracerScript { Target = target, Snapshots = [snapshot] }.BuildFactory();
        var history = new InMemoryTargetHistoryStore();
        history.Add("b.example");
        history.Add("a.example");
        history.Add("8.8.8.8");
        history.Add("c.example");
        var viewModel = new MainWindowViewModel(history, factory);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        try
        {
            var input = window.FindControl<AutoCompleteBox>("TargetInput")!;
            var chevron = window.FindControl<Button>("TargetHistoryButton")!;
            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single(item => (string?)item.Content == "8.8.8.8");
            Click(window, row);
            Assert.Equal("8.8.8.8", input.Text);

            Click(window, window.FindControl<Button>("StartStopButton")!);
            await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["8.8.8.8", "c.example", "a.example", "b.example"], input.ItemsSource!.Cast<string>().ToArray());
            Assert.Equal("8.8.8.8", input.Text);
            Assert.Equal("8.8.8.8", viewModel.Target);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Click(Window window, Control control)
    {
        Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
