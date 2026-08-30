namespace UnityFps.Api.Data;

public sealed class UserAccount
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public PlayerProfile? Profile { get; set; }
    public PlayerLoadout? Loadout { get; set; }
    public PlayerWallet? Wallet { get; set; }
    public List<PlayerInventoryItem> Inventory { get; set; } = [];
    public List<ShopPurchase> Purchases { get; set; } = [];
    public List<MatchRecord> Matches { get; set; } = [];
}

public sealed class PlayerProfile
{
    public long UserId { get; set; }
    public int Level { get; set; } = 1;
    public int Xp { get; set; }
    public int SkillPoints { get; set; }
    public int UpDamage { get; set; }
    public int UpAmmoCap { get; set; }
    public int UpMaxHealth { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
}

public sealed class PlayerLoadout
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string PrimaryWeaponId { get; set; } = "weapon.m4";
    public string SecondaryWeaponId { get; set; } = "weapon.service_pistol";
    public string? ThrowableId { get; set; }
    public long Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
    public List<PlayerLoadoutAttachment> Attachments { get; set; } = [];
}

public sealed class CatalogItem
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemType { get; set; } = "Weapon";
    public string SlotType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AssetKey { get; set; } = string.Empty;
    public long PriceCoins { get; set; }
    public int UnlockLevel { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsImplemented { get; set; } = true;
    public string CalibrationKey { get; set; } = string.Empty;
}

public sealed class PlayerWallet
{
    public long UserId { get; set; }
    public long Coins { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
}

public sealed class PlayerInventoryItem
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public DateTime AcquiredAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
    public CatalogItem Item { get; set; } = null!;
}

public sealed class ShopPurchase
{
    public string PurchaseId { get; set; } = Guid.NewGuid().ToString("N");
    public long UserId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public long UnitPriceCoins { get; set; }
    public long TotalPriceCoins { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
    public CatalogItem Item { get; set; } = null!;
}

public sealed class WalletLedgerEntry
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long DeltaCoins { get; set; }
    public long BalanceAfter { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class PlayerLoadoutAttachment
{
    public long Id { get; set; }
    public long LoadoutId { get; set; }
    public string WeaponSlot { get; set; } = string.Empty;
    public string AttachmentSlot { get; set; } = string.Empty;
    public string AttachmentItemId { get; set; } = string.Empty;
    public PlayerLoadout Loadout { get; set; } = null!;
}

public sealed class MatchRecord
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Score { get; set; }
    public int XpEarned { get; set; }
    public DateTime PlayedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
}

/// <summary>
/// 联机房间注册表（Docs/19 N4，轻量）：只做"房间码→房主 IP/端口"的发现服务；
/// 实时对战仍走 FishNet client-hosted 直连（Docs/04：Dedicated Server 放弃）。
/// 房间生命周期短：心跳过期自动清理（HostSession 每次列表查询时懒清理）。
/// </summary>
public sealed class GameRoom
{
    public long Id { get; set; }
    /// <summary>房间码（6 位大写字母数字，用户口播/输入用）。</summary>
    public string RoomCode { get; set; } = string.Empty;
    public long HostUserId { get; set; }
    public string HostUsername { get; set; } = string.Empty;
    /// <summary>房主 Tugboat 监听地址（LAN IPv4）。</summary>
    public string HostAddress { get; set; } = string.Empty;
    public int HostPort { get; set; } = 7770;
    public int MaxPlayers { get; set; } = 8;
    public int JoinedPlayers { get; set; } = 1;
    public bool IsOpen { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>房主心跳（存活判定：超过 30s 视为废弃，列表不显示并懒删除）。</summary>
    public DateTime LastHeartbeatUtc { get; set; }
    public UserAccount Host { get; set; } = null!;
    public List<GameRoomMember> Members { get; set; } = [];
}

/// <summary>房间加入记录（一个玩家同时只能在一个房间）。</summary>
public sealed class GameRoomMember
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long UserId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public GameRoom Room { get; set; } = null!;
    public UserAccount User { get; set; } = null!;
}
