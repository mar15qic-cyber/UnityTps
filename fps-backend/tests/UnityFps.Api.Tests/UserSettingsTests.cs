using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 每玩家设置偏好（2026-09-07）：键值对按用户隔离；PUT 合并语义（只 upsert 请求中出现的键，
/// 未提及的键保持不变——多端写互不覆盖整份配置）；越界校验返回 422。
/// </summary>
public sealed class UserSettingsTests
{
    [Fact]
    public async Task Get_EmptyInitially_And_IsolatedPerUser()
    {
        await using var db = CreateDb();
        var service = new UserSettingsService(db);
        var a = AddUser(db, "user-a");
        var b = AddUser(db, "user-b");

        var empty = await service.GetAsync(a.Id, CancellationToken.None);
        Assert.Empty(empty.Values);

        await service.SaveAsync(a.Id, new Dictionary<string, string> { ["volume.master"] = "0.5" },
            CancellationToken.None);

        var forA = await service.GetAsync(a.Id, CancellationToken.None);
        var forB = await service.GetAsync(b.Id, CancellationToken.None);
        Assert.Equal("0.5", forA.Values["volume.master"]);
        Assert.Empty(forB.Values);
    }

    [Fact]
    public async Task Save_Upserts_And_MergesWithoutDroppingUntouchedKeys()
    {
        await using var db = CreateDb();
        var service = new UserSettingsService(db);
        var user = AddUser(db, "user-a");
        await service.SaveAsync(user.Id, new Dictionary<string, string>
        {
            ["volume.master"] = "0.5",
            ["key.forward"] = "W",
        }, CancellationToken.None);

        var second = await service.SaveAsync(user.Id, new Dictionary<string, string>
        {
            ["volume.master"] = "0.8",   // 覆盖
            ["input.sensitivity"] = "1.2", // 新增
        }, CancellationToken.None);

        Assert.Equal("0.8", second.Values["volume.master"]);
        Assert.Equal("1.2", second.Values["input.sensitivity"]);
        Assert.Equal("W", second.Values["key.forward"]); // 未提及的键保持不变
    }

    [Fact]
    public async Task Save_RejectsInvalidKeysValuesAndOversizeBatches()
    {
        await using var db = CreateDb();
        var service = new UserSettingsService(db);
        var user = AddUser(db, "user-a");

        await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(user.Id,
            new Dictionary<string, string> { [new string('k', 65)] = "v" }, CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(user.Id,
            new Dictionary<string, string> { ["ok"] = new string('v', 257) }, CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(user.Id,
            Enumerable.Range(0, 129).ToDictionary(i => "k" + i, i => "v"), CancellationToken.None));

        var ok = await service.SaveAsync(user.Id,
            Enumerable.Range(0, 128).ToDictionary(i => "k" + i, i => "v"), CancellationToken.None);
        Assert.Equal(128, ok.Values.Count);
    }

    [Fact]
    public async Task Save_TotalLimitPersistsAcrossRequests_AndAllowsExistingKeyUpdates()
    {
        await using var db = CreateDb();
        var user = AddUser(db, "bounded-user");
        var service = new UserSettingsService(db);
        await service.SaveAsync(user.Id, Enumerable.Range(0, 128).ToDictionary(i => "k" + i, _ => "v"));
        var error = await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(user.Id,
            new Dictionary<string, string> { ["overflow"] = "v", ["k0"] = "should-not-write" }));
        Assert.Equal("SETTINGS_TOO_MANY", error.Code);
        var original = await service.GetAsync(user.Id);
        Assert.Equal(128, original.Values.Count);
        Assert.Equal("v", original.Values["k0"]);
        var updated = await service.SaveAsync(user.Id, new Dictionary<string, string> { ["k0"] = "changed" });
        Assert.Equal("changed", updated.Values["k0"]);
        Assert.Equal(128, updated.Values.Count);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserAccount AddUser(AppDbContext db, string name)
    {
        var user = new UserAccount
        {
            Username = name,
            NormalizedUsername = name.ToUpperInvariant(),
            PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadouts = { new PlayerLoadout { UpdatedAtUtc = DateTime.UtcNow } },
            Wallet = new PlayerWallet { Coins = CatalogSeeder.InitialCoins, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }
}
