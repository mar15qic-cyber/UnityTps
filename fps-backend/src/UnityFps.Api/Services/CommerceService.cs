using System.Data;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public sealed class CommerceService(AppDbContext db)
{
    public async Task<ShopCatalogDto> GetCatalogAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(x => x.Profile).Include(x => x.Wallet).Include(x => x.Inventory)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        var owned = user.Inventory.Select(x => x.ItemId).ToHashSet(StringComparer.Ordinal);
        var items = await db.CatalogItems.AsNoTracking()
            .Where(x => x.AcquisitionSource == "Shop" && x.IsActive)
            .OrderBy(x => x.ItemId).ToListAsync(cancellationToken);
        return new ShopCatalogDto(user.Wallet?.Coins ?? 0, user.Profile?.Level ?? 1, items.Select(x => x.ToDto(owned.Contains(x.ItemId))).ToArray());
    }

    public async Task<InventoryDto> GetInventoryAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(x => x.Wallet).Include(x => x.Inventory).ThenInclude(x => x.Item)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
        return new InventoryDto(user.Wallet?.Coins ?? 0, user.Inventory.OrderBy(x => x.ItemId)
            .Select(x => new InventoryItemDto(x.ItemId, x.Quantity, x.Item.ToDto(true))).ToArray());
    }

    public async Task<PurchaseResultDto> PurchaseAsync(long userId, PurchaseRequest request, CancellationToken cancellationToken)
    {
        // EnableRetryOnFailure 与显式事务唯一兼容组合：事务体经 CreateExecutionStrategy 执行（同 MatchService）；
        // 委托开头 Clear() 保证瞬时故障重试时整体可重放。
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;

            var replay = await db.Purchases.Include(x => x.Item).SingleOrDefaultAsync(
                x => x.UserId == userId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
            if (replay is not null)
            {
                if (replay.ItemId != request.ItemId || replay.Quantity != request.Quantity)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.IdempotencyConflict, "幂等键已用于另一笔购买");
                var replayWallet = await db.Wallets.SingleAsync(x => x.UserId == userId, cancellationToken);
                var replayInventory = await db.InventoryItems.SingleAsync(x => x.UserId == userId && x.ItemId == replay.ItemId, cancellationToken);
                return Result(replay, replayWallet.Coins, replayInventory, true);
            }

            var user = await db.Users.Include(x => x.Profile).Include(x => x.Wallet).Include(x => x.Inventory)
                .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "PROFILE_NOT_FOUND", "档案不存在");
            var item = await db.CatalogItems.SingleOrDefaultAsync(x => x.ItemId == request.ItemId, cancellationToken)
                ?? throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.ItemNotFound, "商品不存在");
            if (!item.IsActive || !item.IsImplemented)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.ItemDisabled, "商品尚未开放");
            if ((user.Profile?.Level ?? 1) < item.UnlockLevel)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.LevelLocked, $"需要等级 {item.UnlockLevel}");
            if (user.Inventory.Any(x => x.ItemId == item.ItemId))
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.AlreadyOwned, "已拥有该武器");
            var wallet = user.Wallet ?? throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.InsufficientCoins, "钱包尚未初始化");
            var total = checked(item.PriceCoins * request.Quantity);
            if (wallet.Coins < total)
                throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.InsufficientCoins, "金币不足");

            wallet.Coins -= total;
            wallet.UpdatedAtUtc = DateTime.UtcNow;
            var purchase = new ShopPurchase
            {
                User = user, Item = item, ItemId = item.ItemId, Quantity = request.Quantity, UnitPriceCoins = item.PriceCoins,
                TotalPriceCoins = total, IdempotencyKey = request.IdempotencyKey, CreatedAtUtc = DateTime.UtcNow
            };
            var inventory = new PlayerInventoryItem { User = user, Item = item, ItemId = item.ItemId, Quantity = request.Quantity, AcquiredAtUtc = DateTime.UtcNow };
            db.Purchases.Add(purchase);
            db.InventoryItems.Add(inventory);
            db.WalletLedger.Add(new WalletLedgerEntry
            {
                UserId = userId, DeltaCoins = -total, BalanceAfter = wallet.Coins, Reason = "WeaponPurchase",
                ReferenceId = purchase.PurchaseId, CreatedAtUtc = DateTime.UtcNow
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return Result(purchase, wallet.Coins, inventory, false);
            }
            catch (DbUpdateException) when (db.Database.IsRelational())
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                var winner = await db.Purchases.Include(x => x.Item).SingleOrDefaultAsync(
                    x => x.UserId == userId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
                if (winner is null) throw;
                if (winner.ItemId != request.ItemId || winner.Quantity != request.Quantity)
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.IdempotencyConflict, "幂等键已用于另一笔购买");
                var winnerWallet = await db.Wallets.SingleAsync(x => x.UserId == userId, cancellationToken);
                var winnerInventory = await db.InventoryItems.SingleAsync(x => x.UserId == userId && x.ItemId == winner.ItemId, cancellationToken);
                return Result(winner, winnerWallet.Coins, winnerInventory, true);
            }
        });
    }

    private static PurchaseResultDto Result(ShopPurchase purchase, long coins, PlayerInventoryItem inventory, bool replayed) =>
        new(purchase.PurchaseId, purchase.ItemId, purchase.Quantity, purchase.UnitPriceCoins, purchase.TotalPriceCoins, coins,
            replayed, new InventoryItemDto(inventory.ItemId, inventory.Quantity, purchase.Item.ToDto(true)));
}
