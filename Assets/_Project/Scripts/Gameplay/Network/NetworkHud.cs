using FishNet.Managing;
using FishNet.Transporting.Tugboat;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 联网启动器（Docs/19 N1 → Docs/27 Day1 C3 改造 → 2026-09-15 P0-B 消费入口迁移）：
    /// 正式客户端入口 = NetworkLaunchContext（大厅 ConfigureClient → ClientMatchSessionCoordinator
    /// 在 Arena 就绪后按代际消费并自动连接——常驻协调器不受 DestroyNewest 影响，第二局照常发起）。
    /// 本组件保留：Arena 网络系统组件的运行时挂载点 + 键位调试入口。
    /// 键位入口降级为 Editor/Development 调试（Release 编译不编译任何网络调试键位）：
    /// - F1 Host：打印 deprecated 警告——正式拓扑为 Dedicated Server，房主不再是服务器进程（Docs/26 Day1）；
    /// - F2 Client：localhost 无票据调试连接（服务器仅在 -allowUnsafeLocalDebugAuth 开启时接受）；
    /// - F3：停止连接。
    /// 单人离线模式不受影响：无 LaunchContext 且不按键则 NetworkManager 不启动。
    /// 输入走 Input System（项目 Active Input Handling = 新输入系统）。
    /// </summary>
    public sealed class NetworkHud : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private ushort port = 7770;
        [Tooltip("F2 调试客户端连接地址（本机测试=127.0.0.1；局域网=服务器 IPv4）")]
        [SerializeField] private string clientAddress = "127.0.0.1";

        private void Awake()
        {
            if (networkManager == null) networkManager = GetComponent<NetworkManager>();
            // Docs/23 P1-2【实施适配 2】：MatchLifecycle 运行时挂载（纯 MonoBehaviour，
            // 零资产改动；NetworkHud 位于 Arena 场景 NetworkSystems 对象——已按 GUID 实地确认）
            if (GetComponent<MatchLifecycle>() == null) gameObject.AddComponent<MatchLifecycle>();
            // R8（审计修复）：首次出生队伍分区接管（服务器事件驱动；客户端/离线零介入）
            if (GetComponent<TeamFirstSpawnDirector>() == null) gameObject.AddComponent<TeamFirstSpawnDirector>();
            // Phase C：对局连接观测（客户端断线/HostLost 统一本地处理）同点挂载
            if (GetComponent<MatchConnectionWatcher>() == null) gameObject.AddComponent<MatchConnectionWatcher>();
            // Day2：认证失败 UI 降级（Bind 由客户端会话路径显式调用；未 Bind = 惰性）
            if (GetComponent<ClientAuthFailureHandler>() == null) gameObject.AddComponent<ClientAuthFailureHandler>();
            // Day3 Phase 2：服务器 hitbox 历史 + 命中回滚（仅服务器侧激活记录；客户端实例惰性）
            if (GetComponent<ServerLagCompensation>() == null) gameObject.AddComponent<ServerLagCompensation>();
            // 2026-09-15 P0-B：客户端启动消费入口迁出 Start——NetworkManager 跨场景常驻
            //（_dontDestroyOnLoad + DestroyNewest）后，第二次进 Arena 的新 NetworkHud 实例会被
            // FishNet 销毁、其 Start 永不生效；改由常驻 ClientMatchSessionCoordinator 消费并连接。
            // 本调用幂等：已存在宿主时仅刷新捕获引用（不覆盖存活的常驻 NetworkManager）。
            ClientMatchSessionCoordinator.EnsureHost(networkManager);
        }

        // 正式客户端入口已迁至 ClientMatchSessionCoordinator（见 Awake 注释）：
        // NetworkLaunchContext 的消费与 StartConnection 由协调器在「Arena 场景就绪」时执行，
        // 不再依赖本组件的 Start（第二局的新实例注定被 DestroyNewest 销毁）。
        // 键位调试入口（F1/F2/F3）保留在下方 Update（仅 Editor/Development 编译）。

        // 注：正式客户端启动（NetworkLaunchContext 消费 + StartConnection）在
        // ClientMatchSessionCoordinator（常驻宿主，sceneLoaded 触发）内执行，本类不再持有该入口。

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void Update()
        {
            if (networkManager == null) return;
            var kb = Keyboard.current;
            if (kb == null) return;
            bool running = networkManager.IsServerStarted || networkManager.IsClientStarted;

            if (kb.f1Key.wasPressedThisFrame && !running)
            {
                Debug.LogWarning("[NetworkHud] F1 host 已弃用（Docs/27 Day1）：正式拓扑为 Dedicated Server，"
                    + "房主不再拥有服务器进程。本键仅作 Editor/Development 调试保留。");
                // 调试 Host：接线票据认证器（远程调试客户端走 unsafe debug 通道），服务器显式 SetAuthenticator
                var hostAuth = JoinTicketAuthenticator.EnsureServerAuthenticator(
                    networkManager, null, JoinTicketDebugAuthGuard.IsAllowedInCurrentProcess());
                networkManager.ServerManager.SetAuthenticator(hostAuth);
                var tugboat = networkManager.TransportManager.Transport as Tugboat;
                if (tugboat != null) tugboat.SetPort(port);
                networkManager.ServerManager.StartConnection();
                networkManager.ClientManager.StartConnection();
                Debug.Log($"[NetworkHud] HOST(debug) started on port {port}");
            }

            if (kb.f2Key.wasPressedThisFrame && !running)
            {
                var tugboat = networkManager.TransportManager.Transport as Tugboat;
                if (tugboat != null)
                {
                    tugboat.SetClientAddress(clientAddress);
                    tugboat.SetPort(port);
                    tugboat.SetTimeout((float)DedicatedServerOptions.DefaultRemoteClientTimeoutSeconds, asServer: false);
                }
                // 无票据调试：认证器按 fail closed 处理（未开 unsafe 通道时本地即拒绝发送）
                var debugAuth = JoinTicketAuthenticator.EnsureClientAuthenticator(
                    networkManager, null, JoinTicketDebugAuthGuard.IsAllowedInCurrentProcess());
                GetComponent<ClientAuthFailureHandler>()?.Bind(debugAuth, isClientSession: true);
                // Day2 补缺：F2 调试连接同样先登记 ConnectionAttempted（调试目标也可能不可达）
                debugAuth.MarkClientConnectionAttempted();
                networkManager.ClientManager.StartConnection();
                Debug.Log($"[NetworkHud] CLIENT(debug) connecting to {clientAddress}:{port}");
            }

            if (kb.f3Key.wasPressedThisFrame && running)
            {
                networkManager.ServerManager.StopConnection(true);
                networkManager.ClientManager.StopConnection();
                Debug.Log("[NetworkHud] connection stopped");
            }
        }
#endif
    }
}
