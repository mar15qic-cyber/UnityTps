using System.Data;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class LoadoutService(AppDbContext db)
{
    public async Task<LoadoutDto> GetAsync(long userId, CancellationToken cancellationToken) =>
        (await db.Loadouts.Include(x => x.Attachments).SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
         ?? throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在")).ToDto();

    public async Task<LoadoutDto> UpdateAsync(long userId, LoadoutRequest request, CancellationToken cancellationToken)
    {
        if (request.ThrowableId is not null)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "本轮不支持投掷物配装");
        // EnableRetryOnFailure 与显式事务唯一兼容组合（同 MatchService/CommerceService）
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            var loadout = await db.Loadouts.Include(x => x.Attachments).SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
            if (loadout.Version != request.ExpectedVersion)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.LoadoutVersionConflict, "配装已在其他位置更新，请刷新后重试");
            await ValidateOwnedSlotAsync(userId, request.PrimaryWeaponId, "Primary", cancellationToken);
            await ValidateOwnedSlotAsync(userId, request.SecondaryWeaponId, "Secondary", cancellationToken);
            loadout.PrimaryWeaponId = request.PrimaryWeaponId;
            loadout.SecondaryWeaponId = request.SecondaryWeaponId;
            loadout.ThrowableId = null;
            loadout.Version++;
            loadout.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return loadout.ToDto();
        });
    }

    public async Task<LoadoutAttachmentsDto> GetAttachmentsAsync(long userId, CancellationToken cancellationToken)
    {
        var loadout = await db.Loadouts.Include(x => x.Attachments).SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
        return new LoadoutAttachmentsDto(loadout.Version, ToAttachmentDtos(loadout));
    }

    public async Task<LoadoutAttachmentsDto> UpdateAttachmentsAsync(long userId, LoadoutAttachmentsRequest request, CancellationToken cancellationToken)
    {
        var loadout = await db.Loadouts.Include(x => x.Attachments).SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
        if (loadout.Version != request.ExpectedVersion)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.LoadoutVersionConflict, "配装已在其他位置更新，请刷新后重试");
        var weaponId = request.WeaponSlot.Equals("Secondary", StringComparison.OrdinalIgnoreCase)
            ? loadout.SecondaryWeaponId : request.WeaponSlot.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                ? loadout.PrimaryWeaponId : string.Empty;
        if (string.IsNullOrEmpty(weaponId))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器槽位无效");
        if (weaponId.StartsWith("weapon.lpw.", StringComparison.Ordinal))
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.AttachmentsUnsupported, "LPW 枪械配件尚未适配");
        if (request.Attachments.Length != 0)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.AttachmentsUnsupported, "该武器暂无已验证配件");
        loadout.Attachments.RemoveAll(x => x.WeaponSlot.Equals(request.WeaponSlot, StringComparison.OrdinalIgnoreCase));
        loadout.Version++;
        loadout.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new LoadoutAttachmentsDto(loadout.Version, ToAttachmentDtos(loadout));
    }

    public Task<AttachmentCompatibilityDto[]> GetCompatibilityAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<AttachmentCompatibilityDto>());

    private async Task ValidateOwnedSlotAsync(long userId, string itemId, string slot, CancellationToken cancellationToken)
    {
        var item = await db.CatalogItems.AsNoTracking().SingleOrDefaultAsync(x => x.ItemId == itemId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器 ID 无效");
        if (!item.IsActive || !item.IsImplemented || !item.SlotType.Equals(slot, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器槽位不匹配");
        if (!await db.InventoryItems.AnyAsync(x => x.UserId == userId && x.ItemId == itemId, cancellationToken))
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.LoadoutNotOwned, "尚未拥有该武器");
    }

    private static LoadoutAttachmentDto[] ToAttachmentDtos(PlayerLoadout loadout) => loadout.Attachments
        .Select(x => new LoadoutAttachmentDto(x.WeaponSlot, x.AttachmentSlot, x.AttachmentItemId)).ToArray();
}
