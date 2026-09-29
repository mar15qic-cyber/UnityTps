using System;
using System.Threading.Tasks;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// Dedicated Server 启动引导（Docs/27 Day1 §4.1 C1 + Codex 审计修复 2026-09-06 两轮）。零场景资产改动。
    /// 入口决策（第三轮 P0 修复）：InitializeOnLoad 只做一次薄委托——
    ///   DedicatedServerEntry.Apply：Dedicated 模式成立即【无条件】安装 headless guard（建驱动对象 +
    ///   Awake 订阅 sceneLoaded），参数无效时 guard 保持有效但启动链终止（GuardOnly：不 Configure、
    ///   不监听、不建控制面、不注册、不心跳、无 DS_READY，只打印明确参数错误）；
    ///   参数有效才授予 FullStartup 并存储 NetworkLaunchContext（无效配置绝不入上下文）。
    /// 启动时序（第二轮 P0 修复——不依赖两个 Start 的偶然顺序）：
    ///   sceneLoaded（先于任何 Start 阶段）：Arena 就位 → SetStartOnHeadless(false)（FishNet 公开 API，
    ///   NetworkManager.Start() 的 StartForHeadless 从此空转——缺参数时同样生效）；
    ///   Start：RunStartupChainAsync（GuardOnly 门控 → 配置 → 先订阅事件 → TryBeginListen 唯一监听入口）；
    ///   Started 事件 → ServerListenGate.Ready → DS_READY → 注册/心跳。
    /// 红线：服务器路径绝不调用 ClientManager.StartConnection()、绝不生成本地 Owner Player；
    /// 普通客户端/离线进程本类零介入（Apply 返回 None：不建驱动、不碰 SetStartOnHeadless）。
    /// 日志标记（§6）：DS_READY / REGISTERED / ROOM_BOUND / ROOM_REBOUND；
    /// 失败标记：SERVER_CONFIG_FAILED / SERVER_START_FAILED / INVARIANT VIOLATED / 参数无效。
    /// </summary>
    public sealed class DedicatedServerBootstrap : MonoBehaviour
    {
        public const string ArenaSceneName = "Arena";

        private const float RegisterRetrySeconds = 10f;
        private const float DefaultHeartbeatSeconds = 15f;
        private const float ServerStartWatchdogSeconds = 20f;

        private DedicatedServerOptions _options;
        private IServerControlPlaneClient _controlPlane;
        private IDedicatedServerRuntime _runtime;
        private INetworkManagerBinding _manager;
        private JoinTicketAuthenticator _authenticator;
        private readonly ServerHeartbeatTracker _heartbeatTracker = new();
        private readonly ServerListenGate _listenGate = new();
        private readonly PlayerDisconnectQueue _disconnectQueue = new();
        /// <summary>终局上报持久补偿队列（2026-09-13）：有界重试耗尽落盘，运行期/重启后重放。
        /// F10（2026-09-19 审计）：不得在 MonoBehaviour 字段初始化器构造（读 persistentDataPath
        /// 非法）——在 RunStartupChainAsync 拿到有效 options 后按实例隔离目录创建。</summary>
        private MatchResultPendingStore _pendingMatchResults;
        /// <summary>掉线上报泵单飞闸（并发通知只允许一个排水循环在途；新条目由在途循环 CollectPending 拾起）。</summary>
        private readonly SingleFlightGate _disconnectDrainGate = new();
        private readonly SingleFlightGate _resultDrainGate = new();
        private bool _transportUnavailable;
        private bool _destroyed;
        private float _heartbeatIntervalSeconds = DefaultHeartbeatSeconds;
        private bool _headlessGuardApplied;
        private DedicatedEntryDecision _entryMode = DedicatedEntryDecision.GuardOnly;

        /// <summary>入口模式：默认 GuardOnly（只装 headless guard、启动链终止）——FullStartup 必须由 DedicatedServerEntry.Apply 显式授予。</summary>
        public DedicatedEntryDecision EntryMode => _entryMode;

        /// <summary>显式启动链准入（Start 的唯一逻辑门）。参数无效（GuardOnly）时恒 false。</summary>
        public bool ShouldRunExplicitStartupChain => _entryMode == DedicatedEntryDecision.FullStartup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeOnLoad()
        {
            // 薄委托：全部入口决策在 DedicatedServerEntry.Apply（可测纯逻辑）内完成——
            // Guard 安装先于 IsValid 判定，堵死"缺参数时提前 return → UNITY_SERVER 自动监听"竞态
            DedicatedServerEntry.Apply(
                DedicatedServerOptions.ParseFromCommandLine(),
                new UnityDedicatedEntryActions());
        }

        /// <summary>入口授予（仅 DedicatedServerEntry.Apply 调用；测试亦可直调断言模式语义）。</summary>
        public void SetEntryMode(DedicatedEntryDecision mode)
        {
            _entryMode = mode;
        }

        /// <summary>测试接缝：注入假 runtime 以断言启动链行为（真实路径由 Awake 注入 UnityDedicatedServerRuntime）。</summary>
        public void InjectRuntimeForTests(IDedicatedServerRuntime runtime)
        {
            _runtime = runtime;
        }

        private void Awake()
        {
            _runtime = new UnityDedicatedServerRuntime();
            // 关键时序：sceneLoaded 回调先于任何场景对象的 Start 阶段——这是
            // NetworkManager.Start()（内部 StartForHeadless 自动监听）之前的可靠禁用点
            SceneManager.sceneLoaded += OnSceneLoadedForHeadlessGuard;
        }

        private void OnDestroy()
        {
            _destroyed = true;
            SceneManager.sceneLoaded -= OnSceneLoadedForHeadlessGuard;
            // Day2 生命周期闭环（接管契约 §2）：静态事件订阅对称解绑——防止重复初始化后
            // OnServerMatchInProgress/OnServerMatchEnded 多次触发 TryEnterMatch
            UnwireMatchLifecycleHooks();
        }

        // ---- MatchLifecycle 静态事件接线（对称解绑 + 单次接线闸，接管契约 §2） ----

        private bool _matchLifecycleHooksWired;

        /// <summary>接线（幂等：单实例接线闸保证静态事件至多一个订阅者——恰一次 TryEnterMatch）。</summary>
        private void WireMatchLifecycleHooks()
        {
            if (_matchLifecycleHooksWired)
                return;
            _matchLifecycleHooksWired = true;
            MatchLifecycle.OnServerMatchInProgress += HandleServerMatchInProgress;
            MatchLifecycle.OnServerMatchEnded += HandleServerMatchEnded;
            MatchLifecycle.OnServerMatchResultReady += HandleServerMatchResultReady; // C3/Q05 终局上报
            MatchLifecycle.OnServerRearmed += HandleServerRearmed;
        }

        /// <summary>对称解绑（OnDestroy/重接线前调用；与 Wire 严格配对）。</summary>
        private void UnwireMatchLifecycleHooks()
        {
            if (!_matchLifecycleHooksWired)
                return;
            _matchLifecycleHooksWired = false;
            MatchLifecycle.OnServerMatchInProgress -= HandleServerMatchInProgress;
            MatchLifecycle.OnServerMatchEnded -= HandleServerMatchEnded;
            MatchLifecycle.OnServerMatchResultReady -= HandleServerMatchResultReady;
            MatchLifecycle.OnServerRearmed -= HandleServerRearmed;
        }

        /// <summary>NetworkManager.Start 阶段之前的 headless 自动启动禁用（P0 修复核心；GuardOnly 模式同样生效）。
        /// Phase 8：不再限定 Arena——守卫幂等 + NM 空路径可重试，任意场景加载（含 Boot/Lobby/新地图）
        /// 都触发尝试；目标场景由 ConfigureAsync 按 mapId 决定（GameMapCatalog 镜像解析）。</summary>
        private void OnSceneLoadedForHeadlessGuard(Scene scene, LoadSceneMode mode)
        {
            if (_headlessGuardApplied)
                return;

            ApplyHeadlessGuardToCurrentArena();
        }

        /// <summary>
        /// headless guard 主体（sceneLoaded 直调；测试接缝直调以断言禁用调用发生且恰一次）。
        /// 幂等：首次应用后重复调用为空操作。
        /// </summary>
        public bool ApplyHeadlessGuardToCurrentArena()
        {
            if (_headlessGuardApplied)
                return true;

            var manager = _runtime.FindArenaNetworkManager();
            if (manager == null)
            {
                // 未置位：NetworkManager 就位后（后续 sceneLoaded/配置链）可重试
                Debug.LogWarning("[DedicatedServer] headless guard: 未找到 NetworkManager——启动链将显式失败（自动监听禁用待 NetworkManager 就位后复证）");
                return false;
            }
            _headlessGuardApplied = true;
            _runtime.DisableAutoHeadlessStart(manager);
            Debug.Log("[DedicatedServer] headless auto-start disabled (SetStartOnHeadless=false) before NetworkManager Start phase");
            return true;
        }

        private async void Start()
        {
            try
            {
                await RunStartupChainAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[DedicatedServer] bootstrap aborted (see exception above)");
            }
        }

        /// <summary>
        /// 显式启动链（Start 的主体；测试接缝可直调 GuardOnly 路径断言零链副作用）。
        /// GuardOnly 门控在最前：参数无效时绝不 Configure、不监听、不建控制面、不注册、不心跳、无 DS_READY。
        /// </summary>
        public async Task RunStartupChainAsync()
        {
            if (!ShouldRunExplicitStartupChain)
            {
                Debug.LogWarning($"[DedicatedServer] startup chain terminated (entry mode={_entryMode})——不 Configure、不监听、不注册、不心跳");
                return;
            }

            _options = NetworkLaunchContext.DedicatedServer;
            if (_options == null || !_options.IsValid)
            {
                // 防御：FullStartup 被授予但上下文缺失/无效（不应发生——Apply 只在有效时存储）
                Debug.LogError($"[DedicatedServer] SERVER_CONFIG_FAILED: 启动上下文缺失或无效（{(_options == null ? "null" : _options.ValidationError)}）——不监听、不注册、不心跳");
                return;
            }

            // F09/F10（2026-09-19 审计）：补偿队列在 options 就绪后的主线程生命周期阶段创建，
            // 目录按 <persistent>/server-results/<env指纹>/<instanceId>/ 隔离（五实例不再共用
            // 同一文件互相覆盖）；共享遗留文件由首个启动的服务器实例收养一次（备份留痕）。
            _pendingMatchResults = CreatePendingMatchResultsStore(_options);

            // P0-A 部署证据（启动即打印——启动器部署门按此行比对"运行进程 vs 目标构建"）：
            // 协议代际 + 本进程部署清单（buildId/构建时间/DLL 哈希摘要；编辑器内无清单 → <none>）
            var deployedManifest = GameProtocolIdentity.TryReadDeployedManifest();
            Debug.Log($"[DedicatedServer] APP_PROTOCOL id={GameProtocolIdentity.ProtocolId} pid={System.Diagnostics.Process.GetCurrentProcess().Id}"
                + $" buildId={(deployedManifest != null ? deployedManifest.buildId : "<none>")}"
                + $" builtAt={(deployedManifest != null ? deployedManifest.builtAtUtc : "<none>")}"
                + $" gameplayDll={(deployedManifest != null ? deployedManifest.gamePlayDllSha256 : "<none>")}");

            _controlPlane = new UnityWebServerControlPlaneClient(
                _options.BackendUrl, _options.InstanceId, _options.ServerKey);

            // 配置阶段（不监听）：Arena → NetworkManager → 禁 headless（幂等）→ 端口 → 认证器
            var configure = await DedicatedServerStartup.ConfigureAsync(_runtime, _options, _controlPlane);
            if (configure.Outcome != DedicatedServerStartup.ConfigureOutcome.Configured)
            {
                Debug.LogError($"[DedicatedServer] SERVER_CONFIG_FAILED: {configure.Reason}——不监听、不注册、不心跳");
                return;
            }
            _manager = configure.Manager;
            _authenticator = configure.Authenticator;
            // 有效票据 consume 的 roomCode → 房间绑定/重绑（心跳从此时上报 Reserved + 已认证 roomCode）
            _authenticator.TicketAccepted += OnTicketAccepted;
            // Day2 掉线生命周期：远端连接停止（主动退出/超时/被踢）且认证档案清理完成后 → 即时心跳，
            // 让后端 CurrentPlayers 事实不等下一个心跳周期（被杀客户端收敛目标 = timeout + 调度余量）
            _authenticator.ClientConnectionCleaned += OnClientConnectionCleaned;
            // Day2 生命周期闭环（任务 A ① + 接管契约 §2）：正式比赛 InProgress/Ended → 生命周期证据
            //（TryEnterMatch 心跳 InMatch / 旧局房间码快照）；接线幂等 + OnDestroy 对称解绑
            WireMatchLifecycleHooks();

            // 先订阅再启动：确保 Started/Stopped 事件不丢（gate 是 DS_READY/注册的唯一准入）
            _manager.ServerConnectionState += OnServerConnectionState;

            if (!DedicatedServerStartup.TryBeginListen(_manager, out string listenReason))
            {
                Debug.LogError($"[DedicatedServer] SERVER_START_FAILED instance={_options.InstanceId} port={_options.Port}: {listenReason}——不注册、不心跳");
                return;
            }

            // Dedicated 不变量运行时自证（结构性保证之外的双保险）
            if (_manager.IsClientConnectionActive)
            {
                Debug.LogError("[DedicatedServer] INVARIANT VIOLATED: client connection active on dedicated path——立即停止服务器");
                _manager.StopServerConnection();
                return;
            }

            // watchdog：Started 未在期限内到达即视为启动失败（不注册、不心跳）
            float deadline = Time.unscaledTime + ServerStartWatchdogSeconds;
            while (!_listenGate.Concluded && Time.unscaledTime < deadline)
                await Task.Yield();
            if (!_listenGate.Concluded)
                _listenGate.OnWatchdogExpired();

            if (_listenGate.Failed)
            {
                Debug.LogError($"[DedicatedServer] SERVER_START_FAILED instance={_options.InstanceId} port={_options.Port}: {_listenGate.FailureReason}——不注册、不心跳");
                _manager.StopServerConnection();
                return;
            }

            // Ready（DS_READY 已在 OnServerConnectionState 首次 Started 时打印）
            await RegisterAndHeartbeatAsync();
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            _transportUnavailable = args.ConnectionState != LocalConnectionState.Started;
            bool wasConcluded = _listenGate.Concluded;
            _listenGate.OnConnectionState(args.ConnectionState);

            if (!wasConcluded && _listenGate.Ready)
            {
                // DS_READY 只在真实启动成功（Transport Started 事件）后出现一次
                Debug.Log($"[DedicatedServer] DS_READY instance={_options.InstanceId} port={_options.Port} scene={ArenaSceneName}");
            }
            else if (wasConcluded && _listenGate.Ready && args.ConnectionState == LocalConnectionState.Stopped)
            {
                // Ready 后的停服属 Day2 比赛生命周期（当前仅记录，心跳继续上报事实）
                Debug.LogWarning("[DedicatedServer] transport stopped: Ready announcements suspended until listening resumes");
                if (!_heartbeatTracker.IsRoomBound) _ = ReportStoppedTransportAsync();
            }
        }

        private async Task ReportStoppedTransportAsync()
        {
            if (_controlPlane == null || _manager == null || _options == null) return;
            try
            {
                var request = _heartbeatTracker.BuildHeartbeat(_manager.ConnectedClientCount, _options.MapId);
                request.state = "Offline";
                await _controlPlane.HeartbeatAsync(request);
            }
            catch (Exception e) { Debug.LogWarning("[ServerRegistry] offline report failed: " + e.Message); }
        }

        private async Task RegisterAndHeartbeatAsync()
        {
            while (!_destroyed)
            {
                if (_transportUnavailable)
                {
                    await Task.Delay(TimeSpan.FromSeconds(RegisterRetrySeconds));
                    continue;
                }
                if (!await TryRegisterOnceAsync())
                {
                    await Task.Delay(TimeSpan.FromSeconds(RegisterRetrySeconds));
                    continue;
                }
                // StateConflict（409）时返回重注册；其余循环心跳
                await HeartbeatUntilConflictAsync();
            }
        }

        /// <summary>
        /// 正式比赛进入 InProgress（任务 A ①）：推进心跳状态机进入 InMatch——后端实例与房间
        /// 状态随实例心跳同步为 InMatch（zcode 心跳契约的合法阶梯 Reserved→InMatch）。
        /// </summary>
        private void HandleServerMatchInProgress()
        {
            DedicatedServerLifecycle.OnServerMatchInProgress(_heartbeatTracker);
        }

        /// <summary>
        /// 服务器比赛终局（接管契约 §1.2）：记录旧局绑定的房间码快照并作废既有重臂证据——
        /// 下一局重臂必须持有全新租约代际证明。
        /// </summary>
        private void HandleServerMatchEnded()
        {
            DedicatedServerLifecycle.OnServerMatchEnded(_heartbeatTracker.BoundRoomCode);
        }

        // ---- C3/Q05：DS 权威终局上报（POST match-result，X-Server-Key） ----

        private const int MatchResultReportMaxAttempts = 3;
        private const float MatchResultReportRetrySeconds = 5f;

        /// <summary>终局结果上报入口（幂等闸 + 载荷构建；交付与补偿见 DeliverMatchResultAsync）。</summary>
        private void HandleServerMatchResultReady(MatchLifecycle.MatchEndedPayload payload)
        {
            if (_controlPlane is not IServerMatchResultReporter reporter || payload == null)
                return;
            if (string.IsNullOrEmpty(payload.matchId) || payload.players == null || payload.players.Length == 0)
                return; // 离线/本地调试局：无权威比赛身份，不上报

            var rows = new System.Collections.Generic.List<ServerMatchResultPlayerRow>(payload.players.Length);
            foreach (var player in payload.players)
            {
                if (player == null || !long.TryParse(player.playerId, out long userId))
                    continue; // 非数字 id = 调试旁路/未知身份，不进后端结算名单
                rows.Add(new ServerMatchResultPlayerRow
                {
                    userId = userId,
                    teamId = string.IsNullOrEmpty(player.teamId) ? MatchRules.TeamNone : player.teamId,
                    kills = player.kills,
                    deaths = player.deaths,
                    assists = player.assists,
                    participationSeconds = player.participationSeconds >= 0
                        ? player.participationSeconds
                        : Mathf.RoundToInt(Mathf.Max(0f, payload.durationSeconds)),
                    rewardEligible = player.rewardEligible,
                    leftAtSeconds = 0,
                    isWin = player.isWin, // R2：逐玩家胜负随上报携带（KillRace 个人胜者 winnerTeam=null 时的唯一载体）
                });
            }
            if (rows.Count == 0)
            {
                Debug.LogWarning("[ServerRegistry] MATCH_RESULT_SKIPPED：终局载荷无可结算的权威身份玩家");
                return;
            }

            var request = new ServerMatchResultReportRequest
            {
                matchId = payload.matchId,
                durationSeconds = Mathf.RoundToInt(Mathf.Max(0f, payload.durationSeconds)),
                // A01（V0）：无胜队（KillRace/平局）序列化为 null——后端按 null 接受；
                // 空串是旧行为（曾触发后端 422"胜队标识非法"），后端现也归一空白为 null 双保险。
                winnerTeam = string.IsNullOrEmpty(payload.winnerTeam) ? null : payload.winnerTeam,
                players = rows.ToArray(),
            };
            _ = DeliverMatchResultAsync(reporter, request);
        }

        /// <summary>F09/F10：按实例隔离目录创建补偿队列（options 已验证；路径消毒+环境指纹见 store）。</summary>
        private static MatchResultPendingStore CreatePendingMatchResultsStore(DedicatedServerOptions options)
        {
            var filePath = MatchResultPendingStore.BuildInstanceFilePath(
                Application.persistentDataPath, options.BackendUrl, options.InstanceId);
            MatchResultPendingStore.AdoptLegacySharedFileIfFirstBoot(Application.persistentDataPath, filePath);
            return new MatchResultPendingStore(filePath);
        }

        /// <summary>交付 + 补偿（2026-09-13）：交付失败落盘 MatchResultPendingStore，
        /// 注册成功等时机由 FlushPendingMatchResultsAsync 重放（幂等 by matchId）。</summary>
        private async Task DeliverMatchResultAsync(IServerMatchResultReporter reporter, ServerMatchResultReportRequest request)
        {
            // Persist before the first await: a crash during delivery must leave a replayable payload.
            _pendingMatchResults.Append(request);
            if (!_resultDrainGate.TryBegin()) return; // Persisted; the periodic pump will collect it.
            try
            {
                if (await ReportMatchResultAsync(reporter, request))
                {
                    _pendingMatchResults.Remove(request.matchId);
                    return;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_DELIVER_FAULT match={request.matchId}: {exception.Message}");
            }
            finally { _resultDrainGate.End(); }
            if (_pendingMatchResults.LastSaveSucceeded)
                Debug.LogError($"[ServerRegistry] MATCH_RESULT_PERSISTED_FOR_COMPENSATION match={request.matchId} pending={_pendingMatchResults.Count}——已落盘，后端恢复后自动重放");
            else
                Debug.LogError($"[ServerRegistry] MATCH_RESULT_PENDING_SAVE_FAILED match={request.matchId} pending={_pendingMatchResults.Count}——磁盘写入失败，未持久化（保留内存待重试）");
        }

        /// <summary>
        /// 终局结果上报（幂等 by matchId；离线/F1 调试无 matchId 或无上报能力时零副作用）：
        /// 传输失败有界重试 3 次；409/404 权威终态不重试（绝不与后端权威状态对抗）。
        /// 返回 true=终态闭环（后端接受或权威冲突）；false=耗尽丢弃——调用方落盘补偿。
        /// </summary>
        private async Task<bool> ReportMatchResultAsync(IServerMatchResultReporter reporter, ServerMatchResultReportRequest request)
        {
            for (int attempt = 1; attempt <= MatchResultReportMaxAttempts; attempt++)
            {
                if (_destroyed) return false;
                try
                {
                    var outcome = await reporter.ReportMatchResultAsync(request);
                    if (outcome == MatchResultReportOutcome.Accepted)
                    {
                        Debug.Log($"[ServerRegistry] MATCH_RESULT_REPORTED match={request.matchId} players={request.players.Length}");
                        return true;
                    }
                    if (outcome == MatchResultReportOutcome.StateConflict)
                    {
                        Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_CONFLICT match={request.matchId}——后端权威拒绝（终态，不重试）");
                        return true;
                    }
                    if (outcome == MatchResultReportOutcome.RewardsPending)
                    {
                        // R3 审计修复：已登记但 rewardsApplied=false（部分玩家发奖失败）——不能当成功收尾，
                        // 重试上报走幂等重放补发（后端重放路径按持久化结果重新套用结算引擎）
                        Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_REWARDS_PENDING match={request.matchId} attempt={attempt}——奖励有缺，{MatchResultReportRetrySeconds:0}s 后重放补发");
                    }
                    else
                    {
                        Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_RETRY match={request.matchId} attempt={attempt}——后端不可达，{MatchResultReportRetrySeconds:0}s 后补交");
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_RETRY match={request.matchId} attempt={attempt}: {exception.Message}");
                }
                if (attempt < MatchResultReportMaxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(MatchResultReportRetrySeconds));
            }
            Debug.LogError($"[ServerRegistry] MATCH_RESULT_DROPPED match={request.matchId}——重试 {MatchResultReportMaxAttempts} 次仍不可达/未补齐，转持久补偿队列（后端恢复后重放，幂等 by matchId）");
            return false;
        }

        /// <summary>补偿重放（注册成功等时机调用）：逐条重发落盘原始请求（来源校验不放松），
        /// 终态（Accepted/StateConflict）移除；其余保留待下轮。失败不影响注册主链。</summary>
        private async Task FlushPendingMatchResultsAsync()
        {
            if (_controlPlane is not IServerMatchResultReporter reporter) return;
            if (_pendingMatchResults == null || _pendingMatchResults.Count == 0 || !_resultDrainGate.TryBegin()) return;
            try
            {
                _pendingMatchResults.EnsureSaved(); // F09：补偿泵每轮重试上次失败的落盘
                Debug.Log($"[ServerRegistry] MATCH_RESULT_COMPENSATION_FLUSH pending={_pendingMatchResults.Count}");
                int attempted = 0;
                foreach (var request in _pendingMatchResults.CollectSnapshot())
                {
                    if (_destroyed || attempted++ >= 8) return;
                    try
                    {
                        var outcome = await reporter.ReportMatchResultAsync(request);
                        if (outcome is MatchResultReportOutcome.Accepted or MatchResultReportOutcome.StateConflict)
                        {
                            _pendingMatchResults.Remove(request.matchId);
                            Debug.Log($"[ServerRegistry] MATCH_RESULT_COMPENSATED match={request.matchId} outcome={outcome} pending={_pendingMatchResults.Count}");
                        }
                        else
                        {
                            return; // The heartbeat interval provides backoff; do not stall the pump.
                        }
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"[ServerRegistry] MATCH_RESULT_COMPENSATION_RETRY match={request.matchId}: {exception.Message}");
                        return; // 后端仍不可达：等下一次注册/心跳周期
                    }
                }
            }
            finally { _resultDrainGate.End(); }
        }

        private async Task<bool> TryRegisterOnceAsync()
        {
            var request = new ServerInstanceRegisterRequest
            {
                instanceId = _options.InstanceId,
                address = _options.PublicAddress,
                port = _options.Port,
                capacity = _options.Capacity,
                buildVersion = _options.BuildVersion,
                // P0-A：应用协议代际——后端按房间协议期望筛选可租实例（旧客户端看不到新 DS）
                protocolId = GameProtocolIdentity.ProtocolId,
                // Phase 8：本实例绑定地图——后端租用查询按房间 mapId 匹配（不匹配实例不被租用）
                mapId = _options.MapId,
            };

            try
            {
                var response = await _controlPlane.RegisterAsync(request);
                // 后端为权威真相：同步状态（Ready/Offline=权威确认未绑定 → tracker 清除本地旧绑定）
                _heartbeatTracker.OnRegistered(response.state);
                // Day2 生命周期闭环（接管契约 §1.2 证据 (a)）：仅注册 ack 明确 Ready/Offline 且
                // tracker 旧 roomCode 已清除才构成释放证据；Reserved/InMatch ack 不携带 roomCode、
                // 无法证明旧房已释放，不置位（旧版任意 ack 置位已按约束废除）
                DedicatedServerLifecycle.OnRegisterAck(response.state, trackerUnbound: !_heartbeatTracker.IsRoomBound);
                _heartbeatIntervalSeconds = response.heartbeatIntervalSeconds > 0
                    ? response.heartbeatIntervalSeconds
                    : DefaultHeartbeatSeconds;
                Debug.Log($"[ServerRegistry] REGISTERED instance={_options.InstanceId} state={response.state} heartbeat={_heartbeatIntervalSeconds:0}s protocol={GameProtocolIdentity.ProtocolId} build={_options.BuildVersion}");
                _ = FlushPendingMatchResultsAsync(); // 后端可达：排空终局上报补偿队列（重启后恢复也在注册后触发）
                if (_transportUnavailable)
                {
                    if (!_heartbeatTracker.IsRoomBound) await ReportStoppedTransportAsync();
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ServerRegistry] register failed, retry in {RegisterRetrySeconds:0}s: {exception.Message}（后端不可达不阻塞本局服务）");
                return false;
            }
        }

        private async Task HeartbeatUntilConflictAsync()
        {
            while (!_destroyed)
            {
                await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSeconds));
                if (_destroyed) return;
                _ = FlushPendingMatchResultsAsync();
                if (!TryBuildHeartbeat(out var request))
                    continue; // F1：释放许可到达但 MatchLifecycle 尚未完成 Ended→Idle，禁止 Ready+0
                var outcome = await _controlPlane.HeartbeatAsync(request);
                switch (outcome)
                {
                    case HeartbeatOutcome.Accepted:
                        break;
                    case HeartbeatOutcome.TransportError:
                        Debug.LogWarning("[ServerRegistry] heartbeat transport error（连接保留，下一轮心跳重试）");
                        break;
                    case HeartbeatOutcome.StateConflict:
                        // zcode 契约：409=状态矛盾。安全处置：保留现有连接，重注册同步后端权威状态；
                        // 绝不本地自造 Ready（Docs/26：后端房间绑定与实例状态是权威真相）
                        Debug.LogWarning("[ServerRegistry] heartbeat 409 state conflict——保留现有连接，重新注册以同步后端权威状态");
                        return;
                }
            }
        }

        /// <summary>
        /// 有效票据 consume 的 roomCode（后端权威事实）：首绑打 ROOM_BOUND；实例释放后重租到新房间
        /// 由 tracker 以权威结果替换旧绑定（ROOM_REBOUND）；同房间重复通知幂等（无日志）。
        /// 接管契约 §1.2 证据 (b)：consume 返回的非空 roomCode 若与旧局 EndedRoomCode 不同，
        /// 即构成新租约代际证据（可重臂下一局）。
        /// </summary>
        private void OnTicketAccepted(FishNet.Connection.NetworkConnection connection, TicketConsumeResult result)
        {
            if (string.IsNullOrEmpty(result.RoomCode))
                return;
            // §1.2 证据 (b)：新租约代际（与旧局房间码不同的非空 consume 结果）——先于幂等短路记录
            DedicatedServerLifecycle.OnTicketAcceptedRoom(result.RoomCode);

            string previous = _heartbeatTracker.BoundRoomCode;
            if (previous == result.RoomCode)
                return; // 幂等：同房间票据

            _heartbeatTracker.OnRoomBound(result.RoomCode);
            Debug.Log(string.IsNullOrEmpty(previous)
                ? $"[ServerRegistry] ROOM_BOUND room={result.RoomCode} conn={connection.ClientId}"
                : $"[ServerRegistry] ROOM_REBOUND room={previous}->{result.RoomCode} conn={connection.ClientId}（旧绑定由后端权威 consume 结果替换）");

            // TicketAccepted 在认证器写入当前 AcceptedUsers、触发 FishNet 认证完成/玩家生成之前同步调用。
            // 因此新租约代际证据已成立时，可在旧局连接确实清空的最后安全窗口立即重臂，避免
            // “新房先抢租”后首个新连接反过来永久阻塞 AcceptedUsersEmpty/eligiblePlayersGone 门禁。
            if (MatchLifecycle.TryRearmForNextMatch(DedicatedServerLifecycle.BackendReadyForNextMatch))
                Debug.Log("[DedicatedServerLifecycle] MATCH_REARMED_BEFORE_NEW_LEASE_AUTH（首个新租约票据认证前完成重臂）");
        }

        /// <summary>即时心跳单飞闸（并发事件只允许一个在途上报；不缓存，周期心跳兜底）。</summary>
        private readonly SingleFlightGate _immediateHeartbeatGate = new();
        private bool _rearmHeartbeatPending;

        private void HandleServerRearmed()
        {
            // A disconnect heartbeat may still be in flight. Preserve the Ready/0
            // announcement until the gate opens; the periodic 15 s tick is too late
            // for a player immediately starting another room.
            _rearmHeartbeatPending = true;
            TrySendImmediateHeartbeat();
        }

        /// <summary>
        /// 掉线收敛加速（Day2）：连接从 FishNet 移除后立即上报 CurrentPlayers 事实（单飞闸防重入）。
        /// 注意只对已就绪的监听生效（_manager 未接线时静默——配置失败路径本就不注册不心跳）。
        /// </summary>
        private void TrySendImmediateHeartbeat()
        {
            if (_manager == null || _controlPlane == null)
                return;
            if (_heartbeatTracker.ReadyHeartbeatAwaitingRearm && MatchLifecycle.Phase != MatchPhase.Idle)
                return; // F1：周期/即时心跳均不能抢在重臂门禁之前发 Ready+0
            if (!_immediateHeartbeatGate.TryBegin())
                return;
            _rearmHeartbeatPending = false;
            _ = SendImmediateHeartbeatAsync();
        }

        // ---- Day2 三缺口：已认证玩家掉线端点上报（players/disconnect） ----

        /// <summary>后端不可达时的掉线上报重试间隔（秒）。</summary>
        private const float DisconnectRetrySeconds = 10f;

        /// <summary>
        /// 掉线上报通知（Day2 三缺口 2026-09-07）：ClientConnectionCleaned 携带删除前的身份快照——
        /// ① 快照为 null/无身份 = 从未认证通过 → 不上报（含 debug 旁路）；
        /// ② 同 userId 仍在认证档案中（已重连新连接）→ 旧连接迟到的 Stopped 不上报（防删新会话成员）；
        /// ③ 否则入有界重试队列并启动排水循环：终态（2xx/404/409）即出队；传输失败按
        ///   10s 间隔补交至上限后丢弃留错误日志——避免后端短时不可达令房间成员永久残留。
        /// </summary>
        private void OnClientConnectionCleaned(FishNet.Connection.NetworkConnection connection, TicketConsumeResult identitySnapshot)
        {
            // 先保证即时心跳（CurrentPlayers 事实收敛），再做掉线端点上报
            TrySendImmediateHeartbeat();

            if (_controlPlane == null)
                return;
            long.TryParse(identitySnapshot?.UserId, out long parsedUserId);
            var notifyResult = _disconnectQueue.Notify(
                identitySnapshot,
                _authenticator != null ? (System.Collections.Generic.IReadOnlyCollection<TicketConsumeResult>)_authenticator.AcceptedUsers.Values : null,
                parsedUserId,
                identitySnapshot?.RoomCode ?? string.Empty);
            switch (notifyResult)
            {
                case PlayerDisconnectQueue.NotifyResult.SkippedNotAuthenticated:
                    Debug.Log($"[ServerRegistry] DISCONNECT_REPORT_SKIPPED conn={connection.ClientId}——未认证连接（或身份/房间码缺失），不上报");
                    return;
                case PlayerDisconnectQueue.NotifyResult.SkippedUserReconnected:
                    Debug.Log($"[ServerRegistry] DISCONNECT_REPORT_SKIPPED_RECONNECTED conn={connection.ClientId} user={identitySnapshot.UserId}——同 userId 已有新连接，旧连接迟到的 Stopped 不上报（保护新会话成员）");
                    return;
                case PlayerDisconnectQueue.NotifyResult.DroppedQueueFull:
                    Debug.LogError($"[ServerRegistry] DISCONNECT_REPORT_DROPPED conn={connection.ClientId} user={identitySnapshot?.UserId}——上报队列已满（{PlayerDisconnectQueue.MaxQueued}），本条丢弃，房间成员可能残留需人工核对");
                    return;
                case PlayerDisconnectQueue.NotifyResult.Enqueued:
                    if (_disconnectDrainGate.TryBegin())
                        _ = DrainDisconnectReportsAsync();
                    return;
            }
        }

        /// <summary>
        /// 掉线上报排水循环（单飞）：逐条发送 players/disconnect——2xx/404（房间已释放）= 完成；
        /// 409（后端权威绑定矛盾）= 终态停止重试并告警；其余 = 传输失败重排，10s 后补交。
        /// 单线程事件模型下无 CollectPending/TryBegin 竞态（全部同步段无 await）。
        /// </summary>
        private async Task DrainDisconnectReportsAsync()
        {
            try
            {
                while (true)
                {
                    var entries = _disconnectQueue.CollectPending();
                    if (entries.Count == 0)
                    {
                        if (_disconnectQueue.Count == 0)
                            break; // 队列排空：正常退出（同步段无 await，新通知不可能插入）
                        continue; // CollectPending/Count 不一致兜底（防御）
                    }

                    bool anyRequeued = false;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        PlayerDisconnectReport report;
                        try
                        {
                            report = await _controlPlane.DisconnectPlayerAsync(
                                new ServerPlayerDisconnectRequest
                                {
                                    userId = entry.UserId,
                                    roomCode = entry.RoomCode,
                                    sessionId = entry.SessionId,
                                });
                        }
                        catch (Exception exception)
                        {
                            Debug.LogWarning($"[ServerRegistry] DISCONNECT_REPORT exception user={entry.UserId}: {exception.Message}");
                            report = PlayerDisconnectReport.Failed(PlayerDisconnectOutcome.TransportError);
                        }

                        switch (report.Outcome)
                        {
                            case PlayerDisconnectOutcome.Accepted:
                                _disconnectQueue.Complete(entry);
                                ConsumeDisconnectReportFacts(entry, report);
                                break;
                            case PlayerDisconnectOutcome.RoomGone:
                                _disconnectQueue.Complete(entry);
                                Debug.Log($"[ServerRegistry] DISCONNECT_REPORTED user={entry.UserId} room={entry.RoomCode}（房间已不存在——终态）");
                                break;
                            case PlayerDisconnectOutcome.StateConflict:
                                _disconnectQueue.Complete(entry);
                                Debug.LogWarning($"[ServerRegistry] DISCONNECT_REPORT state conflict user={entry.UserId} room={entry.RoomCode}——后端权威拒绝（绑定不符），停止重试");
                                break;
                            case PlayerDisconnectOutcome.TransportError:
                                bool exhausted = _disconnectQueue.RequeueOrDrop(entry);
                                if (exhausted)
                                    Debug.LogError($"[ServerRegistry] DISCONNECT_REPORT_DROPPED user={entry.UserId} room={entry.RoomCode}——重试 {PlayerDisconnectQueue.MaxAttemptsPerEntry} 次后端仍不可达，房间成员可能残留需人工核对");
                                else
                                    Debug.LogWarning($"[ServerRegistry] DISCONNECT_REPORT_RETRY user={entry.UserId} room={entry.RoomCode} attempt={entry.Attempts}——后端暂不可达，{DisconnectRetrySeconds:0}s 后补交");
                                anyRequeued = true;
                                break;
                        }
                    }

                    if (anyRequeued)
                        await Task.Delay(TimeSpan.FromSeconds(DisconnectRetrySeconds));
                }
            }
            finally
            {
                _disconnectDrainGate.End();
            }
        }

        /// <summary>
        /// 掉线上报成功后的权威事实消费（P0 租约闭环 2026-09-08，审计 §2）：
        /// ① 后端明确返回 instanceState=Ready 且 remainingPlayers=0（实例已随最后成员退出释放）→
        ///    清空 tracker 旧 roomCode/InMatch 并转 Ready + 通知租约代际门（可安全重臂下一局）——
        ///    这是同一 DS 无需重启即可被新房租用的闭环关键；
        /// ② 其余权威事实（Reserved/InMatch + 剩余人数）→ 绑定保留，只记录（leader 已转移/成员仍在）；
        /// ③ 事实缺失（旧版后端 204 空响应）→ fail closed 保留绑定，交由心跳 409→重注册权威纠正。
        /// 404/409 不进入本路径（上游终态分类已拦截——绝不猜 Ready）。
        /// </summary>
        private void ConsumeDisconnectReportFacts(PlayerDisconnectQueue.Entry entry, PlayerDisconnectReport report)
        {
            if (ServerHeartbeatTracker.IsBackendReleased(report.InstanceState, report.RemainingPlayers))
            {
                _heartbeatTracker.OnBackendReleased();
                DedicatedServerLifecycle.OnDisconnectReportAck(report.InstanceState, report.RemainingPlayers,
                    trackerUnbound: !_heartbeatTracker.IsRoomBound);
                Debug.Log($"[ServerRegistry] INSTANCE_RELEASED_BY_BACKEND room={entry.RoomCode} user={entry.UserId}（后端权威确认最后成员退出：Ready/0）——本地绑定已清空，实例可被新房租用");
                return;
            }
            if (report.RemainingPlayers >= 0)
            {
                Debug.Log($"[ServerRegistry] DISCONNECT_REPORTED user={entry.UserId} room={entry.RoomCode}（绑定保持：state={report.InstanceState} remaining={report.RemainingPlayers}）");
                return;
            }
            Debug.LogWarning($"[ServerRegistry] DISCONNECT_REPORTED user={entry.UserId} room={entry.RoomCode}（响应无权威事实——旧版后端？绑定保留，待心跳/重注册权威纠正）");
        }

        private async Task SendImmediateHeartbeatAsync()
        {
            try
            {
                if (!TryBuildHeartbeat(out var request))
                    return;
                var outcome = await _controlPlane.HeartbeatAsync(request);
                switch (outcome)
                {
                    case HeartbeatOutcome.StateConflict:
                        Debug.LogWarning("[ServerRegistry] immediate heartbeat 409——保留现有连接，交由周期心跳重注册同步后端权威状态");
                        break;
                    case HeartbeatOutcome.TransportError:
                        Debug.LogWarning("[ServerRegistry] immediate heartbeat transport error（下一周期心跳重试）");
                        break;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ServerRegistry] immediate heartbeat failed: {exception.Message}（下一周期心跳重试）");
            }
            finally
            {
                _immediateHeartbeatGate.End();
                if (_rearmHeartbeatPending) TrySendImmediateHeartbeat();
            }
        }

        /// <summary>
        /// F1 Ready+0 重臂门禁：players/disconnect 的 Ready/0 只授予重臂资格；
        /// 只有 MatchLifecycle 已经实际回到 Idle（其内部已验证旧对象、认证档案、延迟移除
        /// 等条件）后，才允许 tracker 生成 Ready 心跳。其他状态心跳照常构建。
        /// </summary>
        private bool TryBuildHeartbeat(out ServerInstanceHeartbeatRequest request)
        {
            if (_transportUnavailable || _destroyed) { request = null; return false; }
            if (_heartbeatTracker.ReadyHeartbeatAwaitingRearm)
            {
                if (!MatchLifecycle.IsReadyForDedicatedServerHeartbeat())
                {
                    request = null;
                    return false;
                }
                _heartbeatTracker.AllowReadyHeartbeatAfterRearm();
            }
            request = _heartbeatTracker.BuildHeartbeat(_manager.ConnectedClientCount, _options.MapId);
            return true;
        }
    }
}
