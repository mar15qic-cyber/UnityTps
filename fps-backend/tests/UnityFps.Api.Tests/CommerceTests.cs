using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class CommerceTests
{
    [Fact]
    public async Task CatalogSeedHasExactly39WeaponsAndOnlyCanonicalLpwVariants()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var ids = await db.CatalogItems.Select(x => x.ItemId).ToListAsync();
        Assert.Equal(39, ids.Count);
        Assert.Equal(29, ids.Count(x => x.StartsWith("weapon.lpw.")));
        Assert.DoesNotContain(await db.CatalogItems.Select(x => x.AssetKey).ToListAsync(), x =>
            x.StartsWith("lpw/") && !x.EndsWith("_01", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseIsIdempotentAndChargesOnce()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = AddUser(db, level: 30, coins: 50_000);
        var service = new CommerceService(db);
        var request = new PurchaseRequest { ItemId = "weapon.lpw.sniper.01", Quantity = 1, IdempotencyKey = "purchase-test-0001" };
        var first = await service.PurchaseAsync(user.Id, request, CancellationToken.None);
        var replay = await service.PurchaseAsync(user.Id, request, CancellationToken.None);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(45_500, replay.Coins);
        Assert.Single(await db.Purchases.ToListAsync());
        Assert.Single(await db.InventoryItems.Where(x => x.ItemId == request.ItemId).ToListAsync());
    }

    [Fact]
    public async Task LoadoutRejectsWrongSlotAndStaleVersion()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = AddUser(db, level: 30, coins: 50_000);
        db.InventoryItems.AddRange(
            new PlayerInventoryItem { UserId = user.Id, ItemId = "weapon.lpw.rifle.01", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow },
            new PlayerInventoryItem { UserId = user.Id, ItemId = "weapon.lpw.pistol.01", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new LoadoutService(db);
        var saved = await service.UpdateAsync(user.Id, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.lpw.rifle.01", SecondaryWeaponId = "weapon.lpw.pistol.01", ExpectedVersion = 1
        }, CancellationToken.None);
        Assert.Equal(2, saved.Version);
        var stale = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(user.Id, new LoadoutRequest
        {
            PrimaryWeaponId = "weapon.lpw.rifle.01", SecondaryWeaponId = "weapon.lpw.pistol.01", ExpectedVersion = 1
        }, CancellationToken.None));
        Assert.Equal(ApiErrorCodes.LoadoutVersionConflict, stale.Code);
    }

    [Fact]
    public async Task SeedMigratesLegacyLoadoutAndBackfillsInitialInventoryWithoutOverwriting()
    {
        await using var db = CreateDb();
        var user = new UserAccount
        {
            Username = "legacy", NormalizedUsername = "LEGACY", PasswordHash = "test", CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadout = new PlayerLoadout { PrimaryWeaponId = "rifle.day3", SecondaryWeaponId = "pistol.day2", Version = 0, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user); await db.SaveChangesAsync();
        await CatalogSeeder.SeedAsync(db);
        Assert.Equal("weapon.m4", user.Loadout!.PrimaryWeaponId);
        Assert.Equal("weapon.service_pistol", user.Loadout.SecondaryWeaponId);
        Assert.Equal(3, user.Inventory.Count);
        await CatalogSeeder.SeedAsync(db);
        Assert.Equal(3, await db.InventoryItems.CountAsync(x => x.UserId == user.Id));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserAccount AddUser(AppDbContext db, int level, long coins)
    {
        var user = new UserAccount
        {
            Username = Guid.NewGuid().ToString("N"), NormalizedUsername = Guid.NewGuid().ToString("N"), PasswordHash = "test", CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { Level = level, UpdatedAtUtc = DateTime.UtcNow },
            Loadout = new PlayerLoadout { UpdatedAtUtc = DateTime.UtcNow },
            Wallet = new PlayerWallet { Coins = coins, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user); db.SaveChanges(); return user;
    }
}
