using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;
using WinMtr.Infrastructure;
using WinMtr.TestSupport;
using static WinMtr.Desktop.Tests.TraceSessionTestHelpers;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The shell's real behaviour without a trace session: the in-memory target
/// history behind its interface. No Avalonia dependency is involved.
/// </summary>
public class TargetHistoryTests
{
    [Fact]
    public void Add_Orders_Most_Recent_First()
    {
        var store = new InMemoryTargetHistoryStore();

        store.Add("a.example");
        store.Add("b.example");

        Assert.Equal(["b.example", "a.example"], store.Entries.ToArray());
    }

    [Fact]
    public void Readd_Moves_To_Front_Without_Duplicates()
    {
        var store = new InMemoryTargetHistoryStore();
        store.Add("a.example");
        store.Add("b.example");

        store.Add("a.example");

        Assert.Equal(["a.example", "b.example"], store.Entries.ToArray());
    }

    [Fact]
    public void Add_Ignores_Blank_Targets()
    {
        var store = new InMemoryTargetHistoryStore();

        store.Add("   ");

        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Clear_Removes_Every_Entry()
    {
        var store = new InMemoryTargetHistoryStore();
        store.Add("a.example");
        store.Add("b.example");

        store.Clear();

        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task StartStop_Requires_A_Target_And_Records_History()
    {
        // Scripted with no snapshots: pressing Start exercises the session
        // without real network traffic; history lands on tracing, once
        // preparation succeeds.
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), ScriptedFactory());

        Assert.False(viewModel.StartStopCommand.CanExecute(null));

        viewModel.Target = "  ";
        Assert.False(viewModel.StartStopCommand.CanExecute(null));

        viewModel.Target = "example.com";
        Assert.True(viewModel.StartStopCommand.CanExecute(null));

        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TargetHistory.Count == 1);
        Assert.Equal(["example.com"], viewModel.TargetHistory.ToArray());
    }

    [Fact]
    public async Task ClearHistory_Removes_Every_Entry_And_Notes_Status()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), ScriptedFactory());
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TargetHistory.Count == 1);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);
        viewModel.Target = "other.example";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TargetHistory.Count == 2);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && !viewModel.StartStopCommand.IsRunning);

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        Assert.True(editor.ClearHistoryCommand.CanExecute(null));
        editor.ClearHistoryCommand.Execute(null);

        Assert.True(editor.IsHistoryCleared);
        Assert.Equal("Will clear 2 targets.", editor.HistoryClearPendingText);

        // The store keeps its entries until OK confirms the request.
        Assert.Equal(2, viewModel.TargetHistory.Count);
        Assert.False(editor.ClearHistoryCommand.CanExecute(null));
        Assert.True(editor.UndoClearHistoryCommand.CanExecute(null));

        Assert.True(viewModel.ApplySettingsEditor(editor));

        Assert.Empty(viewModel.TargetHistory);

        // The dialog already reported the count; the status text stays free.
        Assert.Equal(string.Empty, viewModel.DiagnosticText);
    }

    [Fact]
    public void SuccessiveSessions_Order_Deterministically_With_Reuse_Promotion()
    {
        var store = new InMemoryTargetHistoryStore();
        store.Add("a.example");
        store.Add("b.example");
        store.Add("c.example");

        Assert.Equal(["c.example", "b.example", "a.example"], store.Entries.ToArray());

        // Reusing an entry promotes it without creating a duplicate.
        store.Add("b.example");

        Assert.Equal(["b.example", "c.example", "a.example"], store.Entries.ToArray());

        // A later session builds on the promoted order, still most recent first.
        store.Add("d.example");

        Assert.Equal(["d.example", "b.example", "c.example", "a.example"], store.Entries.ToArray());
    }

    [Fact]
    public void Readd_Matches_Case_Insensitively_Without_Duplicates()
    {
        var store = new InMemoryTargetHistoryStore();
        store.Add("example.com");
        store.Add("other.example");

        store.Add("EXAMPLE.COM");

        Assert.Equal(["EXAMPLE.COM", "other.example"], store.Entries.ToArray());
    }

    [Fact]
    public async Task Successive_Trace_Sessions_Keep_Most_Recent_First_And_Promote_Reuse()
    {
        // Scripted with one snapshot per session: each Start runs offline to
        // completion, so successive sessions are observable without network traffic.
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory());

        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
        viewModel.Target = "other.example";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        // A target accepted for a trace is offered on a later session, most recent first.
        Assert.Equal(["other.example", "example.com"], viewModel.TargetHistory.ToArray());

        // Reusing a target promotes it without creating a duplicate.
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        Assert.Equal(["example.com", "other.example"], viewModel.TargetHistory.ToArray());
    }

    [Fact]
    public async Task Clear_Removes_Earlier_Session_Entries_Without_Resurrection()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory());
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
        viewModel.Target = "other.example";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.ClearHistoryCommand.Execute(null);
        Assert.True(viewModel.ApplySettingsEditor(editor));

        Assert.Empty(viewModel.TargetHistory);

        // A later session offers only its own target: cleared entries stay gone.
        viewModel.Target = "third.example";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        Assert.Equal(["third.example"], viewModel.TargetHistory.ToArray());
    }

    [Fact]
    public void Fresh_Store_Starts_Empty_With_No_Disk_Load()
    {
        var first = new InMemoryTargetHistoryStore();
        first.Add("example.com");

        // Entries live in the instance for the application lifetime only: a new
        // store loads nothing from disk and imports no legacy settings.
        var second = new InMemoryTargetHistoryStore();

        Assert.Empty(second.Entries);
        Assert.Equal(["example.com"], first.Entries.ToArray());
    }

    [Fact]
    public async Task History_Surface_Works_Behind_A_Replacement_Store()
    {
        // The shell depends on the interface, not the implementation, so a
        // future persistence implementation substitutes without redesign.
        var replacement = new RecordingHistoryStore();
        var viewModel = new MainWindowViewModel(replacement, LiveFactory());
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        Assert.Equal(["example.com"], viewModel.TargetHistory.ToArray());

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.ClearHistoryCommand.Execute(null);

        Assert.False(replacement.Cleared);
        Assert.True(viewModel.ApplySettingsEditor(editor));
        Assert.True(replacement.Cleared);
        Assert.Empty(viewModel.TargetHistory);
    }

    [Fact]
    public void Clear_Is_Disabled_When_History_Is_Empty()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), ScriptedFactory());
        SettingsViewModel editor = viewModel.CreateSettingsEditor();

        Assert.False(editor.ClearHistoryCommand.CanExecute(null));
        Assert.False(editor.UndoClearHistoryCommand.CanExecute(null));
        Assert.False(editor.IsHistoryCleared);
    }

    [Fact]
    public async Task Undo_Restores_Cleared_Entries_In_Order()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory());
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);
        viewModel.Target = "other.example";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.ClearHistoryCommand.Execute(null);
        Assert.True(editor.IsHistoryCleared);

        editor.UndoClearHistoryCommand.Execute(null);

        Assert.False(editor.IsHistoryCleared);
        Assert.True(viewModel.ApplySettingsEditor(editor));
        Assert.Equal(["other.example", "example.com"], viewModel.TargetHistory.ToArray());
        Assert.Equal(string.Empty, viewModel.DiagnosticText);
    }

    [Fact]
    public async Task Cancel_Leaves_An_Unconfirmed_Clear_Undone()
    {
        var viewModel = new MainWindowViewModel(new InMemoryTargetHistoryStore(), LiveFactory());
        viewModel.Target = "example.com";
        viewModel.StartStopCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SessionState == "Idle" && viewModel.Rows.Count == 1);

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.ClearHistoryCommand.Execute(null);
        Assert.True(editor.IsHistoryCleared);

        // Cancel just drops the editor: an unconfirmed request never lands.

        Assert.Equal(["example.com"], viewModel.TargetHistory.ToArray());
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static Func<Tracer> ScriptedFactory() =>
        new TracerScript { Target = RouteScript.TestTarget(), Snapshots = [] }.BuildFactory();

    private static Func<Tracer> LiveFactory()
    {
        Target target = RouteScript.TestTarget();
        Route snapshot = RouteScript.Snapshot(
            target, T0,
            RouteScript.RespondingHop(0, "192.0.2.1", "router.example", HopStatistics.Empty.Record(ProbeOutcome.Expired, 10)));
        return new TracerScript { Target = target, Snapshots = [snapshot] }.BuildFactory();
    }

    /// <summary>
    /// A stand-in behind <see cref="ITargetHistoryStore"/> proving the shell
    /// depends on the interface: recording only, still in memory only.
    /// </summary>
    private sealed class RecordingHistoryStore : ITargetHistoryStore
    {
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries => _entries;

        public int HistorySize { get; set; } = AcceptedSettings.DefaultHistorySize;

        public bool Cleared { get; private set; }

        public void Add(string target)
        {
            Cleared = false;
            _entries.Remove(target);
            _entries.Insert(0, target);
        }

        public void Clear()
        {
            Cleared = true;
            _entries.Clear();
        }
    }
}
