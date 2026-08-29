using NUnit.Framework;
using UnityEngine;
using Game.UI;

namespace Game.Gameplay.Tests
{
    public sealed class WeaponShopUiTests
    {
        [Test]
        public void RuntimeCatalog_UsesStableBusinessIdsAndRealPrefabPaths()
        {
            var catalog = WeaponAssetCatalog.CreateRuntime();
            try
            {
                Assert.That(catalog.Entries, Has.Count.EqualTo(10));
                Assert.That(catalog.TryGet("weapon.m4", out var m4), Is.True);
                Assert.That(m4.assetKey, Is.EqualTo("rifle.day3/Assault_Rifle_01"));
                Assert.That(m4.previewPrefabPath, Is.EqualTo("Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab"));
                Assert.That(catalog.TryGet("weapon.ak", out var ak), Is.True);
                Assert.That(ak.assetKey, Is.EqualTo("rifle.02/Assault_Rifle_02"));
                Assert.That(catalog.TryGet("display-name-M4", out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void RuntimeCatalog_ExposesFourShopDimensions()
        {
            var catalog = WeaponAssetCatalog.CreateRuntime();
            try
            {
                var stats = catalog.FindStats("weapon.m4");
                Assert.That(stats.damage, Is.EqualTo(26f));
                Assert.That(stats.roundsPerMinute, Is.EqualTo(600f));
                Assert.That(stats.magazineSize, Is.EqualTo(30f));
                Assert.That(stats.recoil, Is.EqualTo(1.35f));
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }
    }
}

