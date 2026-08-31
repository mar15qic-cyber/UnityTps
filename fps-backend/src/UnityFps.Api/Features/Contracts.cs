using System.ComponentModel.DataAnnotations;

namespace UnityFps.Api.Features;

public sealed class RegisterRequest
{
    [Required, StringLength(32, MinimumLength = 3)] public string Username { get; set; } = string.Empty;
    [Required, StringLength(72, MinimumLength = 8)] public string Password { get; set; } = string.Empty;
}

public sealed class LoginRequest
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

public sealed record AuthSessionDto(string Token, DateTime ExpiresAtUtc, PlayerProfileDto Profile, LoadoutDto Loadout, long Coins);
public sealed record UpgradeLevelsDto(int UpDamage, int UpAmmoCap, int UpMaxHealth);
public sealed record PlayerProfileDto(string Username, int Level, int Xp, int XpToNextLevel, int SkillPoints, long Coins, UpgradeLevelsDto Upgrades);
public sealed record LoadoutAttachmentDto(string WeaponSlot, string AttachmentSlot, string AttachmentItemId);
public sealed record LoadoutDto(string PrimaryWeaponId, string SecondaryWeaponId, string? ThrowableId, long Version, LoadoutAttachmentDto[] Attachments);

public sealed class UpgradeRequest
{
    [Range(0, 5)] public int UpDamage { get; set; }
    [Range(0, 5)] public int UpAmmoCap { get; set; }
    [Range(0, 5)] public int UpMaxHealth { get; set; }
}

public sealed class LoadoutRequest
{
    [Required, StringLength(64)] public string PrimaryWeaponId { get; set; } = string.Empty;
    [Required, StringLength(64)] public string SecondaryWeaponId { get; set; } = string.Empty;
    [StringLength(64)] public string? ThrowableId { get; set; }
    [Range(0, long.MaxValue)] public long ExpectedVersion { get; set; }
}

public sealed record CatalogItemDto(string ItemId, string ItemType, string SlotType, string DisplayName, string Description,
    string AssetKey, long PriceCoins, int UnlockLevel, bool IsActive, bool IsOwned, bool IsImplemented, string CalibrationKey);
public sealed record InventoryItemDto(string ItemId, int Quantity, CatalogItemDto Item);
public sealed record InventoryDto(long Coins, InventoryItemDto[] Items);
public sealed record ShopCatalogDto(long Coins, int Level, CatalogItemDto[] Items);
public sealed class PurchaseRequest
{
    [Required, StringLength(64)] public string ItemId { get; set; } = string.Empty;
    [Range(1, 1)] public int Quantity { get; set; } = 1;
    [Required, StringLength(96, MinimumLength = 8)] public string IdempotencyKey { get; set; } = string.Empty;
}
public sealed record PurchaseResultDto(string PurchaseId, string ItemId, int Quantity, long UnitPriceCoins,
    long TotalPriceCoins, long Coins, bool Replayed, InventoryItemDto Item);

// ---- 房间注册表（Docs/19 N4）----

public sealed class CreateRoomRequest
{
    [Required, StringLength(64)] public string HostAddress { get; set; } = string.Empty;
    [Range(1024, 65535)] public int HostPort { get; set; } = 7770;
    [Range(2, 16)] public int MaxPlayers { get; set; } = 8;
}

public sealed record GameRoomDto(string RoomCode, string HostUsername, string HostAddress, int HostPort,
    int JoinedPlayers, int MaxPlayers, bool IsOpen, DateTime CreatedAtUtc);
public sealed record AttachmentCompatibilityDto(string WeaponId, string AttachmentId, string SlotType, bool IsImplemented, string CalibrationKey);
public sealed record LoadoutAttachmentsDto(long Version, LoadoutAttachmentDto[] Attachments);
public sealed class AttachmentSelectionRequest
{
    [Required, StringLength(24)] public string AttachmentSlot { get; set; } = string.Empty;
    [Required, StringLength(64)] public string AttachmentItemId { get; set; } = string.Empty;
}
public sealed class LoadoutAttachmentsRequest
{
    [Range(0, long.MaxValue)] public long ExpectedVersion { get; set; }
    [Required, StringLength(24)] public string WeaponSlot { get; set; } = string.Empty;
    public AttachmentSelectionRequest[] Attachments { get; set; } = [];
}

public sealed class MatchSubmissionRequest
{
    [Required, StringLength(64, MinimumLength = 8)] public string ClientMatchId { get; set; } = string.Empty;
    [Range(0, int.MaxValue)] public int Kills { get; set; }
    [Range(0, int.MaxValue)] public int Deaths { get; set; }
    [Range(0, int.MaxValue)] public int DurationSeconds { get; set; }
    public bool IsWin { get; set; }
}

public sealed record PassLevelUpDto(int Level, string? RewardType, string? ItemId, int CoinsAmount);
public sealed record UnlockedAchievementDto(string AchievementId, string DisplayName, int PassXpReward);

public sealed record MatchResultDto(
    int XpEarned, int LevelUps, long Coins, int CoinsEarned,
    int PassXpEarned, int PassLevel, int PassXp, int PassXpToNextLevel,
    PassLevelUpDto[] PassLevelUps, string[] NewAttachments,
    UnlockedAchievementDto[] UnlockedAchievements, bool Replayed,
    PlayerProfileDto Profile);

// ---- 通行证（Docs/17 §4.4）----

public sealed record PassRewardDto(int Level, string RewardType, string? ItemId, int CoinsAmount, bool Granted);
public sealed record PassAchievementDto(string Id, string DisplayName, string Description,
    string TargetMetric, int TargetValue, int Progress, bool Unlocked, int PassXpReward);
public sealed record PassDto(string SeasonId, int Level, int Xp, int XpToNextLevel, int MaxLevel,
    PassRewardDto[] Rewards, PassAchievementDto[] Achievements);
public sealed record AchievementDto(string Id, string DisplayName, string Description,
    string TargetMetric, int TargetValue, int Progress, bool Unlocked, int PassXpReward);
