namespace WinMtr.Desktop.Services;

/// <summary>
/// Turns the previous version's numbered host slots into newest-first order.
/// That version wrote each new host into the next slot and wrapped round to
/// slot 1 when the slots were full, recording the last written slot as NrLRU.
/// The newest host therefore sits at NrLRU, the slots below it are newer than
/// the slots above it, and both parts run backwards.
/// </summary>
public static class LegacyRingOrder
{
    public static IEnumerable<string> NewestFirst(IReadOnlyDictionary<int, string> slots, int newestSlot)
    {
        ArgumentNullException.ThrowIfNull(slots);

        var newer = slots.Where(slot => slot.Key <= newestSlot).OrderByDescending(slot => slot.Key);
        var older = slots.Where(slot => slot.Key > newestSlot).OrderByDescending(slot => slot.Key);

        return newer.Concat(older)
            .Select(slot => slot.Value.Trim())
            .Where(host => host.Length > 0);
    }
}
