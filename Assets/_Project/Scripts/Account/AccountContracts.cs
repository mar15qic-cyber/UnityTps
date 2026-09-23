using System;

namespace Game.Account
{
[Serializable] public sealed class RegisterRequest { public string username; public string password; }
[Serializable] public sealed class LoginRequest { public string username; public string password; }
[Serializable] public sealed class PlayerProfileDto
{
    // identityTag=好友查找编码（用户名#编码 的 # 后缀，注册自动生成 4 位，2026-09-20 需求2）
    public string username; public string identityTag; public int level; public int xp; public int xpToNextLevel; public long coins;
}
[Serializable] public sealed class LoadoutAttachmentDto { public string weaponSlot; public string attachmentSlot; public string attachmentItemId; }
[Serializable] public sealed class LoadoutDto
{
    public string primaryWeaponId; public string secondaryWeaponId; public string throwableId; public long version; public LoadoutAttachmentDto[] attachments;
}
[Serializable] public sealed class AuthSessionDto { public string token; public string expiresAtUtc; public PlayerProfileDto profile; public LoadoutDto loadout; public long coins; }
[Serializable] public sealed class LoadoutRequest { public string primaryWeaponId; public string secondaryWeaponId; public string throwableId; public long expectedVersion; }
[Serializable] public sealed class AttachmentSelectionRequest { public string attachmentSlot; public string attachmentItemId; }
[Serializable] public sealed class LoadoutAttachmentsRequest { public long expectedVersion; public string weaponSlot; public string weaponItemId; public AttachmentSelectionRequest[] attachments; }
[Serializable] public sealed class CatalogItemDto
{
    public string itemId; public string itemType; public string slotType; public string displayName; public string description; public string assetKey;
    public long priceCoins; public int unlockLevel; public bool isActive; public bool isOwned; public bool isImplemented; public string calibrationKey;
    // Shop=商城可购；Initial=初始解锁（2026-09-07 起进入目录响应，仓库列表依赖此行）
    public string acquisitionSource;
}
[Serializable] public sealed class InventoryItemDto { public string itemId; public int quantity; public CatalogItemDto item; }
[Serializable] public sealed class InventoryDto { public long coins; public InventoryItemDto[] items; }
[Serializable] public sealed class ShopCatalogDto { public long coins; public int level; public CatalogItemDto[] items; }
// 每玩家设置偏好（键位/音量/灵敏度等；键值对开放扩展，后端 UserSetting 表）
[Serializable] public sealed class UserSettingsDto { public System.Collections.Generic.Dictionary<string, string> values; }
[Serializable] public sealed class SaveSettingsRequest { public System.Collections.Generic.Dictionary<string, string> values = new(); }
[Serializable] public sealed class PurchaseRequest { public string itemId; public int quantity = 1; public string idempotencyKey; }
[Serializable] public sealed class PurchaseResultDto
{
    public string purchaseId; public string itemId; public int quantity; public long unitPriceCoins; public long totalPriceCoins; public long coins; public bool replayed; public InventoryItemDto item;
}
[Serializable] public sealed class AttachmentCompatibilityDto { public string weaponId; public string attachmentId; public string slotType; public bool isImplemented; public string calibrationKey; }
[Serializable] public sealed class LoadoutAttachmentsDto { public long version; public LoadoutAttachmentDto[] attachments; }
// Docs/23 P2（G5）契约对齐（以后端 Contracts.cs L77-94 为准，字段名逐字小驼峰）：
// 请求去掉旧 score（后端无此字段），新增服务器权威 durationSeconds/isWin
// C3（Docs/27 §7.2）：matchId=房间比赛身份（KillRace 兼容提交必带；TDM 自报被后端拒绝）、teamId=对账辅助
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
// 战绩历史（2026-09-17 热更试点 P3，契约对齐后端 Contracts.cs MatchHistoryPageDto）
[Serializable] public sealed class MatchHistoryEntryDto { public string playedAtUtc = ""; public bool isWin; public int kills; public int deaths; public int score; public int xpEarned; public int coinsEarned; }
[Serializable] public sealed class CareerSummaryDto { public int totalMatches; public int wins; public long totalKills; public long totalDeaths; public long totalXp; public long totalCoins; public double winRate; }
[Serializable] public sealed class MatchHistoryPageDto { public int page; public int pageSize; public int totalCount; public int totalPages; public CareerSummaryDto summary; public MatchHistoryEntryDto[] matches; }
[Serializable] public sealed class ProblemDetailsDto
{
    public string title; public int status; public string detail; public string code; public string traceId; public System.Collections.Generic.Dictionary<string, string[]> errors;
}
[Serializable] public sealed class HealthDto { public string status; public string database; }

// ---- 房间聊天（Docs/27 §8 HTTP 传输：Waiting/Starting/Returning；InMatch 收发均 409 走 Owner RPC，
// 见 Game.Gameplay NetworkCombatAuthority/ChatRelayCore——UI 侧两传输共用同一消息形状）----
[Serializable] public sealed class RoomChatSendRequest { public string channel; public string body; public string clientMessageId; }
[Serializable] public sealed class RoomChatMessageDto
{
    public ulong seq; public string channel;
    // 2026-09-10 审计 §4：后端 ChatMessageDto.SenderUserId 为 long?——系统消息（入房/开局）写 null，
    // 无发送者是正常语义。ApiClient 实际用 Newtonsoft 反序列化（非 JsonUtility），
    // 不可空 long 遇 null 会抛异常炸掉整批 ChatFeed → 显示为 InvalidJson。
    public long? senderUserId; public string senderUsername; public string teamId;
    public string body; public string sentAtUtc; public string clientMessageId;
}
[Serializable] public sealed class RoomChatFeedDto { public ulong cursor; public RoomChatMessageDto[] messages; }

// ---- 好友系统（2026-09-20 需求2：用户名#编码 查找 + 请求-同意制 + 在线状态；字段名逐字小驼峰对齐后端）----
[Serializable] public sealed class FriendSendRequest { public string query; }
/// <summary>在线状态字面值（对齐后端 FriendPresence）：InMatch/InRoom 优先于 Online（120s 窗口）/Offline。</summary>
public static class FriendPresence
{
    public const string Offline = "Offline";
    public const string Online = "Online";
    public const string InRoom = "InRoom";
    public const string InMatch = "InMatch";
}
[Serializable] public sealed class FriendEntryDto { public long userId; public string username; public string identityTag; public string presence; }
[Serializable] public sealed class FriendRequestEntryDto { public long requestId; public long userId; public string username; public string identityTag; public System.DateTime createdAtUtc; }
[Serializable] public sealed class FriendListDto { public FriendEntryDto[] friends; public FriendRequestEntryDto[] incoming; public FriendRequestEntryDto[] outgoing; }
}
// ---- 房间契约（Docs/27 v1.2 CF 等待房间：字段名逐字小驼峰） ----
// CreateRoomRequest：全部字段逐字段白名单校验（后端）；创建/加入只进 Waiting，不再返回票据
[Serializable] public sealed class CreateRoomRequest
{
    public int maxPlayers = 16; public string mode = "TDM"; public string mapId = "arena";
    public int killTarget = 100; public int timeLimitMinutes = 10;
    // 2026-09-15 P0-A：客户端应用协议代际（后端冻结在房间上——实例租用按协议筛选；
    // null/空 = 旧客户端）。值由 Game.UI 调用方填 GameProtocolIdentity.ProtocolId。
    public string clientProtocolId;
}
[Serializable] public sealed class GameRoomDto
{
    public long roomId; public string roomCode; public string leaderUsername;
    public int joinedPlayers; public int maxPlayers; public string status;
    public DateTime createdAtUtc;
    // Docs/27 v1.2 只读扩展（Q03 等待房间页展示/轮询判定）
    public string mode; public string mapId; public int killTarget; public int timeLimitMinutes;
    public long roomVersion; public string matchId; public int matchGeneration;
}
[Serializable] public sealed class RoomMemberDto
{
    public long userId; public string username; public string teamId; public bool isReady;
    public bool isLeader; public DateTime joinedAtUtc;
}
[Serializable] public sealed class RoomSelfDto { public long userId; public string teamId; public bool isReady; }
/// <summary>战斗连接信息（仅成员在 Starting/InMatch 下发；明文票据只出现一次，禁止日志/落盘）。</summary>
[Serializable] public sealed class RoomConnectionInfoDto
{
    public string serverAddress; public int serverPort; public string joinTicket;
    public string ticketExpiresAtUtc; public string matchId; public int matchGeneration;
}
/// <summary>详情/加入/选边/准备/设置响应：房间快照。connection 仅在可连接状态出现（Docs/27 §5.3）。</summary>
[Serializable] public sealed class RoomSnapshotDto
{
    public GameRoomDto room; public RoomMemberDto[] members; public RoomSelfDto you;
    public RoomConnectionInfoDto connection;
}
[Serializable] public sealed class RoomJoinRequest { public string teamId; public string clientProtocolId; }
[Serializable] public sealed class RoomTeamRequest { public string teamId; }
[Serializable] public sealed class RoomReadyRequest { public bool isReady; }
[Serializable] public sealed class RoomSettingsRequest
{
    public string mode; public string mapId; public int? killTarget; public int? timeLimitMinutes; public int? maxPlayers;
}
[Serializable] public sealed class RoomReturnRequest { public string matchId; }
[Serializable] public sealed class StartRosterEntryDto { public long userId; public string teamId; }
/// <summary>开局响应（Docs/27 §5.5）：比赛身份 + 冻结规则 + 名单 + 发起者自己的连接信息。</summary>
[Serializable] public sealed class StartMatchDto
{
    public string matchId; public int matchGeneration; public long roomVersion;
    public string mapId; public string mode; public int killTarget; public int timeLimitMinutes;
    public RoomConnectionInfoDto connection; public StartRosterEntryDto[] roster;
}
[Serializable] public sealed class MapCatalogDto
{
    public string mapId; public string displayName; public string sceneName;
    public string[] modes; public int maxCapacity; public string[] spawnGroups;
    public string contentVersion; public string contentHash; public string availability;
}
[Serializable] public sealed class RoomMatchResultPlayerViewDto
{
    public long userId; public string username; public string teamId; public int kills; public int deaths; public int assists;
    /// <summary>逐玩家胜负（R2 审计修复）：KillRace 个人胜者/平局由该字段承载，结果卡按模式消费。</summary>
    public bool isWin;
}
[Serializable] public sealed class RoomMatchResultViewDto
{
    public string matchId; public string status; public string winnerTeam;
    public int durationSeconds; public RoomMatchResultPlayerViewDto[] players;
}

[System.Serializable] public sealed class JoinRoomByCodeRequest { public string roomCode; public string teamId; public string clientProtocolId; }
