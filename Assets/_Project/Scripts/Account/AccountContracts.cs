using System;

namespace Game.Account
{
[Serializable] public sealed class RegisterRequest { public string username; public string password; }
[Serializable] public sealed class LoginRequest { public string username; public string password; }
[Serializable] public sealed class UpgradeLevelsDto { public int upDamage; public int upAmmoCap; public int upMaxHealth; }
[Serializable] public sealed class PlayerProfileDto
{
    public string username; public int level; public int xp; public int xpToNextLevel; public int skillPoints; public long coins; public UpgradeLevelsDto upgrades;
}
[Serializable] public sealed class LoadoutAttachmentDto { public string weaponSlot; public string attachmentSlot; public string attachmentItemId; }
[Serializable] public sealed class LoadoutDto
{
    public string primaryWeaponId; public string secondaryWeaponId; public string throwableId; public long version; public LoadoutAttachmentDto[] attachments;
}
[Serializable] public sealed class AuthSessionDto { public string token; public string expiresAtUtc; public PlayerProfileDto profile; public LoadoutDto loadout; public long coins; }
[Serializable] public sealed class UpgradeRequest { public int upDamage; public int upAmmoCap; public int upMaxHealth; }
[Serializable] public sealed class LoadoutRequest { public string primaryWeaponId; public string secondaryWeaponId; public string throwableId; public long expectedVersion; }
[Serializable] public sealed class AttachmentSelectionRequest { public string attachmentSlot; public string attachmentItemId; }
[Serializable] public sealed class LoadoutAttachmentsRequest { public long expectedVersion; public string weaponSlot; public AttachmentSelectionRequest[] attachments; }
[Serializable] public sealed class CatalogItemDto
{
    public string itemId; public string itemType; public string slotType; public string displayName; public string description; public string assetKey;
    public long priceCoins; public int unlockLevel; public bool isActive; public bool isOwned; public bool isImplemented; public string calibrationKey;
}
[Serializable] public sealed class InventoryItemDto { public string itemId; public int quantity; public CatalogItemDto item; }
[Serializable] public sealed class InventoryDto { public long coins; public InventoryItemDto[] items; }
[Serializable] public sealed class ShopCatalogDto { public long coins; public int level; public CatalogItemDto[] items; }
[Serializable] public sealed class PurchaseRequest { public string itemId; public int quantity = 1; public string idempotencyKey; }
[Serializable] public sealed class PurchaseResultDto
{
    public string purchaseId; public string itemId; public int quantity; public long unitPriceCoins; public long totalPriceCoins; public long coins; public bool replayed; public InventoryItemDto item;
}
[Serializable] public sealed class AttachmentCompatibilityDto { public string weaponId; public string attachmentId; public string slotType; public bool isImplemented; public string calibrationKey; }
[Serializable] public sealed class LoadoutAttachmentsDto { public long version; public LoadoutAttachmentDto[] attachments; }
// Docs/23 P2（G5）契约对齐（以后端 Contracts.cs L77-94 为准，字段名逐字小驼峰）：
// 请求去掉旧 score（后端无此字段），新增服务器权威 durationSeconds/isWin
[Serializable] public sealed class MatchSubmissionRequest { public string clientMatchId; public int kills; public int deaths; public int durationSeconds; public bool isWin; }
[Serializable] public sealed class PassLevelUpDto { public int level; public string rewardType; public string itemId; public int coinsAmount; }
[Serializable] public sealed class UnlockedAchievementDto { public string achievementId; public string displayName; public int passXpReward; }
[Serializable] public sealed class MatchResultDto
{
    public int xpEarned; public int levelUps; public long coins; public int coinsEarned;
    public int passXpEarned; public int passLevel; public int passXp; public int passXpToNextLevel;
    public PassLevelUpDto[] passLevelUps; public string[] newAttachments;
    public UnlockedAchievementDto[] unlockedAchievements; public bool replayed;
    public PlayerProfileDto profile;
}
[Serializable] public sealed class ProblemDetailsDto
{
    public string title; public int status; public string detail; public string code; public string traceId; public System.Collections.Generic.Dictionary<string, string[]> errors;
}
[Serializable] public sealed class HealthDto { public string status; public string database; }
}
