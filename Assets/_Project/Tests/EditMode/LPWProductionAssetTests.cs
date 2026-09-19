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
        // 2026-09-08 追加 P0：catalog 资产移入 Resources（DS build 的 Resources.Load 必须命中
        // 真资产——runtime 十条默认清单的 definition 依赖编辑器回退，不进 build）——测试路径同步。
        private const string CatalogPath = "Assets/_Project/Resources/WeaponAssetCatalog.asset";

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
        public void CatalogRows_AreUnique_AndResolveBySourceIdentity()
        {
            // D4-A.4（Gate A 复审 §2.3）：正式 LPFP 行会随资产票据增长（39→45）——按来源/身份
            // 断言，不再依赖脆弱总数。
            var catalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            // 结构不变式：itemId 唯一
            Assert.That(catalog.Entries.Select(x => x.itemId).Distinct().Count(), Is.EqualTo(catalog.Entries.Count));
            // LPW 身份行冻结在 29（Docs/25 历史只读遗产：不修不扩）——IsLpw 与 assetKey
            // 命名空间迁移守卫必须一致
            Assert.That(catalog.Entries.Count(x => x.IsLpw), Is.EqualTo(29));
            Assert.That(catalog.Entries.Count(x => x.itemId.StartsWith("weapon.lpw.", StringComparison.Ordinal)), Is.EqualTo(29));
            // 构建版本解析不变式（对全部行，无论来源）
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
