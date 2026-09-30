using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class RetiredSuppressorTests
{
    [Fact]
    public async Task P90RejectsGripAndCleansOldLoadoutOnlyOnce()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await CatalogSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);
        var user = new UserAccount {
            Username = "p90-test", NormalizedUsername = "P90-TEST", PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            Loadouts = { new PlayerLoadout { PrimaryWeaponId = "weapon.smg04", SecondaryWeaponId = "weapon.service_pistol",
                Version = 2, UpdatedAtUtc = DateTime.UtcNow } }
        };
        db.Users.Add(user);
        db.AttachmentCompat.Add(new AttachmentCompat { WeaponItemId = "weapon.smg04",
            AttachmentItemId = "attach.lpw.grip.01", SlotType = "Underbarrel",
            IsImplemented = true, CalibrationKey = "socket-v2" });
        user.Loadouts[0].Attachments.Add(new PlayerLoadoutAttachment { WeaponSlot = "Primary",
            AttachmentSlot = "Underbarrel", AttachmentItemId = "attach.lpw.grip.01" });
        user.Loadouts[0].Attachments.Add(new PlayerLoadoutAttachment { WeaponSlot = "Primary",
            AttachmentSlot = "Muzzle", AttachmentItemId = "attach.lpfp.muffler.01" });
        await db.SaveChangesAsync();
        await AttachmentSystemSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);
        Assert.False(await db.AttachmentCompat.AnyAsync(x => x.WeaponItemId == "weapon.smg04" && x.SlotType == "Underbarrel"));
        Assert.Equal(3, user.Loadouts[0].Version);
        var remaining = await db.LoadoutAttachments.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("attach.lpfp.muffler.01", remaining[0].AttachmentItemId);
    }

    [Fact]
    public async Task RetiringLpwSuppressors_RemovesCompatibilityAndLoadout_NotPurchaseHistory()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await CatalogSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db);
        var user = new UserAccount {
            Username = "silencer-test", NormalizedUsername = "SILENCER-TEST", PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            Loadouts = { new PlayerLoadout { PrimaryWeaponId = "weapon.ak", SecondaryWeaponId = "weapon.service_pistol",
                Version = 2, UpdatedAtUtc = DateTime.UtcNow } }
        };
        db.Users.Add(user);
        foreach (var id in new[] { "attach.lpw.muffler.01", "attach.lpw.muffler.02" }) {
            db.CatalogItems.Add(new CatalogItem { ItemId = id, ItemType = "Attachment", SlotType = "Muzzle",
                Category = "Attachment", DisplayName = id, AssetKey = id, Description = "legacy",
                AcquisitionSource = "Shop", IsActive = true, IsImplemented = true });
            db.AttachmentCompat.Add(new AttachmentCompat { WeaponItemId = "weapon.ak", AttachmentItemId = id,
                SlotType = "Muzzle", IsImplemented = true, CalibrationKey = "socket-v2" });
            db.InventoryItems.Add(new PlayerInventoryItem { UserId = user.Id, ItemId = id, Quantity = 1,
                AcquiredAtUtc = DateTime.UtcNow });
            db.Purchases.Add(new ShopPurchase { PurchaseId = id, UserId = user.Id, ItemId = id,
                Quantity = 1, IdempotencyKey = id, CreatedAtUtc = DateTime.UtcNow });
        }
        user.Loadouts[0].Attachments.Add(new PlayerLoadoutAttachment { WeaponSlot = "Primary", AttachmentSlot = "Muzzle",
            AttachmentItemId = "attach.lpw.muffler.02" });
        await db.SaveChangesAsync();
        await AttachmentSystemSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);
        Assert.Empty(await db.AttachmentCompat.Where(x => x.AttachmentItemId.StartsWith("attach.lpw.muffler.")).ToListAsync());
        Assert.Empty(await db.LoadoutAttachments.Where(x => x.AttachmentItemId.StartsWith("attach.lpw.muffler.")).ToListAsync());
        Assert.Equal(3, user.Loadouts[0].Version);
        Assert.Equal(2, await db.Purchases.CountAsync());
        Assert.Equal(2, await db.InventoryItems.CountAsync(x => x.ItemId.StartsWith("attach.lpw.muffler.")));
        Assert.All(await db.CatalogItems.Where(x => x.ItemId.StartsWith("attach.lpw.muffler.")).ToListAsync(), x => Assert.False(x.IsActive));
        Assert.True(await db.AttachmentCompat.AnyAsync(x => x.WeaponItemId == "weapon.ak" && x.AttachmentItemId == "attach.lpfp.muffler.01"));
        Assert.False(await db.AttachmentCompat.AnyAsync(x => x.WeaponItemId == "weapon.smg01" && x.SlotType == "Underbarrel"));
    }
}
