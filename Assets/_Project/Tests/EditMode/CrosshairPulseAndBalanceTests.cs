using System.Reflection;
using Game.Core;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>Day4 回归：准心只能反映真实散布，且重/轻步枪差异必须可见。</summary>
    public sealed class CrosshairPulseAndBalanceTests
    {
        [Test]
        public void CrosshairShot_DoesNotAddWeaponAgnosticPulse()
        {
            var config = ScriptableObject.CreateInstance<CrosshairConfig>();
            var go = new GameObject("CrosshairPresenter");
            try
            {
                var presenter = go.AddComponent<CrosshairPresenter>();
                typeof(CrosshairPresenter).GetField("config", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(presenter, config);
                var handle = typeof(CrosshairPresenter).GetMethod("HandleShot",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(handle, Is.Not.Null);
                handle.Invoke(presenter, new object[] { default(WeaponShot) });
                Assert.That(presenter.Model.ShotPulse, Is.EqualTo(0f), "准心不得叠加所有武器相同的固定脉冲");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void Accuracy_AirborneSpreadIsSharedByGameplayAndHudSource()
        {
            var stats = WeaponStatResolver.Resolve(new WeaponStat
            {
                Damage = 1, Rpm = 600, MagSize = 1, ReserveAmmo = 0, ReloadTime = 1,
                Spread = 1, MaxRange = 100, AdsFov = 60,
                Accuracy = new AccuracyProfileData
                {
                    BaseHipSpread = 1f, BaseAdsSpread = 0.25f, MovementSpreadMax = 0f,
                    SprintSpreadExtra = 0f, AirborneSpreadExtra = 2.5f,
                    ShotBloomPerShot = 0f, MaxBloom = 0f, BloomRecoveryDelay = 0f, BloomRecoverySpeed = 1f
                },
                Recoil = new RecoilProfileData { RecoverySpeed = 1f, MaxAccumulation = 1f },
                Ballistic = new BallisticProfileData { PelletCount = 1 }
            }, null);
            var state = new WeaponAccuracyState();
            var grounded = new WeaponFireContext(0f, 0f, false, true, false);
            var airborne = new WeaponFireContext(0f, 0f, false, false, false);

            Assert.That(state.CurrentSpread(grounded, stats), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(state.CurrentSpread(airborne, stats), Is.EqualTo(3.5f).Within(1e-5f));
        }

        private static WeaponStat LoadStat(string weaponId)
        {
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            Assert.That(balance, Is.Not.Null, "Day2_DemoBalance.asset 加载失败");
            var stat = balance.GetWeaponStat(weaponId);
            Assert.That(stat, Is.Not.Null, $"未找到 {weaponId}");
            return stat;
        }

        [Test]
        public void HeavyAndLightRifles_HavePerceptibleAccuracyAndRecoilDifferences()
        {
            var heavy = LoadStat("rifle.day3");
            var light = LoadStat("rifle.02");
            int dimensions = 0;
            if (Mathf.Abs(heavy.Accuracy.BaseHipSpread - light.Accuracy.BaseHipSpread) >= 0.8f) dimensions++;
            if (Mathf.Abs(heavy.Accuracy.ShotBloomPerShot - light.Accuracy.ShotBloomPerShot) >= 0.15f) dimensions++;
            if (Mathf.Abs(heavy.Recoil.PitchDeg - light.Recoil.PitchDeg) >= 0.3f) dimensions++;
            if (Mathf.Abs(heavy.Recoil.YawDeg - light.Recoil.YawDeg) >= 0.15f) dimensions++;
            if (Mathf.Abs(heavy.Recoil.RecoverySpeed - light.Recoil.RecoverySpeed) >= 1.5f) dimensions++;
            Assert.That(dimensions, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void HeavyAndLightRifles_HipfireGapDifferenceSurvivesCanvasScale()
        {
            var heavy = LoadStat("rifle.day3");
            var light = LoadStat("rifle.02");
            float pxPerRad = (1080f * 0.5f) / Mathf.Tan(30f * Mathf.Deg2Rad);
            float heavyGap = Mathf.Tan(heavy.Accuracy.BaseHipSpread * Mathf.Deg2Rad) * pxPerRad;
            float lightGap = Mathf.Tan(light.Accuracy.BaseHipSpread * Mathf.Deg2Rad) * pxPerRad;
            Assert.That(heavyGap - lightGap, Is.GreaterThanOrEqualTo(10f));
            Assert.That((heavyGap - lightGap) * 0.468f, Is.GreaterThanOrEqualTo(4.5f),
                "898x520 Game View 中经过 CanvasScaler 后仍须可见");
        }

        [Test]
        public void HeavyAndLightRifles_TenShotBloomDifferenceSurvives()
        {
            var heavy = LoadStat("rifle.day3");
            var light = LoadStat("rifle.02");
            float heavyBloom = Mathf.Min(heavy.Accuracy.ShotBloomPerShot * 10f, heavy.Accuracy.MaxBloom);
            float lightBloom = Mathf.Min(light.Accuracy.ShotBloomPerShot * 10f, light.Accuracy.MaxBloom);
            Assert.That(heavyBloom - lightBloom, Is.GreaterThanOrEqualTo(0.5f));
        }
    }
}
