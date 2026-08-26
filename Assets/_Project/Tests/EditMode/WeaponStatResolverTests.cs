using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Core;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CP3（Docs/13 检查点 3）：描述式 Modifier Resolver 合成规则断言——
    /// 排序/加乘顺序/NaN 防护/负值与 0 语义/Clamp/空源直通/组合访问器/旧资产迁移守卫。
    /// </summary>
    public sealed class WeaponStatResolverTests
    {
        private static WeaponStat BaseStat(float pitch = 1.1f, float yaw = 0.3f,
            float first = 1.25f, float ads = 0.6f, float vmBack = 0.045f, float recovery = 6f)
            => new WeaponStat
            {
                Damage = 26, Rpm = 600, MagSize = 30, ReserveAmmo = 120,
                ReloadTime = 2.2f, Spread = 1.2f, MaxRange = 120, AdsFov = 50,
                Recoil = new RecoilProfileData { PitchDeg = pitch, YawDeg = yaw, FirstShotMultiplier = first, AdsRecoilMultiplier = ads, ViewModelKickBack = vmBack, RecoverySpeed = recovery, SpringFrequency = 9, SpringDamping = 0.75f },
                Accuracy = new AccuracyProfileData { BaseHipSpread = 1.2f, BaseAdsSpread = 0.3f, BloomRecoverySpeed = 5 },
                Ballistic = new BallisticProfileData { PelletCount = 1 },
            };

        private sealed class Source : IWeaponStatModifierSource
        {
            public int Priority { get; set; }
            public string SourceId { get; set; }
            public IReadOnlyList<WeaponStatModifier> Mods { get; set; }
            public IReadOnlyList<WeaponStatModifier> GetModifiers() => Mods;
        }

        private static Source S(int priority, string id, params WeaponStatModifier[] mods)
            => new Source { Priority = priority, SourceId = id, Mods = mods };

        [Test]
        public void EmptySources_NeutralPassthrough()
        {
            var r = WeaponStatResolver.Resolve(BaseStat(), null);
            Assert.That(r.VerticalRecoilDeg, Is.EqualTo(1.1f).Within(1e-5f));
            Assert.That(r.HorizontalRecoilDeg, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(r.AimRecoilScale, Is.EqualTo(1f));
            Assert.That(r.SpreadScale, Is.EqualTo(1f));
            Assert.That(r.ViewModelKickScale, Is.EqualTo(1f));
            var empty = new List<IWeaponStatModifierSource>();
            var r2 = WeaponStatResolver.Resolve(BaseStat(), empty);
            Assert.That(r2.VerticalRecoilDeg, Is.EqualTo(1.1f).Within(1e-5f));
        }

        [Test]
        public void AddThenMultiply_OrderFixed()
        {
            // base 1.1 + add 0.4 = 1.5；× mul 1.2 = 1.8（加法先于乘法，Docs/13 §6.2 ③）
            var r = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(0, "a", new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 1.2f, "a"),
                                 new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Add, 0.4f, "a")) });
            Assert.That(r.VerticalRecoilDeg, Is.EqualTo(1.8f).Within(1e-4f));
        }

        [Test]
        public void MultipleSources_AllAccumulated()
        {
            // 倍率连乘 1.2×0.8；加法跨源求和 0.2+0.1
            var r = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(0, "s1", new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 1.2f, "s1"),
                                   new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Add, 0.2f, "s1")),
                        S(10, "s2", new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 0.8f, "s2"),
                                    new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Add, 0.1f, "s2")) });
            Assert.That(r.VerticalRecoilDeg, Is.EqualTo((1.1f + 0.3f) * 1.2f * 0.8f).Within(1e-4f));
        }

        [Test]
        public void ScaleStats_BaseIsOne()
        {
            // 倍率型：base=1；Add 0.2 + mul 1.5 → (1+0.2)×1.5=1.8
            var r = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(10, "skill", new WeaponStatModifier(WeaponStatId.AimRecoil, ModifierOperation.Add, 0.2f, "skill"),
                                        new WeaponStatModifier(WeaponStatId.AimRecoil, ModifierOperation.Multiply, 1.5f, "skill")) });
            Assert.That(r.AimRecoilScale, Is.EqualTo(1.8f).Within(1e-4f));
        }

        [Test]
        public void IllegalValues_RejectedWithWarning()
        {
            LogAssert.Expect(LogType.Warning, new Regex("非法数值"));
            var r = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(0, "broken", new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Add, float.NaN, "broken"),
                                        new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, float.PositiveInfinity, "broken"),
                                        new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Add, 0.4f, "broken")) });
            // NaN/Inf 被拒；合法 Add 0.4 保留
            Assert.That(r.VerticalRecoilDeg, Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(r.SpreadScale, Is.EqualTo(1f));
        }

        [Test]
        public void NegativeMultiply_ClampedToZero_ZeroIsLegal()
        {
            // 负乘数 → clamp 0（完全消除=合法设计）；显式 0 乘数同样保留 0（§6.2 ④）
            var r = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(20, "buff", new WeaponStatModifier(WeaponStatId.HorizontalRecoil, ModifierOperation.Multiply, -1.5f, "buff")) });
            Assert.That(r.HorizontalRecoilDeg, Is.EqualTo(0f));
            var r2 = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(20, "buff", new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, 0f, "buff")) });
            Assert.That(r2.SpreadScale, Is.EqualTo(0f));
        }

        [Test]
        public void Clamp_AppliedLast()
        {
            // 度值上限：1.1+50 → clamp 15（§6.1）
            var r = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(0, "huge", new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Add, 50f, "huge")) });
            Assert.That(r.VerticalRecoilDeg, Is.EqualTo(15f));
            // 倍率上限：(1+0)×100 → clamp 10
            var r2 = WeaponStatResolver.Resolve(BaseStat(),
                new[] { S(0, "huge", new WeaponStatModifier(WeaponStatId.AimRecoil, ModifierOperation.Multiply, 100f, "huge")) });
            Assert.That(r2.AimRecoilScale, Is.EqualTo(10f));
        }

        [Test]
        public void EffectiveAccessors_CombineChannels()
        {
            // EffectiveRecoilPitch = Vertical × AimRecoil（同源作用弹道与相机）
            var r = WeaponStatResolver.Resolve(BaseStat(pitch: 1.1f),
                new[] { S(0, "s", new WeaponStatModifier(WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 0.8f, "s"),
                                  new WeaponStatModifier(WeaponStatId.AimRecoil, ModifierOperation.Multiply, 1.5f, "s"),
                                  new WeaponStatModifier(WeaponStatId.FirstShotRecoil, ModifierOperation.Multiply, 2f, "s"),
                                  new WeaponStatModifier(WeaponStatId.ViewModelKick, ModifierOperation.Multiply, 0.5f, "s")) });
            Assert.That(r.EffectiveRecoilPitchDeg, Is.EqualTo(1.1f * 0.8f * 1.5f).Within(1e-4f));
            Assert.That(r.EffectiveFirstShotMultiplier, Is.EqualTo(1.25f * 2f).Within(1e-4f));
            Assert.That(r.EffectiveViewModelBack, Is.EqualTo(0.045f * 0.5f).Within(1e-5f));
            Assert.That(r.EffectiveRecoverySpeed, Is.EqualTo(6f).Within(1e-5f));
        }

        [Test]
        public void Deterministic_RegardlessOfSourceOrder()
        {
            var a = new[] { S(20, "b", new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, 1.1f, "b")),
                            S(0, "a", new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, 1.2f, "a")) };
            var b = new[] { S(0, "a", new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, 1.2f, "a")),
                            S(20, "b", new WeaponStatModifier(WeaponStatId.Spread, ModifierOperation.Multiply, 1.1f, "b")) };
            Assert.That(WeaponStatResolver.Resolve(BaseStat(), a).SpreadScale,
                Is.EqualTo(WeaponStatResolver.Resolve(BaseStat(), b).SpreadScale).Within(1e-6f));
        }

        [Test]
        public void LegacyZeroAsset_MigrationGuard()
        {
            // 旧资产：分组字段全 0（反序列化默认）+ 平铺 Spread=1.2 → Sanitize 兜底（§6.1 迁移守卫）
            var balance = ScriptableObject.CreateInstance<DemoBalanceConfig>();
            try
            {
                var so = new SerializedObject(balance);
                var weapons = so.FindProperty("weapons");
                weapons.arraySize = 1;
                weapons.GetArrayElementAtIndex(0).FindPropertyRelative("WeaponId").stringValue = "legacy.test";
                var stat = weapons.GetArrayElementAtIndex(0).FindPropertyRelative("Stat");
                stat.FindPropertyRelative("Damage").intValue = 26;
                stat.FindPropertyRelative("Rpm").intValue = 600;
                stat.FindPropertyRelative("MagSize").intValue = 30;
                stat.FindPropertyRelative("Spread").floatValue = 1.2f;
                stat.FindPropertyRelative("AdsFov").floatValue = 50f;
                so.ApplyModifiedPropertiesWithoutUndo();

                var s = balance.GetWeaponStat("legacy.test");
                // Recoil：legacy 弹簧 + 中性乘法 + 决策默认
                Assert.That(s.Recoil.PitchDeg, Is.EqualTo(1.1f));          // CP2 legacy
                Assert.That(s.Recoil.SpringFrequency, Is.EqualTo(9f));      // CP2 legacy
                Assert.That(s.Recoil.SpringDamping, Is.EqualTo(0.75f));     // CP2 legacy
                Assert.That(s.Recoil.FirstShotMultiplier, Is.EqualTo(1f));  // 中性
                Assert.That(s.Recoil.AdsRecoilMultiplier, Is.EqualTo(0.6f));// 决策默认
                // Accuracy：兼容映射 Spread→BaseHipSpread
                Assert.That(s.Accuracy.BaseHipSpread, Is.EqualTo(1.2f));
                Assert.That(s.Accuracy.BloomRecoverySpeed, Is.EqualTo(5f));
                // Ballistic：单发中性
                Assert.That(s.Ballistic.PelletCount, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(balance); }
        }
    }
}
