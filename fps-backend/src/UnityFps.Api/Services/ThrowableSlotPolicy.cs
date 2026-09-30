using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

public static class ThrowableSlotPolicy
{
    public const int SlotCount = 3;
    public static readonly string[] ModelIds = ["throwable.frag", "throwable.frag_02", "throwable.frag_03", "throwable.flash", "throwable.smoke"];
    public static string?[] FromLegacy(string? id) => id switch
    {
        "throwable.standard" => ["throwable.frag", "throwable.flash", "throwable.smoke"],
        "throwable.frag_assault" => ["throwable.frag", "throwable.frag", "throwable.frag"],
        null or "" => [null, null, null],
        _ when ModelIds.Contains(id) => [id, null, null],
        _ => [null, null, null]
    };

    public static string?[] Read(PlayerLoadout loadout) => loadout.Throwables.Count == 0
        ? FromLegacy(loadout.ThrowableId)
        : Enumerable.Range(0, SlotCount).Select(i => loadout.Throwables.FirstOrDefault(x => x.SlotIndex == i)?.ItemId).ToArray();

    // Persist all three rows, including NULL. An empty slot is a saved choice, never a missing default.
    public static void Write(PlayerLoadout loadout, string?[] slots)
    {
        for (var i = 0; i < SlotCount; i++)
        {
            var row = loadout.Throwables.FirstOrDefault(x => x.SlotIndex == i);
            if (row is null) { row = new PlayerLoadoutThrowable { SlotIndex = i }; loadout.Throwables.Add(row); }
            row.ItemId = string.IsNullOrWhiteSpace(slots[i]) ? null : slots[i]!.Trim();
        }
    }
}
