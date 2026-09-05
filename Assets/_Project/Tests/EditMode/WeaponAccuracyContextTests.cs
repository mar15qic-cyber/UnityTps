using Game.Core;
using Game.Gameplay.Movement;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 动态准心情境数值（Docs/23 表现迭代 2026-09-05）：
    /// WeaponAccuracyState.CurrentSpread 是弹道锥角与准心 HUD 的同一数据源——
    /// 锁定 Idle < Walk < Sprint、Air > Grounded，与收拢单调性；映射端（CrosshairPresenter
    /// pxPerRad 公式）以 1080p/60°FOV 换算验证各状态 Gap 差值可感知且不超调。
    /// </summary>
    public sealed class WeaponAccuracyContextTests
    {
        private static readonly WeaponAccuracyState Accuracy = new();

        private static WeaponStat MakeStat()
        {
            var stat = new WeaponStat();
            stat.Accuracy.BaseHipSpread = 0.4f;
            stat.Accuracy.BaseAdsSpread = 0.12f;
            stat.Accuracy.MovementSpreadMax = 0.9f;
            stat.Accuracy.SprintSpreadExtra = 1.2f;
            stat.Accuracy.AirborneSpreadExtra = 1.25f;
            stat.Accuracy.ShotBloomPerShot = 0.4f;
            stat.Accuracy.MaxBloom = 2.2f;
            stat.Accuracy.BloomRecoveryDelay = 0.15f;
            stat.Accuracy.BloomRecoverySpeed = 6f;
            return stat;
        }

        private static ResolvedWeaponStats MakeResolved()
            => new ResolvedWeaponStats(MakeStat(),
                verticalRecoilDeg: 1f, horizontalRecoilDeg: 0.3f,
                aimRecoilScale: 1f, viewModelKickScale: 1f, recoilRecoveryScale: 1f,
                patternScale: 1f, firstShotScale: 1f, adsRecoilScale: 1f, spreadScale: 1f);

        private static WeaponFireContext Ctx(float speed01, bool sprint, bool grounded)
            => new WeaponFireContext(ads01: 0f, horizontalSpeed01: speed01, isSprinting: sprint, isGrounded: grounded, isCrouching: false);

        [Test]
        public void Idle_LessThan_Walk_LessThan_Sprint()
        {
            var s = MakeResolved();
            float idle = Accuracy.CurrentSpread(Ctx(0f, false, true), s);
            float walk = Accuracy.CurrentSpread(Ctx(0.4f, false, true), s);
            float sprint = Accuracy.CurrentSpread(Ctx(1f, true, true), s);

            Assert.That(walk, Is.GreaterThan(idle), "走路散布必须明显大于站立（准心扩张数据源）");
            Assert.That(sprint, Is.GreaterThan(walk), "冲刺散布必须明显大于走路");
            // "明显"量化：走路至少 +0.3°（MovementSpreadMax=0.9 × 0.4 速度比），冲刺至少 +1°
            Assert.That(walk - idle, Is.GreaterThanOrEqualTo(0.3f));
            Assert.That(sprint - walk, Is.GreaterThanOrEqualTo(1f));
        }

        [Test]
        public void Air_GreaterThan_Grounded()
        {
            var s = MakeResolved();
            float grounded = Accuracy.CurrentSpread(Ctx(0f, false, true), s);
            float air = Accuracy.CurrentSpread(Ctx(0f, false, false), s);
            Assert.That(air - grounded, Is.GreaterThanOrEqualTo(1f), "滞空惩罚 AirborneSpreadExtra 必须生效");
        }

        [Test]
        public void Land_ReturnsToGroundedValue_AndIdleRecovers()
        {
            var s = MakeResolved();
            float grounded = Accuracy.CurrentSpread(Ctx(0f, false, true), s);
            float air = Accuracy.CurrentSpread(Ctx(0f, false, false), s);
            // 落地瞬间（同一散布状态，情境切回 grounded）立即恢复地面锥角
            float landed = Accuracy.CurrentSpread(Ctx(0f, false, true), s);
            Assert.That(landed, Is.EqualTo(grounded));
            Assert.That(air, Is.GreaterThan(landed));
        }

        [Test]
        public void GapMapping_1080p_StateDifferences_ArePerceivable()
        {
            // CrosshairPresenter 映射：GapPx = tan(spread)·(屏高/2)/tan(FOV/2)·GapScale
            const float screenHeight = 1080f;
            const float fovDeg = 60f;
            System.Func<float, float> gap = (spreadDeg) =>
                Mathf.Tan(spreadDeg * Mathf.Deg2Rad) * (screenHeight * 0.5f / Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad));

            var s = MakeResolved();
            float idle = Accuracy.CurrentSpread(Ctx(0f, false, true), s);
            float walk = Accuracy.CurrentSpread(Ctx(0.4f, false, true), s);
            float sprint = Accuracy.CurrentSpread(Ctx(1f, true, true), s);
            float air = Accuracy.CurrentSpread(Ctx(0f, false, false), s);

            float gapIdle = gap(idle);
            float gapWalk = gap(walk);
            float gapSprint = gap(sprint);
            float gapAir = gap(air);

            // 可感知阈值：相邻状态 Gap 差 ≥ 5px（1080p 下 0.4°≈7.6px；走路差 0.36°≈5.9px 仍清晰可辨）
            Assert.That(gapWalk - gapIdle, Is.GreaterThanOrEqualTo(5f), "走路扩张在 1080p 下不可感知");
            Assert.That(gapSprint - gapWalk, Is.GreaterThanOrEqualTo(15f), "冲刺扩张在 1080p 下不可感知");
            Assert.That(gapAir - gapIdle, Is.GreaterThanOrEqualTo(15f), "滞空扩张在 1080p 下不可感知");
            // 上限保护：不越过 MaxGap（160px）
            Assert.That(gapSprint, Is.LessThan(160f));
        }

        [Test]
        public void BloomRecovery_MonotonicDecay_NoOvershoot()
        {
            var s = MakeResolved();
            var acc = new WeaponAccuracyState();
            acc.OnShot(s);
            acc.OnShot(s);
            float bloomed = acc.CurrentSpread(Ctx(0f, false, true), s);

            // BloomRecoveryDelay=0.15s 门控后衰减：单调下降，不低于基础值
            float prev = bloomed;
            acc.Tick(0.1f, s); // 门控期内
            Assert.That(acc.CurrentSpread(Ctx(0f, false, true), s), Is.EqualTo(bloomed), "门控期内不衰减");
            for (int i = 0; i < 30; i++)
            {
                acc.Tick(0.1f, s);
                float cur = acc.CurrentSpread(Ctx(0f, false, true), s);
                Assert.That(cur, Is.LessThanOrEqualTo(prev + 0.0001f), "恢复必须单调不超调");
                prev = cur;
            }
            Assert.That(prev, Is.LessThanOrEqualTo(MakeResolved().Stat.Accuracy.BaseHipSpread + 0.0001f), "充分恢复后回到基础锥角");
        }
    }
}
