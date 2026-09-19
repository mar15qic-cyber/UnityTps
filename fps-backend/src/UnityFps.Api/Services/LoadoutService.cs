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
            // 缺陷 A 修复（Docs/21 审计）：配件以 WeaponSlot 为锚，换枪时旧枪配件不能继承到新枪——清空该槽
            if (!string.Equals(loadout.PrimaryWeaponId, request.PrimaryWeaponId, StringComparison.Ordinal))
                loadout.Attachments.RemoveAll(x => x.WeaponSlot.Equals("Primary", StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(loadout.SecondaryWeaponId, request.SecondaryWeaponId, StringComparison.Ordinal))
                loadout.Attachments.RemoveAll(x => x.WeaponSlot.Equals("Secondary", StringComparison.OrdinalIgnoreCase));
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
            ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.LoadoutVersionConflict, "配装不存在");
        if (loadout.Version != request.ExpectedVersion)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.LoadoutVersionConflict, "配装已在其他位置更新，请刷新后重试");
        var weaponId = request.WeaponSlot.Equals("Secondary", StringComparison.OrdinalIgnoreCase)
            ? loadout.SecondaryWeaponId : request.WeaponSlot.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                ? loadout.PrimaryWeaponId : string.Empty;
        if (string.IsNullOrEmpty(weaponId))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器槽位无效");

        // 矩阵校验：目录存在且启用 → 已拥有 → 兼容矩阵放行（IsImplemented=true）
        var attachmentIds = request.Attachments.Select(x => x.AttachmentItemId).Distinct().ToArray();
        var items = await db.CatalogItems
            .Where(x => x.ItemType == "Attachment" && attachmentIds.Contains(x.ItemId))
            .ToDictionaryAsync(x => x.ItemId, cancellationToken);
        var ownedIds = (await db.InventoryItems
            .Where(x => x.UserId == userId && attachmentIds.Contains(x.ItemId))
            .Select(x => x.ItemId).ToListAsync(cancellationToken)).ToHashSet();
        var compatRows = await db.AttachmentCompat
            .Where(x => x.WeaponItemId == weaponId && attachmentIds.Contains(x.AttachmentItemId))
            .ToListAsync(cancellationToken);

        foreach (var selection in request.Attachments)
        {
            if (!items.TryGetValue(selection.AttachmentItemId, out var item) || !item.IsActive)
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.AttachmentInvalid, $"配件无效：{selection.AttachmentItemId}");
            if (!item.SlotType.Equals(selection.AttachmentSlot, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.AttachmentInvalid, $"配件槽位不匹配：{selection.AttachmentItemId}");
            if (!ownedIds.Contains(selection.AttachmentItemId))
                throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.AttachmentNotOwned, $"尚未拥有该配件：{selection.AttachmentItemId}");
            var compat = compatRows.FirstOrDefault(x => x.AttachmentItemId == selection.AttachmentItemId);
            if (compat is null || !compat.IsImplemented)
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, ApiErrorCodes.AttachmentIncompatible,
                    $"该配件尚未在此武器上适配：{selection.AttachmentItemId}");
        }

        loadout.Attachments.RemoveAll(x => x.WeaponSlot.Equals(request.WeaponSlot, StringComparison.OrdinalIgnoreCase));
        foreach (var selection in request.Attachments)
            loadout.Attachments.Add(new PlayerLoadoutAttachment
            {
                WeaponSlot = request.WeaponSlot, AttachmentSlot = selection.AttachmentSlot, AttachmentItemId = selection.AttachmentItemId
            });
        loadout.Version++;
        loadout.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new LoadoutAttachmentsDto(loadout.Version, ToAttachmentDtos(loadout));
    }

    public async Task<AttachmentCompatibilityDto[]> GetCompatibilityAsync(long userId, CancellationToken cancellationToken)
    {
        var rows = await db.AttachmentCompat.AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(x => new AttachmentCompatibilityDto(x.WeaponItemId, x.AttachmentItemId, x.SlotType, x.IsImplemented, x.CalibrationKey)).ToArray();
    }

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
