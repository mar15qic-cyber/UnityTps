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
    public const string ServerError = "SERVER_ERROR";
}
