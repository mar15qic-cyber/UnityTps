using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class WeaponHudIconCatalogTests
    {
        [Test]
        public void AllPlayableLpfpWeaponsUseOriginalSlicedAtlas()
        {
            var catalog = Resources.Load<WeaponHudIconCatalog>("UI/WeaponHudIconCatalog");
            Assert.That(catalog, Is.Not.Null);
            var weapons = new[]
            {
                ("rifle.day3", "assault_rifle_01_icon"), ("rifle.02", "assault_rifle_02_icon"),
                ("rifle.03", "assault_rifle_03_icon"), ("pistol.day2", "handgun_01_icon"),
                ("handgun.02", "handgun_02_icon"), ("handgun.03", "handgun_03_icon"),
                ("handgun.04", "handgun_04_icon"), ("shotgun.01", "shotgun_01_icon"),
                ("smg.01", "smg_01_icon"), ("smg.02", "smg_02_icon"),
                ("smg.03", "smg_03_icon"), ("smg.04", "smg_04_icon"),
                ("smg.05", "smg_05_icon"), ("sniper.01", "sniper_01_icon"),
                ("sniper.02", "sniper_02_icon"), ("sniper.03", "sniper_03_icon"),
            };
            const string atlasPath = "Assets/Low Poly FPS Pack/Components/Textures_&_Sprites/UI/Gun_Icons_Spritesheet/Gun_Icons_Spritesheet.png";
            foreach (var (id, spriteName) in weapons)
            {
                var icon = catalog.Get(id);
                Assert.That(icon, Is.Not.Null, $"Missing LPFP HUD icon for {id}");
                Assert.That(icon.name, Is.EqualTo(spriteName), id);
                Assert.That(AssetDatabase.GetAssetPath(icon), Is.EqualTo(atlasPath), id);
            }
        }
    }
}
