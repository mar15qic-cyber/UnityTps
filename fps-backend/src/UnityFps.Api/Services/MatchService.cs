using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class MatchService(AppDbContext db, IProgressionRules rules)
{
    public async Task<MatchResultDto> SubmitAsync(long userId, MatchSubmissionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
        var user = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new Common.ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        var profile = user.Profile!;
        var clamped = rules.ClampMatch(request.Kills, request.Deaths, request.Score);
        var xp = rules.GetMatchXp(clamped.Kills, clamped.Deaths, clamped.Score);
        db.Matches.Add(new MatchRecord { UserId = userId, Kills = clamped.Kills, Deaths = clamped.Deaths, Score = clamped.Score, XpEarned = xp, PlayedAtUtc = DateTime.UtcNow });
        profile.Xp += xp;
        var levelUps = 0;
        while (profile.Xp >= rules.GetXpToNextLevel(profile.Level))
        {
            profile.Xp -= rules.GetXpToNextLevel(profile.Level);
            profile.Level++;
            profile.SkillPoints++;
            levelUps++;
        }
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new MatchResultDto(xp, levelUps, profile.ToDto(user.Username, rules));
    }
}
