using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 远端视觉缓冲插值（2026-09-17 实机"远端走动掉帧"修复）纯逻辑锁定：
    /// 渲染时刻=now−自适应延迟的两样本线性插值；延迟=EMA(到达间隔)×2.1 钳 [0.05,0.25]；
    /// 饥饿保持最新、超前保持最老、同刻覆盖、陈旧样本裁剪。
    /// </summary>
    public sealed class RemoteVisualInterpolationBufferTests
    {
        private const float MinDelay = MovementPredictionConfig.RemoteVisualInterpMinDelaySeconds;
        private const float MaxDelay = MovementPredictionConfig.RemoteVisualInterpMaxDelaySeconds;

        /// <summary>以固定 30Hz 推入足够多样本让 EMA 收敛，返回 (buffer, lastTime)。</summary>
        private static (RemoteVisualInterpolationBuffer Buffer, float LastTime) FillSteady(int samples, float interval)
        {
            var buffer = new RemoteVisualInterpolationBuffer();
            float t = 0f;
            for (int i = 0; i < samples; i++)
            {
                t = i * interval;
                buffer.Push(t, new Vector3(0f, 0f, i), Quaternion.Euler(0f, i, 0f), MinDelay, MaxDelay);
            }
            return (buffer, t);
        }

        [Test]
        public void Evaluate_InterpolatesLinearlyBetweenSamples()
        {
            var (buffer, lastTime) = FillSteady(90, 1f / 30f);
            // EMA 收敛后 delay = clamp(2.1×0.0333, 0.05, 0.25) ≈ 0.07
            Assert.That(buffer.CurrentDelay, Is.EqualTo(0.07f).Within(0.005f), "自适应延迟应≈2.1×到达间隔");

            float now = lastTime;
            Assert.That(buffer.Evaluate(now, out var pos, out var rot), Is.True);
            // 渲染时刻 = now−0.07 → 比最新样本(第89个,z=89)落后约 2.1 个间隔
            float expectedZ = 89f - 0.07f * 30f;
            Assert.That(pos.z, Is.EqualTo(expectedZ).Within(0.25f), "插值必须落在两样本之间（非台阶跳变）");
            // 旋转同样平滑（yaw 随样本线性）
            Assert.That(rot.eulerAngles.y, Is.GreaterThan(0f));
        }

        [Test]
        public void Evaluate_StarvationHoldsLatest()
        {
            var (buffer, lastTime) = FillSteady(30, 1f / 30f);
            // 断流：now 远超最新样本+延迟 → 保持最新位姿（不后撤、不外推）
            Assert.That(buffer.Evaluate(lastTime + 5f, out var pos, out _), Is.True);
            Assert.That(pos.z, Is.EqualTo(29f).Within(1e-4f));
        }

        [Test]
        public void Evaluate_BeforeFirstSampleHoldsOldest()
        {
            var buffer = new RemoteVisualInterpolationBuffer();
            buffer.Push(10f, new Vector3(0f, 0f, 5f), Quaternion.identity, MinDelay, MaxDelay);
            buffer.Push(10.033f, new Vector3(0f, 0f, 6f), Quaternion.identity, MinDelay, MaxDelay);
            // 渲染时刻早于首个样本 → 保持最老样本
            Assert.That(buffer.Evaluate(10f, out var pos, out _), Is.True);
            Assert.That(pos.z, Is.EqualTo(5f).Within(1e-4f));
        }

        [Test]
        public void Delay_ClampsToMax_OnSlowArrival()
        {
            var buffer = new RemoteVisualInterpolationBuffer();
            for (int i = 0; i < 10; i++)
                buffer.Push(i * 0.5f, new Vector3(0f, 0f, i), Quaternion.identity, MinDelay, MaxDelay);
            Assert.That(buffer.CurrentDelay, Is.EqualTo(MaxDelay).Within(1e-4f),
                "慢到达（0.5s/帧）时延迟必须被钳在上限，不得无限放大视觉滞后");
        }

        [Test]
        public void Push_SameTimestamp_OverwritesInsteadOfAppending()
        {
            var buffer = new RemoteVisualInterpolationBuffer();
            buffer.Push(1f, Vector3.zero, Quaternion.identity, MinDelay, MaxDelay);
            buffer.Push(1f, new Vector3(0f, 0f, 9f), Quaternion.identity, MinDelay, MaxDelay);
            Assert.That(buffer.Count, Is.EqualTo(1), "同一时刻重复推入必须覆盖而非追加（保持时间递增）");
            Assert.That(buffer.LatestPosition.z, Is.EqualTo(9f).Within(1e-4f));
        }

        [Test]
        public void Push_PrunesStaleSamples()
        {
            var buffer = new RemoteVisualInterpolationBuffer();
            buffer.Push(0f, Vector3.zero, Quaternion.identity, MinDelay, MaxDelay);
            buffer.Push(0.033f, Vector3.one, Quaternion.identity, MinDelay, MaxDelay);
            buffer.Push(2f, new Vector3(0f, 0f, 9f), Quaternion.identity, MinDelay, MaxDelay);
            Assert.That(buffer.Count, Is.EqualTo(1), "超过 1.5s 的陈旧样本必须被裁剪");
            Assert.That(buffer.LatestPosition.z, Is.EqualTo(9f).Within(1e-4f));
        }

        [Test]
        public void Reset_ClearsSamplesAndDelay()
        {
            var (buffer, _) = FillSteady(10, 1f / 30f);
            buffer.Reset();
            Assert.That(buffer.HasSamples, Is.False);
            Assert.That(buffer.Evaluate(100f, out _, out _), Is.False);
        }
    }
}
