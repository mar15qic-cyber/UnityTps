using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FishNet.Transporting;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day2 掉线生命周期锁定（入口任务 1）：
    /// ① F9 超时确认与应用——配置阶段以显式值经 INetworkManagerBinding.TrySetRemoteClientTimeout 应用；
    ///    Transport 不支持时只告警不致命（配置继续、监听照常）；
    /// ② SingleFlightGate 单飞语义——掉线即时心跳同一时刻至多一个在途，不排队不缓存；
    /// ③ ServerHeartbeatTracker 掉线后的事实上报——断开后 ConnectedClientCount 下降，
    ///    BuildHeartbeat 携带收敛后人数（权威 CurrentPlayers 事实源）。
    /// Bootstrap 事件接线（ClientConnectionCleaned → SendImmediateHeartbeatAsync）为运行时壳，
    /// EditMode 无法构建真实 NetworkManager 场景，由 Day2 定向联测（杀客户端收敛）实机覆盖。
    /// </summary>
    public sealed class DedicatedServerDisconnectTests
    {
        private sealed class FakeManager : INetworkManagerBinding
        {
            public bool TimeoutSupported = true;
            public double? AppliedTimeoutSeconds;

#pragma warning disable CS0067 // 假实现不触发监听事件
            public event Action<ServerConnectionStateArgs> ServerConnectionState;
#pragma warning restore CS0067

            public bool TrySetTransportPort(ushort port, out string transportName)
            {
                transportName = "Tugboat";
                return true;
            }

            public bool TrySetRemoteClientTimeout(double seconds, out string transportName)
            {
                if (!TimeoutSupported)
                {
                    transportName = "NotATugboat";
                    return false;
                }
                AppliedTimeoutSeconds = seconds;
                transportName = "Tugboat";
                return true;
            }

            public JoinTicketAuthenticator WireServerAuthenticator(IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth) => null;

            public bool StartServerConnection() => true;

            public bool StopServerConnection() => true;

            public bool IsClientConnectionActive => false;

            public int ConnectedClientCount => 0;
        }

        private sealed class FakeRuntime : IDedicatedServerRuntime
        {
            public readonly FakeManager Manager = new();

            public bool IsArenaActive => true;

            public Task LoadArenaAsync() => Task.CompletedTask;

            public INetworkManagerBinding FindArenaNetworkManager() => Manager;

            public void DisableAutoHeadlessStart(INetworkManagerBinding manager) { }
        }

        private static DedicatedServerOptions OptionsWithTimeout(string timeoutArg)
        {
            var args = new List<string>
            {
                "-dedicatedServer", "-instanceId", "arena-01", "-port", "7770",
                "-backendUrl", "http://127.0.0.1:5080", "-serverKey", "k",
            };
            if (timeoutArg != null) args.AddRange(new[] { "-remoteClientTimeout", timeoutArg });
            return DedicatedServerOptions.Parse(args, isBatchMode: false, isUnityServerDefine: false, isReleaseBuild: false);
        }

        // ---- ① F9：远端客户端超时在配置阶段应用 ----

        [Test]
        public void ConfigureAsync_AppliesExplicitRemoteClientTimeout()
        {
            var runtime = new FakeRuntime();

            var result = DedicatedServerStartup.ConfigureAsync(runtime, OptionsWithTimeout("20"), controlPlane: null)
                .GetAwaiter().GetResult();

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.Configured), result.Reason);
            Assert.That(runtime.Manager.AppliedTimeoutSeconds, Is.EqualTo(20d).Within(0.001),
                "显式 -remoteClientTimeout 20 必须在监听前经 Transport 应用（F9：默认 1800s 不可接受）");
        }

        [Test]
        public void ConfigureAsync_AppliesDefaultValue_WhenArgAbsent()
        {
            var runtime = new FakeRuntime();

            var result = DedicatedServerStartup.ConfigureAsync(runtime, OptionsWithTimeout(null), controlPlane: null)
                .GetAwaiter().GetResult();

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.Configured), result.Reason);
            Assert.That(runtime.Manager.AppliedTimeoutSeconds,
                Is.EqualTo(DedicatedServerOptions.DefaultRemoteClientTimeoutSeconds).Within(0.001),
                "未提供参数时必须显式压低 FishNet 默认 1800s");
        }

        [Test]
        public void ConfigureAsync_UnsupportedTimeoutTransport_NonFatal_ConfigurationContinues()
        {
            var runtime = new FakeRuntime { Manager = { TimeoutSupported = false } };

            var result = DedicatedServerStartup.ConfigureAsync(runtime, OptionsWithTimeout("20"), controlPlane: null)
                .GetAwaiter().GetResult();

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.Configured),
                "超时设置失败只影响清理收敛速度，不得令整个配置失败（与端口失败不同级）");
            Assert.That(runtime.Manager.AppliedTimeoutSeconds, Is.Null);
        }

        // ---- ② SingleFlightGate 单飞语义 ----

        [Test]
        public void SingleFlightGate_SecondBeginIsRejected_UntilEnd()
        {
            var gate = new SingleFlightGate();
            Assert.That(gate.TryBegin(), Is.True, "首次占位必须成功");
            Assert.That(gate.TryBegin(), Is.False, "并发触发必须被拒绝（同一时刻至多一个在途上报）");
            gate.End();
            Assert.That(gate.TryBegin(), Is.True, "释放后允许下一次触发");
        }

        [Test]
        public void SingleFlightGate_EndIsIdempotent()
        {
            var gate = new SingleFlightGate();
            gate.End();
            Assert.That(gate.TryBegin(), Is.True, "未占位时 End 无副作用");
        }

        // ---- ③ 掉线后心跳人数收敛 ----

        [Test]
        public void HeartbeatTracker_ReportsConvergedPlayerCount_AfterDisconnect()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMAB");
            tracker.TryEnterMatch();

            // 连接清理后：ConnectedClientCount 由 2 → 1（Bootstrap 立即心跳 + 周期心跳同事实源）
            var request = tracker.BuildHeartbeat(currentPlayers: 1);

            Assert.That(request.currentPlayers, Is.EqualTo(1), "掉线后心跳必须携带收敛后人数（后端 CurrentPlayers 权威事实）");
            Assert.That(request.roomCode, Is.EqualTo("ROOMAB"), "房间绑定保持（实例仍被该房间占用）");
            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateInMatch), "比赛未结束前实例保持 InMatch");
        }

        // ---- ④ P0 租约闭环（2026-09-08 审计 §2）：掉线上报权威释放门 ----

        [Test]
        public void BackendReleaseGate_OnlyExplicitReadyAndZeroReleases()
        {
            // 唯一释放信号 = 后端明确的 instanceState=Ready 且 remainingPlayers=0；
            // 其余一切事实（含字段缺失 remainingPlayers<0）一律不释放——404/409 不带事实，天然不成立
            Assert.That(ServerHeartbeatTracker.IsBackendReleased("Ready", 0), Is.True, "Ready+0 = 唯一允许清绑定的权威信号");
            Assert.That(ServerHeartbeatTracker.IsBackendReleased("Reserved", 0), Is.False, "Reserved（仍有房间）不得清绑定");
            Assert.That(ServerHeartbeatTracker.IsBackendReleased("InMatch", 0), Is.False, "InMatch 不得清绑定");
            Assert.That(ServerHeartbeatTracker.IsBackendReleased("Ready", 1), Is.False, "仍有成员（如重连保护 no-op）不得清绑定");
            Assert.That(ServerHeartbeatTracker.IsBackendReleased("Ready", -1), Is.False, "事实缺失（旧版后端 204 空响应）fail closed");
            Assert.That(ServerHeartbeatTracker.IsBackendReleased(string.Empty, 0), Is.False, "空状态不得视为 Ready");
        }

        [Test]
        public void Tracker_OnBackendReleased_ClearsBindingAndInMatch_ThenHeartbeatsReady()
        {
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMP0");
            tracker.TryEnterMatch();

            tracker.OnBackendReleased();

            Assert.That(tracker.BoundRoomCode, Is.Empty, "后端权威释放后旧绑定必须清空");
            Assert.That(tracker.InMatch, Is.False, "释放后 InMatch 不再成立");
            var request = tracker.BuildHeartbeat(currentPlayers: 0);
            Assert.That(request.state, Is.EqualTo(ServerHeartbeatTracker.StateReady), "释放后心跳上报 Ready（后端未绑定实例的合法转换）");
            Assert.That(request.roomCode, Is.Empty, "释放后心跳不得携带旧房间码");
        }

        [Test]
        public void Lifecycle_DisconnectReportAck_SetsLeaseGenerationOnlyOnAuthoritativeRelease()
        {
            DedicatedServerLifecycle.ResetForTests();

            // 仍有成员/绑定保持的事实：不构成释放证据
            DedicatedServerLifecycle.OnDisconnectReportAck("Reserved", 2, trackerUnbound: false);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False, "Reserved/2 不是释放证据");

            // 权威释放（Ready/0）但 tracker 尚未清绑定（不应发生——防御）：不置位
            DedicatedServerLifecycle.OnDisconnectReportAck("Ready", 0, trackerUnbound: false);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.False, "tracker 旧绑定未清时不得置位（证据 a 要求可观察清除）");

            // 权威释放 + tracker 已清：置位（与注册 ack Ready/Offline 同级证据）
            DedicatedServerLifecycle.OnDisconnectReportAck("Ready", 0, trackerUnbound: true);
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True, "Ready/0 + 旧绑定已清 = 可安全重臂");

            DedicatedServerLifecycle.ResetForTests();
        }

        [Test]
        public void PlayerDisconnectReport_MapsBackendFacts_AndFailedOutcomesCarryNoFacts()
        {
            // 成功响应：三字段权威事实逐字映射（JsonUtility 反序列化形态由契约 DTO 承载）
            var accepted = PlayerDisconnectReport.AcceptedFromBackend(new ServerPlayerDisconnectResponse
            {
                roomCode = "ROOMP0",
                remainingPlayers = 0,
                instanceState = "Ready",
            });
            Assert.That(accepted.Outcome, Is.EqualTo(PlayerDisconnectOutcome.Accepted));
            Assert.That(accepted.RoomCode, Is.EqualTo("ROOMP0"));
            Assert.That(accepted.RemainingPlayers, Is.EqualTo(0));
            Assert.That(accepted.InstanceState, Is.EqualTo("Ready"));

            // 旧版后端 204 空响应：Accepted 但事实缺失（fail closed，调用方保留绑定）
            var legacy = PlayerDisconnectReport.AcceptedFromBackend(null);
            Assert.That(legacy.Outcome, Is.EqualTo(PlayerDisconnectOutcome.Accepted));
            Assert.That(legacy.RemainingPlayers, Is.EqualTo(-1), "无响应体 = 无事实");

            // 终态失败（404/409/传输）：不携带任何事实——绝不猜 Ready
            foreach (var outcome in new[] { PlayerDisconnectOutcome.RoomGone, PlayerDisconnectOutcome.StateConflict, PlayerDisconnectOutcome.TransportError })
            {
                var failed = PlayerDisconnectReport.Failed(outcome);
                Assert.That(failed.Outcome, Is.EqualTo(outcome));
                Assert.That(failed.RemainingPlayers, Is.EqualTo(-1));
                Assert.That(failed.InstanceState, Is.Empty);
                Assert.That(ServerHeartbeatTracker.IsBackendReleased(failed.InstanceState, failed.RemainingPlayers), Is.False,
                    "404/409/传输失败不得构成释放信号");
            }
        }

        [Test]
        public void FullReleaseSequence_ReportFactsThroughTrackerAndGate()
        {
            // 端到端纯逻辑：最后成员退出 → 后端 Ready/0 → tracker 清绑定 → 代际门置位（同一 DS 免重启可再租用）
            DedicatedServerLifecycle.ResetForTests();
            var tracker = new ServerHeartbeatTracker();
            tracker.OnRoomBound("ROOMAB");
            tracker.TryEnterMatch();

            var report = PlayerDisconnectReport.AcceptedFromBackend(new ServerPlayerDisconnectResponse
            {
                roomCode = "ROOMAB",
                remainingPlayers = 0,
                instanceState = "Ready",
            });

            if (ServerHeartbeatTracker.IsBackendReleased(report.InstanceState, report.RemainingPlayers))
            {
                tracker.OnBackendReleased();
                DedicatedServerLifecycle.OnDisconnectReportAck(report.InstanceState, report.RemainingPlayers,
                    trackerUnbound: !tracker.IsRoomBound);
            }

            Assert.That(tracker.BoundRoomCode, Is.Empty);
            var heartbeat = tracker.BuildHeartbeat(0);
            Assert.That(heartbeat.state, Is.EqualTo(ServerHeartbeatTracker.StateReady));
            Assert.That(DedicatedServerLifecycle.BackendReadyForNextMatch, Is.True);

            DedicatedServerLifecycle.ResetForTests();
        }
    }
}
