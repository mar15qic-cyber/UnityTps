using UnityEngine;

namespace Game.Presentation.HUD
{
    /// <summary>References the sliced sprites of the original LPFP Gun_Icons_Spritesheet.</summary>
    public sealed class WeaponHudIconCatalog : ScriptableObject
    {
        [SerializeField] private Sprite[] icons;

        public Sprite Get(string weaponId)
        {
            int index = weaponId switch
            {
                "rifle.day3" => 0, "rifle.02" => 1, "rifle.03" => 2,
                "pistol.day2" => 4, "handgun.02" => 5, "handgun.03" => 6, "handgun.04" => 7,
                "shotgun.01" => 9,
                "smg.01" => 10, "smg.02" => 11, "smg.03" => 12, "smg.04" => 13, "smg.05" => 14,
                "sniper.01" => 15, "sniper.02" => 16, "sniper.03" => 17,
                _ => -1,
            };
            return index >= 0 && icons != null && index < icons.Length ? icons[index] : null;
        }
    }
}
