using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class ReloadAnimationTimingTests
    {
        [Test]
        public void AllConfiguredReloadClips_FitBalanceWindow_WithoutChangingGameplayDuration()
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            Assert.That(balance, Is.Not.Null, "Demo balance asset is required for the timing matrix.");

            var guids = AssetDatabase.FindAssets("t:WeaponDefinition");
            int definitionCount = 0;
            int nonPistolCount = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                if (definition == null) continue;

                definitionCount++;
                var stat = balance.GetWeaponStat(definition.WeaponId);
                bool pistol = definition.WeaponId.IndexOf("pistol", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || definition.WeaponId.IndexOf("handgun", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!pistol) nonPistolCount++;

                var fp = definition.FirstPersonAnimations;
                var tp = definition.ThirdPersonActions;
                AssertClipFits(fp.ReloadAmmoLeft, stat.ReloadTime, definition.name + ".FP.ReloadAmmoLeft");
                AssertClipFits(fp.ReloadOutOfAmmo, stat.ReloadTime, definition.name + ".FP.ReloadOutOfAmmo");
                AssertClipFits(tp.ReloadAmmoLeft, stat.ReloadTime, definition.name + ".TP.ReloadAmmoLeft");
                AssertClipFits(tp.ReloadOutOfAmmo, stat.ReloadTime, definition.name + ".TP.ReloadOutOfAmmo");
            }

            Assert.That(definitionCount, Is.GreaterThanOrEqualTo(8));
            Assert.That(nonPistolCount, Is.GreaterThanOrEqualTo(6),
                "The matrix must cover the shared non-pistol reload path.");
        }

        [Test]
        public void AuthoritativeReloadDuration_RemainsBalanceValue_ForPistolAndRifle()
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            Assert.That(balance, Is.Not.Null);

            AssertReloadUsesBalanceDuration(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_ServicePistol.asset", balance);
            AssertReloadUsesBalanceDuration(
                "Assets/_Project/ScriptableObjects/Weapons/Day3_AssaultRifle.asset", balance);
        }

        private static void AssertClipFits(AnimationClip clip, float reloadDuration, string label)
        {
            if (clip == null) return;
            float speed = ReloadAnimationTiming.GetPlaybackSpeed(clip, reloadDuration);
            Assert.That(speed, Is.GreaterThan(0f), label + " playback speed");
            Assert.That(clip.length / speed, Is.EqualTo(reloadDuration).Within(0.001f),
                label + " must reach its final frame at gameplay completion");
        }

        private static void AssertReloadUsesBalanceDuration(string definitionPath, DemoBalanceConfig balance)
        {
            var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(definitionPath);
            Assert.That(definition, Is.Not.Null, definitionPath);
            var stat = balance.GetWeaponStat(definition.WeaponId);

            var root = new GameObject("ReloadAnimationTimingTest");
            try
            {
                var actions = root.AddComponent<Game.Gameplay.Action.ActionSystem>();
                var combat = root.AddComponent<CombatResolver>();
                var controller = root.AddComponent<WeaponController>();
                SetPrivateField(controller, "actionSystem", actions);
                SetPrivateField(controller, "combatResolver", combat);
                SetPrivateField(controller, "processLocalInput", false);
                controller.Initialize(definition, balance);

                Assert.That(controller.TryFire(), Is.True, definition.name + " must fire for the reload setup");
                Assert.That(controller.TryReload(), Is.True, definition.name + " must start reload");
                Assert.That(actions.Duration, Is.EqualTo(stat.ReloadTime).Within(0.001f),
                    definition.name + " gameplay reload duration");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, target.GetType().Name + "." + fieldName);
            field.SetValue(target, value);
        }
    }
}
