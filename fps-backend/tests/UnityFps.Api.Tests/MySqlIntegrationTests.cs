using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using UnityFps.Api.Data;
using UnityFps.Api.Services;
using UnityFps.Api.Features;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 可选的真实 MySQL 回归探针。未提供凭据时跳过，不会猜测密码或触碰开发库。
/// </summary>
public sealed class MySqlIntegrationTests
{
    [MySqlConfiguredFact]
    public async Task FourNativePistols_BothOnePowerOpticsAndMagazinePersistAcrossSqlReload()
    {
        var connection = Environment.GetEnvironmentVariable("UNITY_FPS_MYSQL_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        Assert.EndsWith("_test", new MySqlConnectionStringBuilder(connection).Database, StringComparison.OrdinalIgnoreCase);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection)).Options);
        await db.Database.MigrateAsync();
        await CatalogSeeder.SeedAsync(db); await PassSeeder.SeedAsync(db); await AttachmentSystemSeeder.SeedAsync(db);
        var name = "optic0930" + Guid.NewGuid().ToString("N")[..10];
        var user = new UserAccount { Username = name, NormalizedUsername = name.ToUpperInvariant(), PasswordHash = "fixture",
            Profile = new() { Level = 10 }, Wallet = new() { Coins = 20000 } };
        user.Loadouts.Add(new PlayerLoadout { ThrowableId = "throwable.standard" });
        foreach(var weapon in new[]{"weapon.service_pistol","weapon.handgun02","weapon.handgun03","weapon.handgun04"})
            user.Inventory.Add(new PlayerInventoryItem { ItemId = weapon, Quantity = 1 });
        db.Users.Add(user); await db.SaveChangesAsync();
        long userId = user.Id;
        try
        {
            await CatalogSeeder.SeedAsync(db);
            var commerce = new CommerceService(db);
            foreach(var item in new[]{"attach.rifle.optic","attach.lpfp.optic.02","attach.pistol.magazine"})
                await commerce.PurchaseAsync(userId,new(){ItemId=item,Quantity=1,IdempotencyKey=name+item},default);
            var loadouts = new LoadoutService(db);
            foreach(var weapon in new[]{"weapon.service_pistol","weapon.handgun02","weapon.handgun03","weapon.handgun04"})
            {
                var current=await loadouts.GetAsync(userId,0,default);
                await loadouts.UpdateAsync(userId,0,new(){PrimaryWeaponId="weapon.m4",SecondaryWeaponId=weapon,ThrowableIds=current.ThrowableIds,ExpectedVersion=current.Version},default);
                foreach(var optic in new[]{"attach.rifle.optic","attach.lpfp.optic.02"})
                {
                    current=await loadouts.GetAsync(userId,0,default);
                    await loadouts.UpdateAttachmentsAsync(userId,0,new(){ExpectedVersion=current.Version,WeaponSlot="Secondary",WeaponItemId=weapon,
                        Attachments=[new(){AttachmentSlot="Optic",AttachmentItemId=optic},new(){AttachmentSlot="Magazine",AttachmentItemId="attach.pistol.magazine"}]},default);
                    db.ChangeTracker.Clear();
                    var restored=await loadouts.GetAsync(userId,0,default);
                    Assert.Equal(weapon,restored.SecondaryWeaponId);
                    Assert.Contains(restored.Attachments,x=>x.AttachmentItemId==optic&&x.WeaponSlot=="Secondary");
                    Assert.Contains(restored.Attachments,x=>x.AttachmentItemId=="attach.pistol.magazine");
                }
                current=await loadouts.GetAsync(userId,0,default);
                await loadouts.UpdateAttachmentsAsync(userId,0,new(){ExpectedVersion=current.Version,WeaponSlot="Secondary",Attachments=[]},default);
                db.ChangeTracker.Clear();
                Assert.Empty((await loadouts.GetAsync(userId,0,default)).Attachments);
            }
        }
        finally
        {
            db.ChangeTracker.Clear();db.Users.Remove(await db.Users.SingleAsync(x=>x.Id==userId));await db.SaveChangesAsync();
        }
    }

    [MySqlConfiguredFact]
    public async Task TestDatabase_MustHaveTestSuffix_AndSupportMigrationRoundTrip()
    {
        var connection = Environment.GetEnvironmentVariable("UNITY_FPS_MYSQL_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        var builder = new MySqlConnectionStringBuilder(connection);
        Assert.EndsWith("_test", builder.Database, StringComparison.OrdinalIgnoreCase);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection))
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        Assert.True(await db.Database.CanConnectAsync());
        Assert.Contains("20260930060000_AddThrowableSlots", await db.Database.GetAppliedMigrationsAsync());

        // An isolated fixture verifies actual relational persistence, not just model discovery.
        await CatalogSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db);
        await AttachmentSystemSeeder.SeedAsync(db);
        var name = "sql0930" + Guid.NewGuid().ToString("N")[..12];
        var user = new UserAccount { Username = name, NormalizedUsername = name.ToUpperInvariant(), PasswordHash = "fixture",
            Profile = new(), Wallet = new() { Coins = 10000 } };
        user.Loadouts.Add(new PlayerLoadout { ThrowableId = "throwable.standard" });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        try
        {
            await CatalogSeeder.SeedAsync(db);
            var commerce = new CommerceService(db);
            foreach (var item in new[] { "throwable.frag_02", "throwable.frag_03", "attach.pistol.magazine" })
                await commerce.PurchaseAsync(user.Id, new PurchaseRequest { ItemId = item, Quantity = 1, IdempotencyKey = name + item }, default);
            var loadouts = new LoadoutService(db);
            var sets = new string?[][] { ["throwable.frag_02", "throwable.frag_02", null], [null, null, null], ["throwable.frag_03", "throwable.smoke", "throwable.flash"] };
            for (int bag = 0; bag < 3; bag++)
            {
                var current = await loadouts.GetAsync(user.Id, bag, default);
                await loadouts.UpdateAsync(user.Id, bag, new LoadoutRequest { PrimaryWeaponId = "weapon.m4", SecondaryWeaponId = "weapon.service_pistol", ThrowableIds = sets[bag], ExpectedVersion = current.Version }, default);
            }
            await CatalogSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();
            for (int bag = 0; bag < 3; bag++) Assert.Equal(sets[bag], (await loadouts.GetAsync(user.Id, bag, default)).ThrowableIds);
            Assert.Equal(7400, (await db.Wallets.SingleAsync(x => x.UserId == user.Id)).Coins);
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.Users.Remove(await db.Users.SingleAsync(x => x.Id == user.Id));
            await db.SaveChangesAsync();
        }
    }
}

public sealed class MySqlConfiguredFactAttribute : FactAttribute
{
    public MySqlConfiguredFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_FPS_MYSQL_TEST_CONNECTION"))) Skip = "需要显式 MySQL 测试库凭据";
    }
}
