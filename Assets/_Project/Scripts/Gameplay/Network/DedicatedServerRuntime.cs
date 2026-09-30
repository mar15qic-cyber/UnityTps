using System;
using System.Threading.Tasks;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Network
{
    // ============================================================================
    // Dedicated Server 启动时序 seam（Codex 审计 P0/P1 修复）。
    // 全部 Unity/FishNet 依赖经接口注入；EditMode 用假实现证明运行时接线不变式：
    //   ① 禁自动 headless → 端口 → 认证器 全部完成于首次 StartServerConnection 之前；
    //   ② Dedicated 路径结构性不存在 Client/Host 启动操作（接口即边界）；
    //   ③ StartConnection 被拒 → 不进入控制面（不注册、不心跳、无 DS_READY）。
    // 本文件只含纯类型与真实实现（无 MonoBehaviour——一类一文件红线）。
    // ============================================================================

    /// <summary>
    /// Dedicated Server 对 Unity 场景/NetworkManager 的运行时依赖面（注入接缝）。
    /// 真实实现见 UnityDedicatedServerRuntime；测试用假实现记录调用顺序。
    /// </summary>
    public interface IDedicatedServerRuntime
    {
        /// <summary>正式 Arena 是否为当前激活场景。</summary>
        bool IsArenaActive { get; }

        /// <summary>加载正式 Arena（异步，完成后 IsArenaActive 应为 true）。</summary>
        Task LoadArenaAsync();

        /// <summary>定位 Arena 的 NetworkManager；找不到返回 null（明确失败）。</summary>
        INetworkManagerBinding FindArenaNetworkManager();

        /// <summary>
        /// 禁用 FishNet headless 自动启动（ServerManager.SetStartOnHeadless 公开 API）。
        /// 必须在 NetworkManager.Start()（Start 阶段）之前的生命周期点调用——
        /// DedicatedServerBootstrap 经 SceneManager.sceneLoaded 回调保证该时序。
        /// </summary>
        void DisableAutoHeadlessStart(INetworkManagerBinding manager);
    }

    /// <summary>
    /// Dedicated 专用 NetworkManager 操作句柄。
    /// 刻意不暴露任何 Client/Host 启动操作——Dedicated 路径结构性不可能启动 Client（编译期保证，
    /// 运行期由 IsClientConnectionActive 不变量自证）。
    /// </summary>
    public interface INetworkManagerBinding
    {
        /// <summary>服务器连接状态事件（先订阅后启动，避免错过 Started）。</summary>
        event Action<ServerConnectionStateArgs> ServerConnectionState;

        /// <summary>设置 Transport 端口。非 Tugboat 返回 false（明确失败，不监听）。</summary>
        bool TrySetTransportPort(ushort port, out string transportName);

        /// <summary>
        /// 设置远端客户端无数据超时（秒，Day2 F9：FishNet 默认 1800s 会令被杀客户端滞留连接表 30 分钟）。
        /// 经 Tugboat.SetTimeout 公开 API 应用（不改 FishNet 源码）；非 Tugboat 返回 false（不致命，仅告警）。
        /// </summary>
        bool TrySetRemoteClientTimeout(double seconds, out string transportName);

        /// <summary>接线票据认证器（Ensure + ServerManager.SetAuthenticator——初始化与结果订阅由此保证）。</summary>
        JoinTicketAuthenticator WireServerAuthenticator(IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth);

        /// <summary>启动服务器连接（FishNet ServerManager.StartConnection() 的 bool 返回值）。</summary>
        bool StartServerConnection();

        /// <summary>停止服务器连接（启动失败/不变量违反时的清理）。</summary>
        bool StopServerConnection();

        /// <summary>Dedicated 不变量：本进程客户端连接是否活跃（正常恒 false）。</summary>
        bool IsClientConnectionActive { get; }

        /// <summary>已连接客户端数（心跳 currentPlayers 事实源）。</summary>
        int ConnectedClientCount { get; }
    }

    /// <summary>
    /// 启动编排核心（纯逻辑）：时序不变式 = 禁 headless → 端口 → 认证器 全部完成后，
    /// 才允许 TryBeginListen 触发首次监听。任何前置失败都返回明确 Outcome，绝不触发监听。
    /// </summary>
    public static class DedicatedServerStartup
    {
        public enum ConfigureOutcome
        {
            Configured,
            ArenaMissing,
            NetworkManagerMissing,
            TransportPortUnsupported,
        }

        public sealed class ConfigureResult
        {
            public ConfigureOutcome Outcome;
            public string Reason = string.Empty;
            public INetworkManagerBinding Manager;
            /// <summary>已接线的票据认证器（供调用方订阅 TicketAccepted 等扩展事件）。</summary>
            public JoinTicketAuthenticator Authenticator;
        }

        /// <summary>
        /// 配置阶段（不触发监听）：Arena 就位 → NetworkManager 确认 → 禁自动 headless →
        /// 端口 → 认证器。ConfigureOutcome.Configured 是进入监听阶段的唯一前提。
        /// </summary>
        public static async Task<ConfigureResult> ConfigureAsync(
            IDedicatedServerRuntime runtime, DedicatedServerOptions options, IServerControlPlaneClient controlPlane)
        {
            if (!runtime.IsArenaActive)
                await runtime.LoadArenaAsync();
            if (!runtime.IsArenaActive)
                return Failed(ConfigureOutcome.ArenaMissing,
                    $"正式战斗场景未能加载（期望 '{GameMapCatalog.ResolveSceneName(options.MapId)}'，mapId='{options.MapId}'）");

            var manager = runtime.FindArenaNetworkManager();
            if (manager == null)
                return Failed(ConfigureOutcome.NetworkManagerMissing, "Arena 中找不到 NetworkManager——需 Codex 批准场景改动才可处理");

            // 时序不变式 #1：先于一切监听禁用 FishNet headless 自动启动（幂等；sceneLoaded 路径已先执行过）
            runtime.DisableAutoHeadlessStart(manager);

            if (!manager.TrySetTransportPort(options.Port, out string transportName))
                return Failed(ConfigureOutcome.TransportPortUnsupported, $"Transport '{transportName}' 不支持 SetPort（期望 Tugboat）——监听未启动");

            // Day2 F9：确认并压低远端客户端超时（默认 1800s → 目标 15~30s）。失败不致命（仅影响
            // 被杀客户端连接的清理收敛速度），但必须留痕——"正式构建实际启用的超时值"由此日志可证。
            if (manager.TrySetRemoteClientTimeout(options.RemoteClientTimeoutSeconds, out string timeoutTransport))
                Debug.Log($"[DedicatedServer] remote client timeout={options.RemoteClientTimeoutSeconds:0.##}s (transport={timeoutTransport}, was FishNet default 1800s)");
            else
                Debug.LogWarning($"[DedicatedServer] remote client timeout 未生效：Transport '{timeoutTransport}' 不支持 SetTimeout——僵尸连接收敛退化为 FishNet 默认 1800s");

            // 时序不变式 #2：认证器在监听前经 SetAuthenticator 完成初始化与结果订阅
            var authenticator = manager.WireServerAuthenticator(controlPlane, options.AllowUnsafeLocalDebugAuth);

            return new ConfigureResult
            {
                Outcome = ConfigureOutcome.Configured,
                Manager = manager,
                Authenticator = authenticator,
            };
        }

        /// <summary>唯一监听触发点。false = 端口占用/Transport 启动失败（明确失败，不进控制面）。</summary>
        public static bool TryBeginListen(INetworkManagerBinding manager, out string reason)
        {
            if (!manager.StartServerConnection())
            {
                reason = "ServerManager.StartConnection() 返回 false（端口占用或 Transport 启动失败）";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>控制面准入决策：只有「配置成功且监听被接受」才允许注册/心跳（DS_READY 由 Started 事件另行门控）。</summary>
        public static bool ShouldBeginControlPlane(ConfigureResult configure, bool listenAccepted)
        {
            return configure != null && configure.Outcome == ConfigureOutcome.Configured && listenAccepted;
        }

        private static ConfigureResult Failed(ConfigureOutcome outcome, string reason) => new()
        {
            Outcome = outcome,
            Reason = reason,
        };
    }

    /// <summary>
    /// 监听阶段门（纯逻辑）：Started 事件 → Ready（唯一允许打印 DS_READY/启动注册心跳的状态）；
    /// Started 之前 Stopped 或 watchdog 超时 → Failed（SERVER_START_FAILED，中止控制面）。
    /// 结论一次性：Ready 后再收到 Stopped 不回退（运行中停服属 Day2 生命周期）。
    /// </summary>
    public sealed class ServerListenGate
    {
        public bool Ready { get; private set; }
        public bool Failed { get; private set; }
        public string FailureReason { get; private set; } = string.Empty;
        public bool Concluded => Ready || Failed;

        public void OnConnectionState(LocalConnectionState state)
        {
            if (Concluded)
                return;
            if (state == LocalConnectionState.Started)
            {
                Ready = true;
            }
            else if (state == LocalConnectionState.Stopped)
            {
                Fail("Transport 在 Started 之前进入 Stopped（端口占用/启动失败）");
            }
        }

        public void OnWatchdogExpired()
        {
            if (!Concluded)
                Fail("watchdog 超时未见 Started 事件");
        }

        private void Fail(string reason)
        {
            Failed = true;
            FailureReason = reason;
        }
    }

    /// <summary>
    /// 单飞闸（Day2 掉线即时心跳防并发重入）：TryBegin 原子占位（true=获得执行权），End 释放。
    /// 同一时刻至多一个持有者；不排队不缓存——错过的事件由周期心跳兜底（即时心跳只是收敛加速）。
    /// </summary>
    public sealed class SingleFlightGate
    {
        private int _busy;

        /// <summary>原子占位：true=获得执行权；false=已有持有者（本次触发丢弃）。</summary>
        public bool TryBegin() => System.Threading.Interlocked.Exchange(ref _busy, 1) == 0;

        /// <summary>释放（幂等；必须配对调用）。</summary>
        public void End() => System.Threading.Interlocked.Exchange(ref _busy, 0);
    }

    /// <summary>
    /// 实例心跳状态合成器（Codex 审计第 2/4 项 / zcode 心跳契约对齐，第三轮修复房间释放与重租）。
    /// 后端房间绑定与实例状态是权威真相——本地只上报已观察事实，绝不编造：
    /// ① 注册/重注册同步：`Ready`/`Offline` = 后端权威确认实例当前未绑定 → 清除本地房间绑定与 InMatch，
    ///    后续心跳为该状态 + 空 roomCode；`Reserved`/`InMatch` 不凭空猜 roomCode——已有本地绑定暂时保留，
    ///    无绑定时上报对应状态 + 空 roomCode，等待有效票据补齐权威房间码；
    /// ② 有效 consume 返回的非空 roomCode 是后端权威事实：允许把旧房间替换为新房间（实例被释放后重租），
    ///    切换房间时清除旧房 InMatch（默认回到 Reserved）；相同 roomCode 重复通知幂等；空白码不改变绑定；
    /// ③ Day2 预留 InMatch 入口：TryEnterMatch（结构性要求已绑定房间）；
    /// ④ 心跳 409（状态矛盾）：保留连接并重注册同步（见 DedicatedServerBootstrap），绝不本地恢复 Ready。
    /// </summary>
    public sealed class ServerHeartbeatTracker
    {
        public const string StateStarting = "Starting";
        public const string StateReady = "Ready";
        public const string StateReserved = "Reserved";
        public const string StateInMatch = "InMatch";
        public const string StateOffline = "Offline";

        private string _lastSyncedBackendState = string.Empty;
        private string _boundRoomCode = string.Empty;
        private bool _inMatch;
        // 后端 players/disconnect 的 Ready/0 是“允许重臂”信号，不等于本地
        // MatchLifecycle 已完成 Ended→Idle。Bootstrap 在门禁打开前不得发 Ready 心跳，
        // 否则周期/即时心跳会把旧 DS 连接清理窗口与新租约重叠。
        private bool _readyHeartbeatAwaitingRearm;

        public string BoundRoomCode => _boundRoomCode;
        public bool IsRoomBound => !string.IsNullOrEmpty(_boundRoomCode);
        public bool InMatch => _inMatch;
        public bool ReadyHeartbeatAwaitingRearm => _readyHeartbeatAwaitingRearm;

        /// <summary>
        /// 注册/重注册成功：同步后端视角状态。Ready/Offline = 后端权威确认未绑定 → 清除本地房间绑定与 InMatch；
        /// 其余状态（Reserved/InMatch 等）不猜 roomCode——已有绑定保留，无绑定时由 BuildHeartbeat 上报同步态 + 空码。
        /// </summary>
        public void OnRegistered(string backendState)
        {
            string state = string.IsNullOrWhiteSpace(backendState) ? string.Empty : backendState.Trim();
            _lastSyncedBackendState = state;

            if (state == StateReady || state == StateOffline)
            {
                // 后端权威确认实例当前未绑定任何房间：本地旧绑定作废（实例已被释放）
                _boundRoomCode = string.Empty;
                _inMatch = false;
                _readyHeartbeatAwaitingRearm = false;
            }
        }

        /// <summary>
        /// 有效票据 consume 的 roomCode（后端权威事实）：允许替换旧绑定（释放后重租 Room B）；
        /// 切换房间时清除旧房 InMatch（回到 Reserved）；相同码幂等；空白码不创建也不清除绑定。
        /// </summary>
        public void OnRoomBound(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
                return; // 空 roomCode 不得清除或创建绑定

            string normalized = roomCode.Trim();
            if (_boundRoomCode == normalized)
                return; // 幂等：同房间重复通知无副作用

            _boundRoomCode = normalized;
            _inMatch = false; // 换房 = 旧房 InMatch 不再成立，默认回到 Reserved
        }

        /// <summary>
        /// 后端权威释放（P0 租约闭环 2026-09-08）：players/disconnect 成功响应明确
        /// instanceState=Ready 且 remainingPlayers=0——清空本地绑定与 InMatch 并同步为 Ready。
        /// 与 OnRegistered(Ready/Offline) 同一权威清除语义的第二入口；404/409/事实缺失不入此
        ///（上游释放门 IsBackendReleased 拦截，绝不猜 Ready）。
        /// </summary>
        public void OnBackendReleased()
        {
            _lastSyncedBackendState = StateReady;
            _boundRoomCode = string.Empty;
            _inMatch = false;
            _readyHeartbeatAwaitingRearm = true;
        }

        /// <summary>MatchLifecycle 已满足 Ended→Idle 的五条件后允许发 Ready+0 重臂心跳。</summary>
        public void AllowReadyHeartbeatAfterRearm() => _readyHeartbeatAwaitingRearm = false;

        /// <summary>后端掉线上报响应是否构成"实例已释放"权威信号（纯函数，P0 租约闭环）：
        /// 只有明确的 Ready 且剩余 0 才成立——Reserved/InMatch/仍有成员/事实缺失（remainingPlayers&lt;0）
        /// 一律不成立（保留绑定，交由周期心跳/重注册权威纠正）。</summary>
        public static bool IsBackendReleased(string instanceState, int remainingPlayers)
            => instanceState == StateReady && remainingPlayers == 0;

        /// <summary>Day2 预留入口：进入比赛 → 心跳上报 InMatch；未绑定房间时拒绝（结构性防误用）。</summary>
        public bool TryEnterMatch()
        {
            if (!IsRoomBound)
                return false;
            _inMatch = true;
            return true;
        }

        public ServerInstanceHeartbeatRequest BuildHeartbeat(int currentPlayers, string mapId = null)
        {
            string state;
            if (_inMatch)
                state = StateInMatch;
            else if (IsRoomBound)
                state = StateReserved;
            else
                state = string.IsNullOrEmpty(_lastSyncedBackendState)
                    ? StateStarting
                    : _lastSyncedBackendState;

            return new ServerInstanceHeartbeatRequest
            {
                roomCode = _boundRoomCode,
                currentPlayers = currentPlayers,
                state = state,
                mapId = mapId, // Phase 8：随心跳同步地图（后端租用匹配消费；null = 旧 DS 二进制）
            };
        }
    }

    // ============================================================================
    // 入口决策（Codex 第三轮 P0 修复）：检测 Dedicated 模式 ≠ 允许进入显式启动链。
    // Dedicated 模式成立即无条件安装 headless guard（先于 NetworkManager.Start 阶段禁用自动监听）；
    // 参数无效 → guard 保持有效但启动链终止（不 Configure/不监听/不建控制面/不注册/不心跳/无 DS_READY）。
    // ============================================================================

    /// <summary>入口决策结果：None=普通客户端零介入；GuardOnly=只装 headless guard、启动链终止；FullStartup=允许显式启动链。</summary>
    public enum DedicatedEntryDecision
    {
        None,
        GuardOnly,
        FullStartup,
    }

    /// <summary>入口副作用（真实实现见 UnityDedicatedEntryActions；测试注入假实现断言决策顺序）。</summary>
    public interface IDedicatedEntryActions
    {
        /// <summary>创建驱动对象并完成 sceneLoaded guard 订阅（Awake 即订阅——先于任何 Start 阶段）。</summary>
        DedicatedServerBootstrap InstallGuardDriver();

        /// <summary>参数无效的明确错误日志。</summary>
        void LogInvalidParameters(string validationError);

        /// <summary>参数有效的 BOOT 日志。</summary>
        void LogBoot(DedicatedServerOptions options);

        /// <summary>存储启动上下文（只允许有效配置——无效 options 绝不入上下文）。</summary>
        void StoreLaunchContext(DedicatedServerOptions options);
    }

    /// <summary>
    /// 启动入口决策核心（纯逻辑，InitializeOnLoad 的可测形式）：
    /// 先按 IsDedicatedServer 判定模式 → 成立即无条件安装 guard（在 IsValid 判定之前——
    /// 修复"参数无效时提前 return 导致 UNITY_SERVER 构建仍被 NetworkManager.Start() 自动监听"的 P0 竞态），
    /// 再按 IsValid 决定 GuardOnly / FullStartup。
    /// </summary>
    public static class DedicatedServerEntry
    {
        public sealed class ApplyResult
        {
            public DedicatedEntryDecision Decision;
            /// <summary>已安装的 guard 驱动（None 时为 null）。</summary>
            public DedicatedServerBootstrap Driver;
        }

        public static ApplyResult Apply(DedicatedServerOptions options, IDedicatedEntryActions actions)
        {
            if (options == null || !options.IsDedicatedServer)
            {
                // 普通客户端/离线/编辑器进程：零介入——不装 guard、不建驱动、不碰 SetStartOnHeadless
                return new ApplyResult { Decision = DedicatedEntryDecision.None };
            }

            // P0 核心：Dedicated 模式成立即无条件安装 headless guard（先于 IsValid 判定，
            // 保证 UNITY_SERVER 构建在缺参数时也不会被 NetworkManager.Start() 以默认端口自动监听）
            var driver = actions.InstallGuardDriver();

            if (!options.IsValid)
            {
                actions.LogInvalidParameters(options.ValidationError);
                // 启动链终止：不 StoreLaunchContext（上下文不残留无效配置）、不授予 FullStartup
                return new ApplyResult { Decision = DedicatedEntryDecision.GuardOnly, Driver = driver };
            }

            actions.StoreLaunchContext(options);
            actions.LogBoot(options);
            driver.SetEntryMode(DedicatedEntryDecision.FullStartup);
            return new ApplyResult { Decision = DedicatedEntryDecision.FullStartup, Driver = driver };
        }
    }

    // ============================================================================
    // 真实实现（Unity/FishNet 侧；仅由 DedicatedServerBootstrap 消费）
    // ============================================================================

    /// <summary>真实入口副作用：创建 DontDestroyOnLoad 驱动对象 + Awake 即订阅 sceneLoaded guard。</summary>
    public sealed class UnityDedicatedEntryActions : IDedicatedEntryActions
    {
        public DedicatedServerBootstrap InstallGuardDriver()
        {
            var driver = new GameObject("DedicatedServerSystems");
            UnityEngine.Object.DontDestroyOnLoad(driver);
            return driver.AddComponent<DedicatedServerBootstrap>();
        }

        public void LogInvalidParameters(string validationError)
        {
            Debug.LogError($"[DedicatedServer] 参数无效，拒绝启动网络（headless 自动启动已禁用，显式启动链终止）：{validationError}");
        }

        public void LogBoot(DedicatedServerOptions options)
        {
            Debug.Log($"[DedicatedServer] BOOT mode={options.DetectionReason} instance={options.InstanceId} port={options.Port} backend={options.BackendUrl} remoteClientTimeout={options.RemoteClientTimeoutSeconds:0.##}s");
        }

        public void StoreLaunchContext(DedicatedServerOptions options)
        {
            NetworkLaunchContext.ConfigureDedicatedServer(options);
        }
    }

    /// <summary>UnityDedicatedServerRuntime 的场景/对象定位实现。</summary>
    public sealed class UnityDedicatedServerRuntime : IDedicatedServerRuntime
    {
        // Phase 8：目标场景 = 本实例绑定的地图（NetworkLaunchContext.DedicatedServer.MapId →
        // GameMapCatalog 镜像解析）；缺省/未知回退 arena。接口不变（测试 Fake 不受影响）。
        private static string ResolveTargetSceneName()
        {
            var options = NetworkLaunchContext.DedicatedServer;
            return GameMapCatalog.ResolveSceneName(options?.MapId);
        }

        public bool IsArenaActive => SceneManager.GetActiveScene().name == ResolveTargetSceneName();

        public async Task LoadArenaAsync()
        {
            var operation = SceneManager.LoadSceneAsync(ResolveTargetSceneName(), LoadSceneMode.Single);
            while (operation != null && !operation.isDone)
                await Task.Yield();
        }

        public INetworkManagerBinding FindArenaNetworkManager()
        {
            var networkManager = UnityEngine.Object.FindFirstObjectByType<NetworkManager>();
            return networkManager == null ? null : new FishNetNetworkManagerBinding(networkManager);
        }

        public void DisableAutoHeadlessStart(INetworkManagerBinding manager)
        {
            if (manager is FishNetNetworkManagerBinding fishnet)
                fishnet.DisableAutoHeadlessStart();
        }
    }

    /// <summary>FishNet NetworkManager 的 Dedicated 操作句柄实现。</summary>
    public sealed class FishNetNetworkManagerBinding : INetworkManagerBinding
    {
        private readonly NetworkManager _networkManager;

        public FishNetNetworkManagerBinding(NetworkManager networkManager)
        {
            _networkManager = networkManager;
        }

        public event Action<ServerConnectionStateArgs> ServerConnectionState
        {
            add => _networkManager.ServerManager.OnServerConnectionState += value;
            remove => _networkManager.ServerManager.OnServerConnectionState -= value;
        }

        public bool TrySetTransportPort(ushort port, out string transportName)
        {
            var tugboat = _networkManager.TransportManager.Transport as Tugboat;
            if (tugboat == null)
            {
                transportName = _networkManager.TransportManager.Transport == null
                    ? "null"
                    : _networkManager.TransportManager.Transport.GetType().Name;
                return false;
            }
            var bind = System.Environment.GetEnvironmentVariable("FPS_SERVER_BIND");
            if (!string.IsNullOrEmpty(bind))
                tugboat.SetServerBindAddress(bind, FishNet.Transporting.IPAddressType.IPv4);
            tugboat.SetPort(port);
            transportName = nameof(Tugboat);
            return true;
        }

        public bool TrySetRemoteClientTimeout(double seconds, out string transportName)
        {
            var tugboat = _networkManager.TransportManager.Transport as Tugboat;
            if (tugboat == null)
            {
                transportName = _networkManager.TransportManager.Transport == null
                    ? "null"
                    : _networkManager.TransportManager.Transport.GetType().Name;
                return false;
            }
            // Tugboat.SetTimeout 以秒为单位（内部 ×1000 写入 LiteNetLib DisconnectTimeout）；
            // UpdateTimeout 会同时应用 client/server 两个 socket——Dedicated 进程仅服务器侧有远端连接
            tugboat.SetTimeout((float)seconds, asServer: true);
            transportName = nameof(Tugboat);
            return true;
        }

        public JoinTicketAuthenticator WireServerAuthenticator(IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth)
        {
            var authenticator = JoinTicketAuthenticator.EnsureServerAuthenticator(_networkManager, controlPlane, allowUnsafeDebugAuth);
            // SetAuthenticator 负责初始化（InitializeOnce）与 OnAuthenticationResult 订阅——监听前必须完成
            _networkManager.ServerManager.SetAuthenticator(authenticator);
            return authenticator;
        }

        public bool StartServerConnection() => _networkManager.ServerManager.StartConnection();

        public bool StopServerConnection() => _networkManager.ServerManager.StopConnection(true);

        public bool IsClientConnectionActive => _networkManager.IsClientStarted;

        public int ConnectedClientCount => _networkManager.ServerManager.Clients.Count;

        /// <summary>FishNet 公开 API：ServerManager.SetStartOnHeadless(false)（不反射、不改第三方源码）。</summary>
        internal void DisableAutoHeadlessStart() => _networkManager.ServerManager.SetStartOnHeadless(false);
    }
}
