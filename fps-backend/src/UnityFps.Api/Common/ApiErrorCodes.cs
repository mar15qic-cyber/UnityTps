namespace UnityFps.Api.Common;

public static class ApiErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string UsernameTaken = "AUTH_USERNAME_TAKEN";
    public const string InvalidCredentials = "AUTH_INVALID_CREDENTIALS";
    public const string Unauthorized = "AUTH_UNAUTHORIZED";
    public const string InvalidWeapon = "LOADOUT_INVALID_WEAPON";
    public const string InvalidUpgrade = "UPGRADE_INVALID_TARGET";
    public const string InsufficientPoints = "UPGRADE_INSUFFICIENT_POINTS";
    public const string ItemNotFound = "SHOP_ITEM_NOT_FOUND";
    public const string ItemDisabled = "SHOP_ITEM_DISABLED";
    public const string LevelLocked = "SHOP_LEVEL_LOCKED";
    public const string InsufficientCoins = "SHOP_INSUFFICIENT_COINS";
    public const string AlreadyOwned = "SHOP_ALREADY_OWNED";
    public const string IdempotencyConflict = "SHOP_IDEMPOTENCY_CONFLICT";
    public const string LoadoutNotOwned = "LOADOUT_ITEM_NOT_OWNED";
    public const string LoadoutVersionConflict = "LOADOUT_VERSION_CONFLICT";
    public const string AttachmentsUnsupported = "ATTACHMENTS_NOT_ADAPTED";
    public const string RoomNotFound = "ROOM_NOT_FOUND";
    public const string RoomClosed = "ROOM_CLOSED";
    public const string RoomFull = "ROOM_FULL";
    public const string RoomCodeExhausted = "ROOM_CODE_EXHAUSTED";
    public const string PassSeasonInvalid = "PASS_SEASON_INVALID";
    public const string PassRewardGrantConflict = "PASS_REWARD_GRANT_CONFLICT";
    public const string AchievementInvalid = "ACHIEVEMENT_INVALID";
    public const string MatchPayloadRejected = "MATCH_PAYLOAD_REJECTED";
    public const string MatchIdempotencyConflict = "MATCH_IDEMPOTENCY_CONFLICT";
    public const string ServerError = "SERVER_ERROR";
}
