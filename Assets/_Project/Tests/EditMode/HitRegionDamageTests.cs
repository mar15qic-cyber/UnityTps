using System.Reflection;
using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class HitRegionDamageTests
    {
        private GameObject _player;
        private GameObject _resolverObject;

        [TearDown]
        public void TearDown()
        {
            if (_player != null) Object.DestroyImmediate(_player);
            if (_resolverObject != null) Object.DestroyImmediate(_resolverObject);
        }

        [Test]
        public void ProductionHitboxes_ResolveHeadTorsoArmsAndLegsWithDistinctDamage()
        {
            _player = new GameObject("Victim");
            var adapter = _player.AddComponent<PlayerNetworkAdapter>();
            var model = new GameObject("TP_Model");
            model.transform.SetParent(_player.transform, false);
            model.transform.localPosition = new Vector3(0f, 0f, .341f);
            var target = model.AddComponent<DamageableTarget>();
            typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(adapter, null);
            _resolverObject = new GameObject("Resolver");
            var resolver = _resolverObject.AddComponent<CombatResolver>();
            Physics.SyncTransforms();

            AssertRegion(resolver, target, 0f, 1.65f, HitBodyRegion.Head, 60);
            AssertRegion(resolver, target, 0f, 1.2f, HitBodyRegion.Torso, 40);
            AssertRegion(resolver, target, -.39f, 1.12f, HitBodyRegion.Arm, 32);
            AssertRegion(resolver, target, .39f, 1.12f, HitBodyRegion.Arm, 32);
            AssertRegion(resolver, target, -.16f, .3f, HitBodyRegion.Leg, 28);
            AssertRegion(resolver, target, .16f, .3f, HitBodyRegion.Leg, 28);

            var gap = resolver.ResolveHitscan(new Vector3(0f, .3f, -2f), Vector3.forward, 10f, 40, ~0, null);
            Assert.That(gap.Hit, Is.False, "The space between the legs must remain empty");
            Assert.That(gap.DamageAmount, Is.Zero);
        }

        private static void AssertRegion(CombatResolver resolver, DamageableTarget target,
            float x, float y, HitBodyRegion region, int damage)
        {
            target.ResetHealth();
            var result = resolver.ResolveHitscan(new Vector3(x, y, -2f), Vector3.forward,
                10f, 40, ~0, null);
            Assert.That(result.Damaged, Is.True, $"{region} must be hittable");
            Assert.That(result.BodyRegion, Is.EqualTo(region));
            Assert.That(result.DamageAmount, Is.EqualTo(damage));
            Assert.That(target.CurrentHealth, Is.EqualTo(100 - damage));
        }
    }
}
