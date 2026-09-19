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

        // weapon.m4（AKM 模型，无顶部导轨）：弹匣 stat-only + 消音器（家族放行），无瞄具行
        var m4 = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.m4").ToArray();
        Assert.Contains(m4, x => x.GetProperty("attachmentId").GetString() == "attach.rifle.magazine"
                              && x.GetProperty("slotType").GetString() == "Magazine"
                              && x.GetProperty("isImplemented").GetBoolean()
                              && x.GetProperty("calibrationKey").GetString() == "stat-only");
        Assert.Contains(m4, x => x.GetProperty("attachmentId").GetString() == "attach.lpfp.muffler.01"
                              && x.GetProperty("isImplemented").GetBoolean()
                              && x.GetProperty("calibrationKey").GetString() == "socket-v2");
        Assert.DoesNotContain(m4, x => x.GetProperty("slotType").GetString() == "Optic");

        // 家族矩阵（2026-09-02 返工）：
        // ak(M4A1)=步枪档瞄具（无高倍镜行）+重型消音器；sniper02(M82A1)=仅高倍镜、无任何消音器；转轮(pistol.05)无枪口行
        var ak = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.ak").ToArray();
        Assert.False(ak.Any(x => x.GetProperty("attachmentId").GetString() == "attach.lpw.optic.07"), "步枪族无高倍狙击镜行");
        Assert.Contains(ak, x => x.GetProperty("attachmentId").GetString() == "attach.lpw.muffler.02"
                              && x.GetProperty("isImplemented").GetBoolean());
        var m82 = rows.EnumerateArray().Where(x => x.GetProperty("weaponId").GetString() == "weapon.sniper02").ToArray();
        Assert.Contains(m82, x => x.GetProperty("attachmentId").GetString() == "attach.lpw.optic.07"
                               && x.GetProperty("isImplemented").GetBoolean());
        Assert.DoesNotContain(m82, x => x.GetProperty("slotType").GetString() == "Muzzle");
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

        // 商城购买配件（AcquisitionSource=Shop，等级 1 可购的消音器）；高倍狙击镜直发库存做档位 422 用例
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

        // 已拥有但矩阵未放行（m4=AKM 无瞄具槽；且高倍镜仅狙击）→ 422
        var blocked = await client.PutAsJsonAsync("/api/loadout/attachments", new
        {
            expectedVersion = version, weaponSlot = "Primary",
            attachments = new[] { new { attachmentSlot = "Optic", attachmentItemId = "attach.lpw.optic.07" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        using var blockedJson = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("ATTACHMENT_NOT_COMPATIBLE", blockedJson.RootElement.GetProperty("code").GetString());

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
}
