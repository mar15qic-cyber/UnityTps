using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CP2（Docs/13 检查点 2）：WeaponRecoilState 弹簧数学与旧 CmFPCameraRecoil
    /// 的逐式等价断言——冲量公式、欠阻尼积分、HardReset、确定性播种。
    /// </summary>
    public sealed class WeaponRecoilStateSpringTests
    {
        private const float Kick = 1.1f;
        private const float Yaw = 0.3f;
        private const float Freq = 9f;
        private const float Damping = 0.75f;
        private const float Dt = 1f / 60f;

        [Test]
        public void Impulse_MatchesLegacyFormula()
        {
            // 旧式冲量：v0 = Kick · ω（yawKick=0 → 无水平分量，纯确定性断言）
            // 1.1 × 9 × 2π = 62.2035…
            Assert.That(Kick * Freq * Mathf.PI * 2f, Is.EqualTo(62.20354f).Within(1e-3f));

            // 状态机 vs legacy 复刻（含冲量+积分）逐步等价
            var state = new WeaponRecoilState(seed: 42);
            state.OnShot(Kick, 0f, Freq);

            Vector2 legacyOffset = Vector2.zero;
            Vector2 legacyVelocity = new Vector2(Kick, 0f) * (Freq * Mathf.PI * 2f);
            for (int i = 0; i < 3; i++)
            {
                state.Tick(Dt, Freq, Damping);
                StepLegacy(ref legacyOffset, ref legacyVelocity);
            }
            Assert.That(state.CurrentOffset.x, Is.EqualTo(legacyOffset.x).Within(1e-4f));
            Assert.That(state.CurrentOffset.y, Is.EqualTo(0f).Within(1e-6f), "yawKick=0 时不应产生水平偏移");
        }

        [Test]
        public void SpringIntegration_MatchesLegacyStepMath()
        {
            // 复刻旧 CmFPCameraRecoil.PostPipelineStageCallback 的逐步积分，逐帧比对 2 秒
            var state = new WeaponRecoilState(seed: 7);
            state.OnShot(Kick, 0f, Freq);

            Vector2 legacyOffset = Vector2.zero, legacyVelocity = Vector2.zero;
            legacyVelocity += new Vector2(Kick, 0f) * (Freq * Mathf.PI * 2f);

            float peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                state.Tick(Dt, Freq, Damping);
                StepLegacy(ref legacyOffset, ref legacyVelocity);
                peak = Mathf.Max(peak, Mathf.Abs(state.CurrentOffset.x));

                Assert.That(state.CurrentOffset.x, Is.EqualTo(legacyOffset.x).Within(1e-4f),
                    $"第 {i + 1} 步 pitch 偏移不等价");
            }

            // 物理合理性（ζ=0.75 重阻尼：峰值≈v0/ωd·e^(-φ)≈0.43°，介于 0.3~1.5 倍 Kick 均合理；
            // 主循环全程记录 peak；120 步≈18 周期后基本回零）
            Assert.That(peak, Is.InRange(Kick * 0.3f, Kick * 1.5f), "阻尼弹簧超调峰值应在合理区间（peak=" + peak + "）");
            Assert.That(Mathf.Abs(state.CurrentOffset.x), Is.LessThan(Kick * 0.05f), "长程回中");
        }

        [Test]
        public void Tick_NonPositiveDt_IsNoOp()
        {
            var state = new WeaponRecoilState(seed: 1);
            state.OnShot(Kick, Yaw, Freq);
            state.Tick(Dt, Freq, Damping);
            var before = state.CurrentOffset;

            state.Tick(0f, Freq, Damping);
            state.Tick(-1f, Freq, Damping);

            Assert.That(state.CurrentOffset, Is.EqualTo(before));
        }

        [Test]
        public void HardReset_ClearsOffsetAndVelocity()
        {
            var state = new WeaponRecoilState(seed: 1);
            state.OnShot(Kick, Yaw, Freq);
            state.Tick(Dt, Freq, Damping);
            Assert.That(state.CurrentOffset, Is.Not.EqualTo(Vector2.zero));

            state.HardReset();

            Assert.That(state.CurrentOffset, Is.EqualTo(Vector2.zero));
            // 复位后无残余速度：继续 Tick 应保持为零
            state.Tick(Dt, Freq, Damping);
            Assert.That(state.CurrentOffset, Is.EqualTo(Vector2.zero).Within(1e-6f));
        }

        [Test]
        public void Deterministic_WithSameSeed()
        {
            var a = new WeaponRecoilState(seed: 99);
            var b = new WeaponRecoilState(seed: 99);
            for (int i = 0; i < 30; i++)
            {
                a.OnShot(Kick, Yaw, Freq);
                b.OnShot(Kick, Yaw, Freq);
                a.Tick(Dt, Freq, Damping);
                b.Tick(Dt, Freq, Damping);
            }
            Assert.That(a.CurrentOffset.x, Is.EqualTo(b.CurrentOffset.x).Within(1e-5f));
            Assert.That(a.CurrentOffset.y, Is.EqualTo(b.CurrentOffset.y).Within(1e-5f));
        }

        [Test]
        public void OffsetRotation_PitchPositive_LooksUp()
        {
            var state = new WeaponRecoilState(seed: 1);
            state.OnShot(5f, 0f, Freq); // 只给 pitch 冲量
            state.Tick(Dt, Freq, Damping);

            // 正 pitch 偏移 → 旋转后的 forward.y > 0（抬头，与旧相机后坐语义一致）
            Vector3 fwd = state.OffsetRotation * Vector3.forward;
            Assert.That(fwd.y, Is.GreaterThan(0f));
        }

        /// <summary>legacy 逐步积分复刻（旧 CmFPCameraRecoil.PostPipelineStageCallback 内联数学）。</summary>
        private static void StepLegacy(ref Vector2 offset, ref Vector2 velocity)
        {
            float omega = Freq * Mathf.PI * 2f;
            float c = 2f * Damping * omega;
            velocity += (-omega * omega * offset - c * velocity) * Dt;
            offset += velocity * Dt;
        }
    }
}
