using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 弹簧数学与旧 CmFPCameraRecoil 逐式等价（低级原语 ApplyImpulse/TickSpring 锚点）
    /// + 高级组合语义（首枪/累计/恢复门控/ADS/硬重置/确定性，CP4 扩展）。
    /// </summary>
    public sealed class WeaponRecoilStateSpringTests
    {
        private const float Kick = 1.1f;
        private const float Yaw = 0.3f;
        private const float Freq = 9f;
        private const float Damping = 0.75f;
        private const float Dt = 1f / 60f;

        private static ResolvedWeaponStats Stats(float pitch = 1.1f, float yaw = 0.3f,
            float first = 1f, float acc = 1f, float maxAcc = 8f, float recDelay = 0.2f, float recSpeed = 6f,
            float vmBack = 0.045f, float vmPitch = 4f, float shake = 0.3f, float springF = Freq, float springD = Damping)
            => WeaponStatResolver.Resolve(new Game.Core.WeaponStat
            {
                Damage = 26, Rpm = 600, MagSize = 30, ReserveAmmo = 120,
                ReloadTime = 2f, Spread = 1f, MaxRange = 100, AdsFov = 50,
                Recoil = new Game.Core.RecoilProfileData
                {
                    PitchDeg = pitch, YawDeg = yaw, FirstShotMultiplier = first,
                    Accumulation = acc, MaxAccumulation = maxAcc,
                    RecoveryDelay = recDelay, RecoverySpeed = recSpeed,
                    SpringFrequency = springF, SpringDamping = springD,
                    ShakePositionAmplitude = shake, ViewModelKickBack = vmBack, ViewModelKickPitch = vmPitch,
                    AdsRecoilMultiplier = 0.6f,
                },
                Accuracy = new Game.Core.AccuracyProfileData { BaseHipSpread = 1f, BaseAdsSpread = 0.3f, BloomRecoverySpeed = 5 },
                Ballistic = new Game.Core.BallisticProfileData { PelletCount = 1 },
            }, null);

        // ---------------- 低级原语：legacy 逐式等价 ----------------

        [Test]
        public void Impulse_MatchesLegacyFormula()
        {
            // legacy：v0 = Kick·ω（yaw=0 无水平分量）
            var state = new WeaponRecoilState(seed: 42);
            state.ApplyImpulse(Kick, 0f, Freq);

            Vector2 legacyOffset = Vector2.zero;
            Vector2 legacyVelocity = new Vector2(Kick, 0f) * (Freq * Mathf.PI * 2f);
            for (int i = 0; i < 3; i++)
            {
                state.TickSpring(Dt, Freq, Damping);
                StepLegacy(ref legacyOffset, ref legacyVelocity);
            }
            Assert.That(state.CurrentOffset.x, Is.EqualTo(legacyOffset.x).Within(1e-4f));
            Assert.That(state.CurrentOffset.y, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void SpringIntegration_MatchesLegacyStepMath()
        {
            var state = new WeaponRecoilState(seed: 7);
            state.ApplyImpulse(Kick, 0f, Freq);

            Vector2 legacyOffset = Vector2.zero, legacyVelocity = Vector2.zero;
            legacyVelocity += new Vector2(Kick, 0f) * (Freq * Mathf.PI * 2f);

            float peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                state.TickSpring(Dt, Freq, Damping);
                StepLegacy(ref legacyOffset, ref legacyVelocity);
                peak = Mathf.Max(peak, Mathf.Abs(state.CurrentOffset.x));
                Assert.That(state.CurrentOffset.x, Is.EqualTo(legacyOffset.x).Within(1e-4f), $"第 {i + 1} 步不等价");
            }
            // ζ=0.75：峰值≈v0/ωd·e^(-φ)≈0.43°（介于 0.3~1.5 倍 Kick）；2 秒后基本回零
            Assert.That(peak, Is.InRange(Kick * 0.3f, Kick * 1.5f), "peak=" + peak);
            Assert.That(Mathf.Abs(state.CurrentOffset.x), Is.LessThan(Kick * 0.05f), "长程回中");
        }

        // ---------------- 高级组合（CP4） ----------------

        [Test]
        public void FirstShot_UsesFirstShotMultiplier_InternalJudgment()
        {
            var s = Stats(first: 2f, recDelay: 0.2f);
            var state = new WeaponRecoilState(seed: 1);
            var ctx = WeaponFireContext.Default;

            var first = state.OnShot(ctx, s);   // 首发（内部判定，无外部传参）
            Assert.That(first.FirstShot, Is.True);
            Assert.That(first.ShotIndex, Is.EqualTo(0));
            Assert.That(first.PitchKickDeg, Is.EqualTo(1.1f * 2f).Within(1e-4f), "首发=base×FirstShotMultiplier");

            // 连发（间隔 1 帧 < RecoveryDelay）：非首枪
            state.Tick(Dt, s);
            var second = state.OnShot(ctx, s);
            Assert.That(second.FirstShot, Is.False);
            Assert.That(second.ShotIndex, Is.EqualTo(1));
            Assert.That(second.PitchKickDeg, Is.EqualTo(1.1f * 1f).Within(1e-4f), "次发 scale=Lerp(1,Acc,1/MaxAcc)，Acc=1→1");

            // 停火超过 RecoveryDelay → 新 burst（首枪判定阈值=RecoveryDelay，§5.3-4）
            for (int i = 0; i < 30; i++) state.Tick(Dt, s); // 0.5s > 0.2s
            var reFirst = state.OnShot(ctx, s);
            Assert.That(reFirst.FirstShot, Is.True, "停火>RecoveryDelay 应判定新 burst");
        }

        [Test]
        public void Accumulation_GrowsWithBurst_CappedAtMax()
        {
            // Acc=2, MaxAcc=4：第 n 发 scale=Lerp(1,2,(n-1)/4) → 1, 1.25, 1.5, 1.75, 2(封顶)
            var s = Stats(first: 1f, acc: 2f, maxAcc: 4f, recDelay: 5f); // 长延迟防测试内判新 burst
            var state = new WeaponRecoilState(seed: 1);
            var ctx = WeaponFireContext.Default;

            float[] expected = { 1f, 1.25f, 1.5f, 1.75f, 2f, 2f };
            for (int i = 0; i < expected.Length; i++)
            {
                var r = state.OnShot(ctx, s);
                state.Tick(Dt, s); // 1 帧间隔（≪RecoveryDelay，同 burst）
                Assert.That(r.PitchKickDeg, Is.EqualTo(1.1f * expected[i]).Within(1e-3f),
                    $"第 {i + 1} 发 kickScale 应为 {expected[i]}");
            }
        }

        [Test]
        public void BurstAccumulation_DecaysAfterRecoveryDelay_NotInstantly()
        {
            var s = Stats(acc: 2f, maxAcc: 4f, recDelay: 0.2f, recSpeed: 6f);
            var state = new WeaponRecoilState(seed: 1);
            var ctx = WeaponFireContext.Default;
            for (int i = 0; i < 4; i++) { state.OnShot(ctx, s); state.Tick(Dt, s); }
            Assert.That(state.BurstAccumulation, Is.EqualTo(4f).Within(1e-4f), "4 发累计=4（=MaxAcc）");

            // 停火 < RecoveryDelay：不衰减（§5.3-4 两套状态）。
            // 时序：起点 t≈0.0167（每发后 1 帧）；+6 帧=0.1s → t≈0.1167 < 0.2s 门控内
            for (int i = 0; i < 6; i++) state.Tick(Dt, s);
            Assert.That(state.BurstAccumulation, Is.EqualTo(4f).Within(1e-4f), "门控前不衰减");

            // 超过门控：+6 帧 → t≈0.2167 > 0.2s，门控生效约 1 帧 → 3.9（探针实测）
            for (int i = 0; i < 6; i++) state.Tick(Dt, s);
            Assert.That(state.BurstAccumulation, Is.EqualTo(3.9f).Within(0.06f), "门控后按生效帧数线性衰减");

            // 长时间停火：衰减到底为 0（+18 帧 × 6/s × 1/60 ≈ 1.8 > 3.9 剩余）
            for (int i = 0; i < 40; i++) state.Tick(Dt, s);
            Assert.That(state.BurstAccumulation, Is.EqualTo(0f).Within(1e-4f), "长停火衰减到底");

            // 下一发：重置计时，累计 0+1
            var r = state.OnShot(ctx, s);
            Assert.That(state.BurstAccumulation, Is.EqualTo(1f).Within(1e-4f), "衰减到底后重新累计");
        }

        [Test]
        public void AdsMultiplier_ScalesKickByContext()
        {
            var s = Stats(); // AdsRecoilMultiplier=0.6
            var state = new WeaponRecoilState(seed: 1);
            var hip = new WeaponFireContext(0f, 0f, false, true, false);
            var ads = new WeaponFireContext(1f, 0f, false, true, false);

            var rHip = state.OnShot(hip, s);
            var rAds = state.OnShot(ads, s);
            Assert.That(rHip.PitchKickDeg, Is.EqualTo(1.1f).Within(1e-4f), "腰射=base");
            Assert.That(rAds.PitchKickDeg, Is.EqualTo(1.1f * 0.6f).Within(1e-4f), "ADS=base×0.6");
        }

        [Test]
        public void HardReset_ClearsAll()
        {
            var s = Stats();
            var state = new WeaponRecoilState(seed: 1);
            var ctx = WeaponFireContext.Default;
            state.OnShot(ctx, s);
            state.Tick(Dt, s);
            Assert.That(state.BurstAccumulation, Is.GreaterThan(0f));
            Assert.That(state.CurrentOffset, Is.Not.EqualTo(Vector2.zero));

            state.HardReset();
            Assert.That(state.CurrentOffset, Is.EqualTo(Vector2.zero));
            Assert.That(state.BurstAccumulation, Is.EqualTo(0f));
            Assert.That(state.ShotIndex, Is.EqualTo(-1));
            state.Tick(Dt, s);
            Assert.That(state.CurrentOffset, Is.EqualTo(Vector2.zero).Within(1e-6f), "无残余速度");
            Assert.That(state.OnShot(ctx, s).FirstShot, Is.True, "重置后首枪");
        }

        [Test]
        public void ViewModelAndShake_CarriedInResult()
        {
            var s = Stats(vmBack: 0.05f, vmPitch: 4f, shake: 0.3f);
            var state = new WeaponRecoilState(seed: 1);
            var r = state.OnShot(WeaponFireContext.Default, s);
            Assert.That(r.ViewModelBackM, Is.EqualTo(0.05f).Within(1e-5f));
            Assert.That(r.ViewModelPitchDeg, Is.EqualTo(4f).Within(1e-5f));
            Assert.That(r.ShakeAmplitude, Is.EqualTo(0.3f).Within(1e-5f));
        }

        [Test]
        public void Deterministic_WithSameSeed()
        {
            var s = Stats(yaw: 1.5f); // 大 yaw 放大随机项差异
            var a = new WeaponRecoilState(99);
            var b = new WeaponRecoilState(99);
            var ctx = WeaponFireContext.Default;
            for (int i = 0; i < 20; i++)
            {
                var ra = a.OnShot(ctx, s); var rb = b.OnShot(ctx, s);
                Assert.That(ra.YawKickDeg, Is.EqualTo(rb.YawKickDeg).Within(1e-5f));
                a.Tick(Dt, s); b.Tick(Dt, s);
                Assert.That(a.CurrentOffset, Is.EqualTo(b.CurrentOffset).Within(1e-5f * Vector2.one));
            }
        }

        private static void StepLegacy(ref Vector2 offset, ref Vector2 velocity)
        {
            float omega = Freq * Mathf.PI * 2f;
            float c = 2f * Damping * omega;
            velocity += (-omega * omega * offset - c * velocity) * Dt;
            offset += velocity * Dt;
        }
    }
}
