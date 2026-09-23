using System.Threading.Tasks;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-15 P0-A/P0-B/P1 定向锁定（一枪终局与二次入场卡住审计）：
    /// ① ClientMatchSessionCore：代际幂等/阶段推进/分段超时/旧会话 Superseded/入场完成判定；
    /// ② AuthFailurePolicy.ResetSession：上局"已接受"不得吞掉本局失败（审计 §3.2 缺口）；
    /// ③ 认证前协议门：JoinTicketAuthService 期望代际已配置时，空/不匹配 → RejectProtocolMismatch
    ///    （零后端消费），匹配 → BeginValidation；门关闭（null）→ 旧行为不变；
    /// ④ 客户端会话消息携带 ProtocolId（旧服务器读到多余字节在认证阶段即断开——绝不带协议差异进对局）；
    /// ⑤ NetworkLaunchContext：代际递增/载荷扩展（SceneName/MatchId/ConfiguredAt）/Peek 不消费。
    /// </summary>
    public sealed class ClientMatchSessionTests
    {
        // ---- ① 会话核心 ----

        [Test]
        public void BeginSession_SameGeneration_IsIdempotent()
        {
            var core = new ClientMatchSessionCore();
            Assert.That(core.BeginSession(7, "m1", 100f, 90f), Is.True, "首次开启");
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.SceneReady));

            Assert.That(core.BeginSession(7, "m1", 101f, 90f), Is.False, "同代际重复触发（sceneLoaded+Awake 双触发）必须幂等拒绝");
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.SceneReady), "幂等拒绝不改变阶段");
        }

        [Test]
        public void NewGeneration_Supersedes_ActiveOldSession_OldCallbacksRejected()
        {
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m1", 100f, 100f);
            core.MarkConnecting(101f);

            Assert.That(core.BeginSession(2, "m2", 200f, 190f), Is.True, "新代际开启");
            Assert.That(core.Generation, Is.EqualTo(2));
            Assert.That(core.IsCurrentSession(1), Is.False, "旧代际回调一律丢弃");
            Assert.That(core.IsCurrentSession(2), Is.True);
        }

        [Test]
        public void PhasePipeline_AdvancesToPlaying_AfterStableWindow()
        {
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m", 0f, 0f);
            core.MarkConnecting(0.1f);
            core.MarkConnected(1f);
            core.MarkAuthenticated(2f);
            core.MarkOwnerReady(3f);

            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.OwnerReady));
            core.MarkPlayingIfStable(3.2f);
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.OwnerReady), "稳定窗未到不推进");
            core.MarkPlayingIfStable(3.6f);
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.Playing), "OwnerReady 稳定 0.5s 即入场完成");
            Assert.That(core.HasEnteredBattle, Is.True);
        }

        [Test]
        public void Timeout_ConnectStarted_FailsAtPhaseBudget()
        {
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m", 0f, 0f);
            core.MarkConnecting(10f);

            Assert.That(core.TickTimeout(10f + ClientMatchSessionCore.ConnectStartedBudgetSeconds + 0.01f, out var reason), Is.True);
            Assert.That(reason, Is.EqualTo(ClientSessionFailureReason.TimeoutConnectStarted));
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.Failed), "超时即终态，不再分段推进");
            Assert.That(core.TickTimeout(999f, out _), Is.False, "终态后看门狗静默");
        }

        [Test]
        public void Timeout_Authenticated_WaitsForOwnerThenFails()
        {
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m", 0f, 0f);
            core.MarkConnecting(0f);
            core.MarkConnected(1f);
            core.MarkAuthenticated(2f);

            Assert.That(core.TickTimeout(2f + ClientMatchSessionCore.OwnerReadyBudgetSeconds - 1f, out _), Is.False);
            Assert.That(core.TickTimeout(2f + ClientMatchSessionCore.OwnerReadyBudgetSeconds + 0.01f, out var reason), Is.True);
            Assert.That(reason, Is.EqualTo(ClientSessionFailureReason.TimeoutOwnerReady));
        }

        [Test]
        public void OverallBudget_RunsFromConfigure_NotSessionBegin()
        {
            var core = new ClientMatchSessionCore();
            // Configure 于 0s，场景加载耗时 70s 后会话才开始（SceneReady）——整体预算余量只剩 5s
            core.BeginSession(1, "m", 70f, 0f);
            Assert.That(core.TickTimeout(70.5f, out _), Is.False, "余量内不触发");
            Assert.That(core.TickTimeout(ClientMatchSessionCore.OverallBudgetSeconds + 0.01f, out var reason), Is.True);
            Assert.That(reason, Is.EqualTo(ClientSessionFailureReason.TimeoutOverall), "P1：从配置点起算，不等 StartConnection 才计时");
        }

        [Test]
        public void OverallBudget_ExemptOncePlaying_MatchMayRunBeyondBudget()
        {
            // 实机双局验证修正：入场完成（Playing）后，对局时长归比赛规则——
            // 协调器不得按入场预算掐死进行中的对局（否则任何对局到 75s 必被断）
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m", 0f, 0f);
            core.MarkConnecting(0.1f);
            core.MarkConnected(1f);
            core.MarkAuthenticated(2f);
            core.MarkOwnerReady(3f);
            core.MarkPlayingIfStable(3.6f);
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.Playing));

            float longMatchSeconds = ClientMatchSessionCore.OverallBudgetSeconds * 20f; // 25 分钟对局
            Assert.That(core.TickTimeout(longMatchSeconds, out var reason), Is.False,
                "Playing 阶段豁免整体/分段预算：比赛计时归 MatchLifecycle");
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.Playing));
            Assert.That(reason, Is.EqualTo(ClientSessionFailureReason.None));

            // Playing 后断线仍按连接事件收场（MarkEnded 路径不受预算影响）
            core.MarkEnded();
            Assert.That(core.IsTerminal, Is.True);
        }

        [Test]
        public void ConnectionLostBeforeAuth_OnlyCounts_InOwnConnectionPhases()
        {
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m", 0f, 0f);
            core.MarkConnectionLostBeforeAuth(); // SceneReady 阶段的 Stopped = 上局收尾，忽略
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.SceneReady));

            core.MarkConnecting(1f);
            core.MarkConnectionLostBeforeAuth(); // 本会话连接阶段断线
            Assert.That(core.Phase, Is.EqualTo(ClientSessionPhase.Failed));
            Assert.That(core.FailureReason, Is.EqualTo(ClientSessionFailureReason.ConnectionLostBeforeAuth));
        }

        [Test]
        public void PreviousStopBarrier_BlocksNewConnectionUntilStoppedEventObserved()
        {
            Assert.That(ClientMatchSessionCoordinator.CanBeginConnection(false, false), Is.True);
            Assert.That(ClientMatchSessionCoordinator.CanBeginConnection(true, false), Is.False,
                "旧连接仍启动时不得连接");
            Assert.That(ClientMatchSessionCoordinator.CanBeginConnection(false, true), Is.False,
                "即使 transport 已变为 stopped，也要等旧 Stopped 回调被消费");
        }

        [Test]
        public void AuthRejected_And_Ended_AreTerminal()
        {
            var core = new ClientMatchSessionCore();
            core.BeginSession(1, "m", 0f, 0f);
            core.MarkConnecting(0f);
            core.MarkConnected(1f);
            core.MarkAuthRejected();
            Assert.That(core.IsTerminal, Is.True);
            Assert.That(core.HasEnteredBattle, Is.False);

            var ended = new ClientMatchSessionCore();
            ended.BeginSession(2, "m", 0f, 0f);
            ended.MarkConnecting(0f);
            ended.MarkConnected(1f);
            ended.MarkAuthenticated(2f);
            ended.MarkOwnerReady(3f);
            ended.MarkPlayingIfStable(4f);
            ended.MarkEnded();
            Assert.That(ended.HasEnteredBattle, Is.True, "Playing 后正常收场仍算已入场（极短局）");
            Assert.That(ended.IsTerminal, Is.True);
        }

        [Test]
        public void DescribeFailure_NeverEmpty_ForKnownReasons()
        {
            foreach (var reason in new[] { ClientSessionFailureReason.TimeoutOverall, ClientSessionFailureReason.TimeoutConnectStarted,
                ClientSessionFailureReason.TimeoutAuthenticated, ClientSessionFailureReason.TimeoutOwnerReady,
                ClientSessionFailureReason.ConnectionLostBeforeAuth, ClientSessionFailureReason.AuthRejected,
                ClientSessionFailureReason.Cancelled })
            {
                Assert.That(ClientMatchSessionCore.DescribeFailure(reason), Is.Not.Null.And.Not.Empty,
                    $"失败文案缺失：{reason}");
            }
        }

        // ---- ② 策略卡复位（审计 §3.2：第一次成功后的失败被 _accepted 吞） ----

        [Test]
        public void AuthFailurePolicy_ResetSession_ClearsAccepted_FromPreviousMatch()
        {
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnAuthResult(true, string.Empty), Is.EqualTo(ClientAuthFailureKind.None), "上局认证成功");
            Assert.That(policy.OnDisconnected(true, true), Is.EqualTo(ClientAuthFailureKind.None),
                "未复位时：上局成功后的断线被吞（审计缺口）");

            policy.ResetSession(); // 新局复位
            Assert.That(policy.OnDisconnected(true, true), Is.EqualTo(ClientAuthFailureKind.GenericDegraded),
                "复位后：本局连接从未建立/无结果 → 通用降级必须触发");
            Assert.That(policy.HasResult, Is.False);
        }

        [Test]
        public void AuthFailurePolicy_ResetSession_ClearsSeenRejectionCodes()
        {
            var policy = new AuthFailurePolicy();
            Assert.That(policy.OnAuthResult(false, "TICKET_REPLAYED"), Is.EqualTo(ClientAuthFailureKind.SpecificRejection));

            policy.ResetSession();
            Assert.That(policy.SeenRejectionCodes, Is.Empty, "复位清空已看错误码——新局同码拒绝必须再次展示");
            Assert.That(policy.CurrentRejectionCode, Is.Empty);
        }

        // ---- ③ 认证前协议门 ----

        [Test]
        public void ProtocolGate_MatchingProtocol_BeginsValidation()
        {
            var service = MakeService(expectedProtocolId: GameProtocolIdentity.ProtocolId);
            var decision = service.EvaluateIncoming(false, false,
                new JoinTicketBroadcast { Ticket = "t", ProtocolId = GameProtocolIdentity.ProtocolId });
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.BeginValidation));
        }

        [Test]
        public void ProtocolGate_LegacyEmptyClient_IsRejectedBeforeTicketConsume()
        {
            var service = MakeService(expectedProtocolId: GameProtocolIdentity.ProtocolId);
            var decision = service.EvaluateIncoming(false, false,
                new JoinTicketBroadcast { Ticket = "t", ProtocolId = "" });
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.RejectProtocolMismatch),
                "旧客户端（空字段）必须在后端 consume 之前被拒绝（票据零消耗）");
        }

        [Test]
        public void ProtocolGate_DifferentProtocol_IsRejected()
        {
            var service = MakeService(expectedProtocolId: "fps-net-2");
            var decision = service.EvaluateIncoming(false, false,
                new JoinTicketBroadcast { Ticket = "t", ProtocolId = "fps-net-1" });
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.RejectProtocolMismatch));
        }

        [Test]
        public void ProtocolGate_Disabled_KeepsLegacyBehavior()
        {
            var service = MakeService(expectedProtocolId: null); // 旧测试构造/门关闭
            var decision = service.EvaluateIncoming(false, false,
                new JoinTicketBroadcast { Ticket = "t", ProtocolId = "" });
            Assert.That(decision, Is.EqualTo(JoinTicketAuthService.IncomingDecision.BeginValidation),
                "门未配置（null）时保持旧判定语义——兼容既有调用方");
        }

        [Test]
        public void ProtocolGate_Precedes_AttackAndDuplicateChecks()
        {
            var service = MakeService(expectedProtocolId: "fps-net-2");
            // 已认证连接再发认证广播 = 攻击（先于协议判定断开）；在途重复 = 忽略
            Assert.That(service.EvaluateIncoming(true, false,
                new JoinTicketBroadcast { Ticket = "t", ProtocolId = "" }), Is.EqualTo(JoinTicketAuthService.IncomingDecision.DisconnectAttacker));
            Assert.That(service.EvaluateIncoming(false, true,
                new JoinTicketBroadcast { Ticket = "t", ProtocolId = "" }), Is.EqualTo(JoinTicketAuthService.IncomingDecision.IgnoreDuplicate));
        }

        private static JoinTicketAuthService MakeService(string expectedProtocolId)
        {
            return new JoinTicketAuthService(new FakeControlPlaneForProtocolTests(), allowUnsafeDebugAuth: false,
                consumeTimeoutSeconds: 10.0, expectedProtocolId: expectedProtocolId);
        }

        private sealed class FakeControlPlaneForProtocolTests : IServerControlPlaneClient
        {
            public Task<ServerInstanceRegisterResponse> RegisterAsync(ServerInstanceRegisterRequest request) => throw new System.NotImplementedException();
            public Task<HeartbeatOutcome> HeartbeatAsync(ServerInstanceHeartbeatRequest request) => throw new System.NotImplementedException();
            public Task<TicketConsumeResult> ConsumeTicketAsync(string ticket) => Task.FromResult(TicketConsumeResult.Rejected("TICKET_INVALID"));
            public Task<PlayerDisconnectReport> DisconnectPlayerAsync(ServerPlayerDisconnectRequest request) => throw new System.NotImplementedException();
        }

        // ---- ④ 客户端会话消息携带协议代际 ----

        [Test]
        public void ClientSessionMessage_CarriesProtocolId()
        {
            var session = new JoinTicketClientSession("client-ticket-1");
            var decision = session.TryBuildAuthBroadcast(false, out var message, out _, out _);
            Assert.That(decision, Is.EqualTo(JoinTicketClientSession.SendDecision.Send));
            Assert.That(message.ProtocolId, Is.EqualTo(GameProtocolIdentity.ProtocolId),
                "出站认证消息必须申报本端协议代际（认证前握手）");
        }

        // ---- ⑤ 启动上下文：代际 + 载荷扩展 + Peek ----

        [SetUp]
        public void SetUp() => NetworkLaunchContext.Clear();

        [TearDown]
        public void TearDown() => NetworkLaunchContext.Clear();

        [Test]
        public void ConfigureClient_GenerationsIncrease_AndPayloadCarriesSceneAndMatch()
        {
            NetworkLaunchContext.ConfigureClient("10.0.0.1", 7770, "ticket-1", "Arena", "match-abc");
            NetworkLaunchContext.ConfigureClient("10.0.0.2", 7771, "ticket-2", "Arena", "match-def");

            var peek = NetworkLaunchContext.PeekClientLaunch();
            Assert.That(peek, Is.Not.Null, "Peek 只读探测，不消费");
            Assert.That(NetworkLaunchContext.HasPendingClientLaunch, Is.True, "Peek 后仍是 pending");
            Assert.That(peek.ServerAddress, Is.EqualTo("10.0.0.2"), "重复配置以最后一次为准");
            Assert.That(peek.SceneName, Is.EqualTo("Arena"));
            Assert.That(peek.MatchId, Is.EqualTo("match-def"));

            var launch = NetworkLaunchContext.TryBeginClientLaunch();
            Assert.That(launch.Generation, Is.EqualTo(NetworkLaunchContext.CurrentGeneration), "代际 = 最新分配值");
            var first = new ClientMatchSessionCore();
            first.BeginSession(launch.Generation - 1, string.Empty, 0f, 0f);
            Assert.That(launch.Generation, Is.GreaterThan(1), "每次配置递增（旧代际 < 新代际）");
        }

        [Test]
        public void Clear_KeepsGenerationCounterMonotonic()
        {
            NetworkLaunchContext.ConfigureClient("a", 1, "t1");
            var gen1 = NetworkLaunchContext.CurrentGeneration;
            NetworkLaunchContext.Clear();
            NetworkLaunchContext.ConfigureClient("b", 2, "t2");
            Assert.That(NetworkLaunchContext.CurrentGeneration, Is.GreaterThan(gen1), "清空不清零代际——防同代际复用");
        }
    }
}
