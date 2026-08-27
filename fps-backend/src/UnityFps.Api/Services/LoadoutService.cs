using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class LoadoutService(AppDbContext db)
{
    private static readonly HashSet<string> Primary = ["rifle.day3", "rifle.02", "rifle.03", "smg.01", "smg.02", "shotgun.01", "sniper.01", "sniper.02"];
    private static readonly HashSet<string> Secondary = ["pistol.day2", "handgun.02"];

    public async Task<LoadoutDto> GetAsync(long userId, CancellationToken cancellationToken) =>
        (await db.Loadouts.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken) ?? throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在")).ToDto();

    public async Task<LoadoutDto> UpdateAsync(long userId, LoadoutRequest request, CancellationToken cancellationToken)
    {
        if (!Primary.Contains(request.PrimaryWeaponId) || !Secondary.Contains(request.SecondaryWeaponId) || request.ThrowableId is not null)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器槽位或武器 ID 无效");
        var loadout = await db.Loadouts.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
        loadout.PrimaryWeaponId = request.PrimaryWeaponId;
        loadout.SecondaryWeaponId = request.SecondaryWeaponId;
        loadout.ThrowableId = null;
        loadout.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return loadout.ToDto();
    }
}
