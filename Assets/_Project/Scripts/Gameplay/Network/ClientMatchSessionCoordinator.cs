using System;
using FishNet;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 客户端对局会话协调器（2026-09-15 P0-B/P1，审计 §3.1 生命周期缺陷的修复主体）。
    /// 背景：Arena 的 NetworkSystems 挂 NetworkManager（_dontDestroyOnLoad=1，_persistence=DestroyNewest）——
    /// 第二次进 Arena 时新实例被 FishNet 销毁、旧实例跨场景存活，而连接入口只在 NetworkHud.Start
    /// → 新一局启动上下文无人消费 → 停在加载画面（不发起连接）。
    /// 方案（计划 P0-B 推荐形态）：保留常驻 NetworkManager，本协调器承担应用层、按代际控制的
    /// 客户端启动编排：
    /// ① 消费入口迁出 NetworkHud.Start——本组件挂在自建 DontDestroyOnLoad 宿主上，不受 FishNet
    ///    DestroyNewest 影响，订阅 sceneLoaded 并过滤到启动载荷声明的目标场景；场景就绪即由
    ///    【实际存活的常驻 NetworkManager】消费票据并发起连接；
    /// ② 每局连接前显式复位跨局残留（MatchLifecycle 静态镜像/MatchExitState/MatchConnectionWatcher/
    ///    ClientAuthFailureHandler 策略卡/认证器尝试标记），等上一连接完全 Stopped 才启动新连接；
    /// ③ 分段状态机 SceneReady→Connecting→Connected→Authenticated→OwnerReady→Playing
    ///    （ClientMatchSessionCore），每段独立超时预算；失败/收场为终态——超时经
    ///    ClientAuthFailureHandler.ReportSessionFailure 弹覆盖层并延迟回大厅（等待房间页自动恢复）；
    /// ④ 代际幂等：同代际至多一次，旧会话迟到回调经代际核对丢弃，绝不清新上下文/不断新连接。
    /// DS/离线进程零介入：无客户端启动上下文（纯服务器/离线模式）时本组件完全惰性。
    /// 日志纪律：[ClientSession] 前缀；绝不打印票据明文。
    /// </summary>
    public sealed class ClientMatchSessionCoordinator : MonoBehaviour
    {
        private const float OwnerPollIntervalSeconds = 0.5f;
        private const float OldConnectionStopWaitSeconds = 10f;

        private static ClientMatchSessionCoordinator _instance;
        private static NetworkManager _capturedManager;

        private readonly ClientMatchSessionCore _core = new();
        private NetworkManager _nm;
        private JoinTicketAuthenticator _eventAuth;
        private ClientAuthFailureHandler _failureHandler;
        private bool _managerEventsBound;
        private float _nextOwnerPollRealtime;
        private float _sceneReadyAtRealtime;
        private NetworkLaunchContext.ClientLaunch _activeLaunch;
        private bool _awaitingPreviousStopEvent;

        /// <summary>当前会话核心（只读探测；null = 无协调器宿主）。</summary>
        public static ClientMatchSessionCore CurrentSession => _instance != null ? _instance._core : null;

        /// <summary>
        /// 确保常驻协调器宿主存在（NetworkHud.Awake 每次进 Arena 调用；幂等）。
        /// capturedManager 只在【存活引用】缺位时写入——第二次进 Arena 时 NetworkHud（注定被
        /// DestroyNewest 销毁的实例）也会调用本方法，不得覆盖已捕获的常驻 NetworkManager。
        /// </summary>
        public static ClientMatchSessionCoordinator EnsureHost(NetworkManager capturedManager)
        {
            if (capturedManager != null && _capturedManager == null)
                _capturedManager = capturedManager;
            if (_instance != null) return _instance;
            var host = new GameObject("ClientMatchSessionHost");
            DontDestroyOnLoad(host);
            return host.AddComponent<ClientMatchSessionCoordinator>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
            // 宿主可能在目标场景 Awake 阶段创建（sceneLoaded 尚未发或已发不可知）——立即自查一次；
            // 与 sceneLoaded 双触发的重入由 BeginSession 同代际幂等拦下
            TryBeginSession(SceneManager.GetActiveScene().name);
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnbindEvents();
            _instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryBeginSession(scene.name);
        }

        private void TryBeginSession(string sceneName)
        {
            var launch = NetworkLaunchContext.PeekClientLaunch();
            if (launch == null) return;
            // 场景过滤：载荷声明了目标场景时，非目标场景的加载不消费（上下文保留待目标场景就绪）
            if (!string.IsNullOrEmpty(launch.SceneName)
                && !string.Equals(launch.SceneName, sceneName, StringComparison.Ordinal))
                return;

            launch = NetworkLaunchContext.TryBeginClientLaunch();
            if (launch == null) return; // 竞态：已被消费
            if (!_core.BeginSession(launch.Generation, launch.MatchId,
                    Time.realtimeSinceStartup, launch.ConfiguredAtRealtime))
            {
                Debug.LogWarning($"[ClientSession] BEGIN rejected（同代际重复触发）gen={launch.Generation} scene={sceneName}");
                return;
            }
            _activeLaunch = launch;
            _awaitingPreviousStopEvent = false;
            _sceneReadyAtRealtime = Time.realtimeSinceStartup;
            Debug.Log($"[ClientSession] BEGIN gen={launch.Generation} match={launch.MatchId} scene={sceneName} "
                + $"endpoint={launch.ServerAddress}:{launch.ServerPort} protocol={GameProtocolIdentity.ProtocolId}");
            ResolveManager();
        }

        private void ResolveManager()
        {
            var nm = _capturedManager != null ? _capturedManager : InstanceFinder.NetworkManager;
            if (nm == null)
            {
                Debug.LogError("[ClientSession] NetworkManager 不可用：无法发起对局连接（请检查 Arena 场景 NetworkSystems 配置）");
                _core.MarkCancelled();
                ReportFailureWithOverlay("无法发起对局连接：NetworkManager 不可用（Arena NetworkSystems 配置缺失）");
                return;
            }
            _nm = nm;
            BindManagerEvents(nm);
        }

        private void BindManagerEvents(NetworkManager nm)
        {
            if (_managerEventsBound) return;
            _managerEventsBound = true;
            nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
        }

        private void UnbindEvents()
        {
            if (_eventAuth != null) _eventAuth.ClientResultReceived -= OnAuthResult;
            if (_nm != null) _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            _eventAuth = null;
            _managerEventsBound = false;
        }

        // ---- 每帧驱动：等待旧连接收口 → 分段推进 → 看门狗 ----

        private void Update()
        {
            if (_core.Phase == ClientSessionPhase.Idle || _core.IsTerminal) return;

            if (_core.TickTimeout(Time.realtimeSinceStartup, out var reason))
            {
                HandleSessionFailure(reason);
                return;
            }

            switch (_core.Phase)
            {
                case ClientSessionPhase.SceneReady:
                    if (_nm == null) return; // ResolveManager 已报告失败
                    if (CanBeginConnection(_nm.IsClientStarted, _awaitingPreviousStopEvent))
                    {
                        var launch = _activeLaunch;
                        _activeLaunch = null; // 票据已交认证器（一次性会话）：协调器不保留明文
                        BeginConnection(launch);
                        return;
                    }
                    // 上一局连接仍在收口：等待完全 Stopped（超限主动停——返房链已 StopConnection，
                    // 此处兜底异常滞留；不 Kill 服务器/Host 进程状态）
                    if (Time.realtimeSinceStartup - _sceneReadyAtRealtime > OldConnectionStopWaitSeconds)
                    {
                        if (!_awaitingPreviousStopEvent)
                        {
                            // 标志必须先于 StopConnection：FishNet 的 Stopped 既可能同步也可能异步回调。
                            // 未观察到该事件前绝不启动新连接，避免旧 Stopped 杀死刚进入 Connecting 的新局。
                            _awaitingPreviousStopEvent = true;
                            Debug.LogWarning("[ClientSession] 上一连接未在期限内 Stopped：主动 StopConnection，并等待 Stopped 屏障");
                            _nm.ClientManager.StopConnection();
                        }
                    }
                    break;

                case ClientSessionPhase.Authenticated:
                case ClientSessionPhase.OwnerReady:
                    _core.MarkPlayingIfStable(Time.realtimeSinceStartup);
                    if (_core.Phase == ClientSessionPhase.Authenticated
                        && Time.realtimeSinceStartup >= _nextOwnerPollRealtime)
                    {
                        _nextOwnerPollRealtime = Time.realtimeSinceStartup + OwnerPollIntervalSeconds;
                        if (FindLocalOwnerPlayer() != null)
                        {
                            _core.MarkOwnerReady(Time.realtimeSinceStartup);
                            Debug.Log($"[ClientSession] OWNER_READY gen={_core.Generation} match={_core.MatchId}");
                        }
                    }
                    if (_core.Phase == ClientSessionPhase.Playing)
                        Debug.Log($"[ClientSession] PLAYING gen={_core.Generation} match={_core.MatchId}——进入战场完成");
                    break;
            }
        }

        /// <summary>本局连接启动（SceneReady 且旧连接完全 Stopped 后，单次执行）。</summary>
        private void BeginConnection(NetworkLaunchContext.ClientLaunch launch)
        {
            if (launch == null) return;

            // P0-B §3.2：新会话开启前的跨局残留复位（全清单，与各组件 OnEnable 语义同源）
            Gameplay.Menu.GameplayInputGate.ResetAll();
            var lifecycle = _nm.GetComponent<MatchLifecycle>();
            if (lifecycle == null) lifecycle = _nm.gameObject.AddComponent<MatchLifecycle>();
            lifecycle.ResetMirrorState(); // 内含 MatchExitState.Reset()
            Gameplay.Menu.GameplayMenuController.BeginSession(launch.Generation);

            var watcher = _nm.GetComponent<MatchConnectionWatcher>();
            if (watcher == null) watcher = _nm.gameObject.AddComponent<MatchConnectionWatcher>();
            watcher.ResetSession();

            var authenticator = JoinTicketAuthenticator.EnsureClientAuthenticator(
                _nm, launch.JoinTicket, JoinTicketDebugAuthGuard.IsAllowedInCurrentProcess());
            authenticator.ResetClientSessionState();

            var failureHandler = _nm.GetComponent<ClientAuthFailureHandler>();
            if (failureHandler == null) failureHandler = _nm.gameObject.AddComponent<ClientAuthFailureHandler>();
            _failureHandler = failureHandler;
            _failureHandler.PrepareForNewSession(); // 清上局策略卡/覆盖层，随后 Bind 必然重新接线
            _failureHandler.Bind(authenticator, isClientSession: true);

            if (_eventAuth != authenticator)
            {
                if (_eventAuth != null) _eventAuth.ClientResultReceived -= OnAuthResult;
                authenticator.ClientResultReceived += OnAuthResult;
                _eventAuth = authenticator;
            }

            var tugboat = _nm.TransportManager.Transport as Tugboat;
            if (tugboat != null)
            {
                tugboat.SetClientAddress(launch.ServerAddress);
                tugboat.SetPort(launch.ServerPort);
                tugboat.SetTimeout((float)DedicatedServerOptions.DefaultRemoteClientTimeoutSeconds, asServer: false);
            }

            authenticator.MarkClientConnectionAttempted(); // 先登记再启动（连接从未建立也有降级）
            _core.MarkConnecting(Time.realtimeSinceStartup);
            _nm.ClientManager.StartConnection();
            Debug.Log($"[ClientSession] CONNECTING gen={launch.Generation} match={launch.MatchId} "
                + $"endpoint={launch.ServerAddress}:{launch.ServerPort}");
        }

        // ---- 事件（按当前会话阶段解释；终态后忽略） ----

        private void OnAuthResult(JoinTicketResultBroadcast message)
        {
            if (_core.Phase == ClientSessionPhase.Idle || _core.IsTerminal) return;
            if (message.Accepted)
            {
                _core.MarkAuthenticated(Time.realtimeSinceStartup);
                Debug.Log($"[ClientSession] AUTHENTICATED gen={_core.Generation} match={_core.MatchId}");
            }
            else
            {
                _core.MarkAuthRejected();
                Debug.Log($"[ClientSession] AUTH_REJECTED gen={_core.Generation} code={message.ErrorCode}"
                    + "——UI 由认证失败覆盖层负责");
            }
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (_core.Phase == ClientSessionPhase.Idle || _core.IsTerminal) return;
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                if (_core.Phase == ClientSessionPhase.Connecting)
                {
                    _core.MarkConnected(Time.realtimeSinceStartup);
                    Debug.Log($"[ClientSession] CONNECTED gen={_core.Generation}");
                }
                return;
            }
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            if (_core.Phase == ClientSessionPhase.SceneReady && _awaitingPreviousStopEvent)
            {
                _awaitingPreviousStopEvent = false;
                Debug.Log($"[ClientSession] PREVIOUS_STOP_CONFIRMED gen={_core.Generation}——允许启动本局连接");
                return;
            }

            // 本会话连接阶段（Connecting/Connected）断线 = 未认证断线；
            // 已认证后（Authenticated/OwnerReady/Playing）断线 = 正常收场（对局中断线 UI 归
            // MatchConnectionWatcher 既有链路）；SceneReady 阶段的 Stopped 属于上一局连接的
            // 收尾事件，与新会话无关，必须忽略——否则会把刚开启的新会话误判为已收场
            if (_core.Phase == ClientSessionPhase.Connecting || _core.Phase == ClientSessionPhase.Connected)
            {
                _core.MarkConnectionLostBeforeAuth();
                Debug.LogWarning($"[ClientSession] CONNECTION_LOST_BEFORE_AUTH gen={_core.Generation}"
                    + "——UI 由认证失败覆盖层负责");
            }
            else if (_core.Phase == ClientSessionPhase.Authenticated
                || _core.Phase == ClientSessionPhase.OwnerReady
                || _core.Phase == ClientSessionPhase.Playing)
            {
                var phaseAtEvent = _core.Phase;
                _core.MarkEnded();
                Debug.LogWarning($"[ClientSession] ENDED_BY_DISCONNECT gen={_core.Generation} phase={phaseAtEvent}");
            }
        }

        /// <summary>旧连接 Stopped 屏障：仅 IsClientStarted=false 不足以证明其迟到事件已经送达。</summary>
        public static bool CanBeginConnection(bool isClientStarted, bool awaitingPreviousStopEvent)
            => !isClientStarted && !awaitingPreviousStopEvent;

        // ---- 失败/收场 ----

        private void HandleSessionFailure(ClientSessionFailureReason reason)
        {
            if (_core.Phase != ClientSessionPhase.Failed) return; // 只解释核心已判定的失败
            Debug.LogWarning($"[ClientSession] FAILED gen={_core.Generation} match={_core.MatchId} reason={reason}");

            switch (reason)
            {
                case ClientSessionFailureReason.TimeoutOverall:
                case ClientSessionFailureReason.TimeoutConnectStarted:
                case ClientSessionFailureReason.TimeoutAuthenticated:
                case ClientSessionFailureReason.TimeoutOwnerReady:
                    ReportFailureWithOverlay(ClientMatchSessionCore.DescribeFailure(reason));
                    break;
                case ClientSessionFailureReason.ConnectionLostBeforeAuth:
                case ClientSessionFailureReason.AuthRejected:
                    // 具体文案/返回由认证失败覆盖层负责（TriggerFailure 同一套收口：清上下文→断连接→回大厅）
                    break;
                case ClientSessionFailureReason.Cancelled:
                    break; // 调用方自管文案（CancelSession 内部已弹覆盖层）
                default:
                    ReportFailureWithOverlay(ClientMatchSessionCore.DescribeFailure(reason));
                    break;
            }
        }

        private void ReportFailureWithOverlay(string message)
        {
            if (_failureHandler != null)
            {
                _failureHandler.ReportSessionFailure(message); // 含清上下文/断连接/覆盖层/延迟回大厅
                return;
            }
            // 无覆盖层兜底（理论不可达）：仍完成失败收口
            Debug.LogError($"[ClientSession] failure without overlay handler: {message}");
            NetworkLaunchContext.Clear();
            var nm = _nm != null ? _nm : InstanceFinder.NetworkManager;
            if (nm != null && nm.ClientManager != null && nm.IsClientStarted)
                nm.ClientManager.StopConnection();
            Gameplay.Menu.GameplayMenuController.ReturnToLobbyLocally();
        }

        /// <summary>
        /// 主动取消当前会话（UI 侧整体超时/用户返回时调用；幂等）。
        /// 收口与失败同一套：清本代际票据 → 断本次连接 → 覆盖层提示 → 延迟回大厅。
        /// </summary>
        public static void CancelSession(string message)
        {
            var instance = _instance;
            if (instance == null || instance._core.Phase == ClientSessionPhase.Idle) return;
            instance._core.MarkCancelled();
            Debug.LogWarning($"[ClientSession] CANCELLED gen={instance._core.Generation}——{message}");
            instance.ReportFailureWithOverlay(message);
        }

        private static NetworkCombatAuthority FindLocalOwnerPlayer()
        {
            foreach (var player in UnityEngine.Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
                if (player.IsOwnerPlayer) return player;
            return null;
        }
    }
}
