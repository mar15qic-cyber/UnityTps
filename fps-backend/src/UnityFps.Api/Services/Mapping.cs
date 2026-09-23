using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

public static class Mapping
{
    public static PlayerProfileDto ToDto(this PlayerProfile profile, UserAccount user, long coins, IProgressionRules rules) =>
        new(user.Username, user.IdentityTag, profile.Level, profile.Xp, rules.GetXpToNextLevel(profile.Level), coins);

    public static LoadoutDto ToDto(this PlayerLoadout loadout) =>
        new(loadout.PrimaryWeaponId, loadout.SecondaryWeaponId, loadout.ThrowableId, loadout.Version,
            loadout.Attachments.Select(x => new LoadoutAttachmentDto(x.WeaponSlot, x.AttachmentSlot, x.AttachmentItemId)).ToArray());

    public static CatalogItemDto ToDto(this CatalogItem item, bool owned) =>
        new(item.ItemId, item.ItemType, item.SlotType, item.DisplayName, item.Description, item.AssetKey,
            item.PriceCoins, item.UnlockLevel, item.IsActive, owned, item.IsImplemented, item.CalibrationKey,
            item.AcquisitionSource);

    public static PassRewardDto ToDto(this PassReward reward, bool granted) =>
        new(reward.PassLevel, reward.RewardType, reward.ItemId, reward.CoinsAmount, granted);

    public static PassAchievementDto ToDto(this AchievementDefinition def, PlayerAchievement? pa) =>
        new(def.AchievementId, def.DisplayName, def.Description, def.TargetMetric, def.TargetValue,
            pa?.Progress ?? 0, pa?.UnlockedAtUtc != null, def.PassXpReward);

    public static AchievementDto ToAchievementDto(this AchievementDefinition def, PlayerAchievement? pa) =>
        new(def.AchievementId, def.DisplayName, def.Description, def.TargetMetric, def.TargetValue,
            pa?.Progress ?? 0, pa?.UnlockedAtUtc != null, def.PassXpReward);
}
