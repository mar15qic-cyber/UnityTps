using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UnityFps.Api.Data;
using UnityFps.Api.Data.Migrations;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class RetiredOpticCleanupTests
{
    private static readonly string[] RetiredOptics =
    [
        "attach.lpw.optic.01", "attach.lpw.optic.02", "attach.lpw.optic.03", "attach.lpw.optic.04",
        "attach.lpw.optic.05", "attach.lpw.optic.06", "attach.lpw.optic.07", "attach.lpw.optic.08",
        "attach.pistol.optic"
    ];

    [Fact]
    public async Task Seeders_RemoveRetiredOpticRows_AndDoNotReintroduceThemOnSecondRun()
    {
        await using var db = CreateDb();
        var user = new UserAccount
        {
            Username = "retired-optics-test",
            NormalizedUsername = "RETIRED-OPTICS-TEST",
            PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            Loadout = new PlayerLoadout
            {
                PrimaryWeaponId = "weapon.m4",
                SecondaryWeaponId = "weapon.service_pistol",
                Version = 1,
                UpdatedAtUtc = DateTime.UtcNow
            },
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Wallet = new PlayerWallet { Coins = 0, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        await CatalogSeeder.SeedAsync(db);

        foreach (var (optic, index) in RetiredOptics.Select((id, i) => (id, i)))
        {
            db.CatalogItems.Add(new CatalogItem
            {
                ItemId = optic, ItemType = "Attachment", SlotType = "Optic", Category = "Attachment",
                DisplayName = optic, Description = "retired", AssetKey = optic, CalibrationKey = "legacy",
                AcquisitionSource = "Shop", IsActive = true, IsImplemented = true
            });
            db.AttachmentCompat.Add(new AttachmentCompat
            {
                WeaponItemId = "weapon.m4", AttachmentItemId = optic, SlotType = "Optic",
                IsImplemented = true, CalibrationKey = "legacy"
            });
            db.InventoryItems.Add(new PlayerInventoryItem { UserId = user.Id, ItemId = optic, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
            db.Purchases.Add(new ShopPurchase
            {
                PurchaseId = "retired-" + index, UserId = user.Id, ItemId = optic, Quantity = 1,
                IdempotencyKey = "retired-" + index, CreatedAtUtc = DateTime.UtcNow
            });
            db.LoadoutAttachments.Add(new PlayerLoadoutAttachment
            {
                LoadoutId = user.Loadout!.Id, WeaponSlot = "Primary", AttachmentSlot = "Optic" + index,
                AttachmentItemId = optic
            });
        }
        db.PassRewards.Add(new PassReward { SeasonId = "S1", PassLevel = 8, RewardType = "Attachment", ItemId = "attach.pistol.optic" });
        db.PassRewardGrants.Add(new PlayerPassRewardGrant { UserId = user.Id, SeasonId = "S1", PassLevel = 8, GrantedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await PassSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);

        Assert.Empty(await db.CatalogItems.Where(x => RetiredOptics.Contains(x.ItemId)).ToListAsync());
        Assert.Empty(await db.AttachmentCompat.Where(x => RetiredOptics.Contains(x.AttachmentItemId)).ToListAsync());
        Assert.Empty(await db.InventoryItems.Where(x => RetiredOptics.Contains(x.ItemId)).ToListAsync());
        Assert.Empty(await db.Purchases.Where(x => RetiredOptics.Contains(x.ItemId)).ToListAsync());
        Assert.Empty(await db.LoadoutAttachments.Where(x => RetiredOptics.Contains(x.AttachmentItemId)).ToListAsync());
        Assert.Empty(await db.PassRewardGrants.Where(x => x.SeasonId == "S1" && x.PassLevel == 8).ToListAsync());
        Assert.Null(await db.PassRewards.SingleOrDefaultAsync(x => x.SeasonId == "S1" && x.PassLevel == 8));
    }

    [Fact]
    public void CleanupMigration_ContainsAllRetiredIdsAndIsOneWay()
    {
        var up = new MigrationBuilder("Pomelo.EntityFrameworkCore.MySql");
        var probe = new MigrationProbe();
        probe.ApplyUp(up);

        var sql = up.Operations.OfType<SqlOperation>().Select(x => x.Sql).ToArray();
        var allSql = string.Join("\n", sql);
        foreach (var optic in RetiredOptics)
            Assert.Contains(optic, allSql, StringComparison.Ordinal);
        Assert.Contains("PlayerLoadoutAttachment", allSql, StringComparison.Ordinal);
        Assert.Contains("AttachmentCompat", allSql, StringComparison.Ordinal);
        Assert.Contains("PlayerInventoryItem", allSql, StringComparison.Ordinal);
        Assert.Contains("ShopPurchase", allSql, StringComparison.Ordinal);
        Assert.Contains("PlayerPassRewardGrant", allSql, StringComparison.Ordinal);
        Assert.Contains("PassReward", allSql, StringComparison.Ordinal);
        Assert.Contains("CatalogItem", allSql, StringComparison.Ordinal);

        var down = new MigrationBuilder("Pomelo.EntityFrameworkCore.MySql");
        probe.ApplyDown(down);
        Assert.Empty(down.Operations);
    }

    private sealed class MigrationProbe : RemoveRetiredOpticRows
    {
        public void ApplyUp(MigrationBuilder builder) => Up(builder);
        public void ApplyDown(MigrationBuilder builder) => Down(builder);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
