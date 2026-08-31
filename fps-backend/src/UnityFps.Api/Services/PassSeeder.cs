using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

/// <summary>
/// Seeds the S1 battle pass reward track (15 levels), achievement definitions and the
/// pass-reward attachment catalog items. Also backfills PlayerPass rows for existing
/// users and sets AcquisitionSource on weapon catalog items (Docs/17 §4.3).
/// </summary>
public static class PassSeeder
{
    public const string SeasonId = "S1";

    /// <summary>
    /// S1 奖励轨附件目录项（两把已验证武器的 3×2 配件）。IsImplemented=false：
    /// 可经通行证获得并入库存，但装配适配由 F 线（Docs/19）回填后才可装备——诚实呈现边界。
    /// </summary>
    private static readonly (string ItemId, string SlotType, string DisplayName, string AssetKey)[] AttachmentCatalog =
    [
        ("attach.rifle.optic", "Optic", "步枪光学瞄具", "attach/rifle/optic"),
        ("attach.rifle.muzzle", "Muzzle", "步枪消音器", "attach/rifle/muzzle"),
        ("attach.rifle.magazine", "Magazine", "步枪加长弹匣", "attach/rifle/magazine"),
        ("attach.pistol.optic", "Optic", "手枪光学瞄具", "attach/pistol/optic"),
        ("attach.pistol.muzzle", "Muzzle", "手枪消音器", "attach/pistol/muzzle"),
        ("attach.pistol.magazine", "Magazine", "手枪加长弹匣", "attach/pistol/magazine"),
    ];

    /// <summary>S1 奖励轨（Docs/17 §4.3 定版表；奇数级金币，2/4/6/8/10/12 级配件）.</summary>
    private static readonly (string RewardType, string? ItemId, int Coins)[] S1Rewards =
    [
        ("Coins", null, 200),                                  // 1
        ("Attachment", "attach.rifle.optic", 0),               // 2
        ("Coins", null, 300),                                  // 3
        ("Attachment", "attach.rifle.muzzle", 0),              // 4
        ("Coins", null, 300),                                  // 5
        ("Attachment", "attach.rifle.magazine", 0),            // 6
        ("Coins", null, 400),                                  // 7
        ("Attachment", "attach.pistol.optic", 0),              // 8
        ("Coins", null, 400),                                  // 9
        ("Attachment", "attach.pistol.muzzle", 0),             // 10
        ("Coins", null, 500),                                  // 11
        ("Attachment", "attach.pistol.magazine", 0),           // 12
        ("Coins", null, 500),                                  // 13
        ("Coins", null, 600),                                  // 14
        ("Coins", null, 800),                                  // 15
    ];

    /// <summary>S1 成就最小集（全部可由服务端从既有数据计算，无客户端上报；Docs/17 §4.3）.</summary>
    private static readonly (string Id, string Name, string Desc, string Metric, int Target, int PassXp, int Sort)[] S1Achievements =
    [
        ("ach.first_win", "首胜", "赢得 1 场对战", "total_wins", 1, 300, 1),
        ("ach.kills_10", "初露锋芒", "累计击杀 10 次", "total_kills", 10, 200, 2),
        ("ach.kills_50", "神枪手", "累计击杀 50 次", "total_kills", 50, 300, 3),
        ("ach.kills_200", "杀神", "累计击杀 200 次", "total_kills", 200, 500, 4),
        ("ach.match_10", "常客", "完成 10 局对战", "total_matches", 10, 200, 5),
        ("ach.single_10", "单局高手", "单局击杀 ≥10", "single_match_kills", 10, 300, 6),
        ("ach.gunsmith_win", "改装致胜", "带 ≥3 配件的武器获胜 1 场", "gunsmith_wins", 1, 400, 7),
        ("ach.level_5", "军衔晋升", "账号等级达到 5", "account_level", 5, 300, 8),
        ("ach.first_buy", "添置军火", "首次商城购买武器", "first_buy", 1, 200, 9),
        ("ach.pass_10", "通行证达人", "通行证达到 10 级", "pass_level", 10, 400, 10),
    ];

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // 分节容错保存：多实例并发启动（测试工厂并行 / 双 API 进程）时同键插入只有一方成功，
        // 败者清空挂起实体跳过该节即可——各节幂等，下次启动自愈；用户级兜底 = PassService 懒回填。
        await SeedAttachmentCatalogItemsAsync(db, cancellationToken);
        await SeedWeaponAcquisitionSourceAsync(db, cancellationToken);
        await SaveSectionTolerantAsync(db, cancellationToken);
        await SeedPassRewardsAsync(db, cancellationToken);
        await SaveSectionTolerantAsync(db, cancellationToken);
        await SeedAchievementsAsync(db, cancellationToken);
        await SaveSectionTolerantAsync(db, cancellationToken);
        await BackfillPlayerPassesAsync(db, cancellationToken);
        await SaveSectionTolerantAsync(db, cancellationToken);
    }

    private static async Task SaveSectionTolerantAsync(AppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    private static async Task SeedAttachmentCatalogItemsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.CatalogItems.ToDictionaryAsync(x => x.ItemId, ct);
        foreach (var (itemId, slotType, displayName, assetKey) in AttachmentCatalog)
        {
            if (!existing.TryGetValue(itemId, out var item))
                db.CatalogItems.Add(new CatalogItem
                {
                    ItemId = itemId, ItemType = "Attachment", SlotType = slotType, Category = "Attachment",
                    DisplayName = displayName, Description = "通行证奖励配件；装配适配由配件系统后续开放",
                    AssetKey = assetKey, PriceCoins = 0, UnlockLevel = 1, IsActive = true,
                    IsImplemented = false, CalibrationKey = "pending", AcquisitionSource = "PassReward"
                });
            else
            {
                item.SlotType = slotType; item.DisplayName = displayName; item.AssetKey = assetKey;
                item.IsImplemented = false; item.AcquisitionSource = "PassReward";
            }
        }
    }

    private static async Task SeedWeaponAcquisitionSourceAsync(AppDbContext db, CancellationToken ct)
    {
        var weapons = await db.CatalogItems.Where(x => x.ItemType == "Weapon").ToListAsync(ct);
        foreach (var item in weapons)
            item.AcquisitionSource = CatalogSeeder.InitialWeapons.Contains(item.ItemId) ? "Initial" : "Shop";
    }

    private static async Task SeedPassRewardsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.PassRewards
            .Where(x => x.SeasonId == SeasonId)
            .ToDictionaryAsync(x => x.PassLevel, ct);
        for (var level = 1; level <= S1Rewards.Length; level++)
        {
            var (rewardType, itemId, coins) = S1Rewards[level - 1];
            if (!existing.TryGetValue(level, out var row))
                db.PassRewards.Add(new PassReward
                {
                    SeasonId = SeasonId, PassLevel = level,
                    RewardType = rewardType, ItemId = itemId, CoinsAmount = coins
                });
            else
            {
                row.RewardType = rewardType; row.ItemId = itemId; row.CoinsAmount = coins;
            }
        }
    }

    private static async Task SeedAchievementsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.AchievementDefinitions.ToDictionaryAsync(x => x.AchievementId, ct);
        foreach (var (id, name, desc, metric, target, passXp, sort) in S1Achievements)
        {
            if (!existing.TryGetValue(id, out var row))
                db.AchievementDefinitions.Add(new AchievementDefinition
                {
                    AchievementId = id, DisplayName = name, Description = desc,
                    TargetMetric = metric, TargetValue = target, PassXpReward = passXp, SortOrder = sort
                });
            else
            {
                row.DisplayName = name; row.Description = desc; row.TargetMetric = metric;
                row.TargetValue = target; row.PassXpReward = passXp; row.SortOrder = sort;
            }
        }
    }

    private static async Task BackfillPlayerPassesAsync(AppDbContext db, CancellationToken ct)
    {
        var userIdsWithPass = (await db.PlayerPasses
            .Where(x => x.SeasonId == SeasonId)
            .Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        var usersWithoutPass = await db.Users
            .Where(x => !userIdsWithPass.Contains(x.Id))
            .ToListAsync(ct);
        foreach (var user in usersWithoutPass)
            db.PlayerPasses.Add(new PlayerPass
            {
                UserId = user.Id, SeasonId = SeasonId, PassLevel = 1, PassXp = 0,
                Version = 1, UpdatedAtUtc = DateTime.UtcNow
            });
    }
}
