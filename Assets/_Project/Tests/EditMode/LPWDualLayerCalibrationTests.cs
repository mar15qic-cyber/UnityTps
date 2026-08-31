using System.Linq;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode
{
    public sealed class LPWDualLayerCalibrationTests
    {
        private const string ManifestPath = "Assets/_Project/ScriptableObjects/Weapons/LPW/LPWWeaponManifest.asset";

        [Test]
        public void Manifest_IsSchemaSixAndKeepsAllTwentyNineRows()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.SchemaVersion, Is.EqualTo(6));
            Assert.That(manifest.Weapons.Count, Is.EqualTo(29));

            Assert.That(manifest.Weapons.Select(x => x.definitionId).Distinct().Count(), Is.EqualTo(29));
            Assert.That(manifest.Weapons.All(x => x.schemaVersion == 6), Is.True);
        }

        [Test]
        public void EveryFormalPrefabUsesTheDynamicSightAxisContract()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            foreach (LPWWeaponSpec spec in manifest.Weapons)
            {
                string token = System.IO.Path.GetFileNameWithoutExtension(spec.sourcePrefabPath);
                string path = "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_" + token + "_View.prefab";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, spec.definitionId);
                FPWeaponPoseProfile profile = prefab.GetComponent<FPWeaponPoseProfile>();
                Assert.That(profile, Is.Not.Null, spec.definitionId);
                Assert.That(profile.HasRootCalibration && profile.HasCompleteInterfaceLayout, Is.True, spec.definitionId);
                Game.Presentation.Weapon.WeaponView view = prefab.GetComponent<Game.Presentation.Weapon.WeaponView>();
                Assert.That(view, Is.Not.Null, spec.definitionId);
                Assert.That(view.AlignAdsToSightAxis && view.SightReference != null, Is.True, spec.definitionId);
            }
        }

        [Test]
        public void AnimationFamilyRulesRemainExplicit()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            LPWWeaponSpec g36 = manifest.Weapons.Single(x => x.definitionId == "lpw.rifle.03");
            Assert.That(g36.animationFamily, Is.EqualTo(FirstPersonAnimationFamily.Rifle01));

            foreach (LPWWeaponSpec sniper in manifest.Weapons.Where(x => x.category == WeaponCatalogCategory.Sniper))
                Assert.That(sniper.animationDefinitionId,
                    Is.EqualTo(sniper.tier == 1 || sniper.tier == 3 ? "sniper.01" : "sniper.02"));
        }
    }
}
