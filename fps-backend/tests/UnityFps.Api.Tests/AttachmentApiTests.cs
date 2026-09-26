using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UnityFps.Api.Data;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>配件装配契约（Docs/21 Phase C）：矩阵查询、购买、装配校验、换枪清空（缺陷 A）.</summary>
public sealed class AttachmentApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    private readonly HttpClient client;

    public AttachmentApiTests(ApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private async Task<(string Token, long UserId)> RegisterAndLoginAsync(string prefix)
    {
        var username = prefix + "_" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString()!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(x => x.Username == username);
        return (token, user.Id);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
        => JsonDocument.Parse(await client.GetStringAsync(url)).RootElement;

    [Fact]
    public async Task CompatibilityEndpointReturnsSeededMatrix()
    {
        var (token, _) = await RegisterAndLoginAsync("compat");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var rows = await GetJsonAsync(client, "/api/loadout/compatibility");
        Assert.True(rows.GetArrayLength() > 150, $"矩阵行数不足: {rows.GetArrayLength()}");

        // weapon.m4（AKM 模型）：弹匣 stat-only + 消音器（家族放行）+ 四款正式 LPFP 瞄具
        var m4 = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.m4").ToArray();
        Assert.Contains(m4, x => x.GetProperty("attachmentId").GetString() == "attach.rifle.magazine"
                              && x.GetProperty("slotType").GetString() == "Magazine"
                              && x.GetProperty("isImplemented").GetBoolean()
                              && x.GetProperty("calibrationKey").GetString() == "stat-only");
        Assert.Contains(m4, x => x.GetProperty("attachmentId").GetString() == "attach.lpfp.muffler.01"
                              && x.GetProperty("isImplemented").GetBoolean()
                              && x.GetProperty("calibrationKey").GetString() == "socket-v2");
        foreach (var optic in new[] { "attach.lpfp.optic.01", "attach.rifle.optic", "attach.lpfp.optic.03", "attach.lpfp.optic.02" })
            Assert.Contains(m4, x => x.GetProperty("attachmentId").GetString() == optic
                                  && x.GetProperty("slotType").GetString() == "Optic"
                                  && x.GetProperty("isImplemented").GetBoolean());

        // 阶段 A 矩阵：正式 LPFP 原生枪开放四款基础瞄具（狙击族除外：仅内置高倍镜，
        // 基础瞄具不提供——2026-09-21 用户拍板）；旧 LPW 镜不再出现。
        var ak = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.ak").ToArray();
        Assert.False(ak.Any(x => x.GetProperty("attachmentId").GetString() == "attach.lpw.optic.07"), "步枪族无高倍狙击镜行");
        Assert.DoesNotContain(ak, x => x.GetProperty("slotType").GetString() == "Underbarrel");
        Assert.Contains(ak, x => x.GetProperty("attachmentId").GetString() == "attach.lpfp.muffler.01"
                              && x.GetProperty("isImplemented").GetBoolean());
        var scar = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.rifle03").ToArray();
        Assert.DoesNotContain(scar, x => x.GetProperty("slotType").GetString() == "Underbarrel");
        var m82 = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.sniper02").ToArray();
        Assert.DoesNotContain(m82, x => x.GetProperty("slotType").GetString() == "Optic");
        Assert.DoesNotContain(m82, x => x.GetProperty("slotType").GetString() == "Muzzle");
        Assert.Contains(m82, x => x.GetProperty("slotType").GetString() == "Magazine");
        var python = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.lpw.pistol.05").ToArray();
        Assert.DoesNotContain(python, x => x.GetProperty("slotType").GetString() == "Muzzle");
        Assert.Contains(python, x => x.GetProperty("slotType").GetString() == "Magazine"
                                  && x.GetProperty("isImplemented").GetBoolean());
    }

    [Fact]
    public async Task AttachmentPurchaseAndEquipValidationMatrix()
    {
        var (token, userId) = await RegisterAndLoginAsync("att");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 商城购买配件（AcquisitionSource=Shop，等级 1 可购的消音器）；旧瞄具库存行应直接判为无效
        var purchase = await client.PostAsJsonAsync("/api/shop/purchases",
            new { itemId = "attach.lpfp.muffler.01", quantity = 1, idempotencyKey = "att-buy-" + Guid.NewGuid().ToString("N") });
        Assert.True(purchase.IsSuccessStatusCode, await purchase.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.InventoryItems.Add(new PlayerInventoryItem
            {
                UserId = userId, ItemId = "attach.lpw.optic.07", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var loadout = await GetJsonAsync(client, "/api/loadout");
        var version = loadout.GetProperty("version").GetInt64();

        // 旧 ID 已从目录和矩阵清理，即使存量库存被手工写回也必须判为无效 → 422
        var blocked = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = version, weaponSlot = "Primary",
            attachments = new[] { new { attachmentSlot = "Optic", attachmentItemId = "attach.lpw.optic.07" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        using var blockedJson = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("ATTACHMENT_INVALID", blockedJson.RootElement.GetProperty("code").GetString());

        // 已拥有且已放行（消音器 socket-v1）→ 200 完整回路
        var equipped = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = version, weaponSlot = "Primary",
            attachments = new[] { new { attachmentSlot = "Muzzle", attachmentItemId = "attach.lpfp.muffler.01" } }
        });
        Assert.True(equipped.IsSuccessStatusCode, await equipped.Content.ReadAsStringAsync());
        using var equippedJson = JsonDocument.Parse(await equipped.Content.ReadAsStringAsync());
        var versionAfter = equippedJson.RootElement.GetProperty("version").GetInt64();

        // 未拥有（弹匣）→ 403
        var notOwned = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = versionAfter, weaponSlot = "Primary",
            attachments = new[] { new { attachmentSlot = "Magazine", attachmentItemId = "attach.rifle.magazine" } }
        });
        Assert.Equal(HttpStatusCode.Forbidden, notOwned.StatusCode);

        // 槽位不匹配 → 422
        var slotMismatch = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = versionAfter, weaponSlot = "Primary",
            attachments = new[] { new { attachmentSlot = "Optic", attachmentItemId = "attach.lpfp.muffler.01" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, slotMismatch.StatusCode);

        // 版本冲突 → 409
        var stale = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = versionAfter + 99, weaponSlot = "Primary", attachments = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task EquippedAttachmentRoundTripAndWeaponChangeClearsSlot()
    {
        var (token, userId) = await RegisterAndLoginAsync("equip");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 直接入库存 + 弹匣矩阵行已实现（stat-only），走完整装配回路
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.InventoryItems.Add(new PlayerInventoryItem
            {
                UserId = userId, ItemId = "attach.rifle.magazine", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var loadout = await GetJsonAsync(client, "/api/loadout");
        var version = loadout.GetProperty("version").GetInt64();

        var put = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = version, weaponSlot = "Primary",
            attachments = new[] { new { attachmentSlot = "Magazine", attachmentItemId = "attach.rifle.magazine" } }
        });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        using var putJson = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal(version + 1, putJson.RootElement.GetProperty("version").GetInt64());
        var row = putJson.RootElement.GetProperty("attachments")[0];
        Assert.Equal("Primary", row.GetProperty("weaponSlot").GetString());
        Assert.Equal("Magazine", row.GetProperty("attachmentSlot").GetString());
        Assert.Equal("attach.rifle.magazine", row.GetProperty("attachmentItemId").GetString());

        var stored = await GetJsonAsync(client, "/api/loadout/attachments");
        Assert.Equal("attach.rifle.magazine", stored.GetProperty("attachments")[0].GetProperty("attachmentItemId").GetString());

        // 缺陷 A：换主武器（m4→ak，两把均已拥有）→ Primary 槽配件被清空
        var afterVersion = stored.GetProperty("version").GetInt64();
        var change = await client.PutAsJsonAsync("/api/loadout", new
        {
            primaryWeaponId = "weapon.ak", secondaryWeaponId = "weapon.service_pistol",
            throwableId = (string?)null, expectedVersion = afterVersion
        });
        Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());
        var cleared = await GetJsonAsync(client, "/api/loadout/attachments");
        Assert.Equal(0, cleared.GetProperty("attachments").GetArrayLength());
    }

    [Fact]
    public async Task SavingPreviewedAkAndPistolEquipsTheWeaponThatWasValidated()
    {
        var (token, userId) = await RegisterAndLoginAsync("previewed");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loadout = await db.Loadouts.SingleAsync(x => x.UserId == userId);
            loadout.PrimaryWeaponId = "weapon.ak"; // M4A1 equipped; gunsmith previews AK-47 (weapon.m4).
            foreach (var itemId in new[] { "weapon.ak", "weapon.handgun02", "attach.lpfp.optic.01", "attach.pistol.magazine" })
                if (!await db.InventoryItems.AnyAsync(x => x.UserId == userId && x.ItemId == itemId))
                    db.InventoryItems.Add(new PlayerInventoryItem
                    { UserId = userId, ItemId = itemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var before = await GetJsonAsync(client, "/api/loadout");
        var akSave = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = before.GetProperty("version").GetInt64(), weaponSlot = "Primary",
            weaponItemId = "weapon.m4",
            attachments = new[] { new { attachmentSlot = "Optic", attachmentItemId = "attach.lpfp.optic.01" } }
        });
        Assert.True(akSave.IsSuccessStatusCode, await akSave.Content.ReadAsStringAsync());
        var afterAk = await GetJsonAsync(client, "/api/loadout");
        Assert.Equal("weapon.m4", afterAk.GetProperty("primaryWeaponId").GetString());
        var pistolSave = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = afterAk.GetProperty("version").GetInt64(), weaponSlot = "Secondary",
            weaponItemId = "weapon.handgun02",
            attachments = new[] { new { attachmentSlot = "Magazine", attachmentItemId = "attach.pistol.magazine" } }
        });
        Assert.True(pistolSave.IsSuccessStatusCode, await pistolSave.Content.ReadAsStringAsync());
        var final = await GetJsonAsync(client, "/api/loadout");
        Assert.Equal("weapon.handgun02", final.GetProperty("secondaryWeaponId").GetString());
        var attachments = final.GetProperty("attachments").EnumerateArray().ToArray();
        Assert.Contains(attachments, x => x.GetProperty("attachmentItemId").GetString() == "attach.lpfp.optic.01");
        Assert.Contains(attachments, x => x.GetProperty("attachmentItemId").GetString() == "attach.pistol.magazine");
    }

    [Fact]
    public async Task SeederRemovesStaleAttachmentSelectionsNoLongerInCompatibilityMatrix()
    {
        var (_, userId) = await RegisterAndLoginAsync("stalegrip");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loadout = await db.Loadouts.Include(x => x.Attachments).SingleAsync(x => x.UserId == userId);
        loadout.PrimaryWeaponId = "weapon.ak"; // Rifle02/M4A1: factory vertical grip.
        loadout.Attachments.Add(new PlayerLoadoutAttachment
        {
            WeaponSlot = "Primary",
            AttachmentSlot = "Underbarrel",
            AttachmentItemId = "attach.lpw.grip.01"
        });
        var versionBefore = loadout.Version;
        await db.SaveChangesAsync();

        await AttachmentSystemSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var cleaned = await db.Loadouts.Include(x => x.Attachments).SingleAsync(x => x.UserId == userId);
        Assert.DoesNotContain(cleaned.Attachments, x => x.AttachmentItemId == "attach.lpw.grip.01");
        Assert.Equal(versionBefore + 1, cleaned.Version);
    }

    [Fact]
    public async Task SniperMagazineMigrationPreservesOwnedAndEquippedOldMagazine()
    {
        var (_, userId) = await RegisterAndLoginAsync("snipermag");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loadout = await db.Loadouts.Include(x => x.Attachments).SingleAsync(x => x.UserId == userId);
        loadout.PrimaryWeaponId = "weapon.sniper03";
        db.InventoryItems.Add(new PlayerInventoryItem
        {
            UserId = userId, ItemId = "attach.rifle.magazine", Quantity = 1, AcquiredAtUtc = DateTime.UtcNow
        });
        loadout.Attachments.Add(new PlayerLoadoutAttachment
        {
            WeaponSlot = "Primary", AttachmentSlot = "Magazine", AttachmentItemId = "attach.rifle.magazine"
        });
        await db.SaveChangesAsync();

        await AttachmentSystemSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
        await AttachmentSystemSeeder.SeedAsync(db); // idempotent on later boots
        db.ChangeTracker.Clear();

        var owned = await db.InventoryItems.Where(x => x.UserId == userId).ToListAsync();
        Assert.Single(owned, x => x.ItemId == "attach.sniper.magazine");
        Assert.Contains(owned, x => x.ItemId == "attach.rifle.magazine");
        var migrated = await db.Loadouts.Include(x => x.Attachments).SingleAsync(x => x.UserId == userId);
        Assert.Contains(migrated.Attachments, x => x.AttachmentItemId == "attach.sniper.magazine");
        Assert.DoesNotContain(migrated.Attachments, x => x.AttachmentItemId == "attach.rifle.magazine");
        Assert.True(await db.AttachmentCompat.AnyAsync(x => x.WeaponItemId == "weapon.sniper03"
            && x.AttachmentItemId == "attach.sniper.magazine" && x.IsImplemented));
        Assert.False(await db.AttachmentCompat.AnyAsync(x => x.WeaponItemId == "weapon.sniper03"
            && x.AttachmentItemId == "attach.rifle.magazine"));
    }
}
