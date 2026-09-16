using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Target history survives a restart through its own JSON file (ADR 0005),
/// keeps the newest-first order of ticket 10, and obeys the history size.
/// </summary>
public class TargetHistoryPersistenceTests
{
    [Fact]
    public void Accepted_Targets_Reload_Newest_First()
    {
        var text = new FakeTextStore();
        var store = new PersistentTargetHistoryStore(text);

        store.Add("a.example");
        store.Add("b.example");

        Assert.Equal(["b.example", "a.example"], new PersistentTargetHistoryStore(text).Entries.ToArray());
    }

    [Fact]
    public void Reuse_Promotes_Across_A_Restart_Without_Duplicates()
    {
        var text = new FakeTextStore();
        var store = new PersistentTargetHistoryStore(text);
        store.Add("a.example");
        store.Add("b.example");

        var restarted = new PersistentTargetHistoryStore(text);
        restarted.Add("a.example");

        Assert.Equal(["a.example", "b.example"], new PersistentTargetHistoryStore(text).Entries.ToArray());
    }

    [Fact]
    public void Clear_Writes_An_Empty_List_And_Keeps_The_File()
    {
        var text = new FakeTextStore();
        var store = new PersistentTargetHistoryStore(text);
        store.Add("a.example");

        store.Clear();

        Assert.True(text.Files.ContainsKey(PersistentTargetHistoryStore.FileName));
        Assert.Empty(new PersistentTargetHistoryStore(text).Entries);
    }

    [Fact]
    public void Clearing_Does_Not_Bring_Back_The_Previous_Version_Entries()
    {
        var text = new FakeTextStore();
        var legacy = new FakeLegacyStore { History = ["old.example"] };
        var store = new PersistentTargetHistoryStore(text, legacy: legacy);

        Assert.Equal(["old.example"], store.Entries.ToArray());

        store.Clear();

        // The file now exists and holds an empty list, so the import cannot run again.
        Assert.Empty(new PersistentTargetHistoryStore(text, legacy: legacy).Entries);
    }

    [Fact]
    public void A_Longer_Stored_History_Is_Trimmed_To_The_History_Size_At_Load()
    {
        var text = new FakeTextStore();
        var wide = new PersistentTargetHistoryStore(text, historySize: 50);
        for (int i = 1; i <= 12; i++)
            wide.Add("host" + i + ".example");

        var narrow = new PersistentTargetHistoryStore(text, historySize: 3);

        Assert.Equal(["host12.example", "host11.example", "host10.example"], narrow.Entries.ToArray());
    }

    [Fact]
    public void Lowering_The_History_Size_Drops_The_Oldest_Entries_At_Once()
    {
        var text = new FakeTextStore();
        var store = new PersistentTargetHistoryStore(text, historySize: 10);
        store.Add("a.example");
        store.Add("b.example");
        store.Add("c.example");

        store.HistorySize = 2;

        Assert.Equal(["c.example", "b.example"], store.Entries.ToArray());
        Assert.Equal(["c.example", "b.example"], new PersistentTargetHistoryStore(text).Entries.ToArray());
    }

    [Fact]
    public void Adding_Beyond_The_History_Size_Drops_The_Oldest_Entry()
    {
        var store = new InMemoryTargetHistoryStore { HistorySize = 2 };

        store.Add("a.example");
        store.Add("b.example");
        store.Add("c.example");

        Assert.Equal(["c.example", "b.example"], store.Entries.ToArray());
    }

    [Fact]
    public void A_Damaged_History_File_Loads_Empty_Silently()
    {
        var text = new FakeTextStore();
        text.Seed(PersistentTargetHistoryStore.FileName, "not json at all");

        var store = new PersistentTargetHistoryStore(text);

        Assert.Empty(store.Entries);

        store.Add("a.example");

        Assert.Equal(["a.example"], new PersistentTargetHistoryStore(text).Entries.ToArray());
    }

    [Fact]
    public void An_Unknown_Schema_Version_Loads_Empty()
    {
        var text = new FakeTextStore();
        text.Seed(PersistentTargetHistoryStore.FileName, @"{""schemaVersion"":99,""entries"":[""a.example""]}");

        Assert.Empty(new PersistentTargetHistoryStore(text).Entries);
    }

    [Fact]
    public void A_Failed_Write_Reports_Once()
    {
        var text = new FakeTextStore { FailWrites = true };
        int failures = 0;
        var store = new PersistentTargetHistoryStore(text) { WriteFailed = () => failures++ };

        store.Add("a.example");

        Assert.Equal(1, failures);
    }

    [Fact]
    public void The_Previous_Version_Hosts_Are_Imported_Only_While_The_File_Is_Absent()
    {
        var text = new FakeTextStore();
        var legacy = new FakeLegacyStore { History = ["newest.example", "older.example"] };

        var first = new PersistentTargetHistoryStore(text, legacy: legacy);

        Assert.Equal(["newest.example", "older.example"], first.Entries.ToArray());

        first.Add("own.example");
        var second = new PersistentTargetHistoryStore(text, legacy: legacy);

        Assert.Equal(["own.example", "newest.example", "older.example"], second.Entries.ToArray());
    }

    [Fact]
    public void The_Window_Offers_The_Reloaded_History()
    {
        var text = new FakeTextStore();
        new PersistentTargetHistoryStore(text).Add("a.example");

        var viewModel = new MainWindowViewModel(new PersistentTargetHistoryStore(text));

        Assert.Equal(["a.example"], viewModel.TargetHistory.ToArray());
    }

    [Fact]
    public void Confirming_A_Smaller_History_Size_Trims_The_Offered_History()
    {
        var text = new FakeTextStore();
        var history = new PersistentTargetHistoryStore(text);
        history.Add("a.example");
        history.Add("b.example");
        history.Add("c.example");
        var viewModel = new MainWindowViewModel(history, settingsStore: new JsonSettingsStore(text));

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.HistorySize = 2;
        Assert.True(viewModel.ApplySettingsEditor(editor));

        Assert.Equal(["c.example", "b.example"], viewModel.TargetHistory.ToArray());
    }
}
