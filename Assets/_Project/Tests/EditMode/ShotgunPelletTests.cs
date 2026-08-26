using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CP4：Shotgun 多弹丸语义（Docs/13 §5.3-10）——弹丸围绕主方向独立锥、聚合主 Result
    /// （Damaged 优先）、单次广播。对私有 ApplySpread 的锥几何在 CP0 基线上复用。
    /// </summary>
    public sealed class ShotgunPelletTests
    {
        private static ResolvedWeaponStats Stats(int pellets, float pelletSpread)
            => WeaponStatResolver.Resolve(new Game.Core.WeaponStat
            {
                Damage = 8, Rpm = 70, MagSize = 6, ReserveAmmo = 30,
                ReloadTime = 3f, Spread = 3.5f, MaxRange = 40, AdsFov = 55,
                Recoil = new Game.Core.RecoilProfileData { PitchDeg = 2.6f, SpringFrequency = 7, SpringDamping = 0.8f, RecoverySpeed = 3, RecoveryDelay = 0.3f, AdsRecoilMultiplier = 0.6f, FirstShotMultiplier = 1, Accumulation = 1, MaxAccumulation = 1, ViewModelKickBack = 0.08f, ViewModelKickPitch = 7f },
                Accuracy = new Game.Core.AccuracyProfileData { BaseHipSpread = 3.5f, BaseAdsSpread = 1.5f, ShotBloomPerShot = 0.8f, MaxBloom = 2f, BloomRecoveryDelay = 0.25f, BloomRecoverySpeed = 3 },
                Ballistic = new Game.Core.BallisticProfileData { PelletCount = pellets, PelletSpread = pelletSpread },
            }, null);

        [Test]
        public void PelletGeometry_AroundMainDirection_WithinPelletCone()
        {
            // 每弹丸 = ApplySpread(主方向, PelletSpread)：围绕主方向、锥角≤PelletSpread。
            // 象限判定用主方向局部系（世界系对倾斜主方向天然只覆盖 2 象限——几何正确非缺陷）。
            var main = Quaternion.Euler(5f, -10f, 0f) * Vector3.forward;
            var mainRight = Vector3.Cross(Vector3.up, main).normalized;
            var mainUp = Vector3.Cross(main, mainRight).normalized;
            var rng = new System.Random(11);
            var method = typeof(WeaponController).GetMethod("ApplySpread",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            float maxAngle = 0f;
            var seen = new bool[4];
            for (int i = 0; i < 400; i++)
            {
                var d = (Vector3)method.Invoke(null, new object[] { main, 3f, rng });
                float ang = Vector3.Angle(main, d);
                Assert.That(ang, Is.LessThanOrEqualTo(3f + 0.15f), "弹丸超出 PelletSpread 锥");
                maxAngle = Mathf.Max(maxAngle, ang);
                float lx = Vector3.Dot(d, mainRight), ly = Vector3.Dot(d, mainUp);
                int q = (lx >= 0 ? 1 : 0) | (ly >= 0 ? 2 : 0);
                seen[q] = true;
            }
            Assert.That(maxAngle, Is.GreaterThan(3f * 0.9f), "弹丸应覆盖锥边缘");
            Assert.That(System.Array.TrueForAll(seen, v => v), "主方向局部系四象限覆盖");
        }

        [Test]
        public void Stat_ShotgunNinePellets_Resolved()
        {
            var s = Stats(9, 3f);
            Assert.That(s.Stat.Ballistic.PelletCount, Is.EqualTo(9));
            Assert.That(s.Stat.Ballistic.PelletSpread, Is.EqualTo(3f));
        }

        [Test]
        public void Pellet_AroundMain_MeanConvergesToMain()
        {
            // 400 样本均值方向应接近主方向（分布中心=主方向，Docs/13 §6.3"主方向+独立锥"）
            var main = Vector3.forward;
            var rng = new System.Random(3);
            var method = typeof(WeaponController).GetMethod("ApplySpread",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var sum = Vector3.zero;
            for (int i = 0; i < 400; i++)
                sum += (Vector3)method.Invoke(null, new object[] { main, 4f, rng });
            float meanAngle = Vector3.Angle(main, sum.normalized);
            Assert.That(meanAngle, Is.LessThan(1.5f), "弹丸分布中心应≈主方向（均值角=" + meanAngle + "°）");
        }
    }
}
