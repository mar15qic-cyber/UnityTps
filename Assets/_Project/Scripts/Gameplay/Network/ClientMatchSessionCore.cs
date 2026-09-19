using System;

namespace Game.Gameplay.Network
{
    /// <summary>客户端会话阶段（2026-09-15 P0-B：Configured → SceneReady → Connecting → Connected →
    /// Authenticated → OwnerReady → Playing；Failed/Ended/Superseded 为终态）。</summary>
    public enum ClientSessionPhase
    {
        /// <summary>无会话。</summary>
        Idle = 0,
        /// <summary>启动上下文已消费（目标场景就绪），待上一连接完全 Stopped。</summary>
        SceneReady,
        /// <summary>StartConnection 已调用，等待连接 Started。</summary>
        Connecting,
        /// <summary>本地连接已 Started，等待认证结果。</summary>
        Connected,
        /// <summary>服务器已接受认证，等待本地 Owner 玩家生成。</summary>
        Authenticated,
        /// <summary>本地 Owner 玩家已生成（相机/HUD 链随玩家对象激活）。</summary>
        OwnerReady,
        /// <summary>入场稳定（OwnerReady 保持稳定窗）——本会话「进入战场完成」。</summary>
        Playing,
        /// <summary>失败终态（FailureReason 细分）。</summary>
        Failed,
        /// <summary>正常收场（终局返房/主动退出/对局中断线——UI 由既有链路处理）。</summary>
        Ended,
        /// <summary>被更新代际顶替（旧会话作废，不算失败）。</summary>
        Superseded,
    }

    /// <summary>会话失败原因（P1：分段超时 + 断线/拒绝归类；连接从未建立由 ClientAuthFailureHandler 通用降级覆盖）。</summary>
    public enum ClientSessionFailureReason
    {
        None = 0,
        /// <summary>Configure → 入场总预算耗尽（含场景加载过慢）。</summary>
        TimeoutOverall,
        /// <summary>StartConnection 后连接迟迟未 Started（服务器不可达/端口未开且无断线事件）。</summary>
        TimeoutConnectStarted,
        /// <summary>连接已 Started 但认证结果未达（服务器 10s 认证截止 + 客户端预算兜底）。</summary>
        TimeoutAuthenticated,
        /// <summary>认证已接受但 Owner 玩家未生成（服务器场景同步/生成失败）。</summary>
        TimeoutOwnerReady,
        /// <summary>认证前连接从有到无（不可达/被服务器断开——具体 UI 由认证失败覆盖层负责）。</summary>
        ConnectionLostBeforeAuth,
        /// <summary>服务器拒绝认证（具体原因由认证失败覆盖层展示）。</summary>
        AuthRejected,
        /// <summary>调用方主动取消（UI 侧超时/用户返回）。</summary>
        Cancelled,
    }

    /// <summary>
    /// 客户端对局会话状态机（纯逻辑，2026-09-15 P0-B/P1；MonoBehaviour 驱动见 ClientMatchSessionCoordinator）。
    /// 幂等纪律：同一代际至多一次会话（重复 Begin/回调直接拒绝）；新代际 Begin 把旧会话置 Superseded
    /// （旧会话的迟到回调绝不断新连接/不清新上下文——由代际核对拦下）。
    /// 超时预算（秒，P1 分段）：整体 75（自 Configure 起算）｜连接 Started 15｜认证结果 15｜
    /// Owner 生成 25｜入场稳定窗 0.5。调参只动本常量区。
    /// </summary>
    public sealed class ClientMatchSessionCore
    {
        public const float OverallBudgetSeconds = 75f;
        public const float ConnectStartedBudgetSeconds = 15f;
        public const float AuthenticatedBudgetSeconds = 15f;
        public const float OwnerReadyBudgetSeconds = 25f;
        public const float PlayingStableSeconds = 0.5f;

        public ClientSessionPhase Phase { get; private set; } = ClientSessionPhase.Idle;
        public ClientSessionFailureReason FailureReason { get; private set; } = ClientSessionFailureReason.None;
        /// <summary>本会话代际（NetworkLaunchContext.ConfigureClient 分配）。</summary>
        public long Generation { get; private set; }
        /// <summary>本会话比赛 id（日志归属；空=未知）。</summary>
        public string MatchId { get; private set; } = string.Empty;

        private float _phaseEnteredRealtime;
        private float _configuredAtRealtime;

        public bool IsTerminal =>
            Phase == ClientSessionPhase.Failed || Phase == ClientSessionPhase.Ended || Phase == ClientSessionPhase.Superseded;

        /// <summary>是否已达成「进入战场完成」（Playing 及之后都算入场成功；Ended 含在内——
        /// 极短局在等待方完成判定前收场也算成功入场）。</summary>
        public bool HasEnteredBattle =>
            Phase == ClientSessionPhase.Playing || Phase == ClientSessionPhase.Ended;

        /// <summary>
        /// 开启新会话。同一代际重复调用返回 false（幂等——sceneLoaded 与 Awake 双触发安全）；
        /// 不同代际把旧会话置 Superseded。终态后代际不可复用（同代际只尝试一次，失败不自动重试——
        /// 重试必须由上层重新取票，走新代际）。
        /// </summary>
        public bool BeginSession(long generation, string matchId, float nowRealtime, float configuredAtRealtime)
        {
            if (Phase != ClientSessionPhase.Idle && Generation == generation)
                return false; // 同代际已开（或已终态）：幂等拒绝
            if (Phase != ClientSessionPhase.Idle && !IsTerminal)
                MarkSuperseded(); // 旧会话仍在途：作废（不清新上下文/不断新连接）
            Generation = generation;
            MatchId = matchId ?? string.Empty;
            FailureReason = ClientSessionFailureReason.None;
            Phase = ClientSessionPhase.SceneReady;
            _phaseEnteredRealtime = nowRealtime;
            _configuredAtRealtime = configuredAtRealtime;
            return true;
        }

        /// <summary>是否已达成「进入战场完成」（Playing 及之后都算入场成功；Ended 含在内——
        /// 极短局在等待方完成判定前收场也算成功入场）。</summary>
        public bool IsCurrentSession(long generation) => Phase != ClientSessionPhase.Idle && !IsTerminal && Generation == generation;

        public void MarkConnecting(float nowRealtime)
        {
            if (Phase != ClientSessionPhase.SceneReady) return;
            Phase = ClientSessionPhase.Connecting;
            _phaseEnteredRealtime = nowRealtime;
        }

        public void MarkConnected(float nowRealtime)
        {
            if (Phase != ClientSessionPhase.Connecting) return;
            Phase = ClientSessionPhase.Connected;
            _phaseEnteredRealtime = nowRealtime;
        }

        public void MarkAuthenticated(float nowRealtime)
        {
            if (Phase != ClientSessionPhase.Connected) return;
            Phase = ClientSessionPhase.Authenticated;
            _phaseEnteredRealtime = nowRealtime;
        }

        public void MarkOwnerReady(float nowRealtime)
        {
            if (Phase != ClientSessionPhase.Authenticated) return;
            Phase = ClientSessionPhase.OwnerReady;
            _phaseEnteredRealtime = nowRealtime;
        }

        /// <summary>入场稳定窗推进（驱动每帧调用；OwnerReady 保持稳定即 Playing）。</summary>
        public void MarkPlayingIfStable(float nowRealtime)
        {
            if (Phase != ClientSessionPhase.OwnerReady) return;
            if (nowRealtime - _phaseEnteredRealtime >= PlayingStableSeconds)
            {
                Phase = ClientSessionPhase.Playing;
                _phaseEnteredRealtime = nowRealtime;
            }
        }

        /// <summary>正常收场（终局返房/主动退出/对局中断线；幂等）。</summary>
        public void MarkEnded()
        {
            if (IsTerminal || Phase == ClientSessionPhase.Idle) return;
            Phase = ClientSessionPhase.Ended;
        }

        /// <summary>认证被拒（具体 UI 由认证失败覆盖层负责；此处只收终态）。</summary>
        public void MarkAuthRejected()
        {
            if (IsTerminal || Phase == ClientSessionPhase.Idle) return;
            Phase = ClientSessionPhase.Failed;
            FailureReason = ClientSessionFailureReason.AuthRejected;
        }

        /// <summary>认证前断线（不可达/被断开；UI 由认证失败覆盖层负责）。</summary>
        public void MarkConnectionLostBeforeAuth()
        {
            if (IsTerminal || Phase == ClientSessionPhase.Idle) return;
            if (Phase != ClientSessionPhase.Connecting && Phase != ClientSessionPhase.Connected) return;
            Phase = ClientSessionPhase.Failed;
            FailureReason = ClientSessionFailureReason.ConnectionLostBeforeAuth;
        }

        /// <summary>主动取消（UI 侧超时/用户返回；幂等终态）。</summary>
        public void MarkCancelled()
        {
            if (IsTerminal || Phase == ClientSessionPhase.Idle) return;
            Phase = ClientSessionPhase.Failed;
            FailureReason = ClientSessionFailureReason.Cancelled;
        }

        private void MarkSuperseded()
        {
            Phase = ClientSessionPhase.Superseded;
        }

        /// <summary>分段看门狗（驱动每帧调用）：返回 true 表示本帧判定超时失败（reason 已写入终态）。
        /// P1 边界（实机双局验证修正）：整体预算只约束「入场前」各阶段——Playing 起对局时长归
        /// 比赛规则（10-15 分钟），协调器不得按入场预算掐死进行中的对局；Playing 后只认
        /// 认证器/连接事件驱动的收场（MatchConnectionWatcher/结算流既有链路）。</summary>
        public bool TickTimeout(float nowRealtime, out ClientSessionFailureReason reason)
        {
            reason = ClientSessionFailureReason.None;
            if (Phase == ClientSessionPhase.Idle || Phase == ClientSessionPhase.Playing || IsTerminal) return false;

            // 整体预算（自 Configure 起算——覆盖场景加载/等待旧连接/全部分段；入场完成即豁免）
            if (nowRealtime - _configuredAtRealtime > OverallBudgetSeconds)
            {
                Phase = ClientSessionPhase.Failed;
                FailureReason = reason = ClientSessionFailureReason.TimeoutOverall;
                return true;
            }

            float elapsed = nowRealtime - _phaseEnteredRealtime;
            switch (Phase)
            {
                case ClientSessionPhase.SceneReady:
                    // 等待旧连接 Stopped + 启动连接合计超 20s 视为整体异常（细分由整体预算兜底）
                    if (elapsed > ConnectStartedBudgetSeconds + OwnerReadyBudgetSeconds)
                    {
                        Phase = ClientSessionPhase.Failed;
                        FailureReason = reason = ClientSessionFailureReason.TimeoutConnectStarted;
                        return true;
                    }
                    break;
                case ClientSessionPhase.Connecting:
                    if (elapsed > ConnectStartedBudgetSeconds)
                    {
                        Phase = ClientSessionPhase.Failed;
                        FailureReason = reason = ClientSessionFailureReason.TimeoutConnectStarted;
                        return true;
                    }
                    break;
                case ClientSessionPhase.Connected:
                    if (elapsed > AuthenticatedBudgetSeconds)
                    {
                        Phase = ClientSessionPhase.Failed;
                        FailureReason = reason = ClientSessionFailureReason.TimeoutAuthenticated;
                        return true;
                    }
                    break;
                case ClientSessionPhase.Authenticated:
                    if (elapsed > OwnerReadyBudgetSeconds)
                    {
                        Phase = ClientSessionPhase.Failed;
                        FailureReason = reason = ClientSessionFailureReason.TimeoutOwnerReady;
                        return true;
                    }
                    break;
                case ClientSessionPhase.OwnerReady:
                case ClientSessionPhase.Playing:
                default:
                    return false; // 入场后不再分段超时（对局断线归 MatchConnectionWatcher 既有链路）
            }
            return false;
        }

        /// <summary>失败原因 → 玩家可读文案（覆盖层/状态栏共用；ConnectionLost/AuthRejected/Cancelled
        /// 由各自链路给出文案，此处给出兜底）。</summary>
        public static string DescribeFailure(ClientSessionFailureReason reason)
        {
            switch (reason)
            {
                case ClientSessionFailureReason.TimeoutOverall:
                    return $"进入战场超时（总预算 {OverallBudgetSeconds:0}s），请返回重试";
                case ClientSessionFailureReason.TimeoutConnectStarted:
                    return $"无法连接到对战服务器（{ConnectStartedBudgetSeconds:0}s 内未建立连接），请确认服务器已启动";
                case ClientSessionFailureReason.TimeoutAuthenticated:
                    return "连接已建立，但服务器认证无响应，请稍后重试";
                case ClientSessionFailureReason.TimeoutOwnerReady:
                    return "认证成功，但入场未完成（玩家生成超时），请稍后重试";
                case ClientSessionFailureReason.ConnectionLostBeforeAuth:
                    return "与服务器的连接已断开，请返回大厅重试";
                case ClientSessionFailureReason.AuthRejected:
                    return "服务器拒绝了本次入场（见具体提示）";
                case ClientSessionFailureReason.Cancelled:
                    return "本次入场已取消";
                default:
                    return "进入战场失败，请返回重试";
            }
        }
    }
}
