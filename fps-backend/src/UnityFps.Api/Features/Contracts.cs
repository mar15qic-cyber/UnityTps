using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using UnityFps.Api.Data;

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
public sealed record PlayerProfileDto(string Username, string IdentityTag, int Level, int Xp, int XpToNextLevel, long Coins);
public sealed record LoadoutAttachmentDto(string WeaponSlot, string AttachmentSlot, string AttachmentItemId);
public sealed record LoadoutDto(string PrimaryWeaponId, string SecondaryWeaponId, string? ThrowableId, long Version, LoadoutAttachmentDto[] Attachments);


public sealed class LoadoutRequest
{
    [Required, StringLength(64)] public string PrimaryWeaponId { get; set; } = string.Empty;
    [Required, StringLength(64)] public string SecondaryWeaponId { get; set; } = string.Empty;
    [StringLength(64)] public string? ThrowableId { get; set; }
    [Range(0, long.MaxValue)] public long ExpectedVersion { get; set; }
}

public sealed record CatalogItemDto(string ItemId, string ItemType, string SlotType, string DisplayName, string Description,
    string AssetKey, long PriceCoins, int UnlockLevel, bool IsActive, bool IsOwned, bool IsImplemented, string CalibrationKey,
    string AcquisitionSource);
public sealed record InventoryItemDto(string ItemId, int Quantity, CatalogItemDto Item);
public sealed record InventoryDto(long Coins, InventoryItemDto[] Items);
public sealed record ShopCatalogDto(long Coins, int Level, CatalogItemDto[] Items);

// ---- 每玩家设置偏好（键位/音量/灵敏度等；键值对开放扩展）----

public sealed class SaveSettingsRequest
{
    [Required] public Dictionary<string, string> Values { get; set; } = new();
}

public sealed record UserSettingsDto(Dictionary<string, string> Values);
public sealed class PurchaseRequest
{
    [Required, StringLength(64)] public string ItemId { get; set; } = string.Empty;
    [Range(1, 1)] public int Quantity { get; set; } = 1;
    [Required, StringLength(96, MinimumLength = 8)] public string IdempotencyKey { get; set; } = string.Empty;
}
public sealed record PurchaseResultDto(string PurchaseId, string ItemId, int Quantity, long UnitPriceCoins,
    long TotalPriceCoins, long Coins, bool Replayed, InventoryItemDto Item);

// ---- 房间（Docs/27 v1 CF 契约：等待房间语义；旧"创建即连 DS"已退役，租 DS/签票移至 start）----

public sealed class CreateRoomRequest
{
    [Range(2, 16)] public int MaxPlayers { get; set; } = 16;
    [StringLength(16)] public string Mode { get; set; } = GameModes.Tdm;
    [StringLength(32)] public string MapId { get; set; } = MapCatalog.DefaultMapId;
    public int KillTarget { get; set; } = 100;
    public int TimeLimitMinutes { get; set; } = 10;
    // 旧 client-hosted 字段：仅为旧客户端请求体解析兼容保留，服务端一律忽略
    [StringLength(64)] public string? HostAddress { get; set; }
    [Range(1024, 65535)] public int HostPort { get; set; } = 7770;
    /// <summary>客户端应用协议代际（2026-09-15 P0-A）：冻结到房间（ExpectedProtocolId），
    /// 入房者必须一致、实例租用按此筛选；null/空 = 旧客户端（只可租未申报协议的旧 DS）。</summary>
    [StringLength(32)] public string? ClientProtocolId { get; set; }
}

/// <summary>加入房间请求：teamId 仅 TDM 有意义（Waiting 选边 / InMatch 补人指定队伍），缺省自动分配未满队。
/// ClientProtocolId 与房间冻结值不一致 → 409 PROTOCOL_MISMATCH（混版本客户端不进同一房）。</summary>
public sealed class RoomJoinRequest
{
    [StringLength(8)] public string? TeamId { get; set; }
    [StringLength(32)] public string? ClientProtocolId { get; set; }
}

public sealed class RoomTeamRequest
{
    [Required, StringLength(8)] public string TeamId { get; set; } = string.Empty;
}

public sealed class RoomReadyRequest
{
    public bool IsReady { get; set; }
}

/// <summary>返房 ack 请求（Docs/27 §5.7）：携带开局响应中的 matchId。</summary>
public sealed class RoomReturnRequest
{
    [Required, StringLength(64, MinimumLength = 8)] public string MatchId { get; set; } = string.Empty;
}

/// <summary>房主设置变更：null 字段保持不变；任一白名单外取值整体 422（Docs/27 §5.4）。</summary>
public sealed class RoomSettingsRequest
{
    [StringLength(16)] public string? Mode { get; set; }
    [StringLength(32)] public string? MapId { get; set; }
    public int? KillTarget { get; set; }
    public int? TimeLimitMinutes { get; set; }
    [Range(2, 16)] public int? MaxPlayers { get; set; }
}

/// <summary>列表/房间信息统一形状（只读投影），绝不携带 ticket/密钥类字段。</summary>
public sealed record GameRoomDto([property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? RoomCode, string LeaderUsername, int JoinedPlayers, int MaxPlayers,
    string Status, DateTime CreatedAtUtc, string Mode, string MapId, int KillTarget, int TimeLimitMinutes,
    long RoomVersion, string? MatchId, int MatchGeneration, long RoomId = 0);

public sealed record RoomMemberDto(long UserId, string Username, string TeamId, bool IsReady, bool IsLeader, DateTime JoinedAtUtc);
public sealed record RoomSelfDto(long UserId, string TeamId, bool IsReady);

/// <summary>战斗连接信息（仅 roster/补人成员在 Starting/InMatch/Returning 可得，Docs/27 §5.2/§5.3）。</summary>
public sealed record RoomConnectionInfoDto(string ServerAddress, int ServerPort, string JoinTicket,
    DateTime TicketExpiresAtUtc, string MatchId, int MatchGeneration);

/// <summary>详情/加入/选边/准备/设置响应：房间快照。connection 仅在可连接状态出现。</summary>
public sealed record RoomSnapshotDto(GameRoomDto Room, RoomMemberDto[] Members, RoomSelfDto You, RoomConnectionInfoDto? Connection);

public sealed record StartRosterEntryDto(long UserId, string TeamId);

// ---- 房间聊天（Docs/27 §8；HTTP 等待房间传输，局内为 Owner RPC——同一消息模型）----

public sealed class ChatSendRequest
{
    /// <summary>All / Team；System 无客户端发送路径。</summary>
    [Required, StringLength(8)] public string Channel { get; set; } = string.Empty;
    /// <summary>不加 [Required]：空白/超长由 ChatPolicy 权威判定并返回 422 CHAT_REJECTED（而非 400 模型错误）。</summary>
    public string Body { get; set; } = string.Empty;
    /// <summary>客户端 UUID：端到端去重与本地乐观消息对账。</summary>
    [Required, StringLength(64, MinimumLength = 8)] public string ClientMessageId { get; set; } = string.Empty;
}

public sealed record ChatMessageDto(ulong Seq, string Channel, long? SenderUserId, string? SenderUsername,
    string? TeamId, string Body, DateTime SentAtUtc, string? ClientMessageId);

public sealed record ChatFeedDto(ulong Cursor, ChatMessageDto[] Messages);

/// <summary>开局响应（Docs/27 §5.5）：比赛身份 + 冻结规则 + 名单 + 发起者自己的连接信息。</summary>
public sealed record StartMatchDto(string MatchId, int MatchGeneration, long RoomVersion, string MapId, string Mode,
    int KillTarget, int TimeLimitMinutes, RoomConnectionInfoDto Connection, StartRosterEntryDto[] Roster);

/// <summary>创建/加入房间响应（旧 Dedicated 契约形状，仅存于 git 历史测试；运行时已被 RoomSnapshotDto 取代）。</summary>
public sealed record RoomConnectionDto(GameRoomDto Room, string ServerAddress, int ServerPort,
    string JoinTicket, DateTime TicketExpiresAtUtc);

// ---- Dedicated Server 控制面（Docs/27 §2.2/§2.3 冻结；X-Server-Key 鉴权，与玩家 JWT 无关）----

public sealed class ServerInstanceRegisterRequest
{
    [Required, StringLength(64, MinimumLength = 4)] public string InstanceId { get; set; } = string.Empty;
    [Required, StringLength(64)] public string Address { get; set; } = string.Empty;
    [Range(1024, 65535)] public int Port { get; set; } = 7770;
    [Range(1, 64)] public int Capacity { get; set; } = 8;
    [StringLength(32)] public string? BuildVersion { get; set; }
    /// <summary>应用协议代际（2026-09-15 P0-A）：存储于实例行并进入池诊断；
    /// 房间按协议期望筛选可租实例。null/空 = 旧 DS（只可租协议期望为 null 的旧房间）。</summary>
    [StringLength(32)] public string? ProtocolId { get; set; }
    /// <summary>Phase 8：实例绑定地图（Docs/27 §4 目录键）。null/空 = 旧 DS（租用匹配按 arena 处理）。
    /// 控制面 JSON——不属于 FishNet wire，协议不递增，但 DS 与后端需同批部署。</summary>
    [StringLength(32)] public string? MapId { get; set; }
}

public sealed record ServerInstanceRegisterDto(string InstanceId, string State, int HeartbeatIntervalSeconds);

public sealed class ServerInstanceHeartbeatRequest
{
    /// <summary>绑定中的房间码；无绑定时留空。</summary>
    [StringLength(6)] public string? RoomCode { get; set; }
    [Range(0, 64)] public int CurrentPlayers { get; set; }
    [Required, StringLength(16)] public string State { get; set; } = string.Empty;
    /// <summary>Phase 8：实例地图同步（空 = 旧 DS，不覆盖已存值）。</summary>
    [StringLength(32)] public string? MapId { get; set; }
}

public sealed class JoinTicketConsumeRequest
{
    [Required, StringLength(64)] public string InstanceId { get; set; } = string.Empty;
    [Required, StringLength(128)] public string Ticket { get; set; } = string.Empty;
}

/// <summary>
/// 票据校验结果：valid=false 时 errorCode 为冻结的 TICKET_* 之一（失败响应不携带任何身份/配装——防泄露）。
/// valid=true 时附带账号权威配装快照（2026-09-08 追加 P0 §6 二.1）：Dedicated Server 据此把网络玩家
/// Arsenal 严格配置为账号实际两槽（primary/secondary + 影响属性/枪模的附件），失败不得回退调试 Arsenal。
/// CF（C3/Q04）：附比赛身份与规则快照——DS 据此做队伍初始化、模式分派与终局上报；
/// MatchId 空 = 无比赛（旧语义）；规则字段 0/null = 旧票据语义（DS 按 KillRace 默认值兜底）。
/// </summary>
public sealed record JoinTicketConsumeDto(bool Valid, string? RoomCode, long? UserId, string? Username,
    DateTime? ExpiresAtUtc, long? SessionId, string? ErrorCode, LoadoutDto? Loadout = null,
    string? MatchId = null, int? MatchGeneration = null, string? TeamId = null,
    string? MatchMode = null, int? KillTarget = null, int? TimeLimitMinutes = null, int? MaxPlayers = null);

// ---- CF 等待房间：地图目录与终局结果（Docs/27 §4/§7）----

/// <summary>地图目录项（服务端常量白名单；客户端/DS 镜像只读）。</summary>
public sealed record MapCatalogDto(string MapId, string DisplayName, string SceneName, string[] Modes,
    int MaxCapacity, string[] SpawnGroups, string? ContentVersion = null, string? ContentHash = null, string Availability = "ready");

/// <summary>DS 权威终局逐玩家结果行。IsWin = 逐玩家胜负（R2）：KillRace 个人胜者 winnerTeam=null 时
/// 由该字段承载；TDM 由胜队推导。旧 DS 上报缺字段 → null = 按胜队/无胜队兜底。</summary>
public sealed record MatchPlayerResultDto(long UserId, string TeamId, int Kills, int Deaths, int Assists,
    int ParticipationSeconds, bool RewardEligible, int? LeftAtSeconds = null, bool? IsWin = null);

/// <summary>DS 终局上报请求（X-Server-Key；按 matchId 幂等，Docs/27 §7.2）。</summary>
public sealed class MatchResultReportRequest
{
    [Required, StringLength(64, MinimumLength = 8)] public string MatchId { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    /// <summary>胜队（Red/Blue）；null = 平局/无胜者。</summary>
    [StringLength(8)] public string? WinnerTeam { get; set; }
    [Required, MinLength(1)] public List<MatchPlayerResultDto> Players { get; set; } = [];
}

/// <summary>玩家侧终局结果查询（Returning 期间轮询；KillRace 由 MatchRecord 聚合）。</summary>
public sealed record RoomMatchResultViewDto(string MatchId, string Status, string? WinnerTeam,
    int DurationSeconds, RoomMatchResultPlayerViewDto[] Players);

public sealed record RoomMatchResultPlayerViewDto(long UserId, string Username, string TeamId,
    int Kills, int Deaths, int Assists, bool IsWin = false);

/// <summary>DS 终局上报处置结果（幂等：Replayed=true 表示此前已登记，零重复副作用）。</summary>
public sealed record MatchResultReportDto(string MatchId, string Status, bool Replayed, bool RewardsApplied);

// ---- 实例池开发诊断（P0 租约闭环 2026-09-08；RequireServerKey 保护，不含密钥/票据）----

/// <summary>实例池诊断汇总：requestedCapacity 视角下的可租性分解（NO_SERVER_AVAILABLE 归因数据源）。
/// ReadyFreshProtocolMismatch：心跳新鲜且容量足够但协议与期望不一致的 Ready 实例数（P0-A 归因）。</summary>
public sealed record ServerInstancePoolSummaryDto(
    int Total, int ReadyFresh, int ReadyStale, int Reserved, int InMatch, int Offline,
    int StaleTotal, int ReadyFreshCapacityShort, int RequestedCapacity,
    int ReadyFreshProtocolMismatch = 0);

/// <summary>单个实例诊断行（不含密钥/票据；heartbeatAgeSeconds 供人工判读新鲜度）。
/// ProtocolId：实例申报的应用协议代际（null = 旧 DS；P0-A 协议筛选可见性）。</summary>
public sealed record ServerInstanceDiagnosticDto(
    string InstanceId, string State, string? RoomCode, int CurrentPlayers, int Capacity,
    int HeartbeatAgeSeconds, bool Fresh, string? ProtocolId = null, string? BuildVersion = null, string? MapId = null, string? Address = null, int Port = 0);

/// <summary>实例池状态查询响应（GET /api/server-instances/pool，仅 X-Server-Key）。</summary>
public sealed record ServerInstancePoolDto(ServerInstancePoolSummaryDto Summary, ServerInstanceDiagnosticDto[] Instances);

/// <summary>
/// 服务器上报玩家掉线（Day2 掉线成员清理）：DS 在远端连接停止（主动退出/超时/被踢）后
/// 按该连接的认证身份档案上报；幂等——成员已不存在同样返回 200 + 权威事实（P0 租约闭环）。
/// userId 同时接受 JSON 数字与字符串（DS 侧身份档案以字符串承载后端数字 id）。
/// </summary>
public sealed class ServerPlayerDisconnectReportRequest
{
    [Required, StringLength(6)] public string RoomCode { get; set; } = string.Empty;
    [Range(1, long.MaxValue), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long UserId { get; set; }
    /// <summary>本次连接成功消费的 ServerJoinTicket.Id。用于拒绝旧连接的迟到掉线上报。</summary>
    [Range(0, long.MaxValue), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long SessionId { get; set; }
}

/// <summary>
/// 掉线上报处置结果（2026-09-08 P0 实例租约闭环）：真实移除与幂等 no-op 一律返回处置后的权威事实，
/// DS 只有在 instanceState=Ready 且 remainingPlayers=0 时才允许清空本地房间绑定转 Ready；
/// 其余事实（Reserved/InMatch + 剩余人数）必须保留绑定。404/409 不携带本 DTO（上报方不得猜 Ready）。
/// </summary>
public sealed record ServerPlayerDisconnectReportDto(string RoomCode, int RemainingPlayers, string InstanceState);
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
    // The gunsmith may preview an owned weapon that is not currently equipped.
    // Omitted by older clients: keep the existing equipped-weapon behavior.
    [StringLength(64)] public string? WeaponItemId { get; set; }
    public AttachmentSelectionRequest[] Attachments { get; set; } = [];
}

public sealed class MatchSubmissionRequest
{
    [Required, StringLength(64, MinimumLength = 8)] public string ClientMatchId { get; set; } = string.Empty;
    [Range(0, int.MaxValue)] public int Kills { get; set; }
    [Range(0, int.MaxValue)] public int Deaths { get; set; }
    [Range(0, int.MaxValue)] public int DurationSeconds { get; set; }
    public bool IsWin { get; set; }
    /// <summary>房间比赛 ID（Docs/27 §7.2）：带此值走比赛绑定校验 + 放宽校验上限（TDM/新版 KillRace）；null = 旧路径。</summary>
    [StringLength(64)] public string? MatchId { get; set; }
    /// <summary>队伍（仅 TDM 有意义，服务端以 roster 权威为准，此字段仅作对账辅助）。</summary>
    [StringLength(8)] public string? TeamId { get; set; }
}

public sealed record PassLevelUpDto(int Level, string? RewardType, string? ItemId, int CoinsAmount);
public sealed record UnlockedAchievementDto(string AchievementId, string DisplayName, int PassXpReward);

public sealed record MatchResultDto(
    int XpEarned, int LevelUps, long Coins, int CoinsEarned,
    int PassXpEarned, int PassLevel, int PassXp, int PassXpToNextLevel,
    PassLevelUpDto[] PassLevelUps, string[] NewAttachments,
    UnlockedAchievementDto[] UnlockedAchievements, bool Replayed,
    PlayerProfileDto Profile);

// ---- 战绩历史（2026-09-17 热更试点 P3：大厅战绩页数据源；本人视角，服务端聚合）----

public sealed record MatchHistoryEntryDto(
    string PlayedAtUtc, bool IsWin, int Kills, int Deaths, int Score, int XpEarned, int CoinsEarned);
public sealed record CareerSummaryDto(
    int TotalMatches, int Wins, long TotalKills, long TotalDeaths, long TotalXp, long TotalCoins, double WinRate);
public sealed record MatchHistoryPageDto(
    int Page, int PageSize, int TotalCount, int TotalPages, CareerSummaryDto Summary, MatchHistoryEntryDto[] Matches);

// ---- 通行证（Docs/17 §4.4）----

public sealed record PassRewardDto(int Level, string RewardType, string? ItemId, int CoinsAmount, bool Granted);
public sealed record PassAchievementDto(string Id, string DisplayName, string Description,
    string TargetMetric, int TargetValue, int Progress, bool Unlocked, int PassXpReward);
public sealed record PassDto(string SeasonId, int Level, int Xp, int XpToNextLevel, int MaxLevel,
    PassRewardDto[] Rewards, PassAchievementDto[] Achievements);
public sealed record AchievementDto(string Id, string DisplayName, string Description,
    string TargetMetric, int TargetValue, int Progress, bool Unlocked, int PassXpReward);

// ---- 好友系统（2026-09-20 需求2：用户名#编码 查找 + 请求-同意制 + 在线状态）----

/// <summary>发送申请的查询串：必须形如 "用户名#1234"（按最后一个 # 切分，编码 4 位数字）。</summary>
public sealed class FriendSendRequest
{
    [Required, StringLength(64, MinimumLength = 3)] public string Query { get; set; } = string.Empty;
}

/// <summary>好友在线状态字面值（客户端镜像）：对局中/房间中 优先于 在线（LastSeenUtc 120s 内）/离线。</summary>
public static class FriendPresence
{
    public const string Offline = "Offline";
    public const string Online = "Online";
    public const string InRoom = "InRoom";
    public const string InMatch = "InMatch";
}

public sealed record FriendEntryDto(long UserId, string Username, string IdentityTag, string Presence);
public sealed record FriendRequestEntryDto(long RequestId, long UserId, string Username, string IdentityTag, DateTime CreatedAtUtc);
public sealed record FriendListDto(FriendEntryDto[] Friends, FriendRequestEntryDto[] Incoming, FriendRequestEntryDto[] Outgoing);

public sealed class JoinRoomByCodeRequest : RoomCodeJoinBase { }
public class RoomCodeJoinBase { public string RoomCode { get; set; } = ""; public string? TeamId { get; set; } public string? ClientProtocolId { get; set; } }
