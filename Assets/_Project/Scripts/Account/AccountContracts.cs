using System;
using System.Collections.Generic;

namespace Game.Account
{

[Serializable]
public sealed class RegisterRequest
{
    public string username;
    public string password;
}

[Serializable]
public sealed class LoginRequest
{
    public string username;
    public string password;
}

[Serializable]
public sealed class UpgradeLevelsDto
{
    public int upDamage;
    public int upAmmoCap;
    public int upMaxHealth;
}

[Serializable]
public sealed class PlayerProfileDto
{
    public string username;
    public int level;
    public int xp;
    public int xpToNextLevel;
    public int skillPoints;
    public UpgradeLevelsDto upgrades;
}

[Serializable]
public sealed class LoadoutDto
{
    public string primaryWeaponId;
    public string secondaryWeaponId;
    public string throwableId;
}

[Serializable]
public sealed class AuthSessionDto
{
    public string token;
    public string expiresAtUtc;
    public PlayerProfileDto profile;
    public LoadoutDto loadout;
}

[Serializable]
public sealed class UpgradeRequest
{
    public int upDamage;
    public int upAmmoCap;
    public int upMaxHealth;
}

[Serializable]
public sealed class LoadoutRequest
{
    public string primaryWeaponId;
    public string secondaryWeaponId;
    public string throwableId;
}

[Serializable]
public sealed class MatchSubmissionRequest
{
    public int kills;
    public int deaths;
    public int score;
}

[Serializable]
public sealed class MatchResultDto
{
    public int xpEarned;
    public int levelUps;
    public PlayerProfileDto profile;
}

[Serializable]
public sealed class ProblemDetailsDto
{
    public string title;
    public int status;
    public string detail;
    public string code;
    public string traceId;
    public Dictionary<string, string[]> errors;
}
}
