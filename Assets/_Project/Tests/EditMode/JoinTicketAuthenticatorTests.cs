using System;
using System.Threading.Tasks;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Docs/27 Day1 §4.3 用例 4/5/8 锁定：JoinTicketAuthService 认证决策核心 + AuthDeadlineTracker。
    /// 语义全部同步可测：假控制面返回已完成 Task，超时路径用注入的同步延迟工厂复现——
    /// 不依赖真实定时器，EditMode 零线程依赖。
    /// 覆盖矩阵：
    /// ① valid 票据 → 一次成功（携带 room/user）；
    /// ② invalid/expired/replayed/instance-mismatch → 拒绝且错误码逐字冻结；
    /// ③ 后端不可达 / 后端超时 / 无控制面 → 全部 fail closed 拒绝；
    /// ④ 同一连接重复发送 auth broadcast → 忽略不重复认证；已认证连接重发 → 攻击断开；
    /// ⑤ unsafe debug 通道：服务器允许时无票据直通（标记 DebugBypass），未允许时拒绝；
    /// ⑥ DeadlineTracker 10s 认证超时清场语义。
    /// MonoBehaviour 胶水层（FishNet broadcast 接线/一次性收口）无法在 EditMode 构建真实
    /// NetworkManager 连接，按项目既有约定（NetworkAuthorityGateTests 同款说明）由
    /// PlayMode/实机联调覆盖（Docs/27 §6 人工验收脚本）。
    /// </summary>
    public sealed class JoinTicketAuthenticatorTests
    {
        private sealed class FakeControlPlane : IServerControlPlaneClient
        {
            public Func<string, Task<TicketConsumeResult>> ConsumeHandler { get; set; }

            public Task<ServerInstanceRegisterResponse> RegisterAsync(ServerInstanceRegisterRequest request)
                => Task.FromResult(new ServerInstanceRegisterResponse());

            public Task<HeartbeatOutcome> HeartbeatAsync(ServerInstanceHeartbeatRequest request)
                => Task.FromResult(HeartbeatOutcome.Accepted);

            public Task<TicketConsumeResult> ConsumeTicketAsync(string ticket)
                => ConsumeHandler?.Invoke(ticket) ?? Task.FromResult(TicketConsumeResult.Rejected("TICKET_INVALID"));

            public Task<PlayerDisconnectReport> DisconnectPlayerAsync(ServerPlayerDisconnectRequest request)
                => Task.FromResult(PlayerDisconnectReport.AcceptedFromBackend(null));
        }

        private static readonly JoinTicketBroadcast ValidTicketMessage =
            new() { Ticket = "valid-ticket-unit-test-0001" };

        private JoinTicketAuthService MakeService(FakeControlPlane controlPlane, bool allowUnsafe = false)
        {
            return new JoinTicketAuthService(controlPlane, allowUnsafe, consumeTimeoutSeconds: 10.0);
        }

        // ---- ① 有效票据 → 一次成功 ----

        [Test]
        public void ValidTicket_ConsumesOnce_AcceptedWithIdentity()
        {
            var controlPlane = new FakeControlPlane
            {
                ConsumeHandler = ticket =>
                    Task.FromResult(ticket == "valid-ticket-unit-test-0001"
                        ? TicketConsumeResult.AcceptedFromBackend("ROOM-7788", "user-42", "Tester")
                        : TicketConsumeResult.Rejected("TICKET_INVALID")),
            };
            var service = MakeService(controlPlane);

            var result = service.ValidateAsync(ValidTicketMessage).Result;

            Assert.That(result.Accepted, Is.True, "后端 consume 有效票据必须放行");
            Assert.That(result.RoomCode, Is.EqualTo("ROOM-7788"));
            Assert.That(result.UserId, Is.EqualTo("user-42"));
            Assert.That(result.Username, Is.EqualTo("Tester"));
            Assert.That(result.DebugBypass, Is.False);
        }

        // ---- ② 冻结错误码逐一拒绝 ----

        [TestCase("TICKET_INVALID")]
        [TestCase("TICKET_EXPIRED")]
        [TestCase("TICKET_REPLAYED")]
        [TestCase("TICKET_INSTANCE_MISMATCH")]
        public void RejectedTicket_PropagatesFrozenErrorCode(string frozenCode)
        {
            var controlPlane = new FakeControlPlane
            {
                ConsumeHandler = _ => Task.FromResult(TicketConsumeResult.Rejected(frozenCode)),
            };
            var result = MakeService(controlPlane).ValidateAsync(ValidTicketMessage).Result;

            Assert.That(result.Accepted, Is.False, $"{frozenCode} 必须拒绝");
            Assert.That(result.ErrorCode, Is.EqualTo(frozenCode), "错误码必须逐字透传给客户端结果广播");
        }

        [Test]
        public void BackendInvalidWithoutCode_MapsToTicketInvalid()
        {
            var controlPlane = new FakeControlPlane
            {
                ConsumeHandler = _ => Task.FromResult(TicketConsumeResult.Rejected("")),
            };
            var result = MakeService(controlPlane).ValidateAsync(ValidTicketMessage).Result;
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("TICKET_INVALID"), "空错误码兜底为 TICKET_INVALID");
        }

        // ---- ③ fail closed：不可达 / 超时 / 无控制面 ----

        [Test]
        public void BackendUnreachableTask_FailsClosed()
        {
            var controlPlane = new FakeControlPlane
            {
                ConsumeHandler = _ => Task.FromException<TicketConsumeResult>(
                    new ControlPlaneRequestException("/api/server-instances/tickets/consume", 503, "connection refused")),
            };
            var result = MakeService(controlPlane).ValidateAsync(ValidTicketMessage).Result;

            Assert.That(result.Accepted, Is.False, "后端不可达必须 fail closed");
            Assert.That(result.ErrorCode, Is.EqualTo("AUTH_BACKEND_UNREACHABLE"));
        }

        [Test]
        public void BackendSyncThrow_FailsClosed()
        {
            var controlPlane = new FakeControlPlane
            {
                ConsumeHandler = _ => throw new InvalidOperationException("sync blow-up"),
            };
            var result = MakeService(controlPlane).ValidateAsync(ValidTicketMessage).Result;

            Assert.That(result.Accepted, Is.False, "控制面同步抛异常必须 fail closed");
            Assert.That(result.ErrorCode, Is.EqualTo("AUTH_BACKEND_UNREACHABLE"));
        }

        [Test]
        public void BackendTimeout_FailsClosed()
        {
            var controlPlane = new FakeControlPlane
            {
                // 永不完成的消费任务：模拟后端挂起
                ConsumeHandler = _ => new TaskCompletionSource<TicketConsumeResult>().Task,
            };
            // 注入同步完成的延迟工厂：零定时器复现「消费超时」
            var service = new JoinTicketAuthService(controlPlane, allowUnsafeDebugAuth: false,
                consumeTimeoutSeconds: 10.0, delayFactory: _ => Task.FromResult(true));

            var result = service.ValidateAsync(ValidTicketMessage).Result;

            Assert.That(result.Accepted, Is.False, "后端超时必须 fail closed（绝不能挂起等到底）");
            Assert.That(result.ErrorCode, Is.EqualTo("AUTH_BACKEND_TIMEOUT"));
        }

        [Test]
        public void NullControlPlane_RealTicketFailsClosed()
        {
            // F1 调试 Host（无后端）收到真实票据：拒绝而非放行，也绝不 NRE
            var service = new JoinTicketAuthService(null, allowUnsafeDebugAuth: false);
            var result = service.ValidateAsync(ValidTicketMessage).Result;
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("AUTH_BACKEND_UNREACHABLE"));
        }

        // ---- ④ 重复 broadcast / 攻击判定 ----

        [Test]
        public void DuplicateBroadcastWhileInFlight_IsIgnored_NotReAuthenticated()
        {
            var service = MakeService(new FakeControlPlane());
            var decision = service.EvaluateIncoming(
                connectionAuthenticated: false, validationInFlight: true, message: ValidTicketMessage);
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.IgnoreDuplicate),
                "同一连接验证在途的重复认证请求必须忽略（不得重复认证）");
        }

        [Test]
        public void BroadcastAfterAuthenticated_IsAttack_Disconnect()
        {
            var service = MakeService(new FakeControlPlane());
            var decision = service.EvaluateIncoming(
                connectionAuthenticated: true, validationInFlight: false, message: ValidTicketMessage);
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.DisconnectAttacker),
                "已认证连接再发认证 broadcast = 攻击，必须断开（Demo 同款处置）");
        }

        [Test]
        public void EmptyTicketWithoutUnsafeChannel_IsRejected()
        {
            var service = MakeService(new FakeControlPlane());
            var decision = service.EvaluateIncoming(
                connectionAuthenticated: false, validationInFlight: false,
                message: new JoinTicketBroadcast { Ticket = "", UnsafeDebugRequest = false });
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.Reject),
                "空票据且未开调试通道：fail closed 拒绝");
        }

        [Test]
        public void WhitespaceTicket_IsRejected()
        {
            var service = MakeService(new FakeControlPlane());
            var decision = service.EvaluateIncoming(
                connectionAuthenticated: false, validationInFlight: false,
                message: new JoinTicketBroadcast { Ticket = "   ", UnsafeDebugRequest = false });
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.Reject),
                "纯空白票据同样视为空票据拒绝");
        }

        // ---- ⑤ unsafe debug 通道 ----

        [Test]
        public void UnsafeDebugRequest_WhenServerAllows_BypassesWithMark()
        {
            var service = MakeService(new FakeControlPlane(), allowUnsafe: true);
            var message = new JoinTicketBroadcast { Ticket = "", UnsafeDebugRequest = true };

            var decision = service.EvaluateIncoming(false, false, message);
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.BeginValidation));

            var result = service.ValidateAsync(message).Result;
            Assert.That(result.Accepted, Is.True, "服务器显式开启的本地调试通道应直通");
            Assert.That(result.DebugBypass, Is.True, "调试直通必须可识别（不写入正式身份档案）");
            Assert.That(result.ErrorCode, Is.EqualTo("UNSAFE_DEBUG_AUTH"));
        }

        [Test]
        public void UnsafeDebugRequest_WhenServerDisallows_FailsClosed()
        {
            var service = MakeService(new FakeControlPlane(), allowUnsafe: false);
            var message = new JoinTicketBroadcast { Ticket = "", UnsafeDebugRequest = true };

            var decision = service.EvaluateIncoming(false, false, message);
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.Reject),
                "服务器未开启调试通道：无票据请求必须拒绝（Release 服务器常态）");
        }

        [Test]
        public void UnsafeDebugFlag_OnRealTicket_DoesNotGrantBypass()
        {
            // 携带真票据 + 混入调试标记：仍走真实 consume，不因标记放行
            var controlPlane = new FakeControlPlane
            {
                ConsumeHandler = _ => Task.FromResult(TicketConsumeResult.Rejected("TICKET_REPLAYED")),
            };
            var service = MakeService(controlPlane, allowUnsafe: true);
            var message = new JoinTicketBroadcast { Ticket = "real-ticket", UnsafeDebugRequest = true };

            var decision = service.EvaluateIncoming(false, false, message);
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.BeginValidation));

            var result = service.ValidateAsync(message).Result;
            Assert.That(result.Accepted, Is.False, "有票据就走真实验证，调试标记不构成放行条件");
            Assert.That(result.DebugBypass, Is.False);
        }

        // ---- ⑥ DeadlineTracker：10s 认证超时清场 ----

        [Test]
        public void DeadlineTracker_MarkContainsRemoveSemantics()
        {
            var tracker = new AuthDeadlineTracker(10.0);
            Assert.That(tracker.Count, Is.EqualTo(0));

            tracker.Mark(7, nowSeconds: 100.0);
            Assert.That(tracker.Contains(7), Is.True);

            tracker.Remove(7);
            Assert.That(tracker.Contains(7), Is.False, "Remove 后不得再被超时清场处理");
        }

        [Test]
        public void DeadlineTracker_NotExpiredBeforeDeadline_ExpiredAfter()
        {
            var tracker = new AuthDeadlineTracker(10.0);
            tracker.Mark(42, nowSeconds: 100.0);

            Assert.That(tracker.CollectExpired(nowSeconds: 109.0), Is.Empty, "截止前不得清场");
            Assert.That(tracker.Contains(42), Is.True);

            var expired = tracker.CollectExpired(nowSeconds: 111.0);
            Assert.That(expired, Is.EqualTo(new[] { 42 }), "截止后必须报出过期连接");
            Assert.That(tracker.Contains(42), Is.False, "清场后不再追踪（断开由认证器执行）");
        }

        [Test]
        public void DeadlineTracker_MultipleConnections_OnlyExpiredOnesCollected()
        {
            var tracker = new AuthDeadlineTracker(10.0);
            tracker.Mark(1, nowSeconds: 100.0);
            tracker.Mark(2, nowSeconds: 105.0);

            var expired = tracker.CollectExpired(nowSeconds: 112.0);
            Assert.That(expired, Is.EqualTo(new[] { 1 }), "只有过期的连接被清场，未到期连接保持追踪");
            Assert.That(tracker.Contains(2), Is.True);
        }
    }
}
