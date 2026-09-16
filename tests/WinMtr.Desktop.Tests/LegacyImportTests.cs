using WinMtr.Desktop.Services;

namespace WinMtr.Desktop.Tests;

/// <summary>
/// The previous version wrote its hosts into numbered slots and wrapped round
/// when the slots were full, so the newest host is the slot NrLRU points at.
/// The import turns that ring into the newest-first target history.
/// </summary>
public class LegacyImportTests
{
    [Fact]
    public void Slots_Below_The_Newest_Follow_It_In_Descending_Order()
    {
        var slots = new Dictionary<int, string>
        {
            [1] = "first.example",
            [2] = "second.example",
            [3] = "third.example",
        };

        string[] ordered = LegacyRingOrder.NewestFirst(slots, newestSlot: 3).ToArray();

        Assert.Equal(["third.example", "second.example", "first.example"], ordered);
    }

    [Fact]
    public void Slots_Above_The_Newest_Are_The_Oldest_Because_The_Ring_Wrapped()
    {
        // NrLRU is 2, so slots 3 and 4 were written before the ring wrapped
        // round and overwrote slots 1 and 2.
        var slots = new Dictionary<int, string>
        {
            [1] = "newer.example",
            [2] = "newest.example",
            [3] = "oldest.example",
            [4] = "older.example",
        };

        string[] ordered = LegacyRingOrder.NewestFirst(slots, newestSlot: 2).ToArray();

        Assert.Equal(["newest.example", "newer.example", "older.example", "oldest.example"], ordered);
    }

    [Fact]
    public void Missing_Slots_And_Blank_Hosts_Are_Skipped()
    {
        var slots = new Dictionary<int, string>
        {
            [1] = "kept.example",
            [3] = "   ",
            [5] = "also-kept.example",
        };

        string[] ordered = LegacyRingOrder.NewestFirst(slots, newestSlot: 5).ToArray();

        Assert.Equal(["also-kept.example", "kept.example"], ordered);
    }

    [Fact]
    public void An_Unknown_Newest_Slot_Falls_Back_To_Descending_Slot_Order()
    {
        var slots = new Dictionary<int, string> { [1] = "a.example", [2] = "b.example" };

        string[] ordered = LegacyRingOrder.NewestFirst(slots, newestSlot: 0).ToArray();

        Assert.Equal(["b.example", "a.example"], ordered);
    }

    [Fact]
    public void The_Registry_Is_Read_On_Windows_Only()
    {
        var store = new WindowsLegacyStore();

        if (OperatingSystem.IsWindows())
        {
            // Whatever this machine holds, reading it must not throw.
            store.ReadSettings();
            Assert.NotNull(store.ReadHistory());
        }
        else
        {
            Assert.Null(store.ReadSettings());
            Assert.Empty(store.ReadHistory());
        }
    }
}
