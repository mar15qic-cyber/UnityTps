using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class PassService(AppDbContext db, IProgressionRules rules)
{
    public async Task<PassDto> GetPassAsync(long userId, CancellationToken cancellationToken)
    {
        var pass = await db.PlayerPasses
            .SingleOrDefaultAsync(x => x.UserId == userId && x.SeasonId == PassSeeder.SeasonId, cancellationToken);
        if (pass is null)
        {
            // 懒回填（老用户首次访问）：并发同请求只有一方插入成功，败者重查即可
            pass = new PlayerPass { UserId = userId, SeasonId = PassSeeder.SeasonId, PassLevel = 1, PassXp = 0, Version = 1, UpdatedAtUtc = DateTime.UtcNow };
            db.PlayerPasses.Add(pass);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
            pass = await db.PlayerPasses
                .SingleOrDefaultAsync(x => x.UserId == userId && x.SeasonId == PassSeeder.SeasonId, cancellationToken)
                ?? pass;
        }

        var rewards = await db.PassRewards
            .Where(x => x.SeasonId == PassSeeder.SeasonId)
            .OrderBy(x => x.PassLevel)
            .ToListAsync(cancellationToken);
        var grants = (await db.PassRewardGrants
            .Where(x => x.UserId == userId && x.SeasonId == PassSeeder.SeasonId)
            .Select(x => x.PassLevel)
            .ToListAsync(cancellationToken)).ToHashSet();

        var achievementDefs = await db.AchievementDefinitions.OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var playerAch = await db.PlayerAchievements
            .Where(x => x.UserId == userId)
            .ToDictionaryAsync(x => x.AchievementId, cancellationToken);

        return new PassDto(
            pass.SeasonId, pass.PassLevel, pass.PassXp,
            rules.GetPassXpToNextLevel(pass.PassLevel), rules.MaxPassLevel,
            rewards.Select(r => r.ToDto(grants.Contains(r.PassLevel))).ToArray(),
            achievementDefs.Select(d => d.ToDto(playerAch.GetValueOrDefault(d.AchievementId))).ToArray());
    }

    public async Task<AchievementDto[]> GetAchievementsAsync(long userId, CancellationToken cancellationToken)
    {
        var defs = await db.AchievementDefinitions.OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var playerAch = await db.PlayerAchievements
            .Where(x => x.UserId == userId)
            .ToDictionaryAsync(x => x.AchievementId, cancellationToken);
        return defs.Select(d => d.ToAchievementDto(playerAch.GetValueOrDefault(d.AchievementId))).ToArray();
    }
}
