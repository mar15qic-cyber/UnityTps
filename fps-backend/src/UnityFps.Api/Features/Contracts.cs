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

public sealed record AuthSessionDto(string Token, DateTime ExpiresAtUtc, PlayerProfileDto Profile, LoadoutDto Loadout);
public sealed record UpgradeLevelsDto(int UpDamage, int UpAmmoCap, int UpMaxHealth);
public sealed record PlayerProfileDto(string Username, int Level, int Xp, int XpToNextLevel, int SkillPoints, UpgradeLevelsDto Upgrades);
public sealed record LoadoutDto(string PrimaryWeaponId, string SecondaryWeaponId, string? ThrowableId);

public sealed class UpgradeRequest
{
    [Range(0, 5)] public int UpDamage { get; set; }
    [Range(0, 5)] public int UpAmmoCap { get; set; }
    [Range(0, 5)] public int UpMaxHealth { get; set; }
}

public sealed class LoadoutRequest
{
    [Required, StringLength(32)] public string PrimaryWeaponId { get; set; } = string.Empty;
    [Required, StringLength(32)] public string SecondaryWeaponId { get; set; } = string.Empty;
    [StringLength(32)] public string? ThrowableId { get; set; }
}

public sealed class MatchSubmissionRequest
{
    [Range(0, int.MaxValue)] public int Kills { get; set; }
    [Range(0, int.MaxValue)] public int Deaths { get; set; }
    [Range(0, int.MaxValue)] public int Score { get; set; }
}

public sealed record MatchResultDto(int XpEarned, int LevelUps, PlayerProfileDto Profile);
