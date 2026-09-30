using System;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 一次性启动上下文（Docs/27 Day1 §2.5 冻结接缝，Game.Gameplay 内、不依赖 Game.Account）：
    /// NetworkLaunchContext.ConfigureClient(address, port, joinTicket)
    /// NetworkLaunchContext.ConfigureDedicatedServer(options)
    /// NetworkLaunchContext.Clear()
    /// Lobby/上层页面（Day2 zcode 接线）在加载 Arena 前调用 ConfigureClient；
    /// ClientMatchSessionCoordinator 在「Arena 场景就绪」后按代际消费并自动连接（2026-09-15 P0-B：
    /// 消费入口从 NetworkHud.Start 迁出——NetworkManager 跨场景常驻后第二次进 Arena 时新实例被
    /// DestroyNewest 销毁，其 Start 永不生效，必须由不受 FishNet 持久化逻辑影响的常驻协调器消费）。
    /// 消费即清空：明文票据只在「配置 → 消费」窗口内停留，消费后本类不再持有；
    /// 断线后的明文清理由消费方（JoinTicketAuthenticator 客户端侧）负责。
    /// 2026-09-15 P0-B/P1 扩展：每次 ConfigureClient 分配递增 Generation（客户端会话代际，
    /// 与 AccountSession.ConnectionGeneration 同步由调用方保证）+ 携带目标场景名/matchId
    /// （场景过滤 + 日志归属）+ ConfiguredAtRealtime（实时超时从配置点起算，而非连接调用点）。
    /// </summary>
    public static class NetworkLaunchContext
    {
        /// <summary>客户端启动载荷（地址 + 端口 + 一次性 join ticket + 会话代际元数据）。</summary>
        public sealed class ClientLaunch
        {
            public string ServerAddress;
            public ushort ServerPort;
            public string JoinTicket;

            /// <summary>本连接代际（ConfigureClient 内部递增分配；同一代际至多消费/启动一次）。</summary>
            public long Generation;

            /// <summary>目标战斗场景名（空 = 不过滤，任何场景就绪即消费——兼容旧调用）。</summary>
            public string SceneName;

            /// <summary>后端权威比赛 id（仅日志归属/审计用，不参与连接）。</summary>
            public string MatchId;

            /// <summary>配置时刻的 Time.realtimeSinceStartup（P1：从配置起算整体超时预算）。</summary>
            public float ConfiguredAtRealtime;
        }

        private static ClientLaunch _pendingClient;
        private static DedicatedServerOptions _serverOptions;
        private static long _nextGeneration;

        /// <summary>是否有待消费的客户端启动（只读探测，不消费）。</summary>
        public static bool HasPendingClientLaunch => _pendingClient != null;

        /// <summary>Dedicated Server 启动参数（服务器引导自取；客户端进程恒为 null）。</summary>
        public static DedicatedServerOptions DedicatedServer => _serverOptions;

        /// <summary>当前已分配的最新代际（ConfigureClient 递增；无历史配置时为 0）。</summary>
        public static long CurrentGeneration { get; private set; }
        public static string CurrentGameplayScene { get; private set; }

        /// <summary>上层页面在进入 Arena 前配置客户端连接；重复配置以最后一次为准（代际随之递增）。</summary>
        public static void ConfigureClient(
            string serverAddress, ushort serverPort, string joinTicket,
            string sceneName = null, string matchId = null)
        {
            _nextGeneration++;
            CurrentGeneration = _nextGeneration;
            CurrentGameplayScene = sceneName;
            _pendingClient = new ClientLaunch
            {
                ServerAddress = serverAddress,
                ServerPort = serverPort,
                JoinTicket = joinTicket,
                Generation = _nextGeneration,
                SceneName = sceneName ?? string.Empty,
                MatchId = matchId ?? string.Empty,
                ConfiguredAtRealtime = UnityEngine.Time.realtimeSinceStartup,
            };
        }

        /// <summary>DedicatedServerBootstrap 服务器路径专用；客户端进程不调用。</summary>
        public static void ConfigureDedicatedServer(DedicatedServerOptions options)
        {
            _serverOptions = options;
        }

        /// <summary>只读探测待处理启动载荷（不消费）。协调器用于场景过滤后决定是否消费。</summary>
        public static ClientLaunch PeekClientLaunch() => _pendingClient;

        /// <summary>
        /// 取走并立即清空待处理客户端启动（单次消费）。无待处理时返回 null。
        /// 调用方拿到 ClientLaunch 后即承担票据明文的保管与清理责任。
        /// </summary>
        public static ClientLaunch TryBeginClientLaunch()
        {
            var launch = _pendingClient;
            _pendingClient = null;
            return launch;
        }

        /// <summary>清空全部状态（客户端票据 + 服务器参数；代际计数保留——单调递增防复用）。
        /// 断线/失败/测试清理共用。</summary>
        public static void Clear()
        {
            _pendingClient = null;
            _serverOptions = null;
        }
    }
}
