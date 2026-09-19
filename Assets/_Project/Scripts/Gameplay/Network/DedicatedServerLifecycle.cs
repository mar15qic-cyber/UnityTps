using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// Dedicated Server 比赛/实例生命周期联动（Day2 生命周期闭环 2026-09-07；租约代际证据按
    /// 接管契约 §1.2 收紧——2026-09-07 第二轮审计约束 1）：
    /// ① MatchLifecycle 进入 InProgress → Bootstrap 经 OnServerMatchInProgress 调用
    ///   ServerHeartbeatTracker.TryEnterMatch → 心跳上报 InMatch → 后端实例与房间状态同步为 InMatch
    ///   （zcode 心跳契约：Reserved→InMatch 合法转换，房间 Status 随实例心跳同步）。
    ///   §1.1：TryEnterMatch 只允许在比赛真实进入 InProgress 且本地已持有【非空权威 roomCode】时执行。
    /// ② Ended→Idle 安全重臂的权威租约代际证据（BackendReadyForNextMatch）只有两种来源：
    ///   (a) 注册 ack 明确返回 Ready/Offline，且 tracker 旧 roomCode 已清除（OnRegistered 语义）；
    ///   (b) 新票据 consume 返回与旧局 EndedRoomCode 不同的【非空】roomCode（新租约代际）。
    ///   当前 ServerInstanceRegisterResponse 不携带 roomCode——Reserved/InMatch 的注册 ack
    ///   无法证明旧房已释放，【不得】单独凭其置位（接管契约 §1.2；旧版无条件置位已废除）。
    ///   服务器终局（OnServerMatchEnded）记录旧局房间码并作废旧证据——每次 Ended 都要求全新代际证明。
    /// 纯逻辑部分（MatchRearmGate）供 EditMode 锁定；Bootstrap 只做接线与证据上报，不持有状态。
    /// </summary>
    public static class DedicatedServerLifecycle
    {
        /// <summary>
        /// 后端权威租约代际证据（可安全重臂下一局）。置位来源仅两种：
        /// (a) OnRegisterAck(Ready/Offline + tracker 旧绑定已清)；(b) OnTicketAcceptedRoom(非空且≠旧局房间码)。
        /// 清位：正式比赛进入 InProgress（新局开始）与服务器终局（旧证据作废，待新代际证明）。
        /// </summary>
        public static bool BackendReadyForNextMatch { get; private set; }

        /// <summary>旧局（已终局待重臂）绑定过的房间码快照（终局时由 Bootstrap 从 tracker 绑定记录；
        /// 空串=无待重臂的已终局比赛，(b) 类证据不适用）。</summary>
        public static string EndedRoomCode { get; private set; } = string.Empty;

        /// <summary>服务器终局（Bootstrap 订阅 MatchLifecycle.OnServerMatchEnded 调用）：
        /// 记录旧局房间码快照 + 作废既有重臂证据（每次 Ended 都要求全新代际证明，防旧证据跨局残留）。</summary>
        public static void OnServerMatchEnded(string boundRoomCode)
        {
            EndedRoomCode = boundRoomCode ?? string.Empty;
            BackendReadyForNextMatch = false;
            Debug.Log($"[DedicatedServerLifecycle] MATCH_ENDED room={(string.IsNullOrEmpty(EndedRoomCode) ? "<unbound>" : EndedRoomCode)}（等待租约代际证据后重臂）");
        }

        /// <summary>
        /// 证据 (a)：注册 ack。仅当 state 明确为 Ready/Offline（tracker 旧 roomCode 已随 OnRegistered
        /// 清除，trackerUnbound 为其可观察证明）才置位；Reserved/InMatch ack 不构成释放证明，忽略。
        /// </summary>
        public static void OnRegisterAck(string state, bool trackerUnbound)
        {
            if (BackendReadyForNextMatch)
                return;
            bool authoritativeUnbound = state == ServerHeartbeatTracker.StateReady
                || state == ServerHeartbeatTracker.StateOffline;
            if (authoritativeUnbound && trackerUnbound)
            {
                BackendReadyForNextMatch = true;
                Debug.Log("[DedicatedServerLifecycle] LEASE_GENERATION_READY（注册 ack Ready/Offline + 旧 roomCode 已清——可安全重臂）");
            }
        }

        /// <summary>
        /// 证据 (a) 第二来源（P0 租约闭环 2026-09-08）：players/disconnect 成功响应明确
        /// instanceState=Ready 且 remainingPlayers=0（实例已随最后成员退出释放），且 tracker 旧 roomCode
        /// 已随之清除（trackerUnbound 为其可观察证明）→ 与注册 ack Ready/Offline 同级的释放证据。
        /// 其余事实（Reserved/InMatch/仍有成员）不构成证据；404/409/传输失败不进入本入口
        ///（上游分类拦截，绝不猜 Ready）。
        /// </summary>
        public static void OnDisconnectReportAck(string instanceState, int remainingPlayers, bool trackerUnbound)
        {
            if (BackendReadyForNextMatch)
                return;
            if (!ServerHeartbeatTracker.IsBackendReleased(instanceState, remainingPlayers))
                return;
            if (!trackerUnbound)
                return;
            BackendReadyForNextMatch = true;
            Debug.Log("[DedicatedServerLifecycle] LEASE_GENERATION_READY（掉线上报 ack Ready/0 + 旧 roomCode 已清——可安全重臂）");
        }

        /// <summary>
        /// 证据 (b)：新票据 consume 的非空 roomCode 与旧局 EndedRoomCode 不同 → 明确进入新租约代际。
        /// 无已终局比赛（EndedRoomCode 空）时不适用；同房间码不构成新代际（不置位）。
        /// </summary>
        public static void OnTicketAcceptedRoom(string acceptedRoomCode)
        {
            if (BackendReadyForNextMatch)
                return;
            if (string.IsNullOrEmpty(EndedRoomCode))
                return;
            if (!string.IsNullOrEmpty(acceptedRoomCode) && acceptedRoomCode != EndedRoomCode)
            {
                BackendReadyForNextMatch = true;
                Debug.Log($"[DedicatedServerLifecycle] LEASE_GENERATION_READY（新票据 room={acceptedRoomCode} ≠ 旧局 room={EndedRoomCode}——新租约代际，可安全重臂）");
            }
        }

        /// <summary>正式比赛开始（清位由 InProgress 钩子承担，见 OnServerMatchInProgress）。</summary>
        public static void ClearBackendReady() => BackendReadyForNextMatch = false;

        /// <summary>测试隔离（仅 EditMode 测试使用）。</summary>
        public static void ResetForTests()
        {
            BackendReadyForNextMatch = false;
            EndedRoomCode = string.Empty;
        }

        /// <summary>
        /// 正式比赛进入 InProgress（服务器侧钩子，Bootstrap 订阅 MatchLifecycle.OnServerMatchInProgress）：
        /// 清 Ready 事实（新局开始）+ 推进实例进入 InMatch。§1.1：仅当本地已持有非空权威 roomCode
        /// 才执行 TryEnterMatch；未绑定房间（离线/F1 调试/未租房）不调用并告警——比赛照常进行，
        /// 后端状态由下一次注册/心跳权威纠正。
        /// </summary>
        public static void OnServerMatchInProgress(ServerHeartbeatTracker tracker)
        {
            ClearBackendReady();
            if (tracker == null || string.IsNullOrEmpty(tracker.BoundRoomCode))
            {
                Debug.LogWarning("[DedicatedServerLifecycle] TryEnterMatch 跳过：本地未持有非空权威 roomCode（§1.1）——离线/F1 调试或未租房");
                return;
            }
            if (tracker.TryEnterMatch())
                Debug.Log("[DedicatedServerLifecycle] MATCH_ENTERED_IN_MATCH（实例心跳转 InMatch，后端房间状态随心跳同步）");
            else
                Debug.LogWarning("[DedicatedServerLifecycle] TryEnterMatch 未生效（状态机拒绝）——后端 InMatch 同步待权威纠正");
        }
    }

    /// <summary>
    /// Ended→Idle 安全重臂判定（纯逻辑，EditMode 锁定）：五条件齐备才允许复位
    ///（用户规则 ③/⑤——禁纯时间自动复位，避免旧客户端仍在结算时被新局污染）：
    /// ① 阶段必须为 Ended（终局已广播）；
    /// ② 旧局客户端已全部离开（服务器无有资格玩家——对象已 despawn，不可能还有人在结算对局内）；
    /// ③ 认证档案为空（AcceptedUsers 无残留连接——离开者清理完成）；
    /// ④ 2 人终局延迟移除窗口已收口（_pendingDeparture 已处理）；
    /// ⑤ 后端权威租约代际证据（BackendReadyForNextMatch——仅 (a) Ready/Offline 清旧绑定 或
    ///    (b) 新票据不同 roomCode 两种来源，见 DedicatedServerLifecycle）。
    /// </summary>
    public static class MatchRearmGate
    {
        public static bool Evaluate(
            bool phaseEnded,
            bool eligiblePlayersGone,
            bool acceptedUsersEmpty,
            bool pendingDepartureClear,
            bool backendReadySynced)
        {
            return phaseEnded
                && eligiblePlayersGone
                && acceptedUsersEmpty
                && pendingDepartureClear
                && backendReadySynced;
        }
    }
}
