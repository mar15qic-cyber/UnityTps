using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Connection;
using FishNet.Managing;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// FishNet join-ticket 认证器（Docs/27 Day1 §4.1 C2，冻结契约 §2.2/§2.3）。
    /// 服务器侧：收到客户端票据 broadcast → 同步前置判定（重复/攻击/空票据 fail closed）
    /// → 后端 consume（X-Server-Key，10s 超时）→ 先回结果 broadcast、再一次 OnAuthenticationResult。
    ///   任何失败（invalid/expired/replayed/instance-mismatch/后端不可达/超时）都拒绝；
    ///   认证通过前 FishNet 不会触发场景同步与 PlayerSpawner（ServerManager 原生门控）。
    /// 客户端侧：连接 Started 后经 JoinTicketClientSession 构建一次性票据消息——构建瞬间即清会话明文
    ///   （Broadcast 只需消息副本；断线时 ClearPlaintext 兜底）。
    /// 安全红线：日志只出现 TicketHashPrefix 的 8 位摘要，绝不记录完整票据与 X-Server-Key。
    /// 接线约定：服务器侧由 DedicatedServerBootstrap（正式）/ NetworkHud F1（调试）AddComponent
    /// 后经 ServerManager.SetAuthenticator 初始化；客户端侧由 NetworkHud 手动 InitializeOnce。
    /// </summary>
    public sealed class JoinTicketAuthenticator : Authenticator
    {
        /// <summary>服务器进程内认证结论事件（FishNet 内部订阅，完成连接或踢出）。</summary>
        public override event Action<NetworkConnection, bool> OnAuthenticationResult;

        /// <summary>票据验证通过后的扩展事件（userId/username/roomCode）——Day2 比赛生命周期消费 seam。</summary>
        public event Action<NetworkConnection, TicketConsumeResult> TicketAccepted;

        /// <summary>
        /// 服务器侧：远端连接停止（主动退出/超时/被踢）且身份档案与在途状态清理完成后触发。
        /// 载荷 = 删除【前】的 TicketConsumeResult 身份快照（Day2 掉线端点接入：订阅者据此得知
        /// WHO 掉线——userId/roomCode；null = 该连接从未认证通过，掉线端点不得为其调用）。
        /// Day2 掉线生命周期消费 seam（Bootstrap 据此即时心跳同步 CurrentPlayers 事实 + 掉线上报）。
        /// </summary>
        public event Action<NetworkConnection, TicketConsumeResult> ClientConnectionCleaned;

        /// <summary>客户端侧：收到服务器认证结果（无论接受/拒绝）——认证失败 UI 降级 seam（Day2）。</summary>
        public event Action<JoinTicketResultBroadcast> ClientResultReceived;

        /// <summary>客户端自身稳定 userId（接受结果携带；空串=调试旁路/未收到——由 MatchPlayerIdentity 回退）。</summary>
        public string SelfUserId { get; private set; } = string.Empty;

        /// <summary>客户端侧：本连接是否已进入认证流程（票据已发或本地 fail-closed）——认证失败通用降级的判定输入。</summary>
        public bool ClientAuthAttempted { get; private set; }

        /// <summary>
        /// 客户端侧：是否已尝试发起客户端连接（Day2 补缺：与 ClientAuthAttempted 区分）。
        /// NetworkHud 在调用 StartConnection【之前】登记——连接从未建立（服务器不可达/端口关闭，
        /// LocalConnectionState 从未 Started、票据从未发送）时，断线降级只认这个信号。
        /// </summary>
        public bool ClientConnectionAttempted { get; private set; }

        /// <summary>登记"即将发起客户端连接"（NetworkHud 在 StartConnection 前调用；幂等置位，不回退）。</summary>
        public void MarkClientConnectionAttempted()
        {
            ClientConnectionAttempted = true;
        }

        /// <summary>
        /// 新客户端会话前的状态复位（2026-09-15 P0-B：常驻 NetworkManager 跨局复用同一认证器实例，
        /// 上局的尝试标记/身份缓存不得泄漏进新局）。ClientMatchSessionCoordinator 在每局连接前调用。
        /// 票据明文本身由 ConfigureClient 重建的 JoinTicketClientSession 承载（旧会话对象整体替换）。
        /// </summary>
        public void ResetClientSessionState()
        {
            ClientAuthAttempted = false;
            ClientConnectionAttempted = false;
            SelfUserId = string.Empty;
        }

        private const double AuthTimeoutSeconds = 10.0;

        /// <summary>拒绝投递窗口硬上限（Codex 终审 F1：≤250ms——超限宁可断开不拖延，fail closed 优先于投递）。提交信息中的 dual-channel 描述为历史过程，最终实现以本文件为准：OnPostTick 冲刷信号 + 250ms 上限。</summary>
        internal const double RejectionDeliveryCapSeconds = 0.250;

        // ---- 注入配置（接线方在 InitializeOnce 前调用；后调用会重建决策核心） ----
        private IServerControlPlaneClient _controlPlane;
        private bool _allowUnsafeDebugAuth;
        private bool _isHostProcess;
        private JoinTicketClientSession _clientSession;

        private JoinTicketAuthService _service;
        private readonly AuthDeadlineTracker _deadlineTracker = new(AuthTimeoutSeconds);
        private readonly HashSet<int> _inFlightValidations = new();
        private readonly Dictionary<int, TicketConsumeResult> _acceptedUsers = new();
        private readonly PendingRejectionTracker _pendingRejections = new();
        /// <summary>TimeManager.OnPostTick 计数——每次递增代表一次出队冲刷已完成（tick 循环内 OnPostTick 紧邻 TryIterateData(false)）。</summary>
        private ulong _postTickFlushCount;
        /// <summary>待定拒绝的最后一次错误码（REJECT_FINALIZED 补发不可靠副本时重建消息用；Stopped 时清理）。</summary>
        private readonly Dictionary<int, string> _lastRejectionCodes = new();

        /// <summary>已认证连接的身份档案（key=ClientId；Day2 结算按稳定 playerId 幂等提交的依据）。</summary>
        public IReadOnlyDictionary<int, TicketConsumeResult> AcceptedUsers => _acceptedUsers;

        // ------------------------------------------------------------------
        // 服务器侧配置（DedicatedServerBootstrap / F1 调试 Host 接线）
        // ------------------------------------------------------------------

        /// <summary>注入控制面与 unsafe debug 开关（须在 ServerManager 初始化本认证器之前）。
        /// 本进程即服务器（Dedicated / F1 调试 Host）→ 本地客户端跳过票据发送
        ///（FishNet 对本地连接原生免认证，见 ServerManager OnRemoteConnection 分支）。</summary>
        public void ConfigureServer(IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth)
        {
            _controlPlane = controlPlane;
            _allowUnsafeDebugAuth = allowUnsafeDebugAuth;
            _isHostProcess = true;
            RebuildService();
        }

        /// <summary>确保 NetworkManager 对象上存在已配置的服务器侧认证器（幂等）。</summary>
        public static JoinTicketAuthenticator EnsureServerAuthenticator(
            NetworkManager networkManager, IServerControlPlaneClient controlPlane, bool allowUnsafeDebugAuth)
        {
            var authenticator = networkManager.gameObject.GetComponent<JoinTicketAuthenticator>();
            if (authenticator == null)
                authenticator = networkManager.gameObject.AddComponent<JoinTicketAuthenticator>();
            authenticator.ConfigureServer(controlPlane, allowUnsafeDebugAuth);
            return authenticator;
        }

        // ------------------------------------------------------------------
        // 客户端侧配置（NetworkHud 接线）
        // ------------------------------------------------------------------

        /// <summary>注入一次性票据（会话化：构建消息即清明文）；断线时兜底清理。无票据调试需显式 unsafe 开关。</summary>
        public void ConfigureClient(string joinTicket, bool allowUnsafeDebugAuth)
        {
            _clientSession = new JoinTicketClientSession(joinTicket);
            _allowUnsafeDebugAuth = allowUnsafeDebugAuth;
            _isHostProcess = false;
        }

        /// <summary>确保 NetworkManager 对象上存在已配置的客户端侧认证器（幂等）。</summary>
        public static JoinTicketAuthenticator EnsureClientAuthenticator(
            NetworkManager networkManager, string joinTicket, bool allowUnsafeDebugAuth)
        {
            var authenticator = networkManager.gameObject.GetComponent<JoinTicketAuthenticator>();
            if (authenticator == null)
                authenticator = networkManager.gameObject.AddComponent<JoinTicketAuthenticator>();
            authenticator.ConfigureClient(joinTicket, allowUnsafeDebugAuth);
            if (!authenticator.Initialized)
                authenticator.InitializeOnce(networkManager);
            return authenticator;
        }

        /// <summary>FishNet 接线：服务端预认证 broadcast + 客户端状态订阅。</summary>
        public override void InitializeOnce(NetworkManager networkManager)
        {
            base.InitializeOnce(networkManager);
            RebuildService();
            // F1 修复：订阅 OnPostTick 计数——tick 循环内 OnPostTick 紧邻 TryIterateData(false)（出队冲刷），
            // 计数递增即代表上一 tick 出队冲刷已完成（拒绝广播的送达信号）
            NetworkManager.TimeManager.OnPostTick += OnPostTickFlush;
            NetworkManager.ClientManager.OnClientConnectionState += ClientManager_OnClientConnectionState;
            NetworkManager.ClientManager.RegisterBroadcast<JoinTicketResultBroadcast>(OnResultBroadcast);
            // requireAuthenticated=false：票据必须在认证完成前可达
            NetworkManager.ServerManager.RegisterBroadcast<JoinTicketBroadcast>(OnTicketBroadcast, false);
            NetworkManager.ServerManager.OnRemoteConnectionState += ServerManager_OnRemoteConnectionState;
        }

        /// <summary>OnPostTick 计数递增（tick 循环内紧邻 TryIterateData(false)——出队冲刷完成信号）。</summary>
        private void OnPostTickFlush()
        {
            _postTickFlushCount++;
        }

        private void RebuildService()
        {
            // P0-A：认证前协议门恒开（生产/调试 Host 同源）——旧客户端（空 ProtocolId）一律
            // PROTOCOL_MISMATCH 零成本拒绝；null 仅保留给未接线的纯测试构造
            _service = new JoinTicketAuthService(_controlPlane, _allowUnsafeDebugAuth, AuthTimeoutSeconds,
                expectedProtocolId: GameProtocolIdentity.ProtocolId);
        }

        // ------------------------------------------------------------------
        // 服务器侧
        // ------------------------------------------------------------------

        /// <summary>服务器收到新远端连接：登记 10s 认证截止时间（超时强制断开）。</summary>
        public override void OnRemoteConnection(NetworkConnection connection)
        {
            _pendingRejections.Cancel(connection.ClientId); // 连接 id 复用防御：新连接不得继承旧待定
            _deadlineTracker.Mark(connection.ClientId, NowSeconds());
            Debug.Log($"[JoinTicketAuthenticator] PENDING conn={connection.ClientId} deadline={AuthTimeoutSeconds:0}s");
        }

        private void ServerManager_OnRemoteConnectionState(NetworkConnection connection, FishNet.Transporting.RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != FishNet.Transporting.RemoteConnectionState.Stopped)
                return;
            _pendingRejections.Cancel(connection.ClientId); // F1：客户端主动断开——待定拒绝作废，连接已死绝不触发结果回调
            _lastRejectionCodes.Remove(connection.ClientId);
            _deadlineTracker.Remove(connection.ClientId);
            _inFlightValidations.Remove(connection.ClientId);
            // Day2 掉线端点接入：先取身份快照再删除——事件订阅者拿到的是删除前的权威身份
            //（null = 从未认证通过，订阅者不得为其调用掉线端点）
            _acceptedUsers.TryGetValue(connection.ClientId, out TicketConsumeResult identitySnapshot);
            bool wasAccepted = _acceptedUsers.Remove(connection.ClientId);
            // Day2 掉线生命周期：AcceptedUsers 等服务器进程内档案在此收敛（FishNet OnRemoteConnectionState
            // 先于 Objects.ClientDisconnected 的 despawn——订阅者此刻仍能按连接找到玩家对象）
            Debug.Log($"[JoinTicketAuthenticator] CONNECTION_CLEANED conn={connection.ClientId} acceptedProfile={(wasAccepted ? "removed" : "none")} user={(wasAccepted && identitySnapshot != null ? identitySnapshot.UserId : "-")}");
            ClientConnectionCleaned?.Invoke(connection, wasAccepted ? identitySnapshot : null);
        }

        private void OnTicketBroadcast(NetworkConnection connection, JoinTicketBroadcast message, FishNet.Transporting.Channel channel)
        {
            var decision = _service.EvaluateIncoming(
                connection.IsAuthenticated, _inFlightValidations.Contains(connection.ClientId), message);

            switch (decision)
            {
                case JoinTicketAuthService.IncomingDecision.DisconnectAttacker:
                    Debug.LogWarning($"[JoinTicketAuthenticator] ATTACK disconnect conn={connection.ClientId} (auth broadcast after authenticated)");
                    connection.Disconnect(true);
                    return;
                case JoinTicketAuthService.IncomingDecision.IgnoreDuplicate:
                    Debug.LogWarning($"[JoinTicketAuthenticator] DUPLICATE auth broadcast ignored conn={connection.ClientId}");
                    return;
                case JoinTicketAuthService.IncomingDecision.Reject:
                    FinishAuthentication(connection, TicketConsumeResult.Rejected("TICKET_INVALID"), message);
                    return;
                case JoinTicketAuthService.IncomingDecision.RejectProtocolMismatch:
                    // P0-A：协议代际不匹配（含旧客户端空字段）——零成本拒绝（不消费后端票据）；
                    // 客户端收 PROTOCOL_MISMATCH 拒绝广播后显示版本不匹配并返回大厅重新取票
                    Debug.LogWarning($"[JoinTicketAuthenticator] PROTOCOL_MISMATCH conn={connection.ClientId} clientProtocol={(string.IsNullOrEmpty(message.ProtocolId) ? "<none/legacy>" : message.ProtocolId)} expected={GameProtocolIdentity.ProtocolId}——拒绝（不消费票据）");
                    FinishAuthentication(connection, TicketConsumeResult.Rejected(GameProtocolIdentity.ProtocolMismatchCode), message);
                    return;
                case JoinTicketAuthService.IncomingDecision.BeginValidation:
                    if (!MapContentIdentity.Matches(Environment.GetEnvironmentVariable("FPS_MAP_CONTENT_HASH"), message.MapContentHash))
                    {
                        FinishAuthentication(connection, TicketConsumeResult.Rejected("MAP_CONTENT_MISMATCH"), message);
                        return;
                    }
                    RunValidation(connection, message);
                    return;
            }
        }

        private async void RunValidation(NetworkConnection connection, JoinTicketBroadcast message)
        {
            int clientId = connection.ClientId;
            _inFlightValidations.Add(clientId);
            try
            {
                var result = await _service.ValidateAsync(message);
                if (!IsConnectionStillPending(clientId))
                {
                    // 验证期间连接已被断开/清理：结果作废，绝不二次认证
                    Debug.LogWarning($"[JoinTicketAuthenticator] STALE result dropped conn={clientId}");
                    return;
                }
                FinishAuthentication(connection, result, message);
            }
            catch (Exception exception)
            {
                // fail closed：决策核心内部已兜底，此分支只是最后防线
                Debug.LogException(exception);
                if (IsConnectionStillPending(clientId))
                    FinishAuthentication(connection, TicketConsumeResult.Rejected("AUTH_BACKEND_UNREACHABLE"), message);
            }
            // 不在此处移除在途标记：接受路径由 FinishAuthentication 移除；拒绝路径保留至
            // REJECT_FINALIZED 收口（F1 修复——待定窗口内二次认证被 EvaluateIncoming 判 Duplicate 忽略）；
            // 客户端断开由 ServerManager_OnRemoteConnectionState 移除。
        }

        private bool IsConnectionStillPending(int clientId)
        {
            return _deadlineTracker.Contains(clientId) && _inFlightValidations.Contains(clientId);
        }

        /// <summary>
        /// 结果收口（Codex 终审 F1 修复，OnPostTick 钩子方案）。投递机制查证 + 真实构建 5 轮判别实验结论：
        /// ① Broadcast 全链无发送完成回调（LiteNetLib TriggerUpdate 仅异步唤醒后台线程）；
        /// ② 广播先写入连接级 PacketBundle（_outgoing 不经此层），TimeManager tick 循环顺序 =
        ///   OnPostTick → PredictionManager.SendStateUpdate → TryIterateData(false)（出队冲刷 →
        ///   ServerSocket._outgoing → NetPeer.Send → LiteNetLib 通道队列）→ 后台线程上线——
        ///   **因此 OnPostTick 时的广播会在本 tick 的 TryIterateData(false) 中被同步冲刷上线**；
        /// ③ 真实构建判别实验：仅 timer 窗口（2×TickDelta=67ms，任意相位）广播 3/3 不达（单发小包
        ///   在 LiteNetLib 合并缓冲区滞留——接受路径靠后续场景同步流量推挤才出）；不踢人时双通道
        ///   3/3 送达——断开并非直接杀包，而是包在合并缓冲区滞留过久后被断开截断；
        /// ④ conn.Disconnect → NetPeer.Shutdown【同步直发】断开包。
        /// 修复（明确的发送机制）：拒绝结果入队后订阅【下一次 TimeManager.OnPostTick】——该事件返回后
        /// 本 tick 的 TryIterateData(false) 立即冲刷本广播；冲刷完成后才触发 OnAuthenticationResult(false)
        /// =断开（首帧无 OnPostTick 到达时由 Update 兜底）。硬上限 250ms（超限 fail closed——断开优先于投递）。
        /// 待定窗口语义（Codex §一.4 全项）：连接保持未认证（PlayerSpawner 不触发）；二次认证被在途集合
        /// 判 Duplicate 忽略；客户端主动断开由 Stopped 处理 Cancel（不触发结果回调）；
        /// OnAuthenticationResult 恰一次（PendingRejectionTracker.CollectDue 一次性取出）。
        /// </summary>
        private void FinishAuthentication(NetworkConnection connection, TicketConsumeResult result, JoinTicketBroadcast request)
        {
            // I3 Pending 容量终验（Docs/26 §2.4）：TDM 队伍容量在【结果广播之前】做 DS 侧终验——
            // 占用 = 已生成玩家 + Pending 认证连接（AcceptedUsers 中尚无生成对象的同队档案），
            // 超员转 DS 本地拒绝（TEAM_FULL，票据已消费——后端重进即补票，fail closed 优先于超员放行）。
            if (result.Accepted && !IsTeamCapacityAvailable(result))
            {
                Debug.LogWarning($"[JoinTicketAuthenticator] TEAM_FULL room={result.RoomCode} team={result.TeamId} user={result.UserId}——DS 容量终验拒绝");
                result = TicketConsumeResult.Rejected("TEAM_FULL");
            }

            // 结果广播先行。接受路径：连接存活，可靠通道随同步流量必达，立即 OnAuthenticationResult(true)。
            // Day2：接受结果附带稳定 userId/username（客户端身份匹配依据）；拒绝路径恒空串。
            NetworkManager.ServerManager.Broadcast(
                connection,
                new JoinTicketResultBroadcast
                {
                    Accepted = result.Accepted,
                    ErrorCode = result.ErrorCode,
                    UserId = result.Accepted ? result.UserId : string.Empty,
                    Username = result.Accepted ? result.Username : string.Empty,
                }, false);

            if (result.Accepted)
            {
                _deadlineTracker.Remove(connection.ClientId);
                _inFlightValidations.Remove(connection.ClientId);
                // 租约代际事件必须先于 AcceptedUsers/OnAuthenticationResult：若旧局刚释放便被新房
                // 抢租，Bootstrap 需要在首个新连接进入认证档案、生成玩家对象之前完成 Ended→Idle
                // 重臂。否则 AcceptedUsers 非空会永久关住重臂门，整间新房只能退出后自愈。
                TicketAccepted?.Invoke(connection, result);
                if (result.DebugBypass)
                {
                    Debug.LogWarning($"[JoinTicketAuthenticator] ACCEPTED (unsafe debug bypass) conn={connection.ClientId}——仅 Editor/Development 合法");
                }
                else
                {
                    _acceptedUsers[connection.ClientId] = result;
                    // Day2 三缺口：accepted 身份墓碑——认证接受瞬间记录 ClientId→userId，
                    // 使 MatchLifecycle 对离开者身份的读取与 Stopped 事件订阅顺序无关（AcceptedUsers
                    // 清理竞态下墓碑仍可解析；ClientId 复用时新 accept 覆盖）
                    MatchPlayerIdentity.RecordServerAccepted(connection.ClientId, result.UserId);
                    // 同 UserId 单活（2026-09-08 追加 P0 §6 三.4，审计 §5.1 缺口 4）：新连接接管——
                    // 新档案已入表（顺序关键），此刻断开同 userId 的旧连接；旧连接迟到的 Stopped 会经
                    // ClientConnectionCleaned 携带快照，但 PlayerDisconnectQueue.Notify 以"同 userId
                    // 仍在认证档案中"判 SkippedUserReconnected——新会话成员资格与玩家对象不被误清。
                    int? oldClientId = FindExistingConnectionForUser(_acceptedUsers, result.UserId, connection.ClientId);
                    if (oldClientId.HasValue
                        && NetworkManager.ServerManager.Clients.TryGetValue(oldClientId.Value, out var superseded))
                    {
                        Debug.LogWarning($"[JoinTicketAuthenticator] USER_TAKEOVER user={result.UserId} oldConn={oldClientId.Value} -> newConn={connection.ClientId}（同账号单活：新连接接管，断开旧连接）");
                        superseded.Disconnect(true);
                    }
                    Debug.Log($"[JoinTicketAuthenticator] ACCEPTED room={result.RoomCode} user={result.UserId} name={result.Username} conn={connection.ClientId}");
                }
                OnAuthenticationResult?.Invoke(connection, true);
                return;
            }

            // 拒绝路径（F1 修复核心）：登记待定——收口条件=「广播冲刷已发生」（OnPostTick 计数推进）
            // 或 250ms 上限到（fail closed——断开优先于投递）。10s 认证截止继续有效兜底。
            _pendingRejections.Mark(connection.ClientId, NowSeconds() + RejectionDeliveryCapSeconds, _postTickFlushCount);
            _lastRejectionCodes[connection.ClientId] = string.IsNullOrEmpty(result.ErrorCode) ? "TICKET_INVALID" : result.ErrorCode;
            Debug.LogWarning($"[JoinTicketAuthenticator] REJECTED code={result.ErrorCode} conn={connection.ClientId} ticketHash={JoinTicketAuthService.TicketHashPrefix(request.Ticket)}（结果已入队，待 OnPostTick 冲刷后断开；上限 {RejectionDeliveryCapSeconds * 1000.0:0}ms）");
        }

        private void Update()
        {
            if (NetworkManager == null)
                return; // 尚未 InitializeOnce 的窗口期（正常接线在同帧完成，防御性兜底）

            // F1 修复：拒绝待定收口（二元条件，先到先收）——
            // ① 广播冲刷已发生：OnPostTick 计数相对登记时推进（tick 循环内 OnPostTick → TryIterateData(false)，
            //    钩子后下一次计数递增即代表本广播已经过冲刷点）；② 250ms 上限到（fail closed——断开优先于投递）。
            // CollectDue 一次性取出（同连接绝不二次触发）；客户端断开已由 Stopped 处理 Cancel（不触发结果回调）。
            var dueRejections = _pendingRejections.CollectDue(_postTickFlushCount, NowSeconds());
            for (int i = 0; i < dueRejections.Count; i++)
            {
                int clientId = dueRejections[i];
                _inFlightValidations.Remove(clientId);
                _deadlineTracker.Remove(clientId);
                if (NetworkManager.ServerManager.Clients.TryGetValue(clientId, out var rejectedConnection))
                {
                    Debug.LogWarning($"[JoinTicketAuthenticator] REJECT_FINALIZED conn={clientId}（广播冲刷完成或上限到——补发不可靠副本后断开）");
                    // 补发不可靠副本：直发队列不经可靠通道簿记（Development 判别实验 3/3 送达）。
                    // 客户端 OnResultBroadcast 对同一错误码幂等，重复副本无害；本帧后 ServerManager 断开。
                    // 拒绝语义不携带身份信息：UserId/Username 恒空串（Day2 契约）。
                    NetworkManager.ServerManager.Broadcast(
                        rejectedConnection,
                        new JoinTicketResultBroadcast
                        {
                            Accepted = false,
                            ErrorCode = _lastRejectionCodes.TryGetValue(clientId, out var code) ? code : "TICKET_INVALID",
                            UserId = string.Empty,
                            Username = string.Empty,
                        }, false,
                        FishNet.Transporting.Channel.Unreliable);
                    OnAuthenticationResult?.Invoke(rejectedConnection, false);
                }
            }

            // 纯服务器进程才需要超时清场；客户端进程 _deadlineTracker 恒空，零开销
            if (_deadlineTracker.Count == 0)
                return;
            var expired = _deadlineTracker.CollectExpired(NowSeconds());
            for (int i = 0; i < expired.Count; i++)
            {
                _inFlightValidations.Remove(expired[i]);
                Debug.LogWarning($"[JoinTicketAuthenticator] REJECTED code=AUTH_TIMEOUT conn={expired[i]} (no valid ticket within {AuthTimeoutSeconds:0}s)");
                if (NetworkManager.ServerManager.Clients.TryGetValue(expired[i], out var connection))
                    connection.Disconnect(true);
            }
        }

        // ------------------------------------------------------------------
        // 客户端侧
        // ------------------------------------------------------------------

        private void ClientManager_OnClientConnectionState(FishNet.Transporting.ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Started)
            {
                if (_isHostProcess)
                {
                    // 本进程即服务器（F1 调试 Host）：本地连接由 FishNet 原生免认证，不发票据
                    Debug.Log("[JoinTicketAuthenticator] HOST local client skips ticket auth");
                    return;
                }
                ClientAuthAttempted = true; // Day2：通用降级判定输入（fail-closed 分支同样算已尝试）
                if (_clientSession == null)
                {
                    Debug.LogError("[JoinTicketAuthenticator] 客户端会话未配置（ConfigureClient 未调用）：拒绝连接（fail closed）");
                    NetworkManager.ClientManager.StopConnection();
                    return;
                }

                var decision = _clientSession.TryBuildAuthBroadcast(
                    _allowUnsafeDebugAuth, out JoinTicketBroadcast message, out string ticketHashPrefix, out string reason);
                switch (decision)
                {
                    case JoinTicketClientSession.SendDecision.AlreadyConsumed:
                        Debug.LogWarning($"[JoinTicketAuthenticator] 忽略重复的连接 Started（会话已消费，旧票据不复用）：{reason}");
                        return;

                    case JoinTicketClientSession.SendDecision.FailClosedNoTicket:
                        Debug.LogError($"[JoinTicketAuthenticator] {reason}：拒绝连接（fail closed）");
                        NetworkManager.ClientManager.StopConnection();
                        return;

                    case JoinTicketClientSession.SendDecision.Send:
                        // TryBuild 返回时会话明文已清除（消息对象是唯一持有者）——
                        // Broadcast 同步序列化入队后，本组件内不再存在可发送的明文票据
                        NetworkManager.ClientManager.Broadcast(message);
                        Debug.Log($"[JoinTicketAuthenticator] CLIENT_AUTH_SENT ticketHash={ticketHashPrefix}（会话明文已清）");
                        return;
                }
            }
            else if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Stopped)
            {
                _clientSession?.ClearPlaintext(); // 断线兜底清理（正常路径在构建消息时已清）
            }
        }

        private void OnResultBroadcast(JoinTicketResultBroadcast message, FishNet.Transporting.Channel channel)
        {
            if (message.Accepted)
            {
                // Day2：记录自身稳定身份（空串=调试旁路/降级——MatchPlayerIdentity 按 debug/ClientId 回退）
                SelfUserId = message.UserId ?? string.Empty;
                Debug.Log($"[JoinTicketAuthenticator] CLIENT auth accepted by server user={SelfUserId}");
            }
            else
            {
                Debug.LogWarning($"[JoinTicketAuthenticator] CLIENT auth rejected by server code={message.ErrorCode}");
            }
            ClientResultReceived?.Invoke(message);
        }

        /// <summary>
        /// 同 UserId 旧连接查找（纯函数，EditMode 锁定，2026-09-08 §6 三.4）：
        /// 在已认证档案中查找与 userId 相同、且不是排除连接（本新连接）的旧 ClientId；
        /// 空用户 Id（调试旁路）不参与单活。返回 null = 无需接管。
        /// </summary>
        internal static int? FindExistingConnectionForUser(
            IReadOnlyDictionary<int, TicketConsumeResult> acceptedUsers, string userId, int excludeClientId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            foreach (var pair in acceptedUsers)
            {
                if (pair.Key == excludeClientId) continue;
                if (pair.Value != null && pair.Value.UserId == userId) return pair.Key;
            }
            return null;
        }

        // ---- I3：TDM Pending 容量终验（Docs/26 §2.4——Pending 连接计入名额） ----

        /// <summary>
        /// 队伍容量终验（接受路径、结果广播前；R7 审计修复后委托 JoinTicketCapacityPolicy 纯核心）：
        /// 占用 = 已生成的同队有效玩家 + 认证档案中尚无生成对象的同队 Pending 连接，但【扣除与本次
        /// 接入同 userId 的既有连接】——该连接随即被单活接管断开，名额由本次顶替（满队同账号重连
        /// 不再被误判超员）。不同用户的 Pending 计入名额（容量红线不变）。
        /// 非 TDM / 旧票（MatchMode 空）/ 容量字段缺失 → 不设限（旧语义）。
        /// </summary>
        private bool IsTeamCapacityAvailable(TicketConsumeResult profile)
        {
            if (profile == null) return true;
            return JoinTicketCapacityPolicy.CanAccept(
                profile.MatchMode, profile.MaxPlayers, profile.TeamId, profile.UserId, _acceptedUsers.Values);
        }

        private static double NowSeconds()
        {
            return Time.unscaledTimeAsDouble;
        }
    }
}
