using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 击杀竞赛三币结算（Docs/17 §4.5/§4.6）：幂等、数值裁剪、通行证扫发、成就收敛与对账快照。
/// 期望值全部由公式手工推导（IProgressionRules 基准），与实现解耦。
/// </summary>
public sealed class MatchSettlementTests
{
    [Fact]
    public async Task SettlementAwardsThreeCurrenciesAndGrantsLevelOneReward()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());

        var result = await service.SubmitAsync(user.Id, Request("match-basic-0001", kills: 0, deaths: 3, isWin: false), CancellationToken.None);

        // 0 杀败局：XP 100、金币 50、PassXP 200；Lv1 奖励 200 金币随首次结算扫发
        Assert.Equal(100, result.XpEarned);
        Assert.Equal(1, result.LevelUps);
        Assert.Equal(50, result.CoinsEarned);
        Assert.Equal(200, result.PassXpEarned);
        Assert.Equal(CatalogSeeder.InitialCoins + 50 + 200, result.Coins);
        Assert.Equal(1, result.PassLevel);
        Assert.Equal(200, result.PassXp);
        Assert.Equal(300, result.PassXpToNextLevel);
        Assert.False(result.Replayed);
        Assert.Empty(result.NewAttachments);
        Assert.Empty(result.UnlockedAchievements);
        var levelOne = Assert.Single(result.PassLevelUps);
        Assert.Equal(1, levelOne.Level);
        Assert.Equal("Coins", levelOne.RewardType);
        Assert.Equal(200, levelOne.CoinsAmount);

        var record = await db.Matches.SingleAsync();
        Assert.Equal(100, record.XpEarned);
        Assert.Equal(50, record.CoinsEarned);
        Assert.Equal(200, record.PassXpEarned);
        Assert.Equal("match-basic-0001", record.ClientMatchId);
        Assert.Equal(new[] { "MatchReward", "PassReward" },
            (await db.WalletLedger.OrderBy(x => x.Id).ToListAsync()).Select(x => x.Reason).ToArray());
    }

    [Fact]
    public async Task SettlementUnlocksAchievementsGrantsAttachmentAndConverges()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());

        // 20 杀胜局：XP 700（3 升级）、金币 350、PassXP 200 + 成就 800（首胜/击杀10/单局10）
        // → 通行证 Lv3（1000：300+350 后余 350）、Lv2 奖励 = attach.rifle.optic
        var result = await service.SubmitAsync(user.Id, Request("match-win-0001", kills: 20, deaths: 5, isWin: true), CancellationToken.None);

        Assert.Equal(700, result.XpEarned);
        Assert.Equal(3, result.LevelUps);
        Assert.Equal(350, result.CoinsEarned);
        Assert.Equal(1000, result.PassXpEarned); // 200 结算 + 800 成就
        Assert.Equal(3, result.PassLevel);
        Assert.Equal(350, result.PassXp);
        // 金币 = 5000 + 350(对局) + 200(Lv1) + 300(Lv3)；Lv2 = 配件
        Assert.Equal(CatalogSeeder.InitialCoins + 350 + 200 + 300, result.Coins);
        Assert.Equal(new[] { "attach.rifle.optic" }, result.NewAttachments);
        Assert.Equal(3, result.PassLevelUps.Length);

        var unlockedIds = result.UnlockedAchievements.Select(x => x.AchievementId).ToHashSet();
        Assert.Equal(new HashSet<string> { "ach.first_win", "ach.kills_10", "ach.single_10" }, unlockedIds);

        var inventory = await db.InventoryItems.ToListAsync();
        Assert.Equal("attach.rifle.optic", Assert.Single(inventory).ItemId);
        Assert.Equal(3, await db.PassRewardGrants.CountAsync());
        var ledger = await db.WalletLedger.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(350, ledger.Single(x => x.Reason == "MatchReward").DeltaCoins);
        Assert.Equal(500, ledger.Where(x => x.Reason == "PassReward").Sum(x => x.DeltaCoins));

        var achievements = await db.PlayerAchievements.ToListAsync();
        Assert.Equal(10, achievements.Count);
        Assert.Equal(3, achievements.Count(x => x.UnlockedAtUtc != null));
        Assert.All(achievements.Where(x => x.UnlockedAtUtc != null), x => Assert.True(x.GrantedPassXp > 0));
        // 未解锁成就也记录进度
        Assert.Equal(20, achievements.Single(x => x.AchievementId == "ach.kills_50").Progress);
        Assert.Equal(1, achievements.Single(x => x.AchievementId == "ach.match_10").Progress);
    }

    [Fact]
    public async Task SettlementIsIdempotentByClientMatchId()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());
        var payload = Request("match-idem-0001", kills: 20, deaths: 5, isWin: true);

        var first = await service.SubmitAsync(user.Id, payload, CancellationToken.None);
        var replay = await service.SubmitAsync(user.Id, payload, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.XpEarned, replay.XpEarned);
        Assert.Equal(first.Coins, replay.Coins); // 重放不改钱包
        Assert.Equal(1, await db.Matches.CountAsync());
        Assert.Equal(3, await db.PassRewardGrants.CountAsync()); // 发奖恰好一次
        Assert.Single(await db.InventoryItems.Where(x => x.ItemId == "attach.rifle.optic").ToListAsync());
    }

    [Fact]
    public async Task SettlementRejectsSameClientMatchIdWithDifferentPayload()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());

        await service.SubmitAsync(user.Id, Request("match-conflict-1", kills: 20, deaths: 5, isWin: true), CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<ApiException>(() =>
            service.SubmitAsync(user.Id, Request("match-conflict-1", kills: 10, deaths: 5, isWin: true), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(ApiErrorCodes.MatchIdempotencyConflict, conflict.Code);
    }

    [Fact]
    public async Task SettlementRejectsOutOfRangePayloadWith422()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());

        var tooManyKills = await Assert.ThrowsAsync<ApiException>(() =>
            service.SubmitAsync(user.Id, Request("match-range-0001", kills: 31, deaths: 0, isWin: true), CancellationToken.None));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, tooManyKills.StatusCode);
        Assert.Equal(ApiErrorCodes.MatchPayloadRejected, tooManyKills.Code);

        var tooLong = await Assert.ThrowsAsync<ApiException>(() =>
            service.SubmitAsync(user.Id, Request("match-range-0002", kills: 5, deaths: 0, duration: 901, isWin: false), CancellationToken.None));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal(ApiErrorCodes.MatchPayloadRejected, tooLong.Code);
    }

    [Fact]
    public async Task PassRewardsAreGrantedExactlyOncePerLevelAcrossSettlements()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());

        // 三局 0 杀败局：PassXP 200/局 → 第 2 局后 Lv2（400-300），第 3 局 300<350 不升级
        for (var i = 1; i <= 3; i++)
            await service.SubmitAsync(user.Id, Request($"match-once-000{i}", kills: 0, deaths: 1, isWin: false), CancellationToken.None);

        Assert.Equal(2, await db.PassRewardGrants.CountAsync()); // Lv1 + Lv2
        var attachment = await db.InventoryItems.Where(x => x.ItemId.StartsWith("attach.")).ToListAsync();
        Assert.Equal("attach.rifle.optic", Assert.Single(attachment).ItemId);
        // 金币 = 5000 + 3×50(对局) + 200(Lv1)
        Assert.Equal(CatalogSeeder.InitialCoins + 150 + 200,
            (await db.Wallets.SingleAsync(x => x.UserId == user.Id)).Coins);
    }

    [Fact]
    public async Task GetPassReturnsSeededTrackAchievementsAndGrantFlags()
    {
        await using var db = CreateDb();
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new MatchService(db, new DemoProgressionRules());
        var passService = new PassService(db, new DemoProgressionRules());

        var before = await passService.GetPassAsync(user.Id, CancellationToken.None);
        Assert.Equal(PassSeeder.SeasonId, before.SeasonId);
        Assert.Equal(1, before.Level);
        Assert.Equal(15, before.Rewards.Length);
        Assert.Equal(10, before.Achievements.Length);
        Assert.All(before.Rewards, r => Assert.False(r.Granted));
        Assert.All(before.Achievements, a => { Assert.False(a.Unlocked); Assert.Equal(0, a.Progress); });

        await service.SubmitAsync(user.Id, Request("match-pass-0001", kills: 20, deaths: 5, isWin: true), CancellationToken.None);
        var after = await passService.GetPassAsync(user.Id, CancellationToken.None);

        Assert.Equal(3, after.Level);
        Assert.Equal(15, after.Rewards.Length);
        Assert.Equal(3, after.Rewards.Count(r => r.Granted));
        Assert.True(after.Rewards.Single(r => r.Level == 2).Granted);
        Assert.False(after.Rewards.Single(r => r.Level == 4).Granted);
        Assert.Equal(new HashSet<string> { "ach.first_win", "ach.kills_10", "ach.single_10" },
            after.Achievements.Where(a => a.Unlocked).Select(a => a.Id).ToHashSet());
    }

    [Fact]
    public async Task ShopCatalogExcludesInitialWeaponsAndPassRewardAttachments()
    {
        await using var db = CreateDb();
        await CatalogSeeder.SeedAsync(db);
        await PassSeeder.SeedAsync(db);
        var user = AddUser(db);
        var service = new CommerceService(db);

        var catalog = await service.GetCatalogAsync(user.Id, CancellationToken.None);

        Assert.Equal(36, catalog.Items.Length); // 39 武器 − 3 初始枪；配件不进商城
        Assert.DoesNotContain(catalog.Items, x => CatalogSeeder.InitialWeapons.Contains(x.ItemId));
        Assert.DoesNotContain(catalog.Items, x => x.ItemId.StartsWith("attach."));
    }

    private static MatchSubmissionRequest Request(string clientMatchId, int kills, int deaths, bool isWin, int duration = 300) =>
        new() { ClientMatchId = clientMatchId, Kills = kills, Deaths = deaths, DurationSeconds = duration, IsWin = isWin };

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserAccount AddUser(AppDbContext db)
    {
        var user = new UserAccount
        {
            Username = Guid.NewGuid().ToString("N")[..12],
            NormalizedUsername = Guid.NewGuid().ToString("N"),
            PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadout = new PlayerLoadout { UpdatedAtUtc = DateTime.UtcNow },
            Wallet = new PlayerWallet { Coins = CatalogSeeder.InitialCoins, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }
}
