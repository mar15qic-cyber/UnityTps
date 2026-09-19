using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FishNet.Transporting;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Codex 第三轮审计 P0 修复锁定（2026-09-06）：入口决策——检测 Dedicated 模式 ≠ 允许显式启动链。
    /// 回归目标：旧实现"参数无效时提前 return"导致 UNITY_SERVER 构建中 NetworkManager.Start()
    /// 仍以默认端口自动监听（guard 未安装）。本套件驱动真实决策核心 DedicatedServerEntry.Apply
    /// （InitializeOnLoad 的可测形式）+ 真实 DedicatedServerBootstrap 组件：
    /// ① UNITY_SERVER + 缺 instanceId/port/backendUrl/serverKey 各自 → guard【已安装】+ 启动链终止
    ///    （不 Configure、不监听、不建控制面、不注册、不心跳、无 DS_READY）+ 明确参数错误 + 上下文不残留；
    /// ② 参数有效 → FullStartup（链准入授予 + 上下文存储）；
    /// ③ 普通客户端 → None：不装 guard、不建驱动（SetStartOnHeadless 的唯一路径是 guard——结构性为零调用）；
    /// ④ GuardOnly 驱动直调 RunStartupChainAsync（注入假 runtime）→ 启动链零副作用、显式监听次数 0；
    /// ⑤ headless guard 主体直调 → DisableAutoHeadlessStart 恰一次（幂等）。
    /// </summary>
    public sealed class DedicatedServerEntryTests
    {
        private readonly List<GameObject> _createdDrivers = new();

        [SetUp]
        public void SetUp()
        {
            NetworkLaunchContext.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var driver in _createdDrivers)
            {
                if (driver != null)
                    UnityEngine.Object.DestroyImmediate(driver);
            }
            _createdDrivers.Clear();
            NetworkLaunchContext.Clear();
        }

        // ---- 测试替身 ----

        private sealed class RecordingEntryActions : IDedicatedEntryActions
        {
            public int GuardInstalled;
            public int BootLogged;
            public string InvalidError;

            public DedicatedServerBootstrap InstallGuardDriver()
            {
                GuardInstalled++;
                return new GameObject("EntryTestDriver").AddComponent<DedicatedServerBootstrap>();
            }

            public void LogInvalidParameters(string validationError)
            {
                InvalidError = validationError;
            }

            public void LogBoot(DedicatedServerOptions options)
            {
                BootLogged++;
            }

            public void StoreLaunchContext(DedicatedServerOptions options)
            {
                NetworkLaunchContext.ConfigureDedicatedServer(options);
            }
        }

        private sealed class FakeRuntime : IDedicatedServerRuntime
        {
            public readonly List<string> Calls = new();
            public FakeManager Manager;

            public FakeRuntime()
            {
                Manager = new FakeManager(Calls);
            }

            public bool IsArenaActive => true;

            public Task LoadArenaAsync()
            {
                Calls.Add("LoadArenaAsync");
                return Task.CompletedTask;
            }

            public INetworkManagerBinding FindArenaNetworkManager()
            {
                Calls.Add("FindArenaNetworkManager");
                return Manager;
            }

            public void DisableAutoHeadlessStart(INetworkManagerBinding manager)
            {
                Calls.Add("DisableAutoHeadlessStart");
            }
        }

        private sealed class FakeManager : INetworkManagerBinding
        {
            private readonly List<string> _calls;

            public FakeManager(List<string> calls)
            {
                _calls = calls;
            }

#pragma warning disable CS0067 // 测试不触发监听事件
            public event Action<ServerConnectionStateArgs> ServerConnectionState;
#pragma warning restore CS0067

            public bool TrySetTransportPort(ushort port, out string transportName)
            {
                _calls.Add("SetPort");
                transportName = "Tugboat";
                return true;
            }

            public bool TrySetRemoteClientTimeout(double seconds, out string transportName)
            {
                _calls.Add("SetRemoteClientTimeout");
                transportName = "Tugboat";
                return true;
            }

            public JoinTicketAuthenticator WireServerAuthenticator(IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth)
            {
                _calls.Add("WireServerAuthenticator");
                return null;
            }

            public bool StartServerConnection()
            {
                _calls.Add("StartServerConnection");
                return true;
            }

            public bool StopServerConnection()
            {
                _calls.Add("StopServerConnection");
                return true;
            }

            public bool IsClientConnectionActive => false;

            public int ConnectedClientCount => 0;
        }

        // ---- 解析辅助 ----

        private static readonly string[] FullServerArgs =
        {
            "-instanceId", "arena-01", "-port", "7770",
            "-backendUrl", "http://127.0.0.1:5080", "-serverKey", "unit-key",
        };

        /// <summary>UNITY_SERVER 构建模拟：移除指定参数名+值后解析（缺参仍应进入 Dedicated 模式）。</summary>
        private static DedicatedServerOptions ParseUnityServerMissing(string omittedArg)
        {
            var args = new List<string>(FullServerArgs);
            int index = args.IndexOf(omittedArg);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"测试参数构造错误：{omittedArg} 不在基线参数组中");
            args.RemoveAt(index + 1);
            args.RemoveAt(index);
            return DedicatedServerOptions.Parse(args, isBatchMode: true, isUnityServerDefine: true, isReleaseBuild: false);
        }

        private DedicatedServerEntry.ApplyResult ApplyWithRecording(DedicatedServerOptions options, out RecordingEntryActions actions)
        {
            actions = new RecordingEntryActions();
            var result = DedicatedServerEntry.Apply(options, actions);
            if (result.Driver != null)
                _createdDrivers.Add(result.Driver.gameObject);
            return result;
        }

        // ---- ① 缺参四项：guard 已安装、启动链终止、上下文不残留 ----

        [TestCase("-instanceId")]
        [TestCase("-port")]
        [TestCase("-backendUrl")]
        [TestCase("-serverKey")]
        public void UnityServer_MissingArg_InstallsGuard_TerminatesChain_DoesNotStoreContext(string omittedArg)
        {
            var options = ParseUnityServerMissing(omittedArg);
            Assert.That(options.IsDedicatedServer, Is.True, "UNITY_SERVER 构建即使缺参也是 Dedicated 模式");
            Assert.That(options.IsValid, Is.False, "前置确认：缺参无效");

            var result = ApplyWithRecording(options, out var actions);

            // P0 核心：参数无效时 guard 也必须已安装（旧实现的 return 漏洞正是漏掉这一步）
            Assert.That(actions.GuardInstalled, Is.EqualTo(1),
                "Dedicated 模式成立即无条件安装 headless guard——先于 IsValid 判定");
            Assert.That(result.Decision, Is.EqualTo(DedicatedEntryDecision.GuardOnly), "缺参 → guard-only（启动链终止）");
            Assert.That(result.Driver, Is.Not.Null, "guard 驱动必须存在（否则 NetworkManager.Start() 将自动监听）");

            // 启动链终止证据
            Assert.That(result.Driver.ShouldRunExplicitStartupChain, Is.False, "缺参驱动不得进入显式启动链（无 Configure/监听/注册/心跳/DS_READY）");
            Assert.That(result.Driver.EntryMode, Is.EqualTo(DedicatedEntryDecision.GuardOnly), "默认模式即 guard-only，未授予 FullStartup");

            // 明确参数错误 + 上下文不残留
            Assert.That(actions.InvalidError, Does.Contain(omittedArg), "必须打印明确的参数错误（点名缺失参数）");
            Assert.That(NetworkLaunchContext.DedicatedServer, Is.Null, "无效 options 绝不进入 NetworkLaunchContext（不残留）");
            Assert.That(actions.BootLogged, Is.EqualTo(0), "参数无效不打 BOOT 日志");
        }

        // ---- ② 参数有效：FullStartup + 上下文存储 ----

        [Test]
        public void ValidDedicated_GrantsFullStartup_AndStoresContext()
        {
            var options = DedicatedServerOptions.Parse(FullServerArgs, isBatchMode: true, isUnityServerDefine: true, isReleaseBuild: false);
            Assert.That(options.IsValid, Is.True);

            var result = ApplyWithRecording(options, out var actions);

            Assert.That(result.Decision, Is.EqualTo(DedicatedEntryDecision.FullStartup));
            Assert.That(actions.GuardInstalled, Is.EqualTo(1), "有效路径同样先装 guard（时序不变式）");
            Assert.That(result.Driver.ShouldRunExplicitStartupChain, Is.True, "参数有效 → 授予显式启动链");
            Assert.That(NetworkLaunchContext.DedicatedServer, Is.SameAs(options), "有效 options 进入上下文");
            Assert.That(actions.BootLogged, Is.EqualTo(1));
            Assert.That(actions.InvalidError, Is.Null);
        }

        // ---- ③ 普通客户端：零介入（不装 guard、不建驱动 → SetStartOnHeadless 结构性零调用） ----

        [Test]
        public void NormalClient_NoGuardNoDriver_NeverTouchesSetStartOnHeadless()
        {
            var options = DedicatedServerOptions.Parse(
                new[] { "Game.exe", "-projectPath", "E:\\somewhere" },
                isBatchMode: false, isUnityServerDefine: false, isReleaseBuild: false);
            Assert.That(options.IsDedicatedServer, Is.False, "前置确认：普通客户端");

            var result = ApplyWithRecording(options, out var actions);

            Assert.That(result.Decision, Is.EqualTo(DedicatedEntryDecision.None), "普通客户端零介入");
            Assert.That(result.Driver, Is.Null, "不建驱动对象");
            Assert.That(actions.GuardInstalled, Is.EqualTo(0),
                "不装 guard——SetStartOnHeadless 的唯一调用路径是 guard（此处结构性为零调用）");
            Assert.That(NetworkLaunchContext.DedicatedServer, Is.Null);
        }

        // ---- ④ GuardOnly 驱动：启动链零副作用（直调真实 RunStartupChainAsync） ----

        [Test]
        public void GuardOnlyDriver_RunStartupChain_MakesZeroRuntimeCalls_ZeroListen()
        {
            var options = ParseUnityServerMissing("-instanceId");
            var result = ApplyWithRecording(options, out _);
            Assert.That(result.Driver.ShouldRunExplicitStartupChain, Is.False);

            var runtime = new FakeRuntime();
            result.Driver.InjectRuntimeForTests(runtime);

            // 直调真实启动链（Start 的主体）：guard-only 必须在第一行终止
            result.Driver.RunStartupChainAsync().GetAwaiter().GetResult();

            Assert.That(runtime.Calls, Is.Empty,
                "启动链零副作用：无 FindArenaNetworkManager/LoadArena/Configure/StartServerConnection——" +
                "显式监听次数 0、控制面 register/heartbeat 0、无 DS_READY 准入（链未进入）");
        }

        // ---- ⑤ headless guard 主体：禁用调用恰一次（幂等） ----

        [Test]
        public void HeadlessGuardBody_DisablesAutoStart_ExactlyOnce()
        {
            var driver = new GameObject("EntryTestDriver").AddComponent<DedicatedServerBootstrap>();
            _createdDrivers.Add(driver.gameObject);
            var runtime = new FakeRuntime();
            driver.InjectRuntimeForTests(runtime);

            bool first = driver.ApplyHeadlessGuardToCurrentArena();
            bool second = driver.ApplyHeadlessGuardToCurrentArena();   // 幂等复证

            Assert.That(first, Is.True, "guard 主体对 Arena NetworkManager 应用成功");
            Assert.That(second, Is.True, "重复应用为空操作（幂等）");
            Assert.That(runtime.Calls.FindAll(c => c == "DisableAutoHeadlessStart").Count, Is.EqualTo(1),
                "DisableAutoHeadlessStart（→ ServerManager.SetStartOnHeadless(false)）恰一次");
        }

        [Test]
        public void HeadlessGuardBody_WithoutNetworkManager_ReturnsFalse()
        {
            var driver = new GameObject("EntryTestDriver").AddComponent<DedicatedServerBootstrap>();
            _createdDrivers.Add(driver.gameObject);
            var runtime = new FakeRuntime { Manager = null };
            driver.InjectRuntimeForTests(runtime);

            bool applied = driver.ApplyHeadlessGuardToCurrentArena();

            Assert.That(applied, Is.False, "NetworkManager 缺失时 guard 显式失败（启动链随后同样显式失败——绝不静默）");
        }
    }
}
