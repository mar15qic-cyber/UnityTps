using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class ProfileService(AppDbContext db, IProgressionRules rules)
{
    public async Task<PlayerProfileDto> GetAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        return user.Profile!.ToDto(user.Username, rules);
    }

    public async Task<PlayerProfileDto> UpgradeAsync(long userId, UpgradeRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
        var user = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        var profile = user.Profile!;
        ValidateTarget(profile, request);
        var cost = 0;
        cost += CostToTarget("damage", profile.UpDamage, request.UpDamage);
        cost += CostToTarget("ammo", profile.UpAmmoCap, request.UpAmmoCap);
        cost += CostToTarget("health", profile.UpMaxHealth, request.UpMaxHealth);
        if (cost > profile.SkillPoints)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.InsufficientPoints, "技能点不足");
        profile.SkillPoints -= cost;
        profile.UpDamage = request.UpDamage;
        profile.UpAmmoCap = request.UpAmmoCap;
        profile.UpMaxHealth = request.UpMaxHealth;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return profile.ToDto(user.Username, rules);
    }

    private int CostToTarget(string stat, int current, int target)
    {
        var total = 0;
        for (var level = current; level < target; level++) total += rules.GetUpgradeCost(stat, level);
        return total;
    }

    private static void ValidateTarget(PlayerProfile profile, UpgradeRequest request)
    {
        if (request.UpDamage < profile.UpDamage || request.UpAmmoCap < profile.UpAmmoCap || request.UpMaxHealth < profile.UpMaxHealth)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidUpgrade, "升级等级只能提高，不能降低");
    }
}
