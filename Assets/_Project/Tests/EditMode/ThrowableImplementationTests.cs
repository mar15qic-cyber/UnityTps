using System.Collections.Generic;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Settings;
using Game.Gameplay.Weapon;
using Game.Presentation.Audio;
using Game.Presentation.FX;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class ThrowableImplementationTests
    {
        private const string Root = "Assets/_Project";

        [Test]
        public void FormalCatalog_HasThreeCompleteDefinitionsAndExpectedTiming()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ThrowableCatalog>(Root + "/Resources/ThrowableCatalog.asset");
            Assert.NotNull(catalog);
            Assert.IsTrue(catalog.IsValid(out var reason), reason);
            Assert.AreEqual(0.35f, catalog.ReleaseDelaySeconds, 0.001f);
            AssertDefinition(catalog.Frag, ThrowableType.Frag, 1.75f, 3.5f, 0.75f);
            AssertDefinition(catalog.Flash, ThrowableType.Flash, 1.25f, 9f, 1.75f);
            AssertDefinition(catalog.Smoke, ThrowableType.Smoke, 0.75f, 3f, 14.25f);
            var prefab = catalog.NetworkProjectilePrefab;
            Assert.NotNull(prefab.GetComponent<NetworkObject>());
            Assert.NotNull(prefab.GetComponent<NetworkTransform>());
            Assert.NotNull(prefab.GetComponent<Rigidbody>());
            Assert.NotNull(prefab.GetComponent<SphereCollider>());
            Assert.AreEqual(0.1f, prefab.GetComponent<SphereCollider>().radius, 0.001f);
            Assert.AreEqual(CollisionDetectionMode.ContinuousDynamic,
                prefab.GetComponent<Rigidbody>().collisionDetectionMode);
            var registry = AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>("Assets/DefaultPrefabObjects.asset");
            Assert.Contains(prefab.GetComponent<NetworkObject>(), new List<NetworkObject>(registry.Prefabs));
            var transformData = new SerializedObject(prefab.GetComponent<NetworkTransform>());
            Assert.IsFalse(transformData.FindProperty("_clientAuthoritative").boolValue);
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(
                Root + "/Prefabs/Player/Player_Day2_Rebuilt.prefab");
            Assert.NotNull(player.GetComponent<ThrowableController>());
            Assert.NotNull(player.GetComponent<FootstepAudioView>());
            var audio = AssetDatabase.LoadAssetAtPath<CombatAudioConfig>(
                Root + "/Resources/CombatAudioConfig.asset");
            Assert.NotNull(audio);
            Assert.IsTrue(audio.IsValid);
        }

        private static void AssertDefinition(ThrowableDefinition definition, ThrowableType type,
            float fuse, float radius, float effect)
        {
            Assert.AreEqual(type, definition.Type);
            Assert.AreEqual(1, definition.InitialCount);
            Assert.AreEqual(fuse, definition.FuseSeconds, 0.001f);
            Assert.AreEqual(radius, definition.Radius, 0.001f);
            Assert.AreEqual(effect, definition.EffectSeconds, 0.001f);
            Assert.AreEqual(12f, definition.ForwardSpeed, 0.001f);
            Assert.AreEqual(2f, definition.UpwardSpeed, 0.001f);
            Assert.AreEqual(0.3f, definition.InheritedHorizontalVelocity, 0.001f);
        }

        [Test]
        public void DefaultThrowBindings_DoNotConflict()
        {
            var seen = new HashSet<UnityEngine.InputSystem.Key>();
            foreach (var binding in SettingsKeyMap.Bindings)
                Assert.IsTrue(seen.Add(binding.defaultKey), $"duplicate default key: {binding.defaultKey}");
            Assert.AreEqual(UnityEngine.InputSystem.Key.Digit3, SettingsKeyMap.DefaultOf(SettingsKeyMap.Action.SelectThrowable));
            Assert.IsNull(SettingsKeyMap.Find(SettingsKeyMap.Action.Slot3));
            Assert.IsNull(SettingsKeyMap.Find(SettingsKeyMap.Action.QuickSwap));
            Assert.IsNull(SettingsKeyMap.Find(SettingsKeyMap.Action.ThrowFrag));
        }

        [Test]
        public void FragDamageFallsOffAndFlashFacesAway()
        {
            Assert.AreEqual(100, ThrowableProjectile.DamageAtDistance(100f, 3.5f, 0f));
            Assert.That(ThrowableProjectile.DamageAtDistance(100f, 3.5f, 1.75f), Is.InRange(49, 51));
            Assert.AreEqual(0, ThrowableProjectile.DamageAtDistance(100f, 3.5f, 3.5f));
            float front = ThrowableScreenEffects.CalculateFlashStrength(2f, 9f, 1f, 1f);
            float back = ThrowableScreenEffects.CalculateFlashStrength(2f, 9f, -1f, 1f);
            Assert.Greater(front, back);
            Assert.AreEqual(0f, ThrowableScreenEffects.CalculateFlashStrength(10f, 9f, 1f, 1f));
            Assert.IsTrue(ThrowableProjectile.CanDamageTarget(true, "Red", "Red"));
            Assert.IsFalse(ThrowableProjectile.CanDamageTarget(false, "Red", "Red"));
            Assert.IsTrue(ThrowableProjectile.CanDamageTarget(false, "Red", "Blue"));
            Assert.IsTrue(ThrowableController.IsNextRequest(0, 1));
            Assert.IsFalse(ThrowableController.IsNextRequest(1, 1));
            Assert.IsFalse(ThrowableController.IsNextRequest(1, 3));
            Assert.IsTrue(ThrowableController.IsPlayablePhase(false, MatchPhase.Idle));
            Assert.IsFalse(ThrowableController.IsPlayablePhase(true, MatchPhase.Idle));
            Assert.IsTrue(ThrowableController.IsPlayablePhase(true, MatchPhase.InProgress));
            Assert.IsFalse(ThrowableController.IsPlayablePhase(true, MatchPhase.Ended));
        }

        [Test]
        public void FlashOcclusion_RejectsWallBetweenCameraAndEffect()
        {
            var from = new Vector3(10000f, 10000f, 10000f);
            var effect = from + Vector3.forward * 8f;
            Assert.IsFalse(ThrowableScreenEffects.IsOccluded(from, effect));
            Assert.IsFalse(ThrowableProjectile.IsBlastBlockedByWorld(from, effect));
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.position = from + Vector3.forward * 4f;
                Physics.SyncTransforms();
                Assert.IsTrue(ThrowableScreenEffects.IsOccluded(from, effect));
                Assert.IsTrue(ThrowableProjectile.IsBlastBlockedByWorld(from, effect));
            }
            finally { Object.DestroyImmediate(wall); }
        }

        [Test]
        public void FormalWeapons_HaveThrowClipsAndNonOverlappingReloadAudio()
        {
            int count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponDefinition", new[] { Root + "/ScriptableObjects/Weapons" }))
            {
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (definition == null || !definition.name.StartsWith("Day")) continue;
                count++;
                Assert.NotNull(definition.FirstPersonAnimations.ThrowGrenade, definition.name);
                var profile = definition.AudioProfile;
                Assert.NotNull(profile, definition.name);
                bool whole = profile.ReloadAmmoLeft.Clip != null || profile.ReloadOutOfAmmo.Clip != null;
                bool staged = profile.MagOut.Clip != null || profile.MagIn.Clip != null
                    || profile.BoltRack.Clip != null;
                Assert.IsTrue(whole || staged, definition.name + " reload audio missing");
                Assert.IsFalse(whole && staged, definition.name + " reload layers overlap");
            }
            Assert.AreEqual(16, count);
        }
    }
}
