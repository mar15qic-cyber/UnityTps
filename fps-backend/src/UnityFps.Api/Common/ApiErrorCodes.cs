namespace UnityFps.Api.Common;

public static class ApiErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string UsernameTaken = "AUTH_USERNAME_TAKEN";
    public const string InvalidCredentials = "AUTH_INVALID_CREDENTIALS";
    public const string Unauthorized = "AUTH_UNAUTHORIZED";
    public const string InvalidWeapon = "LOADOUT_INVALID_WEAPON";
    public const string ItemNotFound = "SHOP_ITEM_NOT_FOUND";
    public const string ItemDisabled = "SHOP_ITEM_DISABLED";
    public const string LevelLocked = "SHOP_LEVEL_LOCKED";
    public const string InsufficientCoins = "SHOP_INSUFFICIENT_COINS";
    public const string AlreadyOwned = "SHOP_ALREADY_OWNED";
    public const string IdempotencyConflict = "SHOP_IDEMPOTENCY_CONFLICT";
    public const string LoadoutNotOwned = "LOADOUT_ITEM_NOT_OWNED";
    public const string LoadoutVersionConflict = "LOADOUT_VERSION_CONFLICT";
    public const string AttachmentsUnsupported = "ATTACHMENTS_NOT_ADAPTED";
    public const string AttachmentInvalid = "ATTACHMENT_INVALID";
    public const string AttachmentNotOwned = "ATTACHMENT_NOT_OWNED";
    public const string AttachmentIncompatible = "ATTACHMENT_NOT_COMPATIBLE";
    public const string RoomNotFound = "ROOM_NOT_FOUND";
    public const string RoomClosed = "ROOM_CLOSED";
    public const string RoomFull = "ROOM_FULL";
    public const string RoomCodeExhausted = "ROOM_CODE_EXHAUSTED";
    // ---- CF 等待房间（Docs/27 v1 §10）----
    public const string RoomStateConflict = "ROOM_STATE_CONFLICT";
    public const string RoomNotReady = "ROOM_NOT_READY";
    public const string RoomStarting = "ROOM_STARTING";
    public const string TeamFull = "TEAM_FULL";
    public const string TeamInvalid = "TEAM_INVALID";
    public const string MapNotAllowed = "MAP_NOT_ALLOWED";
    public const string ModeInvalid = "MODE_INVALID";
    public const string SettingInvalid = "SETTING_INVALID";
    public const string NotLeader = "NOT_LEADER";
    public const string TicketRateLimited = "TICKET_RATE_LIMITED";
    // 客户端应用协议代际与房间冻结值不一致（2026-09-15 P0-A：混版本客户端不进同一房）
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    // ---- 房间聊天（Docs/27 §10）----
    public const string ChatRejected = "CHAT_REJECTED";
    public const string ChatRateLimited = "CHAT_RATE_LIMITED";
    public const string ChatCursorInvalid = "CHAT_CURSOR_INVALID";
    public const string PassSeasonInvalid = "PASS_SEASON_INVALID";
    public const string PassRewardGrantConflict = "PASS_REWARD_GRANT_CONFLICT";
    public const string AchievementInvalid = "ACHIEVEMENT_INVALID";
    public const string MatchPayloadRejected = "MATCH_PAYLOAD_REJECTED";
    public const string MatchIdempotencyConflict = "MATCH_IDEMPOTENCY_CONFLICT";
    public const string ServerError = "SERVER_ERROR";

    // ---- Dedicated Server 控制面（Docs/27）----
    public const string NoServerAvailable = "NO_SERVER_AVAILABLE";
    public const string ServerKeyInvalid = "SERVER_KEY_INVALID";
    public const string ServerInstanceNotFound = "SERVER_INSTANCE_NOT_FOUND";
    // 实例心跳非法状态转换（审计 P0-1 新增业务码：绑定态不得降级/改写绑定，稳定 409）
    public const string ServerInstanceStateConflict = "SERVER_INSTANCE_STATE_CONFLICT";
    // 票据 consume 带内错误码（Docs/27 §2.3 冻结字面值，经 JoinTicketConsumeDto.ErrorCode 返回，非 HTTP 状态码）
    public const string TicketInvalid = "TICKET_INVALID";
    public const string TicketExpired = "TICKET_EXPIRED";
    public const string TicketReplayed = "TICKET_REPLAYED";
    public const string TicketInstanceMismatch = "TICKET_INSTANCE_MISMATCH";

    // ---- 好友系统（2026-09-20 需求2）----
    public const string FriendQueryInvalid = "FRIEND_QUERY_INVALID";
    public const string FriendNotFound = "FRIEND_NOT_FOUND";
    public const string FriendSelf = "FRIEND_SELF";
    public const string AlreadyFriends = "FRIEND_ALREADY_FRIENDS";
    public const string FriendRequestExists = "FRIEND_REQUEST_EXISTS";
    public const string FriendRequestNotFound = "FRIEND_REQUEST_NOT_FOUND";
}
