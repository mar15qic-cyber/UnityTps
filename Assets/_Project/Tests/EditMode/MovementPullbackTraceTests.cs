using System.Collections.Generic;
using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 审计 2026-09-16 M3 + §3.2-4 定向回归：回拉取证必须能回答"哪一个写者把已经向前的位置写回去了"。
    /// 四类写者链：CC.Move 阶段 / 平滑校正步进 / 权威重基（重放后的最终根）/ 视觉插值（相邻渲染帧）；
    /// 并区分"没有反向"与"没想前进"。
    /// </summary>
    public sealed class MovementPullbackTraceTests
    {
        private static PullbackTraceSample Sample(
            Vector3 moveBefore, Vector3 moveAfter, Vector3 root,
            bool snap = false, Vector3 snapFrom = default, Vector3 snapTo = default, Vector2? move = null,
            Vector3? afterSmooth = null, Vector3? renderPrev = null, Vector3? renderNow = null)
        {
            var moveInput = move ?? new Vector2(0f, 1f); // 默认"想前进"
            var smooth = afterSmooth ?? moveAfter;       // 未显式给 = 无平滑校正
            var prev = renderPrev ?? root;
            var now = renderNow ?? root;
            return new PullbackTraceSample
            {
                ClientTick = 10,
                StepsInFrame = 1,
                MoveInput = moveInput,
                MoveBefore = moveBefore,
                MoveAfter = moveAfter,
                MoveAfterSmooth = smooth,
                SnapApplied = snap,
                SnapFrom = snapFrom,
                SnapTo = snapTo,
                RootAfterAll = root,
                RenderPrev = prev,
                RenderNow = now,
                Forward = Vector3.forward,
                ForwardDot = now.z - prev.z,
            };
        }

        /// <summary>CC.Move 阶段就反向 → 碰撞/重叠恢复或模拟状态（不是相机、不是纠偏）。</summary>
        [Test]
        public void Classify_MovePhase()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 0.8f),
                root: new Vector3(0f, 0f, 0.8f));

            Assert.That(MovementPullbackTrace.Classify(sample), Is.EqualTo(PullbackCause.MovePhase));
        }

        /// <summary>审计 §3.2-4：Simulate 前进、平滑校正步进把它写回 → 必须与 MovePhase 分开归类。</summary>
        [Test]
        public void Classify_SmoothPhase_SeparateFromMove()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 1.10f),
                root: new Vector3(0f, 0f, 0.90f),
                afterSmooth: new Vector3(0f, 0f, 0.90f));

            Assert.That(MovementPullbackTrace.Classify(sample), Is.EqualTo(PullbackCause.SmoothPhase),
                "Move 前进、Smooth 把它写回 ≠ MovePhase（旧实现把两者混成一条位移）");
        }

        /// <summary>模拟根前进，但重基+重放后的最终根被写回 → 权威纠偏。</summary>
        [Test]
        public void Classify_AuthorityCorrection()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 1.1f),
                root: new Vector3(0f, 0f, 0.9f),
                snap: true,
                snapFrom: new Vector3(0f, 0f, 1.1f),
                snapTo: new Vector3(0f, 0f, 0.9f));

            Assert.That(MovementPullbackTrace.Classify(sample), Is.EqualTo(PullbackCause.AuthorityCorrection));
        }

        /// <summary>
        /// 模拟根前进，相邻渲染帧的视觉位置反向 → 视觉插值/缓冲重置路径。
        /// 注意口径（§3.2-4）：必须比较**前后两帧**的世界位置，而不是同帧 render−root。
        /// </summary>
        [Test]
        public void Classify_RenderPhase()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 1.2f),
                root: new Vector3(0f, 0f, 1.2f),
                renderPrev: new Vector3(0f, 0f, 1.20f),
                renderNow: new Vector3(0f, 0f, 1.05f));

            Assert.That(MovementPullbackTrace.Classify(sample), Is.EqualTo(PullbackCause.RenderPhase));
        }

        /// <summary>
        /// 审计 §3.2-4 反例（旧口径误报）：正常一个 tick 的插值天然落后根——同帧 render−root 为负，
        /// 但渲染帧之间仍在向前 → 不是回拉。
        /// </summary>
        [Test]
        public void Classify_RenderBehindRootSameFrame_IsNotPullback()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.00f),
                moveAfter: new Vector3(0f, 0f, 1.05f),
                root: new Vector3(0f, 0f, 1.05f),
                renderPrev: new Vector3(0f, 0f, 1.00f),
                renderNow: new Vector3(0f, 0f, 1.03f)); // 落后根 2cm，但相对上一帧仍前进

            Assert.That(MovementPullbackTrace.Classify(sample), Is.EqualTo(PullbackCause.None),
                "视觉插值落后根是正常现象，不得当成回拉（旧口径必然误报）");
        }

        /// <summary>2026-09-16 实测补充：亚厘米的平滑步进是正常收敛，不得当成回拉刷屏
        /// （本局 4 条 SmoothPhase 导出全是 0.005-0.01m 噪声）。</summary>
        [Test]
        public void Classify_SmoothPhase_IgnoresSubCentimeterConvergence()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 1.050f),
                root: new Vector3(0f, 0f, 1.041f),
                afterSmooth: new Vector3(0f, 0f, 1.041f)); // 平滑步进 9mm（向后）

            Assert.That(MovementPullbackTrace.Classify(sample), Is.EqualTo(PullbackCause.None),
                "低于 PullbackSmoothMinMeters(0.02) 的平滑收敛不是回拉");
        }

        /// <summary>没有反向位移、或玩家本来就不想前进（横移/后退）→ 不报回拉。</summary>
        [Test]
        public void Classify_None_WhenNoBackwardOrNotWantsForward()
        {
            var forward = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 1.05f),
                root: new Vector3(0f, 0f, 1.05f));
            Assert.That(MovementPullbackTrace.Classify(forward), Is.EqualTo(PullbackCause.None));

            var backingUp = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 0.6f),
                root: new Vector3(0f, 0f, 0.6f),
                move: new Vector2(0f, -1f));
            Assert.That(MovementPullbackTrace.Classify(backingUp), Is.EqualTo(PullbackCause.None),
                "主动后退不是回拉");

            var noise = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 0.999f),
                root: new Vector3(0f, 0f, 0.999f));
            Assert.That(MovementPullbackTrace.Classify(noise), Is.EqualTo(PullbackCause.None),
                "亚毫米噪声不触发取证");
        }

        /// <summary>环形缓冲：容量固定、取最近 N 条且最旧在前。</summary>
        [Test]
        public void Ring_KeepsCapacity_AndReturnsOldestFirst()
        {
            var trace = new MovementPullbackTrace(capacity: 8);
            for (int i = 0; i < 20; i++)
            {
                var sample = Sample(
                    moveBefore: new Vector3(0f, 0f, i),
                    moveAfter: new Vector3(0f, 0f, i + 0.05f),
                    root: new Vector3(0f, 0f, i + 0.05f));
                sample.ClientTick = (uint)(i + 1);
                trace.Record(sample);
            }

            Assert.That(trace.Count, Is.EqualTo(8), "容量固定，不增长");
            var recent = trace.RecentSamples(3);
            Assert.That(recent.Count, Is.EqualTo(3));
            Assert.That(recent[0].ClientTick, Is.EqualTo(18u), "最旧在前");
            Assert.That(recent[2].ClientTick, Is.EqualTo(20u));
        }

        /// <summary>
        /// 审计 §3.3：多步帧必须逐步写环形记录，且只有最后一步携带帧级视觉位移
        /// （否则帧内非最后一步会被拿"本帧渲染位置"误判成回拉）。
        /// </summary>
        [Test]
        public void RecordFrame_RecordsEveryStep_OnlyLastCarriesFrameVisual()
        {
            var trace = new MovementPullbackTrace(capacity: 8);
            var steps = new List<PullbackTraceSample>
            {
                Sample(new Vector3(0f, 0f, 1.00f), new Vector3(0f, 0f, 1.05f),
                    new Vector3(0f, 0f, 1.05f)),
                Sample(new Vector3(0f, 0f, 1.05f), new Vector3(0f, 0f, 1.10f),
                    new Vector3(0f, 0f, 1.10f)),
            };

            var worst = trace.RecordFrame(steps, new Vector3(0f, 0f, 1.00f), new Vector3(0f, 0f, 1.08f));

            Assert.That(trace.Count, Is.EqualTo(2), "逐步记录");
            Assert.That(worst, Is.EqualTo(PullbackCause.None));
            var recorded = trace.RecentSamples(2);
            Assert.That(recorded[0].RenderPrev, Is.EqualTo(recorded[0].MoveAfterSmooth),
                "帧内非最后一步不携带帧级视觉位移");
            Assert.That(recorded[1].RenderNow.z, Is.EqualTo(1.08f).Within(1e-4f));
            Assert.That(recorded[1].StepsInFrame, Is.EqualTo(2));
        }

        /// <summary>取证行包含完整写者链字段（供日志对账）。</summary>
        [Test]
        public void Format_ContainsWriterChainFields()
        {
            var sample = Sample(
                moveBefore: new Vector3(0f, 0f, 1.0f),
                moveAfter: new Vector3(0f, 0f, 1.1f),
                root: new Vector3(0f, 0f, 0.9f),
                snap: true,
                snapFrom: new Vector3(0f, 0f, 1.1f),
                snapTo: new Vector3(0f, 0f, 0.9f));
            var line = MovementPullbackTrace.Format(sample, MovementPullbackTrace.Classify(sample));

            foreach (var key in new[]
            {
                "[Pullback] ", "cause=AuthorityCorrection", "tick=10", "steps=1", "move=", "before=", "after=",
                "afterSmooth=", "smooth=", "snap=1", "snapFrom=", "snapTo=", "root=", "renderPrev=", "render=",
                "fwd=", "dot=", "stepDot=", "netDot=",
            })
                Assert.That(line, Does.Contain(key), $"取证行缺少字段 {key}：{line}");
        }
    }
}
