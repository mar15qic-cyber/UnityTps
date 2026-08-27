using Game.Core;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>Day4 回归：持续爬升、后坐债务补偿、停火恢复、确定性与方向符号。</summary>
    public sealed class WeaponRecoilStateSpringTests
    {
        private const float Dt = 1f / 60f;

        private static ResolvedWeaponStats Stats(float pitch = 1.1f, float yaw = 0.3f,
            float first = 1f, float acc = 1f, float maxAcc = 8f, float recDelay = 0.2f, float recSpeed = 6f,
            float vmBack = 0.045f, float vmPitch = 4f)
            => WeaponStatResolver.Resolve(new WeaponStat
            {
                Damage = 26, Rpm = 600, MagSize = 30, ReserveAmmo = 120,
                ReloadTime = 2f, Spread = 1f, MaxRange = 100, AdsFov = 50,
                Recoil = new RecoilProfileData
                {
                    PitchDeg = pitch, YawDeg = yaw, FirstShotMultiplier = first,
                    Accumulation = acc, MaxAccumulation = maxAcc,
                    RecoveryDelay = recDelay, RecoverySpeed = recSpeed,
                    SpringFrequency = 9f, SpringDamping = 0.75f,
                    ViewModelKickBack = vmBack, ViewModelKickPitch = vmPitch,
                    AdsRecoilMultiplier = 0.6f,
                },
                Accuracy = new AccuracyProfileData { BaseHipSpread = 1f, BaseAdsSpread = 0.3f, BloomRecoverySpeed = 5f },
                Ballistic = new BallisticProfileData { PelletCount = 1 },
            }, null);

        [Test]
        public void OnShot_AccumulatesAimDebtWhileAutomaticFireIsHeld()
        {
            var s = Stats(pitch: 1.1f, acc: 1.4f, maxAcc: 8f, recDelay: 0.2f);
            var state = new WeaponRecoilState(seed: 1);
            float previous = 0f;
            for (int i = 0; i < 10; i++)
            {
                state.OnShot(WeaponFireContext.Default, s);
                Assert.That(state.CurrentOffset.x, Is.GreaterThanOrEqualTo(previous));
                previous = state.CurrentOffset.x;
                state.Tick(Dt, s); // 自动步枪射速下，始终未超过恢复门控
            }
            Assert.That(state.CurrentOffset.x, Is.GreaterThan(3f));
        }

        [Test]
        public void Recovery_DoesNotStartBeforeDelay_ThenReturnsToZero()
        {
            var s = Stats(recDelay: 0.2f, recSpeed: 6f);
            var state = new WeaponRecoilState(seed: 1);
            state.OnShot(WeaponFireContext.Default, s);
            float afterShot = state.CurrentOffset.x;
            state.Tick(0.1f, s);
            Assert.That(state.CurrentOffset.x, Is.EqualTo(afterShot).Within(1e-5f));
            state.Tick(0.11f, s);
            Assert.That(state.CurrentOffset.x, Is.LessThan(afterShot));
            for (int i = 0; i < 120; i++) state.Tick(Dt, s);
            Assert.That(state.CurrentOffset, Is.EqualTo(Vector2.zero).Within(1e-4f));
        }

        [Test]
        public void Compensation_ConsumesOppositeInputBeforeBaseAim()
        {
            var s = Stats(pitch: 1.1f, yaw: 0.6f, recDelay: 5f);
            var state = new WeaponRecoilState(seed: 7);
            state.OnShot(WeaponFireContext.Default, s);
            float pitchDebt = state.CurrentOffset.x;
            Vector2 remaining = state.ConsumeCompensation(new Vector2(-pitchDebt * 0.5f, 0f));
            Assert.That(remaining.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(state.CurrentOffset.x, Is.EqualTo(pitchDebt * 0.5f).Within(1e-5f));

            remaining = state.ConsumeCompensation(new Vector2(-10f, 0f));
            Assert.That(state.CurrentOffset.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(remaining.x, Is.LessThan(0f), "超出债务的输入必须继续传给基础视角");
        }

        [Test]
        public void OffsetRotation_PositivePitchMovesAimUp()
        {
            var s = Stats(pitch: 1.5f, recDelay: 5f);
            var state = new WeaponRecoilState(seed: 1);
            state.OnShot(WeaponFireContext.Default, s);
            Vector3 forward = state.OffsetRotation * Vector3.forward;
            Assert.That(forward.y, Is.GreaterThan(0f), "正后坐 Pitch 必须把瞄准方向抬高");
        }

        [Test]
        public void AdsMultiplier_ScalesKickByContext()
        {
            var s = Stats();
            var state = new WeaponRecoilState(seed: 1);
            var hip = state.OnShot(new WeaponFireContext(0f, 0f, false, true, false), s);
            state.HardReset();
            var ads = state.OnShot(new WeaponFireContext(1f, 0f, false, true, false), s);
            Assert.That(ads.PitchKickDeg, Is.EqualTo(hip.PitchKickDeg * 0.6f).Within(1e-4f));
        }

        [Test]
        public void HardReset_ClearsDebtAndBurst()
        {
            var s = Stats();
            var state = new WeaponRecoilState(seed: 1);
            state.OnShot(WeaponFireContext.Default, s);
            state.Tick(Dt, s);
            state.HardReset();
            Assert.That(state.CurrentOffset, Is.EqualTo(Vector2.zero));
            Assert.That(state.BurstAccumulation, Is.EqualTo(0f));
            Assert.That(state.ShotIndex, Is.EqualTo(-1));
            Assert.That(state.OnShot(WeaponFireContext.Default, s).FirstShot, Is.True);
        }

        [Test]
        public void Deterministic_WithSameSeed()
        {
            var s = Stats(yaw: 1.5f);
            var a = new WeaponRecoilState(99);
            var b = new WeaponRecoilState(99);
            for (int i = 0; i < 20; i++)
            {
                var ra = a.OnShot(WeaponFireContext.Default, s);
                var rb = b.OnShot(WeaponFireContext.Default, s);
                Assert.That(ra.YawKickDeg, Is.EqualTo(rb.YawKickDeg).Within(1e-5f));
                Assert.That(a.CurrentOffset, Is.EqualTo(b.CurrentOffset).Within(1e-5f * Vector2.one));
                a.Tick(Dt, s); b.Tick(Dt, s);
            }
        }

        [Test]
        public void ViewModelAndShake_CarriedInResult()
        {
            var s = Stats(vmBack: 0.05f, vmPitch: 4f);
            var state = new WeaponRecoilState(seed: 1);
            var r = state.OnShot(WeaponFireContext.Default, s);
            Assert.That(r.ViewModelBackM, Is.EqualTo(0.05f).Within(1e-5f));
            Assert.That(r.ViewModelPitchDeg, Is.EqualTo(4f).Within(1e-5f));
        }
    }
}
