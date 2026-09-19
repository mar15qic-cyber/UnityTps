using FishNet;
using FishNet.Managing.Server;
using FishNet.Transporting;
using Game.Gameplay.Health;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 击杀竞赛生命周期（Docs/23 P1-2，G4 + 退出对局 Phase C）——服务器权威状态机：
    /// Idle → Countdown(3s, 输入冻结) → InProgress → Ended。
    /// 【实施适配 2】纯 MonoBehaviour（非 NetworkBehaviour）：由 NetworkHud.Awake 运行时
    /// AddComponent 挂载（零资产改动），网络广播经已联网的 NetworkCombatAuthority 中继。
    /// 服务器上直接推进静态镜像；客户端经 OnMatchEvent 事件镜像。离线（网络未启动）零副作用。
    /// Phase C 增量：
    /// ① 终局载荷补强——MatchPlayerResult 增稳定 playerId（Day2：后端权威 userId，见 MatchPlayerIdentity），
    ///    MatchEndedPayload 增 endReason / departingPlayerId；
    /// ② 离开对局——ServerRequestLeave 是唯一权威入口：移除前先取服务器有效玩家数，
    ///    2 人（含防御性 ≤2）→ PlayerLeft 终局（离开者判负、剩余者判胜，广播 Ended 后延迟移除，
    ///    给所有端留出接收/结算窗口，超时兜底）；&gt;2 → 仅移除退出者（广播 PlayerLeft + 比分快照，
    ///    其余玩家 Phase/比分/计时不变）；
    /// ③ 非主动断线——服务器连接生命周期事件（OnRemoteConnectionState）走同一策略；
    ///    ④ 幂等——LeaveOnceGuard 保证同一 clientId 只处理一次；_endedBroadcast 保证一次比赛
    ///    只广播一次 Ended；host 掉线=本局作废（client-hosted 既定权衡，客户端侧 HostLost 由
    ///    MatchConnectionWatcher 本地处理）。
    /// Day2 F11 增量：普查资格唯一入口（FindPlayersStatic/IsEligibleNetworkPlayer）——只统计
    /// 「NetworkObject 已生成+有效 Owner+连接已认证」的联网玩家，Arena 作者离线对象不再计入
    /// 开局/终局/胜负/中继宿主/离开人数（不删除作者对象、不改 FishNet，见 MatchEligibility）。
    /// </summary>
    public sealed class MatchLifecycle : MonoBehaviour
    {
        // ---- 跨端静态镜像（服务器直接写；客户端经事件镜像） ----

        /// <summary>当前比赛阶段。</summary>
        public static MatchPhase Phase { get; private set; } = MatchPhase.Idle;

        /// <summary>输入冻结（服务器倒计时内置位；客户端镜像置位）。PlayerNetworkAdapter 消费。</summary>
        public static bool InputFrozen { get; private set; }

        /// <summary>本局客户端对局 id（倒计时开始时生成，结算幂等键；格式 match-yyyyMMddHHmmss-dddd）。
        /// CF 房间对局（C3/Q05）：取自票据 consume 的权威 room matchId（后端结算/返房以此对齐）。</summary>
        public static string ClientMatchId { get; private set; }

        // ---- CF 模式/规则/队伍（C3/Q04：来自票据 consume 的房间设置快照；离线默认 KillRace 值）----

        /// <summary>当前模式（"KillRace" / "TDM"；离线恒 KillRace 语义）。</summary>
        public static string CurrentMode { get; private set; } = MatchRules.ModeKillRace;

        /// <summary>本局击杀目标（TDM=团队目标；KillRace=个人目标）。</summary>
        public static int TargetKills { get; private set; } = MatchRules.TargetKills;

        /// <summary>本局时长上限（秒）。</summary>
        public static int TimeLimitSeconds { get; private set; } = MatchRules.MatchTimeLimitSeconds;

        /// <summary>红队团队击杀（TDM；服务器权威，静态镜像）。</summary>
        public static int RedScore { get; private set; }

        /// <summary>蓝队团队击杀（TDM）。</summary>
        public static int BlueScore { get; private set; }

        /// <summary>
        /// 服务器权威终局结果就绪（C3/Q05）：ServerEndMatch 构建终局载荷后触发，
        /// DedicatedServerBootstrap 订阅 → 构建后端 match-result 上报（X-Server-Key）。
        /// _endedBroadcast 幂等闸保证每局恰一次；无订阅者（离线/F1 调试）零副作用。
        /// </summary>
        public static event System.Action<MatchEndedPayload> OnServerMatchResultReady;

        /// <summary>服务器侧把击杀计入击杀者队伍（TDM；KillRace/None 队不计）。</summary>
        public static void AddTeamKill(string killerTeam)
        {
            if (!IsTeamMatch()) return;
            if (killerTeam == MatchRules.TeamRed) RedScore++;
            else if (killerTeam == MatchRules.TeamBlue) BlueScore++;
        }

        /// <summary>本局是否为团队模式（TDM）。</summary>
        public static bool IsTeamMatch() => CurrentMode == MatchRules.ModeTdm;

        /// <summary>从已认证档案同步本局规则快照（服务器；倒计时开始时调用一次）：
        /// 任一带 MatchMode 的 consume 结果即房间对局——模式/目标/时长/上限/权威 matchId 全取自它；
        /// 无档案（离线/F1 调试）→ 保持默认（KillRace 20 杀 600s，本地生成 matchId）。</summary>
        private void ApplyMatchConfigFromAuthProfiles()
        {
            var authenticator = InstanceFinder.NetworkManager != null
                ? InstanceFinder.NetworkManager.GetComponent<JoinTicketAuthenticator>()
                : null;
            if (authenticator == null) return;
            foreach (var profile in authenticator.AcceptedUsers.Values)
            {
                if (profile == null || string.IsNullOrEmpty(profile.MatchMode)) continue;
                CurrentMode = profile.MatchMode;
                TargetKills = profile.KillTarget > 0 ? profile.KillTarget : MatchRules.TargetKills;
                TimeLimitSeconds = profile.TimeLimitMinutes > 0 ? profile.TimeLimitMinutes * 60 : MatchRules.MatchTimeLimitSeconds;
                if (!string.IsNullOrEmpty(profile.MatchId)) ClientMatchId = profile.MatchId;
                break; // 同房全部连接共享同一房间设置快照
            }
        }

        /// <summary>开局前把每名有效玩家的队伍写入其 NetworkCombatAuthority（服务器权威同步；
        /// 数据源 = 认证档案 TeamId；缺失回退 None）。补入玩家重连/认证后同样由此覆盖。</summary>
        private void SyncPlayerTeams()
        {
            var authenticator = InstanceFinder.NetworkManager != null
                ? InstanceFinder.NetworkManager.GetComponent<JoinTicketAuthenticator>()
                : null;
            if (authenticator == null) return;
            foreach (var player in FindPlayersStatic())
            {
                var nob = player.NetworkObject;
                if (nob?.Owner == null) continue;
                if (!authenticator.AcceptedUsers.TryGetValue(nob.Owner.ClientId, out var profile)) continue;
                player.ServerSetTeam(string.IsNullOrEmpty(profile.TeamId) ? MatchRules.TeamNone : profile.TeamId);
            }
        }

        /// <summary>比赛事件（倒计时/击杀/终局/离开），客户端镜像与 HUD（MatchHudView）共同消费。</summary>
        public static event Action<MatchEventKind, string> OnMatchEvent;

        /// <summary>
        /// 服务器正式比赛进入 InProgress（Day2 生命周期闭环）：DedicatedServerBootstrap 订阅
        /// → DedicatedServerLifecycle.OnServerMatchInProgress(tracker) → TryEnterMatch
        /// → 心跳上报 InMatch，后端实例与房间状态同步。无订阅者（F1 调试/离线）零副作用。
        /// （System.Action 全限定：Game.Gameplay.Action 命名空间在同级作用域遮蔽裸 Action）
        /// </summary>
        public static event System.Action OnServerMatchInProgress;

        /// <summary>
        /// 服务器比赛终局（Day2 生命周期闭环，接管契约 §1.2）：DedicatedServerBootstrap 订阅
        /// → DedicatedServerLifecycle.OnServerMatchEnded(tracker.BoundRoomCode) 记录旧局房间码快照
        /// 并作废既有重臂证据——重臂必须持有全新租约代际证明（之后才可重新置位）。
        /// ServerEndMatch 的 _endedBroadcast 幂等闸保证每局恰触发一次。无订阅者零副作用。
        /// </summary>
        public static event System.Action OnServerMatchEnded;

        // ---- 击杀归因登记表（Docs/23 P1-3）：本局内"被击杀者 → 击杀者" ----

        private static readonly Dictionary<DamageableTarget, NetworkCombatAuthority> _hitRegistry =
            new Dictionary<DamageableTarget, NetworkCombatAuthority>();

        // ---- 助攻登记（战绩面板）：被击杀者 → 近期对其造成伤害的射手（窗口 MatchRules.AssistWindowSeconds） ----

        private readonly struct AssistRecord
        {
            public readonly NetworkCombatAuthority Shooter;
            public readonly float Time;
            public AssistRecord(NetworkCombatAuthority shooter, float time) { Shooter = shooter; Time = time; }
        }

        private static readonly Dictionary<DamageableTarget, List<AssistRecord>> _assistRegistry =
            new Dictionary<DamageableTarget, List<AssistRecord>>();

        /// <summary>战绩快照广播节流（仅 InProgress 周期路径使用；事件驱动路径即时广播）。</summary>
        internal const float SnapshotIntervalSeconds = 2f;
        private static float _snapshotTimer;

        private static readonly LeaveOnceGuard _leaveGuard = new LeaveOnceGuard();
        private static bool _endedBroadcast;

        /// <summary>2 人退出终局后的延迟移除（广播 Ended 的接收/结算窗口；超时兜底=到点必移除）。
        /// 静态：离开处理入口 ServerRequestLeave 为静态方法（离线/服务器上下文均可调）。</summary>
        private static NetworkCombatAuthority _pendingDeparture;
        private static float _pendingDepartureDueRealtime;
        private static readonly float DepartureGraceSeconds = 2f;

        private float _phaseStartRealtime;
        private NetworkCombatAuthority _relayHost;

        /// <summary>对局开始时刻（realtime；静态因终局载荷构建在静态方法内）。OnEnable 复位。</summary>
        private static float _matchStartRealtime;

        // ---- 终局载荷（JSON 走中继；Phase C：增 playerId/endReason/departingPlayerId） ----

        [Serializable]
        public sealed class MatchPlayerResult
        {
            /// <summary>稳定玩家 id（Day2：后端权威 userId，MatchPlayerIdentity 解析；kill feed 同源），客户端按此匹配本地条目。</summary>
            public string playerId;
            public int kills;
            public int deaths;
            public bool isWin;
            /// <summary>所属队伍（C3/Q04 TDM；KillRace/离线为 None）。</summary>
            public string teamId = MatchRules.TeamNone;
            /// <summary>助攻（C3/Q04 终局载荷；后端 match-result 上报复用）。</summary>
            public int assists;
            /// <summary>奖励资格（C3/Q05：已认证且非本局离开者；后端逐玩家结算依据）。</summary>
            public bool rewardEligible;
            /// <summary>参与时长秒（I3：补入/重连从入场起算；-1 = 未记录 → 上报方按全局长度兜底）。</summary>
            public int participationSeconds = -1;
        }

        [Serializable]
        public sealed class MatchEndedPayload
        {
            public string clientMatchId;
            public float durationSeconds;
            /// <summary>终局原因（MatchEndReason 枚举名）。</summary>
            public string endReason;
            /// <summary>离开终局时的离开者 id（其他终局为空串）。</summary>
            public string departingPlayerId;
            public MatchPlayerResult[] players;
            /// <summary>模式（C3/Q04："KillRace"/"TDM"）。</summary>
            public string mode = MatchRules.ModeKillRace;
            /// <summary>权威比赛 id（CF 房间对局=后端 room matchId；离线为空串）。</summary>
            public string matchId = string.Empty;
            /// <summary>胜队（TDM；"Red"/"Blue"，null=平局。KillRace 恒 null——胜者看逐玩家 isWin）。</summary>
            public string winnerTeam;
        }

        [Serializable]
        public sealed class MatchKillPayload
        {
            public string killerId;
            public string victimId;
        }

        /// <summary>PlayerLeft 事件载荷（&gt;2 人局仅移除；含离开者比分快照，despawn 前在服务器采集）。</summary>
        [Serializable]
        public sealed class MatchPlayerLeftPayload
        {
            public string playerId;
            public string reason; // PlayerLeaveReason 枚举名
            public int kills;
            public int deaths;
        }

        private void OnEnable()
        {
            // 场景重入时复位静态镜像（静态字段跨场景存活，必须显式清）
            ResetMirrorState();
            NetworkCombatAuthority.OnMatchEvent += MirrorFromEvent;
            // 服务器侧非主动断线入口（Phase C）：连接生命周期事件 → 同一离开策略。
            // NetworkManager 场景对象此刻已存在（本组件由 NetworkHud.Awake 挂在同对象），可安全订阅
            var nm = InstanceFinder.NetworkManager;
            if (nm != null) nm.ServerManager.OnRemoteConnectionState += HandleRemoteConnectionState;
        }

        /// <summary>静态镜像复位（2026-09-15 P0-B：本组件随常驻 NetworkSystems 跨场景存活，
        /// 第二局进 Arena 时 OnEnable 不会再触发——ClientMatchSessionCoordinator 每局连接前显式调用，
        /// 与 OnEnable 同一清单，一个不漏）。纯客户端镜像语义；服务器侧状态机由 PerformRearmReset 全量重臂。</summary>
        public void ResetMirrorState()
        {
            Phase = MatchPhase.Idle;
            InputFrozen = false;
            ClientMatchId = null;
            CurrentMode = MatchRules.ModeKillRace;
            TargetKills = MatchRules.TargetKills;
            TimeLimitSeconds = MatchRules.MatchTimeLimitSeconds;
            RedScore = 0;
            BlueScore = 0;
            _hitRegistry.Clear();
            _assistRegistry.Clear();
            _snapshotTimer = 0f;
            _leaveGuard.Clear();
            _endedBroadcast = false;
            _pendingDeparture = null;
            _relayHost = null;
            _matchStartRealtime = 0f;
            MatchExitState.Reset();
        }

        private void OnDisable()
        {
            NetworkCombatAuthority.OnMatchEvent -= MirrorFromEvent;
            var nm = InstanceFinder.NetworkManager;
            if (nm != null) nm.ServerManager.OnRemoteConnectionState -= HandleRemoteConnectionState;
        }

        /// <summary>客户端镜像：按事件推进本地静态态（服务器上事件回调幂等，不改变已推进的态）。</summary>
        private void MirrorFromEvent(MatchEventKind kind, string payload)
        {
            if (IsServer()) return; // 服务器静态态由状态机直接推进
            switch (kind)
            {
                case MatchEventKind.CountdownStarted:
                    Phase = MatchPhase.Countdown;
                    InputFrozen = true;
                    break;
                case MatchEventKind.MatchIdAssigned:
                    ClientMatchId = payload;
                    break;
                case MatchEventKind.CountdownEnded:
                    Phase = MatchPhase.InProgress;
                    InputFrozen = false;
                    break;
                case MatchEventKind.Ended:
                    Phase = MatchPhase.Ended;
                    break;
                case MatchEventKind.PlayerLeft:
                    break; // HUD/提示事件，不改阶段
            }
        }

        private void Update()
        {
            if (!IsServer()) return;

            // 2 人退出终局的延迟移除窗口（广播 Ended 的接收/结算缓冲；到点必移除=超时兜底）
            if (_pendingDeparture != null)
            {
                if (Time.realtimeSinceStartup >= _pendingDepartureDueRealtime)
                {
                    DespawnAndKick(_pendingDeparture, null);
                    _pendingDeparture = null;
                }
                return; // 终局窗口内不再推进其他状态
            }

            switch (Phase)
            {
                case MatchPhase.Idle:
                    // 服务器上玩家实例数 ≥ 2 → 开局倒计时（FindObjectsByType 轮询为本项目运行时先例）
                    if (CountPlayers() >= MinimumPlayersToStart()) ServerStartCountdown();
                    break;
                case MatchPhase.Countdown:
                    if (RealtimeSincePhaseStart() >= MatchRules.CountdownSeconds) ServerStartInProgress();
                    break;
                case MatchPhase.InProgress:
                    // 战绩快照周期广播（战绩面板：低频全量，事件驱动路径另见 BroadcastKill 等）
                    _snapshotTimer -= Time.deltaTime;
                    if (_snapshotTimer <= 0f)
                    {
                        _snapshotTimer = SnapshotIntervalSeconds;
                        ServerBroadcastScoreboard();
                    }
                    if (ServerEvaluateEndCondition(out bool timedOut))
                        ServerEndMatch(timedOut ? MatchEndReason.TimeLimit : MatchEndReason.Normal, null);
                    break;
                case MatchPhase.Ended:
                    // Day2 生命周期闭环：Ended→Idle 安全重臂（用户规则 ③/⑤——不按时间，只按状态）。
                    // 旧局客户端全部离开 + 认证档案清空 + 延迟移除收口 + 后端权威已重新注册同步，
                    // 四者齐备才复位——绝不在旧客户端仍连接/结算时让新局污染状态机。
                    if (TryRearmForNextMatch(DedicatedServerLifecycle.BackendReadyForNextMatch))
                        _phaseStartRealtime = 0f; // 比赛计时复位（实例级阶段计时器；静态计时在重臂内清）
                    break;
            }
        }

        // ---- 服务器侧状态推进 ----

        private static int MinimumPlayersToStart()
        {
            var nm = InstanceFinder.NetworkManager;
            var authenticator = nm != null ? nm.GetComponent<JoinTicketAuthenticator>() : null;
            if (authenticator != null)
                foreach (var profile in authenticator.AcceptedUsers.Values)
                    if (profile != null && !string.IsNullOrEmpty(profile.MatchId)) return 1;
            return 2; // Debug sessions without a backend-approved room keep their existing rule.
        }

        private void ServerStartCountdown()
        {
            ApplyMatchConfigFromAuthProfiles();
            if (string.IsNullOrEmpty(ClientMatchId))
                ClientMatchId = "match-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-"
                    + UnityEngine.Random.Range(0, 10000).ToString("D4");
            Phase = MatchPhase.Countdown;
            _phaseStartRealtime = Time.realtimeSinceStartup;
            InputFrozen = true;
            RedScore = 0;
            BlueScore = 0;
            // 新对局开始：清零各玩家上局比分（比分 SyncVar 只在服务器写）
            foreach (var player in FindPlayers())
                player.ServerResetScore();
            SyncPlayerTeams();
            RelayStatic(MatchEventKind.CountdownStarted, string.Empty);
            RelayStatic(MatchEventKind.MatchIdAssigned, ClientMatchId);
            NetworkCombatAuthority.ServerBroadcastChatSystem("比赛开始，正在进入战场"); // C4/I2 局内系统消息
            Debug.Log($"[MatchLifecycle] countdown started, matchId={ClientMatchId} mode={CurrentMode} target={TargetKills} timeLimit={TimeLimitSeconds}s");
        }

        private void ServerStartInProgress()
        {
            Phase = MatchPhase.InProgress;
            _matchStartRealtime = Time.realtimeSinceStartup;
            InputFrozen = false;
            RelayStatic(MatchEventKind.CountdownEnded, string.Empty);
            // Day2 生命周期闭环（任务 A ①）：正式比赛进入 InProgress → 通知 Dedicated 侧
            //（Bootstrap → TryEnterMatch → 心跳 InMatch，后端实例与房间状态同步）
            OnServerMatchInProgress?.Invoke();
            // 开局首份战绩快照（面板立即有全量行）
            _snapshotTimer = SnapshotIntervalSeconds;
            ServerBroadcastScoreboard();
        }

        /// <summary>终局条件（服务器唯一权威）：任一玩家 kills ≥ 目标，或时长超时。
        /// TDM（C3/Q04）：团队分 ≥ 目标或超时。</summary>
        private bool ServerEvaluateEndCondition(out bool timedOut)
        {
            timedOut = Time.realtimeSinceStartup - _matchStartRealtime >= TimeLimitSeconds;
            if (timedOut) return true;

            if (IsTeamMatch())
                return RedScore >= TargetKills || BlueScore >= TargetKills;

            foreach (var player in FindPlayers())
                if (player.Kills >= TargetKills) return true;
            return false;
        }

        // ---- 离开对局（Phase C，服务器唯一权威入口） ----

        /// <summary>
        /// 离开处理唯一入口：主动退出（NetworkCombatAuthority.ServerLeaveMatchRequest）与
        /// 非主动断线（HandleRemoteConnectionState）都汇到这里。客户端不上报任何人数。
        /// </summary>
        public static void ServerRequestLeave(NetworkCombatAuthority leaver, PlayerLeaveReason reason)
        {
            if (!IsServer() || leaver == null) return;
            if (Phase != MatchPhase.Countdown && Phase != MatchPhase.InProgress) return; // 无比赛语义（Idle/已终局）
            if (!_leaveGuard.TryBegin(ClientIdOf(leaver))) return; // 重复请求/事件重放幂等

            // 2026-09-18 实机问题2：CF 式系统消息——玩家离开战斗（主动退出/断线共用此唯一入口，幂等闸保证一次）
            NetworkCombatAuthority.ServerBroadcastChatSystem($"{ResolveDisplayName(leaver)} 离开了战斗");

            // 移除【前】取服务器权威有效玩家集合与人数（FindPlayers 即时快照）
            int effective = FindPlayersStatic().Count;
            bool shouldEnd;
            if (IsTeamMatch())
            {
                // R6（审计修复）：TDM 按队伍剩余人数终局——移除离开者后任一队伍清零即终局
                //（等价性修正：旧"总数≤2"在 3红1蓝唯一蓝退出时不触发终局）；总数≤2 保留兜底
                string leaverTeam = string.IsNullOrEmpty(leaver.TeamId) ? MatchRules.TeamNone : leaver.TeamId;
                int redRemaining = 0, blueRemaining = 0;
                foreach (var player in FindPlayersStatic())
                {
                    if (player == leaver) continue;
                    var team = string.IsNullOrEmpty(player.TeamId) ? MatchRules.TeamNone : player.TeamId;
                    if (team == MatchRules.TeamRed) redRemaining++;
                    else if (team == MatchRules.TeamBlue) blueRemaining++;
                }
                shouldEnd = MatchLeavePolicy.ShouldEndMatchOnTeamLeave(redRemaining, blueRemaining)
                            || MatchLeavePolicy.ShouldEndMatch(effective);
            }
            else
            {
                shouldEnd = MatchLeavePolicy.ShouldEndMatch(effective);
            }
            if (shouldEnd)
            {
                // 2 人局：以 PlayerLeft 终局；离开者延迟移除（先广播 Ended 留接收/结算窗口）
                ServerEndMatch(MatchEndReason.PlayerLeft, leaver);
                _pendingDeparture = leaver;
                _pendingDepartureDueRealtime = Time.realtimeSinceStartup + DepartureGraceSeconds;
            }
            else
            {
                ServerRemovePlayer(leaver, reason);
            }
        }

        /// <summary>服务器侧非主动断线入口：远端连接停止 → 与主动退出同一策略（Phase C ⑦）。
        /// 委托签名 = Action&lt;NetworkConnection, RemoteConnectionStateArgs&gt;（FishNet 4.7 既有形状）。</summary>
        private void HandleRemoteConnectionState(FishNet.Connection.NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (!IsServer()) return;
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;
            var leaver = FindPlayerByConnectionId(args.ConnectionId);
            if (leaver != null)
                ServerRequestLeave(leaver, PlayerLeaveReason.Disconnected);
        }

        /// <summary>&gt;2 人局仅移除退出者：广播 PlayerLeft（含比分快照）→ 清归因 → despawn → 断开。
        /// 其余玩家的 Phase/比分/计时不变。</summary>
        private static void ServerRemovePlayer(NetworkCombatAuthority leaver, PlayerLeaveReason reason)
        {
            var snapshot = new MatchPlayerLeftPayload
            {
                playerId = PlayerId(leaver),
                reason = reason.ToString(),
                kills = leaver.Kills,
                deaths = leaver.Deaths,
            };
            RelayStatic(MatchEventKind.PlayerLeft, JsonUtility.ToJson(snapshot));
            ClearHitEntriesFor(leaver);
            DespawnAndKick(leaver, reason);
            // 花名册变化 → 立即补发战绩快照（面板行数同步）
            ServerBroadcastScoreboard();
            Debug.Log($"[MatchLifecycle] player removed (count>2): id={snapshot.playerId} reason={reason} K{snapshot.kills}/D{snapshot.deaths}");
        }

        private static void DespawnAndKick(NetworkCombatAuthority leaver, PlayerLeaveReason? reason)
        {
            if (leaver == null) return;
            var nm = InstanceFinder.NetworkManager;
            var nob = leaver.NetworkObject;
            if (nm != null && nob != null && nob.IsSpawned)
                nm.ServerManager.Despawn(nob);
            // 非主动断线者连接已消失，无需 Kick；主动退出者由服务器权威断开（客户端不自行决定）
            if (reason.HasValue && nob != null && nob.Owner != null)
                nm?.ServerManager.Kick(nob.Owner.ClientId, KickReason.UnexpectedProblem);
        }

        /// <summary>命中归因清理：离开者的目标登记与其作为击杀者的登记一并移除（Phase C ⑥）。</summary>
        public static void ClearHitEntriesFor(NetworkCombatAuthority player)
        {
            if (player == null) return;
            List<DamageableTarget> stale = null;
            foreach (var kv in _hitRegistry)
            {
                if (kv.Value == player || kv.Key != null && kv.Key.transform.IsChildOf(player.transform))
                {
                    (stale ??= new List<DamageableTarget>()).Add(kv.Key);
                }
            }
            if (stale != null)
            {
                foreach (var key in stale) _hitRegistry.Remove(key);
            }
            // 助攻登记同步清理：该玩家的射手记录与其目标的登记一并移除
            List<DamageableTarget> staleAssists = null;
            foreach (var kv in _assistRegistry)
            {
                bool containsPlayer = false;
                foreach (var record in kv.Value)
                {
                    if (record.Shooter == player) { containsPlayer = true; break; }
                }
                if (containsPlayer || kv.Key != null && kv.Key.transform.IsChildOf(player.transform))
                {
                    (staleAssists ??= new List<DamageableTarget>()).Add(kv.Key);
                }
            }
            if (staleAssists != null)
            {
                foreach (var key in staleAssists) _assistRegistry.Remove(key);
            }
        }

        // ---- 终局（Phase C 重写：全玩家参与排名 + 载荷补强 + 一次广播保证） ----

        private static void ServerEndMatch(MatchEndReason reason, NetworkCombatAuthority departing)
        {
            if (_endedBroadcast) return; // 一次比赛只广播一次 Ended（终局/退出竞态幂等）
            _endedBroadcast = true;

            var players = FindPlayersStatic(); // 移除前快照（离开者含在列）
            var payload = new MatchEndedPayload
            {
                clientMatchId = ClientMatchId,
                durationSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - _matchStartRealtime),
                endReason = reason.ToString(),
                departingPlayerId = departing != null ? PlayerId(departing) : string.Empty,
                players = new MatchPlayerResult[players.Count],
                mode = CurrentMode,
                matchId = ClientMatchId ?? string.Empty,
            };

            bool timedOut = reason == MatchEndReason.TimeLimit;
            string winningTeam = null;
            int winnerIndex;
            if (reason == MatchEndReason.PlayerLeft && departing != null && IsTeamMatch())
            {
                // R2/R6（审计修复）：TDM 离队终局按队伍判定——离场队伍让位，存活的对方队伍获胜；
                // 对方无存活（双方归零）或离场者无队伍 → null 平局。修复旧实现 winnerTeam 恒 null
                // 导致"客户端 isWin=true 而后端无胜队"的结算/展示冲突。
                string departingTeam = string.IsNullOrEmpty(departing.TeamId) ? MatchRules.TeamNone : departing.TeamId;
                string opposingTeam = departingTeam == MatchRules.TeamRed ? MatchRules.TeamBlue
                    : departingTeam == MatchRules.TeamBlue ? MatchRules.TeamRed : null;
                bool opposingHasRemaining = false;
                if (opposingTeam != null)
                {
                    foreach (var player in players)
                    {
                        if (player == departing) continue;
                        var team = string.IsNullOrEmpty(player.TeamId) ? MatchRules.TeamNone : player.TeamId;
                        if (team == opposingTeam) { opposingHasRemaining = true; break; }
                    }
                }
                winningTeam = MatchLeavePolicy.TeamWinnerOnLeaveEnd(departingTeam, opposingHasRemaining);
                winnerIndex = -1; // 团队映射（winningTeam=null = 平局，全员 isWin=false）
            }
            else if (reason == MatchEndReason.PlayerLeft && departing != null)
            {
                winnerIndex = -2; // 离开终局（非团队）：不走排名，直接按「离开者判负、剩余者判胜」映射
            }
            else if (IsTeamMatch())
            {
                // C3/Q04 TDM：团队胜负（达标题材方 / 超时比分 / 平局）
                winningTeam = MatchRules.EvaluateTeamWinner(RedScore, BlueScore, TargetKills, timedOut);
                winnerIndex = -1;
            }
            else
            {
                var kills = new int[players.Count];
                var deaths = new int[players.Count];
                for (int i = 0; i < players.Count; i++) { kills[i] = players[i].Kills; deaths[i] = players[i].Deaths; }
                winnerIndex = MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut);
            }
            payload.winnerTeam = winningTeam;

            for (int i = 0; i < players.Count; i++)
            {
                bool isWin;
                if (winnerIndex == -2)
                    isWin = MatchLeavePolicy.IsWinnerOnLeaveEnd(players[i] == departing); // 离开者判负、剩余者判胜
                else if (IsTeamMatch())
                    isWin = winningTeam != null && players[i].TeamId == winningTeam; // 团队胜负按队伍映射
                else
                    isWin = i == winnerIndex; // 平局(-1)时无人为 true（奖励按败方档，Docs/17 §1.4）
                payload.players[i] = new MatchPlayerResult
                {
                    playerId = PlayerId(players[i]),
                    kills = players[i].Kills,
                    deaths = players[i].Deaths,
                    isWin = isWin,
                    teamId = string.IsNullOrEmpty(players[i].TeamId) ? MatchRules.TeamNone : players[i].TeamId,
                    assists = players[i].Assists,
                    rewardEligible = IsEligibleNetworkPlayer(players[i]) && players[i] != departing,
                    participationSeconds = players[i].MatchJoinedRealtime >= 0f
                        ? Mathf.RoundToInt(Mathf.Max(0f, Time.realtimeSinceStartup - players[i].MatchJoinedRealtime))
                        : Mathf.RoundToInt(Mathf.Max(0f, payload.durationSeconds)),
                };
            }

            Phase = MatchPhase.Ended;
            _hitRegistry.Clear();
            _assistRegistry.Clear();
            // Day2 生命周期闭环（接管契约 §1.2）：终局即通知 Dedicated 侧记录旧局房间码快照并作废
            // 既有重臂证据——_endedBroadcast 幂等闸保证每局恰触发一次
            var endText = IsTeamMatch()
                ? (winningTeam == MatchRules.TeamRed ? "红队获胜"
                    : winningTeam == MatchRules.TeamBlue ? "蓝队获胜" : "双方平局")
                : "比赛结束";
            NetworkCombatAuthority.ServerBroadcastChatSystem(endText); // C4/I2 队伍获胜/终局系统消息
            OnServerMatchEnded?.Invoke();
            // C3/Q05：权威终局结果就绪 → Bootstrap 上报后端 match-result（幂等；离线无订阅者零副作用）
            OnServerMatchResultReady?.Invoke(payload);
            RelayStatic(MatchEventKind.Ended, JsonUtility.ToJson(payload));
            // 终局最终排名快照（面板展示终局站位；在 Ended 之后发，面板保持到下一次开局）
            ServerBroadcastScoreboard();
            Debug.Log($"[MatchLifecycle] match ended (reason={reason}), payload={JsonUtility.ToJson(payload)}");
        }

        // ---- Ended → Idle 安全重臂（Day2 生命周期闭环，任务 A ②~⑤） ----

        /// <summary>
        /// Ended→Idle 安全重臂判定（服务器 Update 驱动；public 供 EditMode 直调锁定语义）。
        /// 条件全部为状态事实、无时间成分（用户规则 ⑤）：
        /// ① Phase==Ended（终局已广播，_endedBroadcast 已置位）；
        /// ② 旧局客户端已全部离开——服务器无有资格玩家（对象均已 despawn，不可能还有人"在对局内结算"）；
        /// ③ AcceptedUsers 为空（所有连接已清理——比 ② 更保守的连接级确认）；
        /// ④ 2 人终局延迟移除窗口已收口（_pendingDeparture 已处理）；
        /// ⑤ 后端权威状态已重新注册/同步（BackendReadyForNextMatch——掉线端点释放→心跳 409→
        ///    重注册 ack 的可观察证据链；后端不可达时恒不重臂，fail-safe）。
        /// </summary>
        public static bool TryRearmForNextMatch(bool backendReadySynced)
        {
            bool pendingDepartureClear = _pendingDeparture == null;
            bool eligiblePlayersGone = FindPlayersStatic().Count == 0;
            bool acceptedUsersEmpty = ServerAcceptedUsersEmpty();
            bool shouldRearm = MatchRearmGate.Evaluate(
                Phase == MatchPhase.Ended, eligiblePlayersGone, acceptedUsersEmpty,
                pendingDepartureClear, backendReadySynced);
            if (!shouldRearm)
                return false;
            PerformRearmReset();
            return true;
        }

        /// <summary>
        /// Ready 心跳的本地安全门：后端的 Ready/0 只表示释放许可，不能绕过旧局实际收口。
        /// 该检查与 Ended→Idle 重臂共用同一组状态事实，避免异常路径仅凭 Phase=Idle 放行。
        /// </summary>
        public static bool IsReadyForDedicatedServerHeartbeat()
        {
            return Phase == MatchPhase.Idle
                && _pendingDeparture == null
                && FindPlayersStatic().Count == 0
                && ServerAcceptedUsersEmpty();
        }

        /// <summary>
        /// 重臂复位（用户规则 ④——全量清理，一个不漏）：Phase/InputFrozen/ClientMatchId/
        /// _endedBroadcast/_leaveGuard/_hitRegistry/_pendingDeparture/中继宿主/比赛计时（静态
        /// _matchStartRealtime；实例 _phaseStartRealtime 由 Update 调用方清）。残余比分不在此处
        /// ——重臂时无有资格玩家存活（条件 ②），下一局各玩家比分由 ServerStartCountdown 的
        /// ServerResetScore 归零（既有行为）。
        /// </summary>
        private static void PerformRearmReset()
        {
            Phase = MatchPhase.Idle;
            InputFrozen = false;
            ChatRelayCore.ResetEpoch(ClientMatchId); // C4/I2：局内聊天 seq 空间随局终结
            ClientMatchId = null;
            CurrentMode = MatchRules.ModeKillRace;
            TargetKills = MatchRules.TargetKills;
            TimeLimitSeconds = MatchRules.MatchTimeLimitSeconds;
            RedScore = 0;
            BlueScore = 0;
            _endedBroadcast = false;
            _leaveGuard.Clear();
            _hitRegistry.Clear();
            _assistRegistry.Clear();
            _pendingDeparture = null;
            _pendingDepartureDueRealtime = 0f;
            _relayHostStatic = null;
            _matchStartRealtime = 0f;
            Debug.Log("[MatchLifecycle] MATCH_REARMED（Ended 已复位为 Idle：旧局客户端全走、认证档案清空、后端权威已重新注册同步）");
        }

        /// <summary>
        /// 服务器侧认证档案是否为空（重臂条件 ③）。无 NetworkManager（EditMode/未启动网络）视为空
        /// （不存在任何连接）；认证器缺失视为空（无票据认证路径的本项目服务器形态不存在，防御口径）。
        /// </summary>
        private static bool ServerAcceptedUsersEmpty()
        {
            var nm = InstanceFinder.NetworkManager;
            if (nm == null) return true;
            var authenticator = nm.GetComponent<JoinTicketAuthenticator>();
            return authenticator == null || authenticator.AcceptedUsers.Count == 0;
        }

        // ---- 击杀归因（NetworkCombatAuthority 服务器事件调用） ----
        public static void RegisterHit(NetworkCombatAuthority shooter, DamageableTarget target)
        {
            if (shooter == null || target == null) return;
            // F01（2026-09-19 审计）：登记入口从服务器专属事件（HandleServerShot）下沉到
            // DamageableTarget.ApplyDamage（离线/客户端预测同样经过）——在此闸住非服务器语境：
            // 无 NetworkObject 为 EditMode 直驱（测试接缝，放行）；有 NetworkObject 但未以
            // 服务器身份初始化（离线已生成物件/纯客户端）一律不进注册表。
            var nob = shooter.NetworkObject;
            if (nob != null && !nob.IsServerInitialized) return;
            _hitRegistry[target] = shooter;
            // 助攻登记：追加本次伤害射手并修剪窗口外记录（战绩面板）
            float now = Time.realtimeSinceStartup;
            if (!_assistRegistry.TryGetValue(target, out var records))
            {
                records = new List<AssistRecord>();
                _assistRegistry[target] = records;
            }
            records.Add(new AssistRecord(shooter, now));
            records.RemoveAll(record => now - record.Time > MatchRules.AssistWindowSeconds);
        }

        // ---- EditMode 测试接缝（InternalsVisibleTo("Game.Gameplay.Tests")；归因顺序回归） ----
        internal static bool TryPeekKillerForTests(DamageableTarget target, out NetworkCombatAuthority killer) =>
            _hitRegistry.TryGetValue(target, out killer);
        internal static int PeekAssistRecordCountForTests(DamageableTarget target) =>
            _assistRegistry.TryGetValue(target, out var records) ? records.Count : 0;
        internal static void ClearAttributionForTests()
        {
            _hitRegistry.Clear();
            _assistRegistry.Clear();
        }

        /// <summary>目标死亡时取走击杀者（取后移除，防残留引用泄漏）。</summary>
        public static NetworkCombatAuthority ConsumeKillerOf(DamageableTarget victim)
        {
            if (victim == null) return null;
            if (_hitRegistry.TryGetValue(victim, out var killer))
                _hitRegistry.Remove(victim);
            return killer;
        }

        /// <summary>
        /// 目标死亡时取走助攻名单（去重、窗口内、可选排除击杀者；取后移除）。
        /// 只返回有联网比赛资格的射手（与击杀归因同口径——假人/未认证对象不贡献助攻）。
        /// </summary>
        public static List<NetworkCombatAuthority> ConsumeAssistsOf(DamageableTarget victim, NetworkCombatAuthority killer)
        {
            if (victim == null || !_assistRegistry.TryGetValue(victim, out var records))
                return null;
            _assistRegistry.Remove(victim);
            float now = Time.realtimeSinceStartup;
            var assists = new List<NetworkCombatAuthority>();
            foreach (var record in records)
            {
                var shooter = record.Shooter;
                if (shooter == null || shooter == killer) continue;
                if (now - record.Time > MatchRules.AssistWindowSeconds) continue;
                if (assists.Contains(shooter)) continue;
                if (!IsEligibleNetworkPlayer(shooter)) continue;
                assists.Add(shooter);
            }
            return assists;
        }

        /// <summary>广播击杀事件（服务器调用；payload：击杀者/被杀者 id + 武器）。</summary>
        public static void BroadcastKill(NetworkCombatAuthority killer, NetworkCombatAuthority victim)
        {
            var payload = JsonUtility.ToJson(new MatchKillPayload
            {
                killerId = PlayerId(killer),
                victimId = PlayerId(victim)
            });
            RelayStatic(MatchEventKind.Kill, payload);
            // 比分变化 → 立即补发战绩快照（面板准实时；周期广播兜底其余字段）
            ServerBroadcastScoreboard();
        }

        // ---- 战绩快照（Docs/23 P1-6 战绩面板，服务器唯一权威构建） ----

        /// <summary>构建并广播全量战绩快照（仅服务器语义；离线/无中继宿主时 RelayStatic 内部告警丢弃）。</summary>
        private static void ServerBroadcastScoreboard()
        {
            var payload = BuildScoreboardPayload();
            RelayStatic(MatchEventKind.ScoreboardSnapshot, JsonUtility.ToJson(payload));
        }

        /// <summary>服务器权威战绩快照构建：普查有效玩家 → 纯核心（个人击杀/助攻排序；TDM 附队伍与团队分）。</summary>
        private static MatchScoreboardPayload BuildScoreboardPayload()
        {
            var players = FindPlayersStatic();
            var inputs = new List<MatchScoreboardInput>(players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                inputs.Add(new MatchScoreboardInput
                {
                    playerId = PlayerId(player),
                    displayName = ResolveDisplayName(player),
                    kills = player.Kills,
                    deaths = player.Deaths,
                    assists = player.Assists,
                    pingMs = player.LastPingMs,
                    teamId = string.IsNullOrEmpty(player.TeamId) ? MatchRules.TeamNone : player.TeamId,
                    isDead = player.IsDead, // Phase 4：阵亡标识（服务器 _dead SyncVar 权威投影）
                });
            }
            return new MatchScoreboardPayload
            {
                timeLeftSeconds = ServerTimeLeftSeconds(),
                entries = MatchScoreboardSnapshot.BuildEntries(inputs),
                mode = CurrentMode,
                killTarget = TargetKills,
                redKills = RedScore,
                blueKills = BlueScore,
            };
        }

        /// <summary>剩余时间（秒；-1 = 无进行中比赛）。倒计时阶段返回满时长。</summary>
        private static long ServerTimeLeftSeconds()
        {
            switch (Phase)
            {
                case MatchPhase.Countdown:
                    return TimeLimitSeconds;
                case MatchPhase.InProgress:
                    float elapsed = Time.realtimeSinceStartup - _matchStartRealtime;
                    return (long)Mathf.Max(0f, TimeLimitSeconds - elapsed);
                default:
                    return -1;
            }
        }

        /// <summary>2026-09-18 实机问题2：玩家进入战斗的系统消息（CF 式左下消息流）。
        /// 触发点=玩家对象在服务器生成且比赛已 InProgress（对局中补入）；开局波次由"比赛开始"消息覆盖，
        /// Countdown 阶段不刷进入消息。</summary>
        public static void ServerNotifyPlayerJoined(NetworkCombatAuthority player)
        {
            if (!IsServer() || player == null) return;
            if (Phase != MatchPhase.InProgress) return;
            NetworkCombatAuthority.ServerBroadcastChatSystem($"{ResolveDisplayName(player)} 加入了战斗");
        }

        /// <summary>
        /// 显示名解析（服务器侧）：认证档案 Username → playerId 回退（kill feed / 终局载荷同源 id）。
        /// 客户端不该走此路径（AcceptedUsers 仅服务器填充）。
        /// </summary>
        private static string ResolveDisplayName(NetworkCombatAuthority player)
        {
            var userId = PlayerId(player);
            var networkObject = player != null ? player.NetworkObject : null;
            if (networkObject == null || networkObject.Owner == null || networkObject.Owner.ClientId < 0)
                return userId;
            var networkManager = InstanceFinder.NetworkManager;
            var authenticator = networkManager != null ? networkManager.GetComponent<JoinTicketAuthenticator>() : null;
            if (authenticator != null
                && authenticator.AcceptedUsers.TryGetValue(networkObject.Owner.ClientId, out var profile)
                && !string.IsNullOrEmpty(profile.Username))
                return profile.Username;
            return userId;
        }

        /// <summary>
        /// 稳定玩家 id（Day2：后端权威 userId，经 MatchPlayerIdentity 解析——FishNet ClientId 只是连接临时键，
        /// 不再作为玩家永久身份；调试旁路/离线按 debug-{ClientId}/unknown 同构回退）。kill feed 与终局载荷同源。
        /// </summary>
        public static string PlayerId(NetworkCombatAuthority player)
        {
            return MatchPlayerIdentity.Resolve(player);
        }

        private static long ClientIdOf(NetworkCombatAuthority player)
        {
            var netObject = player != null ? player.NetworkObject : null;
            return netObject != null && netObject.Owner != null ? netObject.Owner.ClientId : -1;
        }

        private static NetworkCombatAuthority FindPlayerByConnectionId(int connectionId)
        {
            foreach (var player in FindPlayersStatic())
            {
                var nob = player.NetworkObject;
                if (nob != null && nob.Owner != null && nob.Owner.ClientId == connectionId)
                    return player;
            }
            return null;
        }

        // ---- 中继与工具 ----

        private static void RelayStatic(MatchEventKind kind, string payload)
        {
            // 惰性解析中继宿主（服务器上第一个【有资格】的 NetworkCombatAuthority 实例——Day2 F11：
            // 作者离线对象不再可能成为中继宿主；host 即服务器，host 掉线 = 本局作废，符合 Docs/04
            // 既定权衡）；Unity 假 null 自动触发重解析
            if (_relayHostStatic == null)
            {
                var found = FindPlayersStatic();
                if (found.Count > 0) _relayHostStatic = found[0];
            }
            if (_relayHostStatic == null)
            {
                Debug.LogWarning($"[MatchLifecycle] 无中继宿主（无 NetworkCombatAuthority 实例），丢弃事件 {kind}");
                return;
            }
            _relayHostStatic.ServerRelayMatchEvent(kind, payload);
        }

        private static NetworkCombatAuthority _relayHostStatic;

        private static bool IsServer()
        {
            var nm = InstanceFinder.NetworkManager;
            return nm != null && nm.IsServerStarted;
        }

        private float RealtimeSincePhaseStart() => Time.realtimeSinceStartup - _phaseStartRealtime;

        private static int CountPlayers() => FindPlayersStatic().Count;

        private List<NetworkCombatAuthority> FindPlayers() => FindPlayersStatic();

        /// <summary>
        /// 服务器侧有效玩家普查（Day2 F11 唯一过滤入口）：全部消费点（开局门槛/终局载荷/胜负/
        /// 击杀中继宿主/离开人数判定/按连接查人）共用同一资格结果——只有「NetworkObject 已生成 +
        /// 有效 Owner + 连接已认证」的联网玩家计入（作者离线对象排除，见 MatchEligibility）。
        /// </summary>
        public static bool IsEligibleNetworkPlayer(NetworkCombatAuthority player)
        {
            // EditMode 测试接缝：无 NetworkObject 的直驱场景无法满足 EvaluatePlayer 的
            // spawned/authenticated 前置；归因回归需要资格语义参与（助攻过滤等）。
            if (EligibilityProbeForTests != null) return EligibilityProbeForTests(player);
            return MatchEligibility.EvaluatePlayer(player);
        }

        /// <summary>仅测试装配点注入（运行时恒 null → 走 MatchEligibility.EvaluatePlayer）。</summary>
        internal static System.Func<NetworkCombatAuthority, bool> EligibilityProbeForTests;

        private static List<NetworkCombatAuthority> FindPlayersStatic()
        {
            var found = FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None);
            var players = new List<NetworkCombatAuthority>(found.Length);
            for (int i = 0; i < found.Length; i++)
            {
                if (IsEligibleNetworkPlayer(found[i]))
                    players.Add(found[i]);
            }
            return players;
        }
    }
}
