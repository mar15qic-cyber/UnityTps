using System.Data;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>
/// 击杀竞赛结算（Docs/17 §4.5/§4.6）：ClientMatchId 幂等 + 三币（账号 XP / 金币 / 通行证经验）
/// + 通行证升级自动发奖 + 成就解锁（PassXP 收敛循环）。事务体经 CreateExecutionStrategy 执行——
/// EnableRetryOnFailure 与显式事务唯一兼容组合；委托开头 Clear() 保证整体可重放。
/// </summary>
public sealed class MatchService(AppDbContext db, IProgressionRules rules)
{
    /// <summary>成就→通行证升级再触发成就的收敛上限（防死循环，Docs/17 风险 #1）.</summary>
    private const int ConvergenceMaxRounds = 3;

    public async Task<MatchResultDto> SubmitAsync(long userId, MatchSubmissionRequest request, CancellationToken cancellationToken)
    {
        // 1. 幂等预检：同 (UserId, ClientMatchId) 已结算 → 载荷一致则重放，否则 409
        var existing = await db.Matches.AsNoTracking().SingleOrDefaultAsync(
            x => x.UserId == userId && x.ClientMatchId == request.ClientMatchId, cancellationToken);
        if (existing is not null)
            return await ReplayOrConflictAsync(userId, request, existing, cancellationToken);

        // 2. 数值校验（kills ≤ 30、duration ≤ 900s；超限 422 拒绝，不做静默钳制——Docs/17 §4.5）
        rules.ValidateMatchPayload(request.Kills, request.DurationSeconds);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // 瞬时故障重试时丢弃上一轮挂起实体，保证委托整体可重放
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            try
            {
                var result = await SettleAsync(userId, request, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                var winner = await db.Matches.AsNoTracking().SingleOrDefaultAsync(
                    x => x.UserId == userId && x.ClientMatchId == request.ClientMatchId, cancellationToken);
                if (winner is null) throw; // 非幂等冲突（如瞬时故障）→ 交还执行策略判定是否重试
                return await ReplayOrConflictAsync(userId, request, winner, cancellationToken);
            }
        });
    }

    /// <summary>结算事务体：固定锁顺序 Profile → Wallet → Pass → Achievement → MatchRecord（Docs/17 §4.5）。调用方负责 SaveChanges + Commit。</summary>
    private async Task<MatchResultDto> SettleAsync(long userId, MatchSubmissionRequest request, CancellationToken ct)
    {
        var user = await db.Users
            .Include(x => x.Profile).Include(x => x.Wallet).Include(x => x.Passes)
            .SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        var profile = user.Profile!;
        var wallet = user.Wallet ?? throw new ApiException(StatusCodes.Status404NotFound, "WALLET_MISSING", "钱包未初始化");
        var pass = user.Passes.FirstOrDefault(x => x.SeasonId == PassSeeder.SeasonId)
            ?? db.PlayerPasses.Add(new PlayerPass
            {
                UserId = userId, SeasonId = PassSeeder.SeasonId, PassLevel = 1, PassXp = 0,
                Version = 1, UpdatedAtUtc = DateTime.UtcNow
            }).Entity;

        var achievementDefs = await db.AchievementDefinitions.OrderBy(x => x.SortOrder).ToListAsync(ct);
        var playerAchievements = await db.PlayerAchievements
            .Where(x => x.UserId == userId).ToDictionaryAsync(x => x.AchievementId, ct);
        var passRewards = await db.PassRewards.Where(x => x.SeasonId == PassSeeder.SeasonId)
            .ToDictionaryAsync(x => x.PassLevel, ct);
        var grantedLevels = (await db.PassRewardGrants
            .Where(x => x.UserId == userId && x.SeasonId == PassSeeder.SeasonId)
            .Select(x => x.PassLevel).ToListAsync(ct)).ToHashSet();

        var xpEarned = rules.GetMatchXp(request.Kills, request.IsWin);
        var coinsEarned = rules.GetMatchCoins(request.Kills, request.IsWin);
        var passXpEarned = rules.GetMatchPassXp();

        // 账号 XP + 升级（升级发技能点是遗留行为：Docs/17 §4.9 本轮冻结不删）
        profile.Xp += xpEarned;
        var levelUps = 0;
        while (profile.Xp >= rules.GetXpToNextLevel(profile.Level))
        {
            profile.Xp -= rules.GetXpToNextLevel(profile.Level);
            profile.Level++;
            profile.SkillPoints++;
            levelUps++;
        }
        profile.UpdatedAtUtc = DateTime.UtcNow;

        // 金币（MatchReward 流水；对账锚点=每局结算恰一条）
        wallet.Coins += coinsEarned;
        wallet.UpdatedAtUtc = DateTime.UtcNow;
        db.WalletLedger.Add(new WalletLedgerEntry
        {
            UserId = userId, DeltaCoins = coinsEarned, BalanceAfter = wallet.Coins,
            Reason = "MatchReward", ReferenceId = request.ClientMatchId, CreatedAtUtc = DateTime.UtcNow
        });

        // 通行证经验 + 成就→升级收敛循环
        pass.PassXp += passXpEarned;
        var unlockedAchievements = new List<UnlockedAchievementDto>();
        for (var round = 0; round < ConvergenceMaxRounds; round++)
        {
            while (pass.PassXp >= rules.GetPassXpToNextLevel(pass.PassLevel) && pass.PassLevel < rules.MaxPassLevel)
            {
                pass.PassXp -= rules.GetPassXpToNextLevel(pass.PassLevel);
                pass.PassLevel++;
            }
            pass.UpdatedAtUtc = DateTime.UtcNow;

            var stats = await ComputeStatsAsync(userId, request, profile.Level, pass.PassLevel, ct);
            var achievementPassXp = 0;
            foreach (var def in achievementDefs)
            {
                if (!playerAchievements.TryGetValue(def.AchievementId, out var pa))
                {
                    pa = new PlayerAchievement { UserId = userId, AchievementId = def.AchievementId };
                    db.PlayerAchievements.Add(pa);
                    playerAchievements[def.AchievementId] = pa;
                }
                if (pa.UnlockedAtUtc is not null) continue; // GrantedPassXp 一次性兜底

                pa.Progress = Math.Max(pa.Progress, ComputeAchievementProgress(def.TargetMetric, stats));
                if (pa.Progress < def.TargetValue) continue;

                pa.UnlockedAtUtc = DateTime.UtcNow;
                pa.GrantedPassXp = def.PassXpReward;
                pass.PassXp += def.PassXpReward;
                passXpEarned += def.PassXpReward;
                achievementPassXp += def.PassXpReward;
                unlockedAchievements.Add(new UnlockedAchievementDto(def.AchievementId, def.DisplayName, def.PassXpReward));
            }
            if (achievementPassXp == 0) break;
        }

        // 扫发 1..当前等级 的未发放奖励（幂等兜底 = PlayerPassRewardGrant 复合主键）
        var (passLevelUps, newAttachments) = await GrantDuePassRewardsAsync(
            userId, wallet, pass, passRewards, grantedLevels, ct);

        // 结算快照（三币实发 + ClientMatchId 幂等键）
        db.Matches.Add(new MatchRecord
        {
            UserId = userId, Kills = request.Kills, Deaths = request.Deaths, Score = 0,
            XpEarned = xpEarned, CoinsEarned = coinsEarned, PassXpEarned = passXpEarned,
            IsWin = request.IsWin, ClientMatchId = request.ClientMatchId, PlayedAtUtc = DateTime.UtcNow
        });

        return BuildResult(xpEarned, levelUps, wallet.Coins, coinsEarned, passXpEarned, pass,
            passLevelUps, newAttachments, unlockedAchievements, replayed: false, profile, user.Username);
    }

    /// <summary>扫发通行证奖励：配件写库存（仅目录存在且未拥有），金币写钱包 + PassReward 流水。</summary>
    private async Task<(List<PassLevelUpDto> LevelUps, List<string> NewAttachments)> GrantDuePassRewardsAsync(
        long userId, PlayerWallet wallet, PlayerPass pass,
        Dictionary<int, PassReward> passRewards, HashSet<int> grantedLevels, CancellationToken ct)
    {
        var levelUps = new List<PassLevelUpDto>();
        var newAttachments = new List<string>();
        for (var level = 1; level <= pass.PassLevel; level++)
        {
            if (!passRewards.TryGetValue(level, out var reward) || grantedLevels.Contains(level)) continue;
            grantedLevels.Add(level);
            db.PassRewardGrants.Add(new PlayerPassRewardGrant
            {
                UserId = userId, SeasonId = pass.SeasonId, PassLevel = level, GrantedAtUtc = DateTime.UtcNow
            });

            if (reward.RewardType == "Attachment" && reward.ItemId is not null)
            {
                var owned = await db.InventoryItems.AnyAsync(x => x.UserId == userId && x.ItemId == reward.ItemId, ct);
                if (!owned)
                {
                    var catalogItem = await db.CatalogItems.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.ItemId == reward.ItemId, ct);
                    // 目录缺项时只记发放不进库存：配置缺口不阻断结算，扫发幂等保证不重复补发
                    if (catalogItem is not null)
                    {
                        db.InventoryItems.Add(new PlayerInventoryItem
                        {
                            UserId = userId, ItemId = reward.ItemId, Quantity = 1, AcquiredAtUtc = DateTime.UtcNow
                        });
                        newAttachments.Add(reward.ItemId);
                    }
                }
            }
            else if (reward.RewardType == "Coins" && reward.CoinsAmount > 0)
            {
                wallet.Coins += reward.CoinsAmount;
                wallet.UpdatedAtUtc = DateTime.UtcNow;
                db.WalletLedger.Add(new WalletLedgerEntry
                {
                    UserId = userId, DeltaCoins = reward.CoinsAmount, BalanceAfter = wallet.Coins,
                    Reason = "PassReward", ReferenceId = $"pass.{pass.SeasonId}.{level}", CreatedAtUtc = DateTime.UtcNow
                });
            }
            levelUps.Add(new PassLevelUpDto(level, reward.RewardType, reward.ItemId, reward.CoinsAmount));
        }
        return (levelUps, newAttachments);
    }

    /// <summary>成就统计量（全部服务端计算；当前局未落库，按手动累加计入）.</summary>
    private async Task<MatchStats> ComputeStatsAsync(long userId, MatchSubmissionRequest request, int accountLevel, int passLevel, CancellationToken ct)
    {
        var totalKills = await db.Matches.Where(x => x.UserId == userId).SumAsync(x => (long)x.Kills, ct) + request.Kills;
        var totalWins = await db.Matches.CountAsync(x => x.UserId == userId && x.IsWin, ct) + (request.IsWin ? 1 : 0);
        var totalMatches = await db.Matches.CountAsync(x => x.UserId == userId, ct) + 1;
        var hasPurchase = await db.Purchases.AnyAsync(x => x.UserId == userId, ct);
        // gunsmith_wins 近似：结算时读当前配装置 ≥3 配件且本局获胜（服务端可算，无客户端上报）
        var loadoutId = await db.Loadouts.Where(x => x.UserId == userId).Select(x => x.Id).FirstOrDefaultAsync(ct);
        var attachmentCount = loadoutId == 0 ? 0
            : await db.LoadoutAttachments.CountAsync(x => x.LoadoutId == loadoutId, ct);
        return new MatchStats(totalKills, totalWins, totalMatches, request.Kills, accountLevel, passLevel,
            hasPurchase, request.IsWin && attachmentCount >= 3 ? 1 : 0);
    }

    private static int ComputeAchievementProgress(string metric, MatchStats stats) => metric switch
    {
        "total_kills" => (int)Math.Min(stats.TotalKills, int.MaxValue),
        "total_wins" => (int)Math.Min(stats.TotalWins, int.MaxValue),
        "total_matches" => stats.TotalMatches,
        "single_match_kills" => stats.SingleMatchKills,
        "account_level" => stats.AccountLevel,
        "pass_level" => stats.PassLevel,
        "first_buy" => stats.HasPurchase ? 1 : 0,
        "gunsmith_wins" => stats.GunsmithWins,
        _ => 0
    };

    private async Task<MatchResultDto> ReplayOrConflictAsync(long userId, MatchSubmissionRequest request, MatchRecord existing, CancellationToken ct)
    {
        if (existing.Kills != request.Kills || existing.Deaths != request.Deaths || existing.IsWin != request.IsWin)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.MatchIdempotencyConflict,
                "同一 ClientMatchId 已用于不同载荷");
        var user = await db.Users.AsNoTracking().Include(x => x.Profile).Include(x => x.Wallet)
            .SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        var pass = await db.PlayerPasses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId && x.SeasonId == PassSeeder.SeasonId, ct);
        return BuildResult(existing.XpEarned, 0, user.Wallet?.Coins ?? 0, existing.CoinsEarned,
            existing.PassXpEarned, pass, [], [], [], replayed: true, user.Profile!, user.Username);
    }

    private MatchResultDto BuildResult(int xpEarned, int levelUps, long coins, int coinsEarned,
        int passXpEarned, PlayerPass? pass, List<PassLevelUpDto> passLevelUps, List<string> newAttachments,
        List<UnlockedAchievementDto> unlockedAchievements, bool replayed, PlayerProfile profile, string username)
    {
        var passLevel = pass?.PassLevel ?? 1;
        return new MatchResultDto(
            xpEarned, levelUps, coins, coinsEarned, passXpEarned,
            passLevel, pass?.PassXp ?? 0, rules.GetPassXpToNextLevel(passLevel),
            [.. passLevelUps], [.. newAttachments], [.. unlockedAchievements], replayed,
            profile.ToDto(username, coins, rules));
    }

    private sealed record MatchStats(
        long TotalKills, long TotalWins, int TotalMatches, int SingleMatchKills,
        int AccountLevel, int PassLevel, bool HasPurchase, int GunsmithWins);
}
