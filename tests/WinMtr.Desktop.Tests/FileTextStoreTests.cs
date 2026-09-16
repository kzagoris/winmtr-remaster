using WinMtr.Desktop.Services;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The one place the persistence work touches a real folder: the per-user path
/// resolves, a write is atomic, and an absent file reads as nothing.
/// </summary>
public class FileTextStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "WinMtrTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Absent_File_Reads_As_Nothing()
    {
        var store = new FileTextStore(_folder);

        Assert.Null(store.Read("settings.json"));
    }

    [Fact]
    public void Write_Then_Read_Round_Trips_And_Leaves_No_Temporary_File()
    {
        var store = new FileTextStore(_folder);

        Assert.True(store.TryWrite("settings.json", "{}"));

        Assert.Equal("{}", store.Read("settings.json"));
        Assert.Equal("settings.json", Assert.Single(Directory.GetFiles(_folder).Select(Path.GetFileName)));
    }

    [Fact]
    public void A_Second_Write_Replaces_The_First()
    {
        var store = new FileTextStore(_folder);
        store.TryWrite("history.json", "one");

        store.TryWrite("history.json", "two");

        Assert.Equal("two", store.Read("history.json"));
    }

    [Fact]
    public void The_Default_Folder_Is_Per_User_And_Named_For_The_Application()
    {
        string folder = FileTextStore.DefaultFolder();

        Assert.Equal("WinMTR", Path.GetFileName(folder));
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            folder,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
