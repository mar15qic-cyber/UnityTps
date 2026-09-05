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
    /// ① 终局载荷补强——MatchPlayerResult 增稳定 playerId（= FishNet Owner ClientId，与 kill feed
    ///    同源），MatchEndedPayload 增 endReason / departingPlayerId；
    /// ② 离开对局——ServerRequestLeave 是唯一权威入口：移除前先取服务器有效玩家数，
    ///    2 人（含防御性 ≤2）→ PlayerLeft 终局（离开者判负、剩余者判胜，广播 Ended 后延迟移除，
    ///    给所有端留出接收/结算窗口，超时兜底）；&gt;2 → 仅移除退出者（广播 PlayerLeft + 比分快照，
    ///    其余玩家 Phase/比分/计时不变）；
    /// ③ 非主动断线——服务器连接生命周期事件（OnRemoteConnectionState）走同一策略；
    /// ④ 幂等——LeaveOnceGuard 保证同一 clientId 只处理一次；_endedBroadcast 保证一次比赛
    ///    只广播一次 Ended；host 掉线=本局作废（client-hosted 既定权衡，客户端侧 HostLost 由
    ///    MatchConnectionWatcher 本地处理）。
    /// </summary>
    public sealed class MatchLifecycle : MonoBehaviour
    {
        // ---- 跨端静态镜像（服务器直接写；客户端经事件镜像） ----

        /// <summary>当前比赛阶段。</summary>
        public static MatchPhase Phase { get; private set; } = MatchPhase.Idle;

        /// <summary>输入冻结（服务器倒计时内置位；客户端镜像置位）。PlayerNetworkAdapter 消费。</summary>
        public static bool InputFrozen { get; private set; }

        /// <summary>本局客户端对局 id（倒计时开始时生成，结算幂等键；格式 match-yyyyMMddHHmmss-dddd）。</summary>
        public static string ClientMatchId { get; private set; }

        /// <summary>比赛事件（倒计时/击杀/终局/离开），客户端镜像与 HUD（MatchHudView）共同消费。</summary>
        public static event Action<MatchEventKind, string> OnMatchEvent;

        // ---- 击杀归因登记表（Docs/23 P1-3）：本局内"被击杀者 → 击杀者" ----

        private static readonly Dictionary<DamageableTarget, NetworkCombatAuthority> _hitRegistry =
            new Dictionary<DamageableTarget, NetworkCombatAuthority>();

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
            /// <summary>稳定玩家 id（= FishNet Owner ClientId；kill feed 同源），客户端按此匹配本地条目。</summary>
            public string playerId;
            public int kills;
            public int deaths;
            public bool isWin;
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
            Phase = MatchPhase.Idle;
            InputFrozen = false;
            ClientMatchId = null;
            _hitRegistry.Clear();
            _leaveGuard.Clear();
            _endedBroadcast = false;
            _pendingDeparture = null;
            _relayHost = null;
            _matchStartRealtime = 0f;
            MatchExitState.Reset();
            NetworkCombatAuthority.OnMatchEvent += MirrorFromEvent;
            // 服务器侧非主动断线入口（Phase C）：连接生命周期事件 → 同一离开策略。
            // NetworkManager 场景对象此刻已存在（本组件由 NetworkHud.Awake 挂在同对象），可安全订阅
            var nm = InstanceFinder.NetworkManager;
            if (nm != null) nm.ServerManager.OnRemoteConnectionState += HandleRemoteConnectionState;
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
                    if (CountPlayers() >= 2) ServerStartCountdown();
                    break;
                case MatchPhase.Countdown:
                    if (RealtimeSincePhaseStart() >= MatchRules.CountdownSeconds) ServerStartInProgress();
                    break;
                case MatchPhase.InProgress:
                    if (ServerEvaluateEndCondition(out bool timedOut))
                        ServerEndMatch(timedOut ? MatchEndReason.TimeLimit : MatchEndReason.Normal, null);
                    break;
            }
        }

        // ---- 服务器侧状态推进 ----

        private void ServerStartCountdown()
        {
            ClientMatchId = "match-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-"
                + UnityEngine.Random.Range(0, 10000).ToString("D4");
            Phase = MatchPhase.Countdown;
            _phaseStartRealtime = Time.realtimeSinceStartup;
            InputFrozen = true;
            // 新对局开始：清零各玩家上局比分（比分 SyncVar 只在服务器写）
            foreach (var player in FindPlayers())
                player.ServerResetScore();
            RelayStatic(MatchEventKind.CountdownStarted, string.Empty);
            RelayStatic(MatchEventKind.MatchIdAssigned, ClientMatchId);
            Debug.Log($"[MatchLifecycle] countdown started, matchId={ClientMatchId}");
        }

        private void ServerStartInProgress()
        {
            Phase = MatchPhase.InProgress;
            _matchStartRealtime = Time.realtimeSinceStartup;
            InputFrozen = false;
            RelayStatic(MatchEventKind.CountdownEnded, string.Empty);
        }

        /// <summary>终局条件（服务器唯一权威）：任一玩家 kills ≥ 20，或 600s 超时。</summary>
        private bool ServerEvaluateEndCondition(out bool timedOut)
        {
            timedOut = Time.realtimeSinceStartup - _matchStartRealtime >= MatchRules.MatchTimeLimitSeconds;
            if (timedOut) return true;

            foreach (var player in FindPlayers())
                if (player.Kills >= MatchRules.TargetKills) return true;
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

            // 移除【前】取服务器权威有效玩家集合与人数（FindPlayers 即时快照）
            int effective = FindPlayersStatic().Count;
            if (MatchLeavePolicy.ShouldEndMatch(effective))
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
            if (stale == null) return;
            foreach (var key in stale) _hitRegistry.Remove(key);
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
                players = new MatchPlayerResult[players.Count]
            };

            bool timedOut = reason == MatchEndReason.TimeLimit;
            int winnerIndex;
            if (reason == MatchEndReason.PlayerLeft && departing != null)
            {
                winnerIndex = -2; // 离开终局：不走排名，直接按「离开者判负、剩余者判胜」映射
            }
            else
            {
                var kills = new int[players.Count];
                var deaths = new int[players.Count];
                for (int i = 0; i < players.Count; i++) { kills[i] = players[i].Kills; deaths[i] = players[i].Deaths; }
                winnerIndex = MatchRules.EvaluateWinnerMulti(kills, deaths, timedOut);
            }

            for (int i = 0; i < players.Count; i++)
            {
                bool isWin;
                if (winnerIndex == -2)
                    isWin = !MatchLeavePolicy.IsWinnerOnLeaveEnd(players[i] == departing); // 离开者判负、剩余者判胜
                else
                    isWin = i == winnerIndex; // 平局(-1)时无人为 true（奖励按败方档，Docs/17 §1.4）
                payload.players[i] = new MatchPlayerResult
                {
                    playerId = PlayerId(players[i]),
                    kills = players[i].Kills,
                    deaths = players[i].Deaths,
                    isWin = isWin
                };
            }

            Phase = MatchPhase.Ended;
            _hitRegistry.Clear();
            RelayStatic(MatchEventKind.Ended, JsonUtility.ToJson(payload));
            Debug.Log($"[MatchLifecycle] match ended (reason={reason}), payload={JsonUtility.ToJson(payload)}");
        }

        // ---- 击杀归因（NetworkCombatAuthority 服务器事件调用） ----

        /// <summary>服务器命中时登记"击杀者 → 被击中目标"（同一目标多次命中覆盖，死亡时取最后击杀者）。</summary>
        public static void RegisterHit(NetworkCombatAuthority shooter, DamageableTarget target)
        {
            if (shooter == null || target == null) return;
            _hitRegistry[target] = shooter;
        }

        /// <summary>目标死亡时取走击杀者（取后移除，防残留引用泄漏）。</summary>
        public static NetworkCombatAuthority ConsumeKillerOf(DamageableTarget victim)
        {
            if (victim == null) return null;
            if (_hitRegistry.TryGetValue(victim, out var killer))
                _hitRegistry.Remove(victim);
            return killer;
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
        }

        /// <summary>稳定玩家 id：FishNet Owner ClientId（kill feed 与终局载荷同源；比赛期内稳定）。</summary>
        public static string PlayerId(NetworkCombatAuthority player)
        {
            var netObject = player != null ? player.NetworkObject : null;
            return netObject != null && netObject.Owner != null
                ? netObject.Owner.ClientId.ToString()
                : "unknown";
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
            // 惰性解析中继宿主（服务器上第一个 NetworkCombatAuthority 实例；host 即服务器，
            // host 掉线 = 本局作废，符合 Docs/04 既定权衡）；Unity 假 null 自动触发重解析
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

        private static List<NetworkCombatAuthority> FindPlayersStatic()
        {
            return new List<NetworkCombatAuthority>(
                FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None));
        }
    }
}
