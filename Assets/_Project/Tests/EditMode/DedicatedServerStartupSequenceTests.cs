using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FishNet.Transporting;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Codex 审计 P0/P1 修复锁定（2026-09-06）：Dedicated Server 启动时序与监听门控——
    /// 用共享调用日志的假 IDedicatedServerRuntime/INetworkManagerBinding 驱动真实编排器
    /// DedicatedServerStartup（运行时接线验证，非源码字符串搜索）：
    /// ① 配置阶段调用顺序：Arena → NetworkManager → 禁 headless → 端口 → 认证器，
    ///    且配置阶段绝不触发 StartServerConnection；
    /// ② 首次 StartServerConnection 只能经 TryBeginListen（bool 门控）发生，位于全部配置之后；
    /// ③ StartConnection 被拒（端口占用/Transport 失败）→ ShouldBeginControlPlane=false
    ///    （不注册、不心跳、无 DS_READY 路径）；
    /// ④ 配置失败（Arena 缺失/NM 缺失/Transport 不支持端口）→ 绝不触发监听；
    /// ⑤ Dedicated 路径结构性无 Client/Host 启动操作（接口不暴露 + 计数佐证）。
    /// 测试方法全部同步（假实现返回已完成 Task，编排器同步收敛——项目既有模式，零 async 运行器依赖）。
    /// MonoBehaviour 壳（sceneLoaded 禁 headless 的时序挂载、事件接线、注册/心跳循环）
    /// 无法在 EditMode 构建真实场景，按票据约定由 headless 实机冒烟覆盖（报告 §3）。
    /// </summary>
    public sealed class DedicatedServerStartupSequenceTests
    {
        private sealed class FakeManager : INetworkManagerBinding
        {
            private readonly List<string> _calls;
            public bool PortSupported = true;
            public bool StartConnectionAccepted = true;
            public int ClientStartRequests;

            public FakeManager(List<string> calls)
            {
                _calls = calls;
            }

#pragma warning disable CS0067 // 真实监听壳（Bootstrap）才订阅该事件；假实现不触发
            public event Action<ServerConnectionStateArgs> ServerConnectionState;
#pragma warning restore CS0067

            public bool TrySetTransportPort(ushort port, out string transportName)
            {
                _calls.Add($"SetPort:{port}");
                if (PortSupported)
                {
                    transportName = "Tugboat";
                    return true;
                }
                transportName = "NotATugboat";
                return false;
            }

            public double? AppliedRemoteClientTimeout;

            public bool TrySetRemoteClientTimeout(double seconds, out string transportName)
            {
                AppliedRemoteClientTimeout = seconds;
                _calls.Add($"SetRemoteClientTimeout:{seconds:0.##}");
                transportName = PortSupported ? "Tugboat" : "NotATugboat";
                return PortSupported;
            }

            public JoinTicketAuthenticator WireServerAuthenticator(IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth)
            {
                // 编排器只透传认证器引用；真实认证器行为由 JoinTicketAuthenticatorTests 覆盖
                _calls.Add("WireServerAuthenticator");
                return null;
            }

            public bool StartServerConnection()
            {
                _calls.Add("StartServerConnection");
                return StartConnectionAccepted;
            }

            public bool StopServerConnection()
            {
                _calls.Add("StopServerConnection");
                return true;
            }

            public bool IsClientConnectionActive => ClientStartRequests > 0;

            public int ConnectedClientCount => 0;

            /// <summary>测试专用：模拟外部路径错误启动客户端（Dedicated 编排禁止触碰本方法）。</summary>
            public void SimulateClientStart()
            {
                ClientStartRequests++;
            }
        }

        private sealed class FakeRuntime : IDedicatedServerRuntime
        {
            public readonly List<string> Calls = new();
            public FakeManager Manager;
            public bool ArenaActive = true;
            public bool ArenaLoadsSuccessfully = true;
            public bool NetworkManagerPresent = true;

            public FakeRuntime()
            {
                // 共享调用日志：编排器跨 runtime/manager 的全局顺序可被单一序列证明
                Manager = new FakeManager(Calls);
            }

            public bool IsArenaActive => ArenaActive;

            public Task LoadArenaAsync()
            {
                Calls.Add("LoadArenaAsync");
                ArenaActive = ArenaLoadsSuccessfully;
                return Task.CompletedTask;
            }

            public INetworkManagerBinding FindArenaNetworkManager()
            {
                Calls.Add("FindArenaNetworkManager");
                return NetworkManagerPresent ? Manager : null;
            }

            public void DisableAutoHeadlessStart(INetworkManagerBinding manager)
            {
                Calls.Add("DisableAutoHeadlessStart");
            }
        }

        private static DedicatedServerOptions ValidOptions()
        {
            return DedicatedServerOptions.Parse(
                new[] { "-dedicatedServer", "-instanceId", "arena-01", "-port", "7770", "-backendUrl", "http://127.0.0.1:5080", "-serverKey", "k" },
                isBatchMode: true, isUnityServerDefine: false, isReleaseBuild: false);
        }

        private static DedicatedServerStartup.ConfigureResult Configure(FakeRuntime runtime)
        {
            return DedicatedServerStartup.ConfigureAsync(runtime, ValidOptions(), controlPlane: null).GetAwaiter().GetResult();
        }

        // ---- ① 配置阶段全局顺序 + 不触发监听 ----

        [Test]
        public void ConfigureAsync_DisablesHeadless_SetsPort_WiresAuthenticator_InOrder_NeverListens()
        {
            var runtime = new FakeRuntime();

            var result = Configure(runtime);

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.Configured), result.Reason);
            // 全局时序不变式（P0）：禁 headless 自动启动 → 端口 → 远端超时（Day2 F9）→ 认证器，
            // 全部完成后才可能监听
            Assert.That(runtime.Calls, Is.EqualTo(new[]
            {
                "FindArenaNetworkManager",
                "DisableAutoHeadlessStart",
                "SetPort:7770",
                "SetRemoteClientTimeout:30",
                "WireServerAuthenticator",
            }), "配置顺序必须为：定位 NM → 禁 headless → 端口 → 远端客户端超时 → 认证器");
            Assert.That(runtime.Calls, Does.Not.Contain("StartServerConnection"),
                "配置阶段绝不允许触发监听（P0：先全部配置，后显式启动）");
        }

        [Test]
        public void ConfigureAsync_LoadsArenaFirst_WhenNotActive()
        {
            var runtime = new FakeRuntime { ArenaActive = false };

            var result = Configure(runtime);

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.Configured));
            Assert.That(runtime.Calls[0], Is.EqualTo("LoadArenaAsync"), "Arena 未就位时必须先加载再配置");
        }

        // ---- ② 首次监听只能经 TryBeginListen，位于全部配置之后 ----

        [Test]
        public void TryBeginListen_IsTheOnlyListenEntry_AfterFullConfiguration()
        {
            var runtime = new FakeRuntime();
            var result = Configure(runtime);

            bool listenAccepted = DedicatedServerStartup.TryBeginListen(result.Manager, out string reason);

            Assert.That(listenAccepted, Is.True);
            Assert.That(reason, Is.Empty);
            Assert.That(runtime.Calls, Is.EqualTo(new[]
            {
                "FindArenaNetworkManager",
                "DisableAutoHeadlessStart",
                "SetPort:7770",
                "SetRemoteClientTimeout:30",
                "WireServerAuthenticator",
                "StartServerConnection",
            }), "完整管线：禁 headless → 端口 → 远端超时 → 认证器 → 首次 StartServerConnection（最后一步）");
        }

        // ---- ③ 监听被拒 → 不进控制面 ----

        [Test]
        public void StartConnectionRefused_NeverEntersControlPlane()
        {
            var runtime = new FakeRuntime();
            runtime.Manager.StartConnectionAccepted = false;
            var configure = Configure(runtime);

            bool listenAccepted = DedicatedServerStartup.TryBeginListen(configure.Manager, out string reason);

            Assert.That(listenAccepted, Is.False, "端口占用/Transport 启动失败必须被拒绝");
            Assert.That(reason, Does.Contain("StartConnection"), "拒绝原因必须明确指向监听失败");
            Assert.That(DedicatedServerStartup.ShouldBeginControlPlane(configure, listenAccepted), Is.False,
                "监听被拒 → 不注册、不心跳、无 DS_READY 路径（控制面准入必须为 false）");
        }

        [Test]
        public void ConfiguredAndAccepted_DoesEnterControlPlane()
        {
            var runtime = new FakeRuntime();
            var configure = Configure(runtime);
            bool listenAccepted = DedicatedServerStartup.TryBeginListen(configure.Manager, out _);

            Assert.That(DedicatedServerStartup.ShouldBeginControlPlane(configure, listenAccepted), Is.True,
                "配置成功 + 监听被接受 → 允许进入注册/心跳（DS_READY 仍由 Started 事件另行门控）");
        }

        // ---- ④ 配置失败 → 绝不触发监听 ----

        [Test]
        public void ArenaMissing_NeverTouchesListen()
        {
            var runtime = new FakeRuntime { ArenaActive = false, ArenaLoadsSuccessfully = false };

            var result = Configure(runtime);

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.ArenaMissing));
            Assert.That(runtime.Calls, Does.Not.Contain("StartServerConnection"));
            Assert.That(DedicatedServerStartup.ShouldBeginControlPlane(result, listenAccepted: true), Is.False,
                "配置失败时即使误传 listenAccepted=true 也不得进入控制面");
        }

        [Test]
        public void NetworkManagerMissing_NeverTouchesListen()
        {
            var runtime = new FakeRuntime { NetworkManagerPresent = false };

            var result = Configure(runtime);

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.NetworkManagerMissing));
            Assert.That(result.Reason, Does.Contain("NetworkManager"));
            Assert.That(runtime.Calls, Does.Not.Contain("StartServerConnection"));
        }

        [Test]
        public void TransportPortUnsupported_NeverTouchesListen()
        {
            var runtime = new FakeRuntime();
            runtime.Manager.PortSupported = false;

            var result = Configure(runtime);

            Assert.That(result.Outcome, Is.EqualTo(DedicatedServerStartup.ConfigureOutcome.TransportPortUnsupported));
            Assert.That(result.Reason, Does.Contain("NotATugboat"));
            Assert.That(runtime.Calls, Does.Not.Contain("StartServerConnection"),
                "端口未设置成功绝不允许进入监听");
            Assert.That(runtime.Calls, Does.Not.Contain("WireServerAuthenticator"),
                "端口失败即中止——认证器接线不会发生（端口先于认证器的时序）");
        }

        // ---- ⑤ Dedicated 路径结构性无 Client 启动 ----

        [Test]
        public void DedicatedPath_NeverStartsClient_StructuralInvariant()
        {
            var runtime = new FakeRuntime();
            var configure = Configure(runtime);
            DedicatedServerStartup.TryBeginListen(configure.Manager, out _);

            // 编排完成后：Dedicated 路径没有任何代码触碰过客户端启动（接口也不暴露该操作）
            Assert.That(runtime.Manager.ClientStartRequests, Is.EqualTo(0),
                "Dedicated 路径绝不调用 ClientManager.StartConnection（结构性 + 计数双证）");
            Assert.That(runtime.Manager.IsClientConnectionActive, Is.False);
        }

        [Test]
        public void ClientActiveInvariant_IsDetectable()
        {
            // 若外部路径误启客户端（未来回归），IsClientConnectionActive 必须能被 Bootstrap 自证捕获
            var manager = new FakeManager(new List<string>());
            Assert.That(manager.IsClientConnectionActive, Is.False);
            manager.SimulateClientStart();
            Assert.That(manager.IsClientConnectionActive, Is.True,
                "不变量自证通道必须有效（Bootstrap 据此打印 INVARIANT VIOLATED 并停服）");
        }

        // ---- 监听门（DS_READY 的唯一准入状态）----

        [Test]
        public void ListenGate_StartedBecomesReady_StoppedFirstBecomesFailed()
        {
            var gate = new ServerListenGate();
            Assert.That(gate.Concluded, Is.False);

            gate.OnConnectionState(LocalConnectionState.Stopped);
            Assert.That(gate.Failed, Is.True, "Started 之前 Stopped = 启动失败（端口占用/Transport 失败）");
            Assert.That(gate.Ready, Is.False, "失败后绝不 Ready——DS_READY/注册/心跳被拒");

            var gate2 = new ServerListenGate();
            gate2.OnConnectionState(LocalConnectionState.Started);
            Assert.That(gate2.Ready, Is.True, "真实 Started 事件才允许 DS_READY");
        }

        [Test]
        public void ListenGate_ConclusionIsFinal_WatchdogExpires()
        {
            var gate = new ServerListenGate();
            gate.OnConnectionState(LocalConnectionState.Started);
            gate.OnConnectionState(LocalConnectionState.Stopped);
            Assert.That(gate.Ready, Is.True, "Ready 后的 Stopped 不回退（运行中停服属 Day2 生命周期）");
            Assert.That(gate.Failed, Is.False);

            var gate2 = new ServerListenGate();
            gate2.OnWatchdogExpired();
            Assert.That(gate2.Failed, Is.True, "watchdog 超时未见 Started = 启动失败");
            Assert.That(gate2.FailureReason, Does.Contain("watchdog"));
        }
    }
}
