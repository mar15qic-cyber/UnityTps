using FishNet;
using Game.Gameplay.Health;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 击杀竞赛生命周期（Docs/23 P1-2，G4）——服务器权威状态机：
    /// Idle → Countdown(3s, 输入冻结) → InProgress → Ended。
    /// 【实施适配 2】纯 MonoBehaviour（非 NetworkBehaviour）：由 NetworkHud.Awake 运行时
    /// AddComponent 挂载（零资产改动），网络广播经已联网的 NetworkCombatAuthority 中继，
    /// 行为语义与 Docs/23 原文"场景 NetworkBehaviour + MatchSystems 对象"一致。
    /// 服务器上直接推进静态镜像；客户端经 OnMatchEvent 事件镜像。离线（网络未启动）零副作用。
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

        /// <summary>比赛事件（倒计时/击杀/终局），客户端镜像与 HUD（MatchHudView）共同消费。</summary>
        public static event Action<MatchEventKind, string> OnMatchEvent;

        // ---- 击杀归因登记表（Docs/23 P1-3）：本局内"被击杀者 → 击杀者" ----

        private static readonly Dictionary<DamageableTarget, NetworkCombatAuthority> _hitRegistry =
            new Dictionary<DamageableTarget, NetworkCombatAuthority>();

        private float _phaseStartRealtime;
        private float _matchStartRealtime;
        private NetworkCombatAuthority _relayHost;

        // ---- 终局载荷（JSON 走中继；Docs/23 P1-2：双方 kills/deaths/isWin + 时长） ----

        [Serializable]
        public sealed class MatchPlayerResult
        {
            public int kills;
            public int deaths;
            public bool isWin;
        }

        [Serializable]
        public sealed class MatchEndedPayload
        {
            public string clientMatchId;
            public float durationSeconds;
            public MatchPlayerResult[] players;
        }

        [Serializable]
        public sealed class MatchKillPayload
        {
            public string killerId;
            public string victimId;
        }

        private void OnEnable()
        {
            // 场景重入时复位静态镜像（静态字段跨场景存活，必须显式清）
            Phase = MatchPhase.Idle;
            InputFrozen = false;
            ClientMatchId = null;
            _hitRegistry.Clear();
            _relayHost = null;
            NetworkCombatAuthority.OnMatchEvent += MirrorFromEvent;
        }

        private void OnDisable()
        {
            NetworkCombatAuthority.OnMatchEvent -= MirrorFromEvent;
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
            }
        }

        private void Update()
        {
            if (!IsServer()) return;

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
                    if (ServerEvaluateEndCondition(out bool timedOut)) ServerEndMatch(timedOut);
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

        private void ServerEndMatch(bool timedOut)
        {
            var players = FindPlayers();
            var payload = new MatchEndedPayload
            {
                clientMatchId = ClientMatchId,
                durationSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - _matchStartRealtime),
                players = new MatchPlayerResult[players.Count]
            };

            // 1v1：前两个实例为对阵双方（架构不写死人数上限，>2 人留扩展——多余实例按旁观处理）
            int result = 0;
            if (players.Count >= 2)
            {
                result = MatchRules.EvaluateWinner(
                    players[0].Kills, players[0].Deaths,
                    players[1].Kills, players[1].Deaths, timedOut);
            }

            for (int i = 0; i < players.Count; i++)
            {
                // isWin 为每玩家相对值：胜方 true，败/平 false（平局双方 false，Docs/17 §1.4）
                bool isWin = players.Count >= 2
                    && ((i == 0 && result == 1) || (i == 1 && result == -1));
                payload.players[i] = new MatchPlayerResult
                {
                    kills = players[i].Kills,
                    deaths = players[i].Deaths,
                    isWin = isWin
                };
            }

            Phase = MatchPhase.Ended;
            _hitRegistry.Clear();
            RelayStatic(MatchEventKind.Ended, JsonUtility.ToJson(payload));
            Debug.Log($"[MatchLifecycle] match ended (timedOut={timedOut}), result={result}, payload={JsonUtility.ToJson(payload)}");
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

        private static string PlayerId(NetworkCombatAuthority player)
        {
            var netObject = player != null ? player.NetworkObject : null;
            return netObject != null && netObject.Owner != null
                ? netObject.Owner.ClientId.ToString()
                : "unknown";
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
