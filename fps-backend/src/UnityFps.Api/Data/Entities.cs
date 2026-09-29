namespace UnityFps.Api.Data;

using UnityFps.Api.Features;

public sealed class UserAccount
{
    public bool Disabled { get; set; }
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    /// <summary>好友查找编码（用户名#编码 的 # 后缀，注册时随机生成 4 位数字含前导零，不可修改）。
    /// 用户名全局唯一 ⇒ 「名字+编码」组合唯一；编码本身不要求全局唯一。</summary>
    public string IdentityTag { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    /// <summary>单活会话版本（2026-09-17 实测缺口）：每次登录 +1 并写入 JWT `tv` 声明；
    /// 认证中间件比对声明与库值，旧客户端 token 立即失效（同账号多端登录=后者顶替前者）。</summary>
    public long TokenVersion { get; set; }
    /// <summary>最近一次活跃时间（好友在线状态锚点）：GET /api/friends 与房间心跳等热点端点节流刷新；
    /// 在场判定：房间成员资格（房间中/对局中）优先于该时间（120s 内=在线）。</summary>
    public DateTime? LastSeenUtc { get; set; }
    public PlayerProfile? Profile { get; set; }
    public PlayerLoadout? Loadout { get; set; }
    public PlayerWallet? Wallet { get; set; }
    public List<PlayerInventoryItem> Inventory { get; set; } = [];
    public List<ShopPurchase> Purchases { get; set; } = [];
    public List<MatchRecord> Matches { get; set; } = [];
    public List<PlayerPass> Passes { get; set; } = [];
}

public sealed class PlayerProfile
{
    public long UserId { get; set; }
    public int Level { get; set; } = 1;
    public int Xp { get; set; }
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
    /// <summary>Shop = 可购买武器; PassReward = 通行证奖励配件; Initial = 初始解锁.</summary>
    public string AcquisitionSource { get; set; } = "Shop";
}

public sealed class PlayerWallet
{
    public long UserId { get; set; }
    public long Coins { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
}

/// <summary>
/// 每玩家设置偏好（2026-09-07）：键值对按用户隔离保存（键位/音量/灵敏度等），
/// 键集合开放——后续新增设置项直接加键即可，无需迁移。
/// </summary>
public sealed class UserSetting
{
    public long UserId { get; set; }
    public string SettingKey { get; set; } = string.Empty;
    public string SettingValue { get; set; } = string.Empty;
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
    public int CoinsEarned { get; set; }
    public int PassXpEarned { get; set; }
    public bool IsWin { get; set; }
    /// <summary>客户端生成的一局唯一 ID，用于结算幂等（Docs/17 §4.5）.</summary>
    public string? ClientMatchId { get; set; }
    /// <summary>房间比赛 ID（Docs/27 §7.2；带此值的提交走 TDM/新版校验上限，null = 旧路径）.</summary>
    public string? MatchId { get; set; }
    public DateTime PlayedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
}

/// <summary>武器—配件兼容矩阵（权威放行/拒绝）。一行 = 某武器某槽位可装某配件；IsImplemented=false 表示尚未完成挂点校准（诚实呈现，Docs/15 §9）.</summary>
public sealed class AttachmentCompat
{
    public string WeaponItemId { get; set; } = string.Empty;
    public string AttachmentItemId { get; set; } = string.Empty;
    public string SlotType { get; set; } = string.Empty;
    public bool IsImplemented { get; set; }
    public string CalibrationKey { get; set; } = string.Empty;
    public CatalogItem WeaponItem { get; set; } = null!;
    public CatalogItem AttachmentItem { get; set; } = null!;
}

/// <summary>
/// 联机房间（Docs/27 §2 Dedicated 语义）：房间只是"成员名单 + 绑定的服务器实例"，
/// 不再承载房主直连地址。生命周期由绑定实例的心跳决定（玩家心跳已废弃）。
/// </summary>
public sealed class GameRoom
{
    public long Id { get; set; }
    /// <summary>房间码（6 位大写字母数字，用户口播/输入用）。</summary>
    public string RoomCode { get; set; } = string.Empty;
    /// <summary>房间 leader（Docs/27 §2.1：可转移的大厅管理身份；物理列沿用旧名，语义已不是服务器拥有者）。</summary>
    public long HostUserId { get; set; }
    /// <summary>房间 leader 用户名（同上，物理列沿用旧名 HostUsername）。</summary>
    public string HostUsername { get; set; } = string.Empty;
    /// <summary>旧 client-hosted 字段：Dedicated 拓扑下不再接受玩家上报，仅保留列避免迁移删数据。</summary>
    public string HostAddress { get; set; } = string.Empty;
    /// <summary>旧 client-hosted 字段：同上，忽略玩家上报值。</summary>
    public int HostPort { get; set; } = 7770;
    public int MaxPlayers { get; set; } = 8;
    public int JoinedPlayers { get; set; } = 1;
    /// <summary>旧字段：开闭语义改由 Status 承载，创建时恒 true，仅作遗留兼容。</summary>
    public bool IsOpen { get; set; } = true;
    /// <summary>房间状态（Docs/27 v1 状态机，见 RoomStatus）。</summary>
    public string Status { get; set; } = RoomStatus.Waiting;
    /// <summary>对局模式（Docs/27 §1 白名单）。</summary>
    public string Mode { get; set; } = GameModes.Tdm;
    /// <summary>地图 ID（地图目录白名单，禁止硬编码场景路径）。</summary>
    public string MapId { get; set; } = MapCatalog.DefaultMapId;
    public int KillTarget { get; set; } = 100;
    public int TimeLimitMinutes { get; set; } = 10;
    /// <summary>房间可变状态版本：设置变更/状态迁移时 +1（Docs/27 §1）。</summary>
    public long RoomVersion { get; set; } = 1;
    /// <summary>当前比赛（Starting/InMatch/Returning 期间非空）。</summary>
    public string? CurrentMatchId { get; set; }
    /// <summary>房间内已开始的第几局；每次成功 start +1，初始 0。</summary>
    public int MatchGeneration { get; set; }
    /// <summary>上一局的 matchId（返房后保留供结果查询）。</summary>
    public string? LastMatchId { get; set; }
    /// <summary>本房间冻结的客户端应用协议代际（2026-09-15 P0-A：建房者申报，入房者必须一致；
    /// 实例租用按此筛选——协议不匹配的 DS 对本房间不可见。null = 旧客户端建的房，只能租未申报协议的旧 DS）。</summary>
    public string? ExpectedProtocolId { get; set; }
    /// <summary>最近一次状态迁移时间：Starting/Returning 超时判定基准。</summary>
    public DateTime StateChangedAtUtc { get; set; }
    /// <summary>租用时绑定的服务器实例；房间过期（实例心跳失联）后清理。</summary>
    public long? ServerInstanceId { get; set; }
    public ServerInstance? ServerInstance { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>旧玩家心跳列：不再维护（成员保活见 GameRoomMember.LastSeenUtc，房间过期由实例心跳/成员过期决定）。</summary>
    public DateTime LastHeartbeatUtc { get; set; }
    public UserAccount Host { get; set; } = null!;
    public List<GameRoomMember> Members { get; set; } = [];
    public List<RoomMatchRoster> Rosters { get; set; } = [];
}

/// <summary>
/// 房间状态字面值。Docs/27 v1（CF 契约）状态机：Waiting → Starting → InMatch → Returning → Waiting，终态 Closed。
/// WaitingForServer/WaitingForPlayers 为旧 Dedicated 契约遗留值：数据库迁移统一映射为 Waiting，
/// 运行时读取按 NormalizeStatus 防御性归一，新代码禁止写入旧值。
/// </summary>
public static class RoomStatus
{
    public const string Waiting = "Waiting";
    public const string Starting = "Starting";
    public const string InMatch = "InMatch";
    public const string Returning = "Returning";
    public const string Closed = "Closed";
    // 旧值（仅迁移映射与读取兼容）
    public const string WaitingForServer = "WaitingForServer";
    public const string WaitingForPlayers = "WaitingForPlayers";

    /// <summary>旧值 → Waiting 的读取归一；新值原样返回。</summary>
    public static string Normalize(string status) => status switch
    {
        WaitingForServer or WaitingForPlayers => Waiting,
        _ => status,
    };
}

/// <summary>队伍标识字面值（Docs/27 §1 冻结值）。KillRace 全员 None。类型名避开实体属性 TeamId 重名。</summary>
public static class Teams
{
    public const string None = "None";
    public const string Red = "Red";
    public const string Blue = "Blue";
    public static readonly string[] All = [None, Red, Blue];
}

/// <summary>对局模式白名单（Docs/27 §1）。KillRace = 现有 FFA 击杀竞赛（回归路径），TDM = 团队死斗。</summary>
public static class GameModes
{
    public const string Tdm = "TDM";
    public const string KillRace = "KillRace";
    public static readonly string[] All = [Tdm, KillRace];
    public static bool IsTeamMode(string mode) => mode == Tdm;
}

/// <summary>服务器实例状态字面值（心跳可上报；租用置 Reserved，退役中不可租，释放/重臂回 Ready）。</summary>
public static class InstanceState
{
    public const string Ready = "Ready";
    public const string Reserved = "Reserved";
    public const string InMatch = "InMatch";
    public const string Offline = "Offline";
    // Backend-only tombstone. An expired battle process must use a new identity after restart.
    public const string Fenced = "Fenced";
    /// <summary>退役中（A03，V0）：比赛已终局/返房，DS 旧连接清理与重臂未获权威确认——不可租用；
    /// 仅 DS 重臂后的 Ready 心跳（CurrentPlayers=0）才翻回 Ready 并清绑定回池。</summary>
    public const string Draining = "Draining";
    public static readonly string[] All = [Ready, Reserved, InMatch, Offline, Draining];
}

/// <summary>房间加入记录（一个玩家同时只能在一个房间）。</summary>
public sealed class GameRoomMember
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long UserId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    /// <summary>队伍（Docs/27 §1）；Waiting 可改，InMatch 禁改。</summary>
    public string TeamId { get; set; } = Teams.None;
    public bool IsReady { get; set; }
    /// <summary>成员保活心跳（Waiting 不连 DS，房间活性由成员心跳决定）。</summary>
    public DateTime LastSeenUtc { get; set; }
    /// <summary>入房时的聊天 seq 水位（新成员不补发此前历史，Docs/27 §8.3；Q06 启用）。</summary>
    public ulong ChatJoinSeq { get; set; }
    public GameRoom Room { get; set; } = null!;
    public UserAccount User { get; set; } = null!;
}

/// <summary>
/// 一场比赛的开局名单快照（Docs/27 §3）：start 成功时从成员表冻结；
/// 补人成员无行（不属开局名单）；终局资格/结果聚合以本表为准。
/// </summary>
public sealed class RoomMatchRoster
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public GameRoom Room { get; set; } = null!;
    /// <summary>所属比赛（GUID N 格式，与 GameRoom.CurrentMatchId 同源）。</summary>
    public string MatchId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string TeamId { get; set; } = Teams.None;
    public DateTime IssuedAtUtc { get; set; }
    /// <summary>成员中途退出/掉线移除时回填（资格判定依据，Docs/26 §2.4）。</summary>
    public DateTime? LeftAtUtc { get; set; }
    /// <summary>返房 ack 时间（仅记录客户端已收到终局；不能代替 DS 真实连接退场）。</summary>
    public DateTime? ReturnedAtUtc { get; set; }
}

/// <summary>
/// DS 权威终局结果（Docs/27 §7.2）：按 MatchId 幂等；TDM 奖励发放依据；
/// KillRace 不写本表（沿用客户端提交路径）。
/// </summary>
public sealed class RoomMatchResult
{
    /// <summary>比赛 ID（GUID N 格式）。</summary>
    public string MatchId { get; set; } = string.Empty;
    public long RoomId { get; set; }
    /// <summary>胜队；null = 平局/无胜者（双方同时归零）。</summary>
    public string? WinnerTeam { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime EndedAtUtc { get; set; }
    /// <summary>逐玩家结果（RoomMatchPlayerResultDto[] 序列化，勿手改）。</summary>
    public string PlayersJson { get; set; } = string.Empty;
    /// <summary>奖励发放完成时间；null = 尚未发放（幂等再入点）。</summary>
    public DateTime? RewardsAppliedAtUtc { get; set; }
    /// <summary>首次登记的上报来源实例（R04：重放必须同源，防任意 server-key 实例改写他局）。</summary>
    public string? ReportedByInstanceId { get; set; }
}

/// <summary>
/// Dedicated Server 实例注册表（Docs/27 §2）：Unity Server 进程持 X-Server-Key 注册/心跳；
/// 创建房间时原子租用一个心跳新鲜的 Ready 实例（Ready→Reserved，一实例一房间）。
/// 与 roomLeader（可转移管理身份）、matchAuthority（FishNet 权威逻辑）严格解耦。
/// </summary>
public sealed class ServerInstance
{
    public long Id { get; set; }
    /// <summary>服务器进程自报的稳定标识（如 arena-01）。</summary>
    public string InstanceId { get; set; } = string.Empty;
    /// <summary>客户端可直连地址（服务器注册时上报，玩家上报值一律不采信）。</summary>
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 7770;
    public int Capacity { get; set; } = 8;
    public string? BuildVersion { get; set; }
    /// <summary>应用协议代际（2026-09-15 P0-A：DS register 上报；实例租用按房间协议期望筛选。
    /// null = 旧 DS 二进制——只能被协议期望同为 null 的旧客户端房间租用）。</summary>
    public string? ProtocolId { get; set; }
    /// <summary>Phase 8：实例绑定地图（Docs/27 §4 目录键；DS register/心跳上报，租用按房间 mapId
    /// 匹配，不匹配实例不被租用）。null = 旧 DS 二进制（租用匹配按 arena 处理）。</summary>
    public string? MapId { get; set; }
    /// <summary>Ready / Reserved / InMatch / Offline（心跳可上报；租用置 Reserved，释放回 Ready）。</summary>
    public string State { get; set; } = "Ready";
    public int CurrentPlayers { get; set; }
    /// <summary>当前绑定的房间码（Reserved/InMatch 时非空）。</summary>
    public string? RoomCode { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
    /// <summary>实例心跳时间；超过 TTL 即不再可租（房间随之懒清理）。</summary>
    public DateTime LastHeartbeatUtc { get; set; }
    /// <summary>乐观并发令牌：租用/释放竞态的提供方无关兜底（InMemory 测试无串行化事务）。</summary>
    public long Version { get; set; } = 1;
}

/// <summary>
/// 一次性 opaque 加入票据（Docs/27 §2.3）：32 字节随机数的 Base64Url 只向客户端下发一次，
/// 库内只存 SHA-256 hash；TTL 90 秒，绑定 instance+room+user；consume 事务内写 ConsumedAtUtc。
/// </summary>
public sealed class ServerJoinTicket
{
    public long Id { get; set; }
    /// <summary>SHA-256(明文票据) 小写十六进制（64 字符），唯一索引。</summary>
    public string TicketHash { get; set; } = string.Empty;
    public long ServerInstanceId { get; set; }
    public ServerInstance Instance { get; set; } = null!;
    public string RoomCode { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    /// <summary>签发时的比赛身份（R01）：消费严格比对，旧票不得被解释为下一局票据。</summary>
    public string? MatchId { get; set; }
    /// <summary>签发时的局号（R01，随 MatchId 一并冻结）。</summary>
    public int? MatchGeneration { get; set; }
    /// <summary>乐观并发令牌：双并发 consume 只有一方成功，另一方落到 REPLAYED。</summary>
    public long Version { get; set; } = 1;
}

// ===== 通行证与成就系统（Docs/17 §4.3）=====

/// <summary>玩家通行证进度（每用户每赛季一行）.</summary>
public sealed class PlayerPass
{
    public long UserId { get; set; }
    public string SeasonId { get; set; } = "S1";
    public int PassXp { get; set; }
    public int PassLevel { get; set; } = 1;
    public long Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
}

/// <summary>奖励轨配置（Seeder 维护，S1 共 15 级）.</summary>
public sealed class PassReward
{
    public string SeasonId { get; set; } = string.Empty;
    public int PassLevel { get; set; }
    /// <summary>Attachment / Coins.</summary>
    public string RewardType { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public int CoinsAmount { get; set; }
}

/// <summary>发放幂等记录——保证每级奖励恰好发放一次.</summary>
public sealed class PlayerPassRewardGrant
{
    public long UserId { get; set; }
    public string SeasonId { get; set; } = string.Empty;
    public int PassLevel { get; set; }
    public DateTime GrantedAtUtc { get; set; }
}

/// <summary>成就配置（Seeder 维护）.</summary>
public sealed class AchievementDefinition
{
    public string AchievementId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>累计指标标识：total_kills / total_wins / total_matches / single_match_kills / account_level / pass_level / gunsmith_wins / first_buy.</summary>
    public string TargetMetric { get; set; } = string.Empty;
    public int TargetValue { get; set; }
    public int PassXpReward { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>玩家成就进度.</summary>
public sealed class PlayerAchievement
{
    public long UserId { get; set; }
    public string AchievementId { get; set; } = string.Empty;
    public int Progress { get; set; }
    public DateTime? UnlockedAtUtc { get; set; }
    public int GrantedPassXp { get; set; }
}

// ===== 好友系统（2026-09-20 需求2：用户名#编码 查找 + 请求-同意制 + 在线状态）=====

/// <summary>待处理好友申请（一方一行）。accept 后删除本行并建双向 Friendship；拒绝/撤销直接删除。</summary>
public sealed class FriendRequest
{
    public long Id { get; set; }
    public long FromUserId { get; set; }
    public long ToUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>好友关系（对称存两行：查列表各查各的行；删除删两行）。复合主键 (UserId, FriendId)。</summary>
public sealed class Friendship
{
    public long UserId { get; set; }
    public long FriendId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
