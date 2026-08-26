using Game.Core;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>CP4：动态散布合成（Docs/13 §6.3）——腰射/ADS/移动/冲刺/Bloom/恢复门控/修饰。</summary>
    public sealed class WeaponAccuracyStateTests
    {
        private static ResolvedWeaponStats Stats(float hip = 1.2f, float ads = 0.3f, float moveMax = 1.5f,
            float sprintExtra = 1.8f, float bloom = 0.22f, float maxBloom = 2.5f, float bloomDelay = 0.15f, float bloomSpeed = 5f)
            => WeaponStatResolver.Resolve(new WeaponStat
            {
                Damage = 26, Rpm = 600, MagSize = 30, ReserveAmmo = 120,
                ReloadTime = 2f, Spread = hip, MaxRange = 100, AdsFov = 50,
                Recoil = new RecoilProfileData { PitchDeg = 1.1f, SpringFrequency = 9, SpringDamping = 0.75f, RecoverySpeed = 6, AdsRecoilMultiplier = 0.6f, FirstShotMultiplier = 1, Accumulation = 1, MaxAccumulation = 1, RecoveryDelay = 0.2f, ViewModelKickBack = 0.04f, ViewModelKickPitch = 4f },
                Accuracy = new AccuracyProfileData { BaseHipSpread = hip, BaseAdsSpread = ads, MovementSpreadMax = moveMax, SprintSpreadExtra = sprintExtra, ShotBloomPerShot = bloom, MaxBloom = maxBloom, BloomRecoveryDelay = bloomDelay, BloomRecoverySpeed = bloomSpeed },
                Ballistic = new BallisticProfileData { PelletCount = 1 },
            }, null);

        [Test]
        public void Composition_HipAdsMoveSprintBloom()
        {
            var s = Stats();
            var st = new WeaponAccuracyState();

            // 静止腰射
            Assert.That(st.CurrentSpread(new(0f, 0f, false, true, false), s), Is.EqualTo(1.2f).Within(1e-4f));
            // 完全 ADS：BaseAdsSpread（真降精度通道，非仅 UI）
            Assert.That(st.CurrentSpread(new(1f, 0f, false, true, false), s), Is.EqualTo(0.3f).Within(1e-4f));
            // 半 ADS
            Assert.That(st.CurrentSpread(new(0.5f, 0f, false, true, false), s), Is.EqualTo((1.2f + 0.3f) * 0.5f).Within(1e-4f));
            // 移动（速度 50%）
            Assert.That(st.CurrentSpread(new(0f, 0.5f, false, true, false), s), Is.EqualTo(1.2f + 0.75f).Within(1e-4f));
            // 冲刺（速度 100% + sprintExtra）
            Assert.That(st.CurrentSpread(new(0f, 1f, true, true, false), s), Is.EqualTo(1.2f + 1.5f + 1.8f).Within(1e-4f));
        }

        [Test]
        public void Bloom_Accumulates_CappedAtMax()
        {
            var s = Stats(bloom: 0.5f, maxBloom: 1.2f);
            var st = new WeaponAccuracyState();
            for (int i = 0; i < 10; i++) st.OnShot(s);
            Assert.That(st.CurrentBloom, Is.EqualTo(1.2f).Within(1e-4f), "Bloom 封顶 MaxBloom");
            Assert.That(st.CurrentSpread(WeaponFireContext.Default, s), Is.EqualTo(1.2f + 1.2f).Within(1e-4f), "Bloom 进合成");
        }

        [Test]
        public void Bloom_Recovery_DelayedThenLinear()
        {
            var s = Stats(bloomDelay: 0.15f, bloomSpeed: 5f);
            var st = new WeaponAccuracyState();
            st.OnShot(s);
            float after = st.CurrentBloom;

            // 门控前不衰减
            for (int i = 0; i < 6; i++) st.Tick(1f / 60f, s); // 0.1s < 0.15s
            Assert.That(st.CurrentBloom, Is.EqualTo(after).Within(1e-5f), "BloomRecoveryDelay 门控前不衰减");

            // 门控后 0.2s × 5°/s = 1.0
            for (int i = 0; i < 12; i++) st.Tick(1f / 60f, s);
            Assert.That(st.CurrentBloom, Is.EqualTo(Mathf.Max(0f, after - 1.0f)).Within(0.02f), "门控后线性衰减");
        }

        [Test]
        public void Spread_Modifier_AppliesAtEnd()
        {
            // Spread×0.5 修饰（Resolver 层）→ 合成末端整体减半
            var source = new TestSource(new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, 0.5f, "test"));
            var baseStat = Stats(); // 只取 Stat
            var s = WeaponStatResolver.Resolve(baseStat.Stat, new[] { source });
            var st = new WeaponAccuracyState();
            st.OnShot(s); // +0.22 bloom
            Assert.That(st.CurrentSpread(WeaponFireContext.Default, s), Is.EqualTo((1.2f + 0.22f) * 0.5f).Within(1e-4f));
        }

        [Test]
        public void HardReset_ClearsBloom()
        {
            var s = Stats();
            var st = new WeaponAccuracyState();
            st.OnShot(s); st.OnShot(s);
            Assert.That(st.CurrentBloom, Is.GreaterThan(0f));
            st.HardReset();
            Assert.That(st.CurrentBloom, Is.EqualTo(0f));
        }

        private sealed class TestSource : IWeaponStatModifierSource
        {
            private readonly WeaponStatModifier[] _mods;
            public TestSource(params WeaponStatModifier[] mods) => _mods = mods;
            public int Priority => 10;
            public string SourceId => "test";
            public System.Collections.Generic.IReadOnlyList<WeaponStatModifier> GetModifiers() => _mods;
        }
    }
}
