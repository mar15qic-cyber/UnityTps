using Game.Gameplay.Network;
using Game.Presentation.HUD;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase 3 定向测试：RespawnProtectionHudView 三态纯函数（Hidden/Respawning/Invincible）、
    /// 剩余秒与填充量换算、本地钳制、死亡优先级、非法 tickRate 回退。
    /// 表现端挂载/SetActive 属实机验收（HUD 渲染不可 EditMode 断言）。
    /// </summary>
    public sealed class RespawnProtectionHudTests
    {
        private const int TickRate30 = 30;

        [Test]
        public void ComputePhase_Defaults_Hidden()
        {
            var phase = RespawnProtectionHudView.ComputePhase(
                false, 0u, 0u, 0u, TickRate30, out float remaining, out float fill);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Hidden));
            Assert.That(remaining, Is.EqualTo(0f));
            Assert.That(fill, Is.EqualTo(0f));
        }

        [Test]
        public void ComputePhase_DeadQueued_IsRespawning_WithCountdown()
        {
            // 3s 延迟 @30Hz = 90 tick；now=60 → 剩余 1.0s、填充 1/3
            var phase = RespawnProtectionHudView.ComputePhase(
                true, 90u, 0u, 60u, TickRate30, out float remaining, out float fill);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Respawning));
            Assert.That(remaining, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(fill, Is.EqualTo(1f / 3f).Within(1e-4f));
        }

        [Test]
        public void ComputePhase_RespawnPastDue_ClampsRemainingToZero()
        {
            // 本地 tick 已过 deadline 但服务器重生快照未到：剩余钳 0，态保持 Respawning（不闪 Hidden）
            var phase = RespawnProtectionHudView.ComputePhase(
                true, 90u, 0u, 95u, TickRate30, out float remaining, out float fill);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Respawning));
            Assert.That(remaining, Is.EqualTo(0f));
            Assert.That(fill, Is.EqualTo(0f));
        }

        [Test]
        public void ComputePhase_ProtectedWindow_IsInvincible()
        {
            // 2.5s 保护 @30Hz = 75 tick；now=30 → 剩余 1.5s、填充 0.6
            var phase = RespawnProtectionHudView.ComputePhase(
                false, 0u, 75u, 30u, TickRate30, out float remaining, out float fill);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Invincible));
            Assert.That(remaining, Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(fill, Is.EqualTo(0.6f).Within(1e-4f));
        }

        [Test]
        public void ComputePhase_ProtectionExpired_Hidden()
        {
            // 截止 tick 当刻即出保护（半开区间语义与服务器 IsInvincibleAt 一致）
            var phase = RespawnProtectionHudView.ComputePhase(
                false, 0u, 75u, 75u, TickRate30, out _, out _);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Hidden));
        }

        [Test]
        public void ComputePhase_DeadWinsOverProtection()
        {
            // 重生瞬间两端状态可能短暂并存（ SyncVar 刷新时序）：死亡展示恒优先
            var phase = RespawnProtectionHudView.ComputePhase(
                true, 90u, 75u, 30u, TickRate30, out _, out _);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Respawning));
        }

        [Test]
        public void ComputePhase_InvalidTickRate_FallsBackTo30()
        {
            // tickRate 0 回退 30Hz：until=31, now=1 → 剩余 30 tick = 1.0s
            var phase = RespawnProtectionHudView.ComputePhase(
                false, 0u, 31u, 1u, 0, out float remaining, out _);
            Assert.That(phase, Is.EqualTo(RespawnProtectionHudView.Phase.Invincible));
            Assert.That(remaining, Is.EqualTo(1f).Within(1e-4f));
        }
    }
}
