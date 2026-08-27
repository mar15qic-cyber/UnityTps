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
    public string PrimaryWeaponId { get; set; } = "rifle.day3";
    public string SecondaryWeaponId { get; set; } = "pistol.day2";
    public string? ThrowableId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public UserAccount User { get; set; } = null!;
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
