using System.Data;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class LoadoutService(AppDbContext db)
{
    public async Task<LoadoutDto> GetAsync(long userId, int backpackIndex, CancellationToken cancellationToken)
    {
        var row = await db.Loadouts.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.BackpackIndex == backpackIndex, cancellationToken);
        if (row is not null) return row.ToDto();
        // 背包 1/2 懒创建：读取按策略合成（不落库）；背包 0 必须注册已建行
        if (backpackIndex != 0)
            return (await BackpackSetInternalAsync(userId, cancellationToken))[backpackIndex];
        throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
    }

    /// <summary>三背包全集（Unity 仓库/大厅一次拉取；缺失背包按 BackpackPolicy 懒默认合成）。</summary>
    public async Task<BackpackSetDto> GetBackpackSetAsync(long userId, CancellationToken cancellationToken) =>
        new(await BackpackSetInternalAsync(userId, cancellationToken), BackpackPolicy.DefaultActiveIndex);

    private async Task<LoadoutDto[]> BackpackSetInternalAsync(long userId, CancellationToken cancellationToken)
    {
        var rows = await db.Loadouts.Include(x => x.Attachments)
            .Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        if (rows.Count == 0)
            throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
        return BackpackPolicy.BuildBackpackSet(rows);
    }

    public async Task<LoadoutDto> UpdateAsync(long userId, int backpackIndex, LoadoutRequest request, CancellationToken cancellationToken)
    {
        // EnableRetryOnFailure 与显式事务唯一兼容组合（同 MatchService/CommerceService）
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            var loadout = await db.Loadouts.Include(x => x.Attachments)
                .SingleOrDefaultAsync(x => x.UserId == userId && x.BackpackIndex == backpackIndex, cancellationToken);
            if (loadout is null)
            {
                // 背包 1/2 懒创建（背包 0 缺失 = 异常态，与旧语义一致 404）
                if (backpackIndex == 0)
                    throw new ApiException(StatusCodes.Status404NotFound, "LOADOUT_NOT_FOUND", "配装不存在");
                loadout = new PlayerLoadout
                {
                    UserId = userId, BackpackIndex = backpackIndex, Version = 1, UpdatedAtUtc = DateTime.UtcNow,
                    ThrowableId = BackpackPolicy.DefaultThrowableItemId,
                };
                db.Loadouts.Add(loadout);
            }
            else if (loadout.Version != request.ExpectedVersion)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.LoadoutVersionConflict, "配装已在其他位置更新，请刷新后重试");
            await ValidateOwnedSlotAsync(userId, request.PrimaryWeaponId, "Primary", cancellationToken);
            await ValidateOwnedSlotAsync(userId, request.SecondaryWeaponId, "Secondary", cancellationToken);
            if (request.ThrowableIds is null && !string.IsNullOrWhiteSpace(request.ThrowableId)
                && request.ThrowableId is not "throwable.standard" and not "throwable.frag_assault"
                && !ThrowableSlotPolicy.ModelIds.Contains(request.ThrowableId))
                throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "投掷物型号无效");
            var throwableSlots = request.ThrowableIds ?? ThrowableSlotPolicy.FromLegacy(request.ThrowableId);
            if (throwableSlots.Length != ThrowableSlotPolicy.SlotCount)
                throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "投掷物必须包含三格（允许空槽）");
            foreach (var id in throwableSlots.Distinct())
                await ValidateThrowableAsync(userId, id, cancellationToken);
            ThrowableSlotPolicy.Write(loadout, throwableSlots);
            // 缺陷 A 修复（Docs/21 审计）：配件以 WeaponSlot 为锚，换枪时旧枪配件不能继承到新枪——清空该槽
            if (!string.Equals(loadout.PrimaryWeaponId, request.PrimaryWeaponId, StringComparison.Ordinal))
                loadout.Attachments.RemoveAll(x => x.WeaponSlot.Equals("Primary", StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(loadout.SecondaryWeaponId, request.SecondaryWeaponId, StringComparison.Ordinal))
                loadout.Attachments.RemoveAll(x => x.WeaponSlot.Equals("Secondary", StringComparison.OrdinalIgnoreCase));
            loadout.PrimaryWeaponId = request.PrimaryWeaponId;
            loadout.SecondaryWeaponId = request.SecondaryWeaponId;
            loadout.ThrowableId = string.IsNullOrWhiteSpace(request.ThrowableId) ? null : request.ThrowableId.Trim();
            loadout.Version++;
            loadout.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return loadout.ToDto();
        });
    }

    public async Task<LoadoutAttachmentsDto> GetAttachmentsAsync(long userId, int backpackIndex, CancellationToken cancellationToken)
    {
        var loadout = await db.Loadouts.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.BackpackIndex == backpackIndex, cancellationToken);
        if (loadout is null)
        {
            // 背包 1/2 懒默认：空附件 + Version=1（与 BackpackPolicy 合成一致）
            if (backpackIndex == 0)
                throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.LoadoutVersionConflict, "配装不存在");
            return new LoadoutAttachmentsDto(1, []);
        }
        return new LoadoutAttachmentsDto(loadout.Version, ToAttachmentDtos(loadout));
    }

    public async Task<LoadoutAttachmentsDto> UpdateAttachmentsAsync(long userId, int backpackIndex, LoadoutAttachmentsRequest request, CancellationToken cancellationToken)
    {
        var loadout = await db.Loadouts.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.UserId == userId && x.BackpackIndex == backpackIndex, cancellationToken);
        if (loadout is null)
        {
            // 背包 1/2 首次配件保存 = 懒创建该行（武器取请求/默认，配件随后写入）
            if (backpackIndex == 0)
                throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.LoadoutVersionConflict, "配装不存在");
            loadout = new PlayerLoadout
            {
                UserId = userId, BackpackIndex = backpackIndex, Version = 1, UpdatedAtUtc = DateTime.UtcNow,
                ThrowableId = BackpackPolicy.DefaultThrowableItemId,
            };
            db.Loadouts.Add(loadout);
        }
        else if (loadout.Version != request.ExpectedVersion)
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.LoadoutVersionConflict, "配装已在其他位置更新，请刷新后重试");
        var secondary = request.WeaponSlot.Equals("Secondary", StringComparison.OrdinalIgnoreCase);
        var primary = request.WeaponSlot.Equals("Primary", StringComparison.OrdinalIgnoreCase);
        var equippedWeaponId = secondary ? loadout.SecondaryWeaponId : primary ? loadout.PrimaryWeaponId : string.Empty;
        var weaponId = string.IsNullOrWhiteSpace(request.WeaponItemId) ? equippedWeaponId : request.WeaponItemId.Trim();
        if (string.IsNullOrEmpty(weaponId))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器槽位无效");
        if (!primary && !secondary)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "武器槽位无效");
        if (!string.Equals(weaponId, equippedWeaponId, StringComparison.Ordinal))
            await ValidateOwnedSlotAsync(userId, weaponId, secondary ? "Secondary" : "Primary", cancellationToken);

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

        // Validation above is for the actual previewed gun. Apply the equip switch
        // and attachment changes together so a successful save cannot target two guns.
        if (secondary) loadout.SecondaryWeaponId = weaponId;
        else loadout.PrimaryWeaponId = weaponId;
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

    /// <summary>投掷物槽校验（2026-09-30 解冻）：null/空白 = 不带雷（放行）；非空必须是
    /// 已拥有 + 启用 + IsImplemented + SlotType="Throwable" 的目录项。</summary>
    private async Task ValidateThrowableAsync(long userId, string? throwableId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(throwableId)) return;
        var itemId = throwableId.Trim();
        if (!ThrowableSlotPolicy.ModelIds.Contains(itemId))
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "投掷物型号无效");
        var item = await db.CatalogItems.AsNoTracking().SingleOrDefaultAsync(x => x.ItemId == itemId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "投掷物 ID 无效");
        if (!item.ItemType.Equals(BackpackPolicy.ThrowableSlotType, StringComparison.OrdinalIgnoreCase)
            || !item.SlotType.Equals(BackpackPolicy.ThrowableSlotType, StringComparison.OrdinalIgnoreCase)
            || !item.IsActive || !item.IsImplemented)
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidWeapon, "投掷物槽位不匹配");
        if (!await db.InventoryItems.AnyAsync(x => x.UserId == userId && x.ItemId == itemId, cancellationToken))
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.LoadoutNotOwned, "尚未拥有该投掷物");
    }

    private static LoadoutAttachmentDto[] ToAttachmentDtos(PlayerLoadout loadout) => loadout.Attachments
        .Select(x => new LoadoutAttachmentDto(x.WeaponSlot, x.AttachmentSlot, x.AttachmentItemId)).ToArray();
}
