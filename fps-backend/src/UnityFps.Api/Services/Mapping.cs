using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public static class Mapping
{
    public static PlayerProfileDto ToDto(this PlayerProfile profile, string username, IProgressionRules rules) =>
        new(username, profile.Level, profile.Xp, rules.GetXpToNextLevel(profile.Level), profile.SkillPoints,
            new UpgradeLevelsDto(profile.UpDamage, profile.UpAmmoCap, profile.UpMaxHealth));

    public static LoadoutDto ToDto(this PlayerLoadout loadout) =>
        new(loadout.PrimaryWeaponId, loadout.SecondaryWeaponId, loadout.ThrowableId);
}
