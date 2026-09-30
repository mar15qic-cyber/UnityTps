using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using UnityFps.Api.Common;
using Xunit;

namespace UnityFps.Api.Tests;

public class RealTest0930Tests
{
    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    [Fact]
    public async Task ThreeSlotsRoundTripDuplicatesEmptyAndUnownedAreIndependentAndSeedSafe()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        var user = new UserAccount { Username = "slots", NormalizedUsername = "SLOTS", PasswordHash = "test", Profile = new(), Wallet = new() };
        user.Loadouts.Add(new PlayerLoadout { ThrowableId = "throwable.standard" });
        db.Users.Add(user); await db.SaveChangesAsync(); await CatalogSeeder.SeedAsync(db);
        var service = new LoadoutService(db);
        var saved = await service.UpdateAsync(user.Id, 0, new LoadoutRequest { PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol", ThrowableIds = ["throwable.frag", "throwable.frag", null], ExpectedVersion = 1 }, default);
        Assert.Equal(new string?[] { "throwable.frag", "throwable.frag", null }, saved.ThrowableIds);
        await service.UpdateAsync(user.Id, 1, new LoadoutRequest { PrimaryWeaponId = "weapon.ak", SecondaryWeaponId = "weapon.service_pistol", ThrowableIds = [null, null, null], ExpectedVersion = 1 }, default);
        await CatalogSeeder.SeedAsync(db); db.ChangeTracker.Clear();
        Assert.Equal(saved.ThrowableIds, (await service.GetAsync(user.Id, 0, default)).ThrowableIds);
        Assert.Equal(new string?[3], (await service.GetAsync(user.Id, 1, default)).ThrowableIds);
        var error = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(user.Id, 0, new LoadoutRequest { PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol", ThrowableIds = ["throwable.frag_02", null, null], ExpectedVersion = 2 }, default));
        Assert.Equal(403, error.StatusCode);
    }
    [Fact]
    public async Task PresetAndSuppressorMigrationPreservesOwnershipAndPassShopChannels()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db); await PassSeeder.SeedAsync(db); await AttachmentSystemSeeder.SeedAsync(db);
        var user = new UserAccount { Username = "legacy", NormalizedUsername = "LEGACY", PasswordHash = "test", Profile = new(), Wallet = new() };
        user.Inventory.Add(new PlayerInventoryItem { ItemId = "attach.rifle.muzzle", Quantity = 1 });
        user.Loadouts.Add(new PlayerLoadout { ThrowableId = "throwable.frag_assault", Attachments = [new() { WeaponSlot = "Primary", AttachmentSlot = "Muzzle", AttachmentItemId = "attach.rifle.muzzle" }] });
        db.Users.Add(user); await db.SaveChangesAsync();
        await CatalogSeeder.SeedAsync(db); await AttachmentSystemSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db); await AttachmentSystemSeeder.SeedAsync(db);
        Assert.Equal(new[] { "throwable.frag", "throwable.frag", "throwable.frag" }, ThrowableSlotPolicy.Read(user.Loadouts[0]));
        Assert.Equal("attach.lpfp.muffler.01", user.Loadouts[0].Attachments.Single().AttachmentItemId);
        Assert.True(await db.InventoryItems.AnyAsync(x => x.UserId == user.Id && x.ItemId == "attach.rifle.muzzle"));
        Assert.Equal(1, await db.InventoryItems.CountAsync(x => x.UserId == user.Id && x.ItemId == "attach.lpfp.muffler.01"));
        Assert.False((await db.CatalogItems.FindAsync("attach.rifle.muzzle"))!.IsActive);
        var magazine = (await db.CatalogItems.FindAsync("attach.pistol.magazine"))!;
        Assert.Equal("Shop", magazine.AcquisitionSource); Assert.Equal(1500, magazine.PriceCoins); Assert.Equal(1, magazine.UnlockLevel);
        Assert.Equal("attach.pistol.magazine", (await db.PassRewards.SingleAsync(x => x.SeasonId == "S1" && x.PassLevel == 12)).ItemId);
        Assert.Equal("attach.lpfp.muffler.01", (await db.PassRewards.SingleAsync(x => x.SeasonId == "S1" && x.PassLevel == 4)).ItemId);
        Assert.Equal(5, await db.CatalogItems.CountAsync(x => x.ItemType == "Throwable" && x.IsActive && x.AcquisitionSource == "Shop"));
    }
    [Fact]
    public async Task LevelOneCanBuyPermanentGrenadesAndPistolMagazineAndEquipDuplicateModels()
    {
        await using var db=CreateDb();await CatalogSeeder.SeedAsync(db);await PassSeeder.SeedAsync(db);await AttachmentSystemSeeder.SeedAsync(db);
        var user=new UserAccount{Username="buyer",NormalizedUsername="BUYER",PasswordHash="test",Profile=new(){Level=1},Wallet=new(){Coins=10000}};
        user.Loadouts.Add(new PlayerLoadout{ThrowableId="throwable.standard"});db.Users.Add(user);await db.SaveChangesAsync();await CatalogSeeder.SeedAsync(db);
        long userId=user.Id;var commerce=new CommerceService(db);
        foreach(var (id,price) in new[]{("throwable.frag_02",700),("throwable.frag_03",400),("attach.pistol.magazine",1500)})
        {
            var request=new PurchaseRequest{ItemId=id,Quantity=1,IdempotencyKey="0930-"+id};
            var first=await commerce.PurchaseAsync(userId,request,default);var replay=await commerce.PurchaseAsync(userId,request,default);
            Assert.False(first.Replayed);Assert.True(replay.Replayed);Assert.Equal(price,first.TotalPriceCoins);Assert.Equal(first.Coins,replay.Coins);
        }
        Assert.Equal(7400,(await db.Wallets.SingleAsync(x=>x.UserId==userId)).Coins);
        var loadouts=new LoadoutService(db);var saved=await loadouts.UpdateAsync(userId,0,new(){PrimaryWeaponId="weapon.m4",SecondaryWeaponId="weapon.service_pistol",ThrowableIds=["throwable.frag_02","throwable.frag_02","throwable.frag_03"],ExpectedVersion=1},default);
        await CatalogSeeder.SeedAsync(db);await AttachmentSystemSeeder.SeedAsync(db);db.ChangeTracker.Clear();
        Assert.Equal(saved.ThrowableIds,(await loadouts.GetAsync(userId,0,default)).ThrowableIds);
        Assert.Equal(3,await db.Purchases.CountAsync(x=>x.UserId==userId));
    }
}
