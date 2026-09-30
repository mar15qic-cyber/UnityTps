using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class ProfileService(AppDbContext db, IProgressionRules rules)
{
    public async Task<PlayerProfileDto> GetAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(x => x.Profile).Include(x => x.Wallet).SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        return user.Profile!.ToDto(user, user.Wallet?.Coins ?? 0, rules);
    }

}
