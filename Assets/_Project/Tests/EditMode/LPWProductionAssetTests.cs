using System;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class LPWProductionAssetTests
    {
        private const string ManifestPath = "Assets/_Project/ScriptableObjects/Weapons/LPW/LPWWeaponManifest.asset";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/Account/WeaponAssetCatalog.asset";

        [Test]
        public void ManifestContainsExactly29CanonicalGunTypes()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.SchemaVersion, Is.GreaterThanOrEqualTo(1));
            Assert.That(manifest.Weapons, Has.Count.EqualTo(29));
            Assert.That(manifest.Weapons.Select(x => x.itemId).Distinct().Count(), Is.EqualTo(29));
            Assert.That(manifest.Weapons.Select(x => x.definitionId).Distinct().Count(), Is.EqualTo(29));
            Assert.That(manifest.Weapons.Count(x => x.category == WeaponCatalogCategory.Rifle), Is.EqualTo(6));
            Assert.That(manifest.Weapons.Count(x => x.category == WeaponCatalogCategory.Pistol), Is.EqualTo(6));
            Assert.That(manifest.Weapons.Count(x => x.category == WeaponCatalogCategory.Shotgun), Is.EqualTo(5));
            Assert.That(manifest.Weapons.Count(x => x.category == WeaponCatalogCategory.Smg), Is.EqualTo(6));
            Assert.That(manifest.Weapons.Count(x => x.category == WeaponCatalogCategory.Sniper), Is.EqualTo(6));
            Assert.That(manifest.Weapons.All(x => x.sourcePrefabPath.EndsWith("_01.prefab", StringComparison.Ordinal)), Is.True);
            Assert.That(manifest.Weapons.Any(x => x.sourcePrefabPath.Contains("HeavyWeapon") || x.sourcePrefabPath.Contains("LMG") || x.sourcePrefabPath.Contains("HandWeapon")), Is.False);
        }

        [Test]
        public void CatalogContains39DirectBuildTimeDefinitions()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Entries, Has.Count.EqualTo(39));
            Assert.That(catalog.Entries.Select(x => x.itemId).Distinct().Count(), Is.EqualTo(39));
            Assert.That(catalog.Entries.Count(x => x.itemId.StartsWith("weapon.lpw.", StringComparison.Ordinal)), Is.EqualTo(29));
            Assert.That(catalog.Entries.All(x => x.definition != null), Is.True, "构建版本不得靠 Editor 搜索解析 Definition");
            Assert.That(catalog.Entries.All(x => x.previewPrefab != null), Is.True, "商城预览必须直接引用正式 TP prefab");
        }

        [Test]
        public void EveryLpwDefinitionHasCombatViewsAnchorsAndAnimations()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            var catalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogPath);
            foreach (var spec in manifest.Weapons)
            {
                Assert.That(catalog.TryGet(spec.itemId, out var entry), Is.True, spec.itemId);
                var definition = entry.definition;
                Assert.That(definition, Is.Not.Null, spec.itemId);
                Assert.That(definition.WeaponId, Is.EqualTo(spec.definitionId));
                Assert.That(definition.FirstPersonViewPrefab, Is.Not.Null);
                Assert.That(definition.ThirdPersonViewPrefab, Is.Not.Null);
                Assert.That(definition.FirstPersonAnimations.Idle, Is.Not.Null, spec.itemId + " idle");
                Assert.That(definition.FirstPersonAnimations.Fire, Is.Not.Null, spec.itemId + " fire");
                Assert.That(definition.FirstPersonAnimations.ReloadAmmoLeft, Is.Not.Null, spec.itemId + " reload");
                Assert.That(definition.ThirdPersonViewPrefab.transform.Find("Muzzle"), Is.Not.Null, spec.itemId + " TP muzzle");
                Assert.That(definition.ThirdPersonViewPrefab.transform.Find("LeftHandTarget"), Is.Not.Null, spec.itemId + " TP IK");
                Assert.That(definition.FirstPersonViewPrefab.GetComponentsInChildren<Collider>(true), Is.Empty, spec.itemId + " FP collider");
                Assert.That(definition.ThirdPersonViewPrefab.GetComponentsInChildren<Collider>(true), Is.Empty, spec.itemId + " TP collider");
            }
        }
    }
}
