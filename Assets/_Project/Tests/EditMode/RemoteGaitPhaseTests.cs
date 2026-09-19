using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 远端步态相位本地积分（2026-09-17 方案B，实机"翻小人书"修复）纯逻辑锁定：
    /// ①相位以 1/周期固定速率推进（与服务器 RootMotionProfile/Locomotor 同源）；
    /// ②包裹与跨零边界的最短路径误差；③低频锚定=超阈值对位、小误差指数拉拢、速率一致时不可见。
    /// </summary>
    public sealed class RemoteGaitPhaseTests
    {
        private const float WalkCycle = RemoteGaitPhase.FallbackWalkCycleSeconds;      // 0.9333
        private const float SprintCycle = RemoteGaitPhase.FallbackSprintCycleSeconds;  // 0.6667

        [Test]
        public void Advance_AdvancesAtFixedCycleRate()
        {
            float phase = 0.2f;
            phase = RemoteGaitPhase.Advance(phase, WalkCycle * 0.5f, WalkCycle);
            Assert.That(phase, Is.EqualTo(0.7f).Within(1e-4f), "半周期推进 = 相位 +0.5（与服务器固定速率同源）");
        }

        [Test]
        public void Advance_WrapsAroundCycle()
        {
            float phase = RemoteGaitPhase.Advance(0.9f, WalkCycle * 0.3f, WalkCycle);
            Assert.That(phase, Is.EqualTo(0.2f).Within(1e-4f));
        }

        [Test]
        public void Advance_MatchesServerCadence_Over30Ticks()
        {
            // 30Hz tick × 30 步（1 秒）冲刺：服务器推进 1/0.6667 ≈ 1.5 相位
            float phase = 0f;
            for (int i = 0; i < 30; i++) phase = RemoteGaitPhase.Advance(phase, 1f / 30f, SprintCycle);
            Assert.That(phase, Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void Advance_InvalidCycle_HoldsPhase()
        {
            Assert.That(RemoteGaitPhase.Advance(0.42f, 1f, 0f), Is.EqualTo(0.42f).Within(1e-6f));
        }

        [Test]
        public void PhaseError_UsesShortestWrappedPath()
        {
            // current=0.95, anchor=0.05：直线差 -0.9，包裹后 +0.1
            Assert.That(RemoteGaitPhase.PhaseError(0.95f, 0.05f), Is.EqualTo(0.1f).Within(1e-4f));
            Assert.That(RemoteGaitPhase.PhaseError(0.05f, 0.95f), Is.EqualTo(-0.1f).Within(1e-4f));
        }

        [Test]
        public void CorrectToward_SnapsWhenErrorExceedsThreshold()
        {
            // current=0.1, anchor=0.5：误差 +0.4 ≥ 0.25 → 直接对位
            float next = RemoteGaitPhase.CorrectToward(0.1f, 0.5f, 0.25f, 4f, 1f / 60f);
            Assert.That(next, Is.EqualTo(0.5f).Within(1e-4f), "误差 0.4 超阈值必须对位（状态切换/重生态）");
            // 边界对照：current=0.1, anchor=0.9 的包裹误差仅 -0.2 < 0.25 → 不得对位
            float pulled = RemoteGaitPhase.CorrectToward(0.1f, 0.9f, 0.25f, 4f, 1f / 60f);
            Assert.That(pulled, Is.Not.EqualTo(0.9f).Within(1e-3f), "包裹后小误差不许误判对位（跨零边界）");
        }

        [Test]
        public void CorrectToward_PullsGentlyBelowThreshold_AndIsInvisibleWhenRatesMatch()
        {
            // 误差 0.1（<0.25）：指数拉拢而非对位
            float next = RemoteGaitPhase.CorrectToward(0.5f, 0.6f, 0.25f, 4f, 1f / 60f);
            Assert.That(next, Is.GreaterThan(0.5f).And.LessThan(0.6f), "小误差应部分收拢不是瞬移");

            // 速率一致场景：本地推进与服务器锚定同步前进，误差保持≈0（锚定不可见=丝滑的关键）
            float local = 0.3f;
            float server = 0.3f;
            for (int i = 0; i < 300; i++)
            {
                local = RemoteGaitPhase.Advance(local, 1f / 120f, WalkCycle);     // 渲染 120Hz 本地推进
                server = RemoteGaitPhase.Advance(server, 1f / 120f, WalkCycle);   // 服务器同速率（模拟）
                // 每 12 帧（10Hz）锚定一次
                if (i % 12 == 0) local = RemoteGaitPhase.CorrectToward(local, server, 0.25f, 4f, 1f / 120f);
            }
            Assert.That(Mathf.Abs(RemoteGaitPhase.PhaseError(local, server)), Is.LessThan(0.01f),
                "速率一致时相位差必须保持亚相位 1%——锚定纠偏对观感不可见");
        }

        [Test]
        public void CorrectToward_RecoversFromTenHzAnchorJitter()
        {
            // 远端 120Hz 本地推进；服务器锚定 10Hz 才更新一次（中间冻结）——最坏情况误差 ≈
            // 锚定周期间隔×速率 = 0.1s/0.9333 ≈ 0.107 相位 < 0.25 阈值 → 永远走指数拉拢，绝不跳变
            float local = 0f;
            float server = 0f;
            float maxVisibleJump = 0f;
            float prevLocal = 0f;
            for (int i = 0; i < 600; i++)
            {
                server = RemoteGaitPhase.Advance(server, 1f / 120f, WalkCycle); // 服务器连续推进
                local = RemoteGaitPhase.Advance(local, 1f / 120f, WalkCycle);
                // 10Hz 锚定：仅每 12 帧拿到新同步值（期间 anchor 冻结）
                float anchor = RemoteGaitPhase.Advance(0f, Mathf.Floor(i / 12f) * 12f / 120f, WalkCycle);
                local = RemoteGaitPhase.CorrectToward(local, anchor, 0.25f, 4f, 1f / 120f);
                float jump = Mathf.Abs(RemoteGaitPhase.PhaseError(prevLocal, local));
                if (i > 0 && jump > maxVisibleJump) maxVisibleJump = jump;
                prevLocal = local;
            }
            // 每帧可见位移上限 = 本地推进步长 + 纠偏量，必须远小于"直接覆写 10Hz 值"的 0.107 相位台阶
            Assert.That(maxVisibleJump, Is.LessThan(0.02f),
                "锚定抖动下每帧相位位移必须远小于 10Hz 覆写台阶（0.107），否则等于没修");
        }
    }
}
