using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 认证失败降级判定锁定（入口任务 2 + 连接从未建立补缺，纯逻辑）：
    /// ① 收到 TICKET_* 拒绝 → SpecificRejection（显示具体原因）；空码归一化 TICKET_INVALID；
    /// ② F1 修复的不可靠补发副本 → 同码幂等不重复报错；
    /// ③ 断线时未收到任何结果且已尝试认证 → GenericDegraded（F8：Release 拒绝原因 0/4 送达的通用降级）；
    /// ④ Day2 补缺：连接尝试过但从未建立（未 Started、票据从未发送——服务器不可达/端口关闭）
    ///    → GenericDegraded（不再静默失败；ConnectionAttempted 与 AuthAttempted 两个信号在此区分）；
    /// ⑤ 完全未尝试连接的断线（无 StartConnection 的普通停止）→ None；
    /// ⑥ 已接受后的断线 → None（对局断线归 MatchConnectionWatcher，不在此重复处理）。
    /// </summary>
    public sealed class AuthFailurePolicyTests
    {
        [Test]
        public void RejectionBroadcast_SpecificRejection_WithCode()
        {
            var policy = new AuthFailurePolicy();
            var verdict = policy.OnAuthResult(accepted: false, errorCode: "TICKET_REPLAYED");

            Assert.That(verdict, Is.EqualTo(ClientAuthFailureKind.SpecificRejection),
                "收到拒绝结果必须展示具体原因（Development 服务器 3/3 送达）");
            Assert.That(policy.CurrentRejectionCode, Is.EqualTo("TICKET_REPLAYED"));
            Assert.That(policy.HasResult, Is.True);
        }

        [Test]
        public void EmptyErrorCode_IsNormalizedToTicketInvalid()
        {
            var policy = new AuthFailurePolicy();
            policy.OnAuthResult(accepted: false, errorCode: string.Empty);
            Assert.That(policy.CurrentRejectionCode, Is.EqualTo("TICKET_INVALID"), "空错误码不得原样透传");
        }

        [Test]
        public void UnreliableResendCopy_IsIdempotent()
        {
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnAuthResult(false, "TICKET_INVALID"), Is.EqualTo(ClientAuthFailureKind.SpecificRejection));
            Assert.That(policy.OnAuthResult(false, "TICKET_INVALID"), Is.EqualTo(ClientAuthFailureKind.None),
                "F1 修复的补发不可靠副本必须幂等（不重复触发失败收口）");
        }

        [Test]
        public void DisconnectWithoutResult_WhenAuthAttempted_IsGenericDegraded()
        {
            var policy = new AuthFailurePolicy();
            var verdict = policy.OnDisconnected(connectionAttempted: true, authAttempted: true);

            Assert.That(verdict, Is.EqualTo(ClientAuthFailureKind.GenericDegraded),
                "认证前断线且无结果广播（F8）→ 通用降级：连接被拒绝或票据已失效");
            Assert.That(policy.CurrentRejectionCode, Is.Empty, "通用降级没有具体码可展示");
        }

        [Test]
        public void DisconnectWithoutResult_WhenConnectionNeverEstablished_IsGenericDegraded()
        {
            // Day2 补缺主案：服务器不可达/端口关闭——LocalConnectionState 从未 Started、
            // 票据从未发送（authAttempted=false），但 StartConnection 已调用（connectionAttempted=true）
            var policy = new AuthFailurePolicy();
            var verdict = policy.OnDisconnected(connectionAttempted: true, authAttempted: false);

            Assert.That(verdict, Is.EqualTo(ClientAuthFailureKind.GenericDegraded),
                "连接从未建立也必须走通用降级（清上下文/回大厅/不复用旧票据），不得静默失败");
        }

        [Test]
        public void Disconnect_WhenNeverAttempted_IsNone()
        {
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnDisconnected(connectionAttempted: false, authAttempted: false),
                Is.EqualTo(ClientAuthFailureKind.None),
                "完全未尝试连接的断线（无 StartConnection 的普通停止）不是连接失败");
        }

        [Test]
        public void AuthAttemptedImpliesConnectionAttempted_EvenIfMarkerMissing()
        {
            // 防御：即使 NetworkHud 未来漏登 ConnectionAttempted，已进入认证流程
            //（authAttempted=true 必然意味着连接曾经建立过）仍走通用降级——不因缺登记而漏报
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnDisconnected(connectionAttempted: false, authAttempted: true),
                Is.EqualTo(ClientAuthFailureKind.GenericDegraded));
        }

        [Test]
        public void AcceptThenDisconnect_IsNone_MatchWatcherOwnsThatPath()
        {
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnAuthResult(accepted: true, errorCode: string.Empty), Is.EqualTo(ClientAuthFailureKind.None));
            Assert.That(policy.OnDisconnected(connectionAttempted: true, authAttempted: true),
                Is.EqualTo(ClientAuthFailureKind.None),
                "已接受后的断线是对局/停服语义，归 MatchConnectionWatcher，不得误报认证失败");
        }

        [Test]
        public void RejectionThenLateDisconnect_DoesNotDoubleTrigger()
        {
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnAuthResult(false, "TICKET_EXPIRED"), Is.EqualTo(ClientAuthFailureKind.SpecificRejection));
            Assert.That(policy.OnDisconnected(connectionAttempted: true, authAttempted: true),
                Is.EqualTo(ClientAuthFailureKind.None),
                "具体原因已展示后服务端断开（≤250ms 收口）不得再触发通用降级");
        }

        [Test]
        public void AcceptAfterConnectionFailureMarker_StaysNone()
        {
            // 竞态防御：登记过 ConnectionAttempted 后又成功接受 → 断线仍归 MatchConnectionWatcher
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnAuthResult(accepted: true, errorCode: string.Empty), Is.EqualTo(ClientAuthFailureKind.None));
            Assert.That(policy.OnDisconnected(connectionAttempted: true, authAttempted: false),
                Is.EqualTo(ClientAuthFailureKind.None));
        }
    }
}
