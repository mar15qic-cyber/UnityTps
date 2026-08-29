using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

public static class WeaponCatalogSeed
{
    private sealed record Family(string Key, string SourcePrefix, string Category, string Slot, string[] Names, long[] Prices, int[] Levels);

    public static IReadOnlyList<CatalogItem> All { get; } = Build();

    private static IReadOnlyList<CatalogItem> Build()
    {
        var items = new List<CatalogItem>
        {
            Legacy("weapon.m4", "Primary", "Rifle", "M4", "rifle/day3", 0, 1),
            Legacy("weapon.ak", "Primary", "Rifle", "AK", "rifle/02", 0, 1),
            Legacy("weapon.service_pistol", "Secondary", "Pistol", "Service Pistol", "pistol/day2", 0, 1),
            Legacy("weapon.rifle03", "Primary", "Rifle", "Rifle 03", "rifle/03", 5000, 6),
            Legacy("weapon.smg01", "Primary", "Smg", "SMG 01", "smg/01", 3500, 4),
            Legacy("weapon.smg02", "Primary", "Smg", "SMG 02", "smg/02", 5500, 7),
            Legacy("weapon.shotgun01", "Primary", "Shotgun", "Shotgun 01", "shotgun/01", 5000, 7),
            Legacy("weapon.sniper01", "Primary", "Sniper", "Sniper 01", "sniper/01", 8000, 10),
            Legacy("weapon.sniper02", "Primary", "Sniper", "Sniper 02", "sniper/02", 12000, 15),
            Legacy("weapon.handgun02", "Secondary", "Pistol", "Handgun 02", "handgun/02", 2500, 4)
        };

        var families = new[]
        {
            new Family("rifle", "AssaultRifle", "Rifle", "Primary",
                Names("现代突击步枪", 6),
                [3000, 4500, 6500, 8500, 11000, 14000], [3, 5, 8, 11, 14, 18]),
            new Family("pistol", "Pistol", "Pistol", "Secondary",
                Names("战术半自动手枪", 6),
                [1000, 1800, 2800, 4000, 5500, 7500], [2, 3, 5, 7, 9, 12]),
            new Family("shotgun", "Shotgun", "Shotgun", "Primary",
                Names("战术霰弹枪", 5),
                [3000, 5000, 7500, 10000, 13500], [4, 7, 10, 14, 18]),
            new Family("smg", "SMG", "Smg", "Primary",
                Names("紧凑型冲锋枪", 6),
                [2500, 4000, 5500, 7500, 9500, 12000], [3, 5, 7, 9, 12, 15]),
            new Family("sniper", "SniperRifle", "Sniper", "Primary",
                Names("精确射手步枪", 6),
                [4500, 7000, 10000, 14000, 19000, 25000], [6, 10, 14, 18, 23, 28])
        };
        foreach (var family in families)
        for (var index = 0; index < family.Names.Length; index++)
        {
            var tier = index + 1;
            items.Add(new CatalogItem
            {
                ItemId = $"weapon.lpw.{family.Key}.{tier:00}", ItemType = "Weapon", SlotType = family.Slot,
                Category = family.Category, DisplayName = family.Names[index], Description = "LPW正式枪械；配件尚未适配",
                AssetKey = $"lpw/{family.SourcePrefix}{tier}_01", PriceCoins = family.Prices[index], UnlockLevel = family.Levels[index],
                IsActive = true, IsImplemented = true, CalibrationKey = $"lpw.{family.Key}.{tier:00}.v1"
            });
        }
        return items;
    }

    private static CatalogItem Legacy(string id, string slot, string category, string name, string assetKey, long price, int level) => new()
    {
        ItemId = id, ItemType = "Weapon", SlotType = slot, Category = category, DisplayName = name,
        Description = "LPFP原有武器", AssetKey = assetKey, PriceCoins = price, UnlockLevel = level,
        IsActive = true, IsImplemented = true, CalibrationKey = "legacy"
    };

    private static string[] Names(string category, int count) =>
        Enumerable.Range(1, count).Select(x => $"{category} {x:00}").ToArray();
}

public static class CatalogSeeder
{
    public const long InitialCoins = 5_000;
    public static readonly string[] InitialWeapons = ["weapon.m4", "weapon.ak", "weapon.service_pistol"];

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var existing = await db.CatalogItems.ToDictionaryAsync(x => x.ItemId, cancellationToken);
        foreach (var seed in WeaponCatalogSeed.All)
        {
            if (!existing.TryGetValue(seed.ItemId, out var item)) db.CatalogItems.Add(seed);
            else Copy(seed, item);
        }
        await db.SaveChangesAsync(cancellationToken);

        var users = await db.Users.Include(x => x.Wallet).Include(x => x.Inventory).Include(x => x.Loadout).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            user.Wallet ??= new PlayerWallet { User = user, Coins = InitialCoins, UpdatedAtUtc = DateTime.UtcNow };
            foreach (var itemId in InitialWeapons)
                if (user.Inventory.All(x => x.ItemId != itemId))
                    user.Inventory.Add(new PlayerInventoryItem { User = user, ItemId = itemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
            if (user.Loadout is not null)
            {
                user.Loadout.PrimaryWeaponId = MigrateItemId(user.Loadout.PrimaryWeaponId);
                user.Loadout.SecondaryWeaponId = MigrateItemId(user.Loadout.SecondaryWeaponId);
                if (user.Loadout.Version < 1) user.Loadout.Version = 1;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string MigrateItemId(string id) => id switch
    {
        "rifle.day3" => "weapon.m4", "rifle.02" => "weapon.ak", "rifle.03" => "weapon.rifle03",
        "pistol.day2" => "weapon.service_pistol", "handgun.02" => "weapon.handgun02",
        "smg.01" => "weapon.smg01", "smg.02" => "weapon.smg02", "shotgun.01" => "weapon.shotgun01",
        "sniper.01" => "weapon.sniper01", "sniper.02" => "weapon.sniper02", _ => id
    };

    private static void Copy(CatalogItem source, CatalogItem target)
    {
        target.ItemType = source.ItemType; target.SlotType = source.SlotType; target.Category = source.Category;
        target.DisplayName = source.DisplayName; target.Description = source.Description; target.AssetKey = source.AssetKey;
        target.PriceCoins = source.PriceCoins; target.UnlockLevel = source.UnlockLevel; target.IsActive = source.IsActive;
        target.IsImplemented = source.IsImplemented; target.CalibrationKey = source.CalibrationKey;
    }
}
