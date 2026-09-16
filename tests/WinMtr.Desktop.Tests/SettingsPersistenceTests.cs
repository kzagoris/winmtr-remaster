using WinMtr.Desktop.Services;
using WinMtr.Desktop.ViewModels;
using WinMtr.Core;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// Accepted settings survive a restart through the JSON store (ADR 0005).
/// Every test drives the text seam in memory, so nothing here reaches a real
/// folder.
/// </summary>
public class SettingsPersistenceTests
{
    [Fact]
    public void Save_Then_Load_Returns_Every_Accepted_Value()
    {
        var text = new FakeTextStore();
        var saved = new AcceptedSettings(
            ProbeSettings.Default with
            {
                Cadence = TimeSpan.FromSeconds(2.5),
                PayloadBytes = 128,
                HopLimit = 40,
                ReplyTimeout = TimeSpan.FromSeconds(12.5),
                ResolveNames = false,
            },
            AppTheme.Dark,
            HistorySize: 25);

        new JsonSettingsStore(text).Save(saved);
        AcceptedSettings loaded = new JsonSettingsStore(text).Load();

        Assert.Equal(TimeSpan.FromSeconds(2.5), loaded.Probe.Cadence);
        Assert.Equal(128, loaded.Probe.PayloadBytes);
        Assert.Equal(40, loaded.Probe.HopLimit);
        Assert.Equal(TimeSpan.FromSeconds(12.5), loaded.Probe.ReplyTimeout);
        Assert.False(loaded.Probe.ResolveNames);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(25, loaded.HistorySize);
    }

    [Fact]
    public void Absent_File_Loads_Defaults_And_Writes_Nothing()
    {
        var text = new FakeTextStore();

        AcceptedSettings loaded = new JsonSettingsStore(text).Load();

        Assert.Equal(AcceptedSettings.Default, loaded);
        Assert.Equal(0, text.WriteCount);
    }

    [Fact]
    public void Damaged_File_Loads_Defaults_Silently_And_Is_Replaced_By_The_Next_Write()
    {
        var text = new FakeTextStore();
        text.Seed(JsonSettingsStore.FileName, "{ this is not json");
        var store = new JsonSettingsStore(text);

        Assert.Equal(AcceptedSettings.Default, store.Load());

        store.Save(AcceptedSettings.Default with { Theme = AppTheme.Light });

        Assert.Equal(AppTheme.Light, new JsonSettingsStore(text).Load().Theme);
    }

    [Fact]
    public void Unknown_Schema_Version_Is_Treated_As_Damaged()
    {
        var text = new FakeTextStore();
        text.Seed(JsonSettingsStore.FileName, @"{""schemaVersion"":99,""hopLimit"":7}");

        Assert.Equal(AcceptedSettings.Default, new JsonSettingsStore(text).Load());
    }

    [Fact]
    public void Missing_Field_Takes_Its_Default_Value()
    {
        var text = new FakeTextStore();
        text.Seed(JsonSettingsStore.FileName, @"{""schemaVersion"":1,""hopLimit"":12}");

        AcceptedSettings loaded = new JsonSettingsStore(text).Load();

        Assert.Equal(12, loaded.Probe.HopLimit);
        Assert.Equal(ProbeSettings.Default.PayloadBytes, loaded.Probe.PayloadBytes);
        // A file written before the timeout was a dialog field takes its default.
        Assert.Equal(ProbeSettings.Default.ReplyTimeout, loaded.Probe.ReplyTimeout);
        Assert.Equal(AcceptedSettings.DefaultHistorySize, loaded.HistorySize);
        Assert.Equal(AppTheme.System, loaded.Theme);
    }

    [Theory]
    [InlineData(128, AcceptedSettings.MaxHistorySize)]
    [InlineData(0, AcceptedSettings.MinHistorySize)]
    public void History_Size_Outside_The_Range_Moves_To_The_Nearest_Allowed_Value(int stored, int expected)
    {
        var text = new FakeTextStore();
        text.Seed(JsonSettingsStore.FileName, @"{""schemaVersion"":1,""historySize"":" + stored + @",""hopLimit"":9}");

        AcceptedSettings loaded = new JsonSettingsStore(text).Load();

        Assert.Equal(expected, loaded.HistorySize);
        // The other values are kept: only the bad one moves.
        Assert.Equal(9, loaded.Probe.HopLimit);
    }

    [Fact]
    public void Other_Values_Outside_Their_Range_Move_To_The_Nearest_Allowed_Value()
    {
        var text = new FakeTextStore();
        text.Seed(
            JsonSettingsStore.FileName,
            @"{""schemaVersion"":1,""hopLimit"":900,""payloadBytes"":-5,""probeIntervalSeconds"":0,""theme"":""Sideways""}");

        AcceptedSettings loaded = new JsonSettingsStore(text).Load();

        Assert.Equal(255, loaded.Probe.HopLimit);
        Assert.Equal(0, loaded.Probe.PayloadBytes);
        // The allowed interval has no smallest member, so an impossible one
        // takes the default rather than a nearest value.
        Assert.Equal(ProbeSettings.Default.Cadence, loaded.Probe.Cadence);
        Assert.Equal(AppTheme.System, loaded.Theme);
    }

    [Theory]
    [InlineData("0", 0.1)]
    [InlineData("-1", 0.1)]
    [InlineData("0.05", 0.1)]
    [InlineData("120", 60)]
    public void Reply_Timeout_Outside_The_Range_Moves_To_The_Nearest_Allowed_Value(string stored, double expected)
    {
        var text = new FakeTextStore();
        text.Seed(
            JsonSettingsStore.FileName,
            @"{""schemaVersion"":1,""replyTimeoutSeconds"":" + stored + @",""hopLimit"":9}");

        AcceptedSettings loaded = new JsonSettingsStore(text).Load();

        Assert.Equal(TimeSpan.FromSeconds(expected), loaded.Probe.ReplyTimeout);
        // The other values are kept: only the bad one moves.
        Assert.Equal(9, loaded.Probe.HopLimit);
    }

    [Fact]
    public void A_Failed_Write_Is_Reported_To_The_Caller()
    {
        var text = new FakeTextStore { FailWrites = true };
        var store = new JsonSettingsStore(text);

        Assert.False(store.Save(AcceptedSettings.Default));
    }

    [Fact]
    public void Legacy_Settings_Are_Imported_Only_While_The_File_Is_Absent()
    {
        var text = new FakeTextStore();
        var legacy = new FakeLegacyStore
        {
            Settings = new LegacySettings(ProbeIntervalSeconds: 2, PayloadBytes: 100, ResolveNames: false, HistorySize: 128),
        };

        AcceptedSettings imported = new JsonSettingsStore(text, legacy).Load();

        Assert.Equal(TimeSpan.FromSeconds(2), imported.Probe.Cadence);
        Assert.Equal(100, imported.Probe.PayloadBytes);
        Assert.False(imported.Probe.ResolveNames);
        // The legacy default of 128 arrives as the maximum allowed value.
        Assert.Equal(AcceptedSettings.MaxHistorySize, imported.HistorySize);
        // The previous version knew no hop limit and no theme.
        Assert.Equal(ProbeSettings.Default.HopLimit, imported.Probe.HopLimit);
        Assert.Equal(AppTheme.System, imported.Theme);

        // Once this application owns a file, the previous version is not read again.
        new JsonSettingsStore(text).Save(AcceptedSettings.Default with { Theme = AppTheme.Light });
        AcceptedSettings second = new JsonSettingsStore(text, legacy).Load();

        Assert.Equal(ProbeSettings.Default.Cadence, second.Probe.Cadence);
        Assert.Equal(AppTheme.Light, second.Theme);
    }

    [Fact]
    public void Confirming_The_Dialog_Saves_And_A_Later_Start_Loads_The_Accepted_Settings()
    {
        var text = new FakeTextStore();
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            settingsStore: new JsonSettingsStore(text));

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.HopLimit = 42;
        editor.Theme = AppTheme.Dark;
        editor.HistorySize = 7;
        editor.ReplyTimeoutSeconds = 12.5;
        Assert.True(viewModel.ApplySettingsEditor(editor));

        var restarted = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            settingsStore: new JsonSettingsStore(text));

        Assert.Equal(42, restarted.AcceptedSettings.HopLimit);
        Assert.Equal(TimeSpan.FromSeconds(12.5), restarted.AcceptedSettings.ReplyTimeout);
        Assert.Equal(AppTheme.Dark, restarted.AcceptedTheme);
        Assert.Equal(7, restarted.CreateSettingsEditor().HistorySize);
        Assert.Equal(12.5, restarted.CreateSettingsEditor().ReplyTimeoutSeconds);
    }

    [Fact]
    public void Cancelling_The_Dialog_Writes_Nothing()
    {
        var text = new FakeTextStore();
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            settingsStore: new JsonSettingsStore(text));

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.HopLimit = 42;
        // The dialog was cancelled, so nothing is applied.

        Assert.Equal(0, text.WriteCount);
        Assert.Equal(ProbeSettings.Default.HopLimit, viewModel.AcceptedSettings.HopLimit);
    }

    [Fact]
    public void A_Failed_Save_Shows_One_Message_Per_Session()
    {
        var text = new FakeTextStore { FailWrites = true };
        var viewModel = new MainWindowViewModel(
            new InMemoryTargetHistoryStore(),
            settingsStore: new JsonSettingsStore(text));

        SettingsViewModel editor = viewModel.CreateSettingsEditor();
        editor.HopLimit = 42;
        viewModel.ApplySettingsEditor(editor);
        SettingsViewModel again = viewModel.CreateSettingsEditor();
        again.HopLimit = 43;
        viewModel.ApplySettingsEditor(again);

        Assert.Single(viewModel.Banners);
        Assert.Equal(BannerSeverity.Warning, viewModel.Banners[0].Severity);
    }

    [Fact]
    public void The_History_Size_Control_Validates_Its_Range()
    {
        var editor = new SettingsViewModel();

        editor.HistorySize = 0;
        Assert.False(editor.IsValid);
        Assert.True(editor.HasHistorySizeError);

        editor.HistorySize = 101;
        Assert.False(editor.IsValid);

        editor.HistorySize = 100;
        Assert.True(editor.IsValid);
        Assert.Equal(100, editor.HistorySize);
    }
}
