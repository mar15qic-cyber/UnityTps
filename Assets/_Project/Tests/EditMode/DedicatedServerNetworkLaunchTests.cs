using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/27 Day1 §4.3 用例 3 + 票据日志纪律锁定：NetworkLaunchContext 一次性启动接缝。
    /// ① ConfigureClient → TryBeginClientLaunch 单次消费，第二次返回 null；
    /// ② 消费后上下文不再持有明文票据（断线清理由认证器负责，上下文先清）；
    /// ③ Clear 不残留；ConfigureDedicatedServer/Clear 生命周期；
    /// ④ TicketHashPrefix 日志摘要纪律：8 位、不含票据明文（§2.3 安全红线）。
    /// </summary>
    public sealed class DedicatedServerNetworkLaunchTests
    {
        [SetUp]
        public void SetUp()
        {
            NetworkLaunchContext.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            NetworkLaunchContext.Clear();
        }

        [Test]
        public void ConfigureClient_MakesPendingLaunch_Visible()
        {
            NetworkLaunchContext.ConfigureClient("10.0.0.5", 7770, "ticket-A-unit-test");
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.True);
        }

        [Test]
        public void TryBeginClientLaunch_ConsumesOnce_SecondAttemptReturnsNull()
        {
            NetworkLaunchContext.ConfigureClient("10.0.0.5", 7770, "ticket-A-unit-test");

            var first = NetworkLaunchContext.TryBeginClientLaunch();
            Assert.That(first, Is.Not.Null, "配置后首次消费必须拿到启动载荷");
            Assert.That(first.ServerAddress, Is.EqualTo("10.0.0.5"));
            Assert.That(first.ServerPort, Is.EqualTo(7770));
            Assert.That(first.JoinTicket, Is.EqualTo("ticket-A-unit-test"));
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.False, "消费后不得再有 pending");

            var second = NetworkLaunchContext.TryBeginClientLaunch();
            Assert.That(second, Is.Null, "一次性消费：第二次必须为 null（票据不得重复使用）");
        }

        [Test]
        public void ClearedContext_KeepsNoTicketAnywhere()
        {
            NetworkLaunchContext.ConfigureClient("10.0.0.5", 7770, "ticket-secret-unit-test");
            NetworkLaunchContext.Clear();

            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.False);
            Assert.That(NetworkLaunchContext.TryBeginClientLaunch(), Is.Null, "Clear 后不得消费出任何票据");
            Assert.That(NetworkLaunchContext.DedicatedServer, Is.Null);
        }

        [Test]
        public void ReconfigureAfterConsume_StartsFreshLaunch()
        {
            NetworkLaunchContext.ConfigureClient("a", 1, "ticket-1");
            Assert.That(NetworkLaunchContext.TryBeginClientLaunch().JoinTicket, Is.EqualTo("ticket-1"));

            // 下一局：重新配置 → 再次可消费（断线重进等场景由 Day2 调用方保证新票据）
            NetworkLaunchContext.ConfigureClient("b", 2, "ticket-2");
            var relaunch = NetworkLaunchContext.TryBeginClientLaunch();
            Assert.That(relaunch, Is.Not.Null);
            Assert.That(relaunch.ServerAddress, Is.EqualTo("b"));
            Assert.That(relaunch.ServerPort, Is.EqualTo(2));
            Assert.That(relaunch.JoinTicket, Is.EqualTo("ticket-2"));
        }

        [Test]
        public void ConfigureDedicatedServer_ExposesSameOptionsInstance()
        {
            var options = DedicatedServerOptions.Parse(
                new[] { "-dedicatedServer", "-instanceId", "arena-01", "-port", "7770", "-backendUrl", "http://x", "-serverKey", "k" },
                isBatchMode: true, isUnityServerDefine: false, isReleaseBuild: false);
            NetworkLaunchContext.ConfigureDedicatedServer(options);
            Assert.That(NetworkLaunchContext.DedicatedServer, Is.SameAs(options));
        }

        // ---- 票据日志摘要纪律（§2.3：日志不得出现完整 ticket） ----

        [Test]
        public void TicketHashPrefix_IsStable_AndNeverContainsPlaintext()
        {
            const string ticket = "unit-test-ticket-plaintext-123456";
            string hash1 = JoinTicketAuthService.TicketHashPrefix(ticket);
            string hash2 = JoinTicketAuthService.TicketHashPrefix(ticket);

            Assert.That(hash1, Is.EqualTo(hash2), "同一票据摘要必须稳定（日志可对账）");
            Assert.That(hash1.Length, Is.EqualTo(8), "日志只允许 8 位摘要");
            Assert.That(hash1, Does.Not.Contain(ticket), "摘要绝不包含票据明文");
            Assert.That(JoinTicketAuthService.TicketHashPrefix(ticket), Is.Not.EqualTo(
                JoinTicketAuthService.TicketHashPrefix("unit-test-ticket-plaintext-123457")),
                "不同票据摘要必须不同");
        }

        [Test]
        public void TicketHashPrefix_EmptyTicket_IsNone()
        {
            Assert.That(JoinTicketAuthService.TicketHashPrefix(null), Is.EqualTo("none"));
            Assert.That(JoinTicketAuthService.TicketHashPrefix(string.Empty), Is.EqualTo("none"));
        }
    }
}
