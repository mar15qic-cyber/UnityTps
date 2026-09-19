using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Codex 审计 P1（客户端明文票据生命周期）修复锁定（2026-09-06）：
    /// ① 发送（构建 broadcast）后会话立即不再持有明文——Broadcast 完成同步序列化入队后，
    ///    进程内不存在可再次发送的旧票据；
    /// ② 同一会话一次性：二次构建 → AlreadyConsumed（断线前后均无旧票据可重发）；
    /// ③ 无票据且未开 unsafe debug 通道 → FailClosedNoTicket 且会话终止；
    /// ④ 无票据 + 服务器/客户端均开 unsafe debug → 直通消息（UnsafeDebugRequest=true）；
    /// ⑤ 断线 ClearPlaintext 兜底幂等；日志摘要不含明文。
    /// </summary>
    public sealed class JoinTicketClientSessionTests
    {
        private const string Plaintext = "client-ticket-plaintext-98765";

        [Test]
        public void BuildAuthBroadcast_ConsumesPlaintext_Immediately()
        {
            var session = new JoinTicketClientSession(Plaintext);
            Assert.That(session.HasPlaintextTicket, Is.True, "配置后发送前持有明文（正常）");

            var decision = session.TryBuildAuthBroadcast(
                allowUnsafeDebugAuth: false, out var message, out string hash, out string reason);

            Assert.That(decision, Is.EqualTo(JoinTicketClientSession.SendDecision.Send), reason);
            Assert.That(session.HasPlaintextTicket, Is.False,
                "构建消息的瞬间明文即被清除——Broadcast() 只需消息副本，不再需要源票据");
            Assert.That(message.Ticket, Is.EqualTo(Plaintext), "出站消息本身按协议携带票据（唯一持有者）");
            Assert.That(message.UnsafeDebugRequest, Is.False);
            Assert.That(hash, Does.Not.Contain(Plaintext), "日志摘要绝不包含明文");
        }

        [Test]
        public void SecondBuild_AfterSend_IsAlreadyConsumed_NoResendableTicket()
        {
            var session = new JoinTicketClientSession(Plaintext);
            session.TryBuildAuthBroadcast(false, out _, out _, out _);

            var second = session.TryBuildAuthBroadcast(false, out var message2, out _, out string reason2);

            Assert.That(second, Is.EqualTo(JoinTicketClientSession.SendDecision.AlreadyConsumed),
                "同一会话绝不重复发送——旧票据不可复用");
            Assert.That(message2.Ticket, Is.Null, "二次构建不得产出携带旧票据的消息");
            Assert.That(session.HasPlaintextTicket, Is.False, "断线前后均不存在可再次发送的旧票据");
        }

        [Test]
        public void DisconnectFallback_ClearPlaintext_IsIdempotent_AndKeepsConsumedSemantics()
        {
            var session = new JoinTicketClientSession(Plaintext);
            session.TryBuildAuthBroadcast(false, out _, out _, out _);
            session.ClearPlaintext();   // 断线兜底
            session.ClearPlaintext();   // 幂等

            Assert.That(session.HasPlaintextTicket, Is.False);
            Assert.That(session.TryBuildAuthBroadcast(false, out _, out _, out _),
                Is.EqualTo(JoinTicketClientSession.SendDecision.AlreadyConsumed),
                "兜底清理不复活会话：清理后依然不可重发");
        }

        [Test]
        public void NoTicket_WithoutUnsafeChannel_FailsClosed_AndTerminatesSession()
        {
            var session = new JoinTicketClientSession(null);

            var decision = session.TryBuildAuthBroadcast(
                allowUnsafeDebugAuth: false, out var message, out _, out string reason);

            Assert.That(decision, Is.EqualTo(JoinTicketClientSession.SendDecision.FailClosedNoTicket),
                "无票据且未开 -allowUnsafeLocalDebugAuth：fail closed 拒绝");
            Assert.That(reason, Does.Contain("no ticket"));
            Assert.That(message.Ticket, Is.Null, "fail closed 不得产出任何出站消息载荷");
            Assert.That(session.HasPlaintextTicket, Is.False);

            // fail closed 也会话终止：不得重试旧状态
            Assert.That(session.TryBuildAuthBroadcast(true, out _, out _, out _),
                Is.EqualTo(JoinTicketClientSession.SendDecision.AlreadyConsumed),
                "一次 fail closed 后即使补开 unsafe 通道也不得用旧会话重试");
        }

        [Test]
        public void NoTicket_WithUnsafeChannel_SendsDebugBypassRequest()
        {
            var session = new JoinTicketClientSession(string.Empty);

            var decision = session.TryBuildAuthBroadcast(
                allowUnsafeDebugAuth: true, out var message, out string hash, out _);

            Assert.That(decision, Is.EqualTo(JoinTicketClientSession.SendDecision.Send));
            Assert.That(message.UnsafeDebugRequest, Is.True, "无票据调试请求必须显式标记（服务器端另行门控）");
            Assert.That(message.Ticket, Is.Empty);
            Assert.That(hash, Is.EqualTo("none"));
            Assert.That(session.HasPlaintextTicket, Is.False);
        }

        [Test]
        public void RealTicket_IgnoresUnsafeFlagInMessage()
        {
            // 携带真实票据时 unsafe 通道状态不影响消息语义（真票据走真实 consume）
            var session = new JoinTicketClientSession(Plaintext);
            var decision = session.TryBuildAuthBroadcast(
                allowUnsafeDebugAuth: true, out var message, out _, out _);

            Assert.That(decision, Is.EqualTo(JoinTicketClientSession.SendDecision.Send));
            Assert.That(message.UnsafeDebugRequest, Is.False,
                "有票据就必须走真实验证，UnsafeDebugRequest 不得置位");
        }
    }
}
