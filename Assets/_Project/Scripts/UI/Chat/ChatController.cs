using System;
using System.Collections.Generic;
using FishNet;
using Game.Account;
using Game.Gameplay.Menu;
using Game.Gameplay.Network;
using UnityEngine;

namespace Game.UI.Chat
{
    /// <summary>
    /// 聊天控制器（C4/I2，Docs/27 §8.4 + 复审 R06）：等待期 HTTP（游标增量拉取）与局内 Owner RPC
    /// 的传输协调——按阶段自动切换（InMatch→RPC；Waiting/Starting/Returning→HTTP）；
    /// clientMessageId 端到端去重 + (transport, epoch, seq) 服务端消息身份去重；客户端令牌桶预检
    /// （服务器权威执法兜底）；可见历史 30 条；退出房间清空、同一房间返房保留。
    /// 纯协调层：HTTP 走 ApiClient，RPC 走 NetworkCombatAuthority 静态事件/SubmitChatRequest。
    /// </summary>
    public sealed class ChatController : MonoBehaviour
    {
        private const float HttpFetchIntervalSeconds = 2f;
        private const float FetchFailureLogIntervalSeconds = 15f;
        private const int DedupCapacity = 256;
        private const string TransportHttp = "H";
        private const string TransportRpc = "R";

        private ChatHudView _view;
        private IApiClient _api;
        private AccountSession _session;
        private NetworkCombatAuthority _localPlayer;
        private Canvas _viewCanvas;
        private bool _viewBound;
        /// <summary>房间会话代际（审计 2026-09-16 §5.3-5）：换房/退房递增，在途 HTTP 回包按
        /// (roomCode, generation) 双重校验——退出后**重进同一房间**的旧回包也不得写入新会话。</summary>
        private int _roomGeneration;
        private float _nextFetchAtRealtime;
        private float _nextFetchFailureLogRealtime;
        private double _clientTokens = ChatRules.TokenCapacity;
        private DateTime _clientTokensAt = DateTime.UtcNow;

        // 2026-09-13 矩阵 D：RPC 聊天确认闭环（发送→回显确认→清草稿 / 超时→还原草稿）
        private const float RpcConfirmTimeoutSeconds = 5f;
        private string _pendingRpcMessageId;
        private string _pendingRpcBody;
        private float _pendingRpcAtRealtime;
        private bool _rpcSubscribed;
        private bool _running;

        public static ChatController Instance { get; private set; }

        /// <summary>挂载/确保运行（幂等）：视图 + 控制器挂**同一目标画布**（等待房间页与 Arena HUD 各自调用）。
        /// R9：消息/去重/游标持久在 ChatRoomSession（房间作用域）——返房跨场景重建后从会话补水，
        /// 离房（roomCode 变化/显式 Reset）才清空。
        /// 审计 2026-09-16 §5.3-4：不允许任意画布上的旧 Hud 冒充本次挂载；反复调用只保留一套 UI、
        /// 一份事件订阅（BindViewOnce 守卫，修复"再次入房重复 += 事件 → 同一次发送多个请求"）。</summary>
        public static ChatController EnsureRunning(Canvas canvas, IApiClient api, AccountSession session)
        {
            if (Instance != null && Instance._running && Instance._view != null && canvas != null)
            {
                var viewCanvas = Instance._view.GetComponentInParent<Canvas>();
                if (viewCanvas != null && viewCanvas != canvas)
                {
                    // 目标画布变化（房间页重建/战斗 HUD）：卸载旧画布上的 UI，重建到目标画布
                    TeardownView(Instance, keepSession: true);
                    Instance = null;
                }
            }
            if (Instance != null && Instance._running)
            {
                Instance._api = api;
                Instance._session = session;
                Instance.BindViewOnce();
                if (session?.Room?.RoomCode != null) Instance.EnsureRoomGeneration(session.Room.RoomCode);
                Instance._view?.SetInteractive(true);
                return Instance;
            }
            var view = ChatHudView.TryMount(canvas);
            if (view == null) return null;
            var controllerGo = view.gameObject;
            var controller = controllerGo.GetComponent<ChatController>();
            if (controller == null) controller = controllerGo.AddComponent<ChatController>();
            controller._view = view;
            controller._viewCanvas = canvas;
            controller._api = api;
            controller._session = session;
            controller._running = true;
            Instance = controller;
            controller.BindViewOnce();
            if (session?.Room?.RoomCode != null) controller.EnsureRoomGeneration(session.Room.RoomCode);
            controller.SubscribeRpc();
            // R9：从房间会话补水（跨场景保留的可见消息；直接进视图显示层，不再走去重）
            foreach (var message in ChatRoomSession.Messages)
                view.Push(message);
            return controller;
        }

        /// <summary>视图事件只绑定一次（审计 §5.3-4：旧实现每次 EnsureRunning 都 +=，重复入房后
        /// 同一次发送会触发多个 SendRequested → 多个 clientMessageId，服务端去重也合并不了）。</summary>
        private void BindViewOnce()
        {
            if (_viewBound || _view == null) return;
            _viewBound = true;
            _view.SendRequested += HandleSendRequested;
            _view.Closed += HandleViewClosed;
            _view.ChannelChanged += HandleChannelChanged;
        }

        private void UnBindView()
        {
            if (!_viewBound || _view == null) return;
            _viewBound = false;
            _view.SendRequested -= HandleSendRequested;
            _view.Closed -= HandleViewClosed;
            _view.ChannelChanged -= HandleChannelChanged;
        }

        /// <summary>房间会话代际推进：房间码变化 = 新会话（旧回包全部作废）。</summary>
        private void EnsureRoomGeneration(string roomCode)
        {
            if (ChatRoomSession.IsRoom(roomCode)) return;
            ChatRoomSession.EnsureRoom(roomCode);
            _roomGeneration++;
        }

        /// <summary>
        /// 幂等房间会话退出入口（审计 2026-09-16 §5.3-1）：停止收发、作废在途请求、解绑全部事件、
        /// 清 pending/草稿/焦点，并**真正卸载**聊天 UI（SetActive(false)+Destroy——旧实现只清数据，
        /// Canvas 下的 Hud 继续运行，Enter 还能重新打开，窗口残留到大厅/登录页）。
        /// 无实例也清静态 ChatRoomSession（旧实现直接 return，静态历史残留）。
        /// </summary>
        public static void StopAndClear()
        {
            var controller = Instance;
            if (controller == null)
            {
                ChatRoomSession.Reset(string.Empty);
                return;
            }
            TeardownView(controller, keepSession: false);
            if (Instance == controller) Instance = null; // 身份核对（防替换实例误清）
        }

        /// <summary>留房跨页/跨场景过渡：卸载 UI 与收发，保留房间会话历史（返房补水、局内消息不丢）。</summary>
        public static void StopKeepSession()
        {
            var controller = Instance;
            if (controller == null) return;
            TeardownView(controller, keepSession: true);
            if (Instance == controller) Instance = null;
        }

        /// <summary>
        /// 页面上下文兜底（LobbyPresenter.Navigate 唯一入口调用，审计 §5.3-3）：当前页不允许聊天时，
        /// 卸载挂在该画布上的聊天 UI（保留房间会话——留房跨页/返房不丢历史）。其它画布（Arena HUD）的
        /// 实例不受影响。
        /// </summary>
        public static void SetPageContext(Canvas canvas, bool chatAllowed)
        {
            if (chatAllowed) return;                       // 允许页由 EnsureRunning 负责挂载
            var controller = Instance;
            if (controller == null || controller._view == null) return;
            var viewCanvas = controller._view.GetComponentInParent<Canvas>();
            if (canvas != null && viewCanvas != null && viewCanvas != canvas) return;
            TeardownView(controller, keepSession: true);
            if (Instance == controller) Instance = null;
        }

        private static void TeardownView(ChatController controller, bool keepSession)
        {
            controller._running = false;
            controller._roomGeneration++; // 在途 Fetch/Send 回包全部作废（§5.3-5）
            if (controller._view != null)
            {
                controller._view.SetInteractive(false);   // 关展开窗 + 释放焦点/IME（先于 Destroy）
                controller._view.ForceCloseAndRelease();
                if (!keepSession) controller._view.ClearHistory();
                controller._view.ClearDraft();
                controller.UnBindView();
                DestroyChatRoot(controller._view.gameObject);
            }
            controller._view = null;
            controller._viewCanvas = null;
            if (controller._rpcSubscribed)
            {
                NetworkCombatAuthority.OnChatMessageReceived -= controller.HandleRpcMessage;
                controller._rpcSubscribed = false;
            }
            controller._pendingRpcMessageId = null; // RPC pending 草稿计时一并清（§5.3-5）
            controller._pendingRpcBody = null;
            controller._localPlayer = null;
            GameplayInputGate.SetChatFocused(false);
            if (!keepSession) ChatRoomSession.Reset(string.Empty);
        }

        /// <summary>卸载聊天根：先 SetActive(false)（当帧起不可交互、不再能被激活实例找到）再销毁。
        /// 编辑器（EditMode 测试/工具）用 DestroyImmediate——Destroy 在编辑器上下文非法。</summary>
        private static void DestroyChatRoot(GameObject root)
        {
            if (root == null) return;
            root.SetActive(false);
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return;
            }
#endif
            UnityEngine.Object.Destroy(root);
        }

        private void OnDestroy()
        {
            // 场景卸载（画布销毁）：清静态实例与 RPC 订阅，防陈旧实例阻塞下一场景挂载。
            // 审计 §5.3-2：**不**清 ChatRoomSession——留房跨场景返房要保留历史；
            // 房间历史只由显式退出入口（StopAndClear）清。
            // 审计 §5.2：身份核对必须稳健（多实例/替换场景下 Instance 可能已不是 this）。
            if (_rpcSubscribed)
            {
                NetworkCombatAuthority.OnChatMessageReceived -= HandleRpcMessage;
                _rpcSubscribed = false;
            }
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!_running || _view == null || _api == null || _session == null) return;

            // 2026-09-10 审计 §4：等待房间 HTTP 拉取不得被战斗菜单状态（含跨场景残留的 MenuOpen）
            // 无限阻止——菜单只让出聊天输入焦点（ChatHudView 侧处理开合），后台拉取照常推进。
            EnsureLocalPlayer();
            bool rpcMode = ShouldUseRpcTransport();
            if (!rpcMode)
            {
                // HTTP 拉取（Waiting/Starting/Returning）：2s 节流；游标由后端权威推进（持久于会话）
                if (Time.unscaledTime >= _nextFetchAtRealtime && !string.IsNullOrEmpty(_session.Room?.RoomCode))
                {
                    _nextFetchAtRealtime = Time.unscaledTime + HttpFetchIntervalSeconds;
                    _ = FetchHttpAsync(_session.Room.RoomCode);
                }
            }

            // 2026-09-13 矩阵 D：RPC 发送有界失败恢复——5s 未收到服务器回显，把在途正文
            // 还原回输入行（草稿不丢），提示用户可重试（服务器令牌桶/资格拒绝不回执，只能超时兜底）
            if (_pendingRpcMessageId != null && Time.unscaledTime - _pendingRpcAtRealtime > RpcConfirmTimeoutSeconds)
            {
                _pendingRpcMessageId = null;
                _pendingRpcBody = null;
                _view?.FailPendingRpcDraft();
                PushLocalSystem("消息未确认送达（可能被服务器拒绝或掉线），草稿已还原，可重新发送");
            }
        }

        /// <summary>传输选择（Docs/27 §8.4）：InMatch（比赛进行中且已连 DS）→ Owner RPC；其余 → HTTP。</summary>
        private bool ShouldUseRpcTransport()
        {
            var phase = MatchLifecycle.Phase;
            bool inBattle = phase == MatchPhase.Countdown || phase == MatchPhase.InProgress;
            bool hasRoomMatch = !string.IsNullOrEmpty(_session.Room?.MatchId);
            return inBattle && hasRoomMatch && _localPlayer != null
                && InstanceFinder.NetworkManager != null && InstanceFinder.NetworkManager.IsClientStarted;
        }

        private void EnsureLocalPlayer()
        {
            if (_localPlayer != null && _localPlayer.IsOwnerPlayer) return;
            _localPlayer = null;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (player.IsOwnerPlayer) { _localPlayer = player; break; }
            }
        }

        private void SubscribeRpc()
        {
            if (_rpcSubscribed) return;
            _rpcSubscribed = true;
            NetworkCombatAuthority.OnChatMessageReceived += HandleRpcMessage;
        }

        private void HandleRpcMessage(ChatRelayCore.ChatMessage message)
        {
            if (message == null) return;
            var client = new ChatClientMessage
            {
                Transport = TransportRpc,
                Epoch = message.Epoch,
                Seq = message.Seq,
                Channel = message.Channel,
                SenderUserId = message.SenderUserId,
                SenderUsername = message.SenderUsername,
                TeamId = message.TeamId,
                Body = message.Body,
                ClientMessageId = message.ClientMessageId,
                AtUtc = DateTime.UtcNow,
            };
            PushIfNew(client, "R:" + message.Epoch + ":" + message.Seq);
        }

        private async System.Threading.Tasks.Task FetchHttpAsync(string roomCode)
        {
            int generation = _roomGeneration;
            var token = destroyCancellationToken;
            ulong cursor = ChatRoomSession.HttpCursor;
            var result = await _api.FetchRoomChatAsync(roomCode, cursor, token);
            if (token.IsCancellationRequested || result == null) return;
            // 旧房间响应隔离（2026-09-10 审计 §4）：await 期间可能已退房/换房（会话已指向新房间），
            // 迟到的旧房间响应不得写入新会话。
            // 审计 2026-09-16 §5.3-5：退出后**重进同一房间**（roomCode 相同）也必须拒绝——
            // 会话代际已递增，同房旧回包不得补回 UI/游标。
            if (!ChatRoomSession.IsRoom(roomCode) || generation != _roomGeneration || !_running) return;
            if (!result.Success || result.Data?.messages == null)
            {
                // 2026-09-10 审计 §4：拉取失败不再完全静默——有界节流留痕（房间/状态码/错误码/cursor；
                // 不记 token）。游标越界（后端重启 seq 清零）一次性复位重拉，服务端 ChatJoinSeq 水位
                // 保证不倒灌历史，房间身份隔离由会话保证。
                if (result.Code == ApiClientErrorCodes.ChatCursorInvalid)
                {
                    if (ChatRoomSession.IsRoom(roomCode)) ChatRoomSession.HttpCursor = 0;
                    Debug.LogWarning($"[Chat] 拉取游标越界，已复位重拉 room={roomCode} cursor={cursor}");
                    return;
                }
                if (Time.unscaledTime >= _nextFetchFailureLogRealtime)
                {
                    _nextFetchFailureLogRealtime = Time.unscaledTime + FetchFailureLogIntervalSeconds;
                    Debug.LogWarning($"[Chat] 拉取失败（保持 2s 节奏重试）room={roomCode} cursor={cursor} status={result.StatusCode} code={(string.IsNullOrEmpty(result.Code) ? "N/A" : result.Code)} msg={result.Message}");
                }
                return; // InMatch 阶段本控制器已切 RPC 不再拉取
            }
            _nextFetchFailureLogRealtime = 0f;
            foreach (var dto in result.Data.messages)
            {
                if (dto == null) continue;
                PushIfNew(new ChatClientMessage
                {
                    Transport = TransportHttp,
                    Epoch = "room",
                    Seq = dto.seq,
                    Channel = dto.channel,
                    SenderUserId = dto.senderUserId ?? 0L, // 系统消息无发送者（后端 null），展示层按系统消息渲染
                    SenderUsername = dto.senderUsername,
                    TeamId = dto.teamId,
                    Body = dto.body,
                    ClientMessageId = dto.clientMessageId,
                    AtUtc = ParseUtc(dto.sentAtUtc),
                }, "H:" + dto.seq);
            }
            ChatRoomSession.HttpCursor = result.Data.cursor;
        }

        private void HandleSendRequested(string channel, string body)
        {
            if (!_running) return;
            // 队伍频道资格（与服务器 ChatRules 同口径；None 队不允许）——拒绝即还原在途草稿
            if (channel == ChatRules.ChannelTeam && !ChatRules.CanUseTeamChannel(_session.Room?.TeamId))
            {
                PushLocalSystem("未加入队伍，不能使用队伍频道");
                _view?.RestorePendingDraft();
                return;
            }
            // 客户端令牌桶预检（服务器权威执法兜底）——限流同样还原在途草稿
            var (allowed, tokens, refilledAt, retryAfter) = ChatRules.TryConsumeToken(
                _clientTokens, _clientTokensAt, DateTime.UtcNow, ChatRules.TokenCapacity, ChatRules.TokenRefillSeconds);
            _clientTokens = tokens;
            _clientTokensAt = refilledAt;
            if (!allowed)
            {
                PushLocalSystem($"发言过于频繁，请 {retryAfter} 秒后重试");
                _view?.RestorePendingDraft();
                return;
            }

            var clientMessageId = Guid.NewGuid().ToString("N");
            if (ShouldUseRpcTransport() && _localPlayer != null)
            {
                _localPlayer.SubmitChatRequest(channel, body, clientMessageId);
                // 2026-09-13 矩阵 D：RPC 发送不再立即清草稿——登记回显等待，收到服务器回显
                //（clientMessageId 匹配，PushIfNew 统一入口）才确认清除；5s 超时还原草稿（有界失败恢复）
                _pendingRpcMessageId = clientMessageId;
                _pendingRpcBody = body;
                _pendingRpcAtRealtime = Time.unscaledTime;
                return;
            }

            var roomCode = _session.Room?.RoomCode;
            if (string.IsNullOrEmpty(roomCode))
            {
                PushLocalSystem("当前不在房间中，无法发送");
                _view?.RestorePendingDraft();
                return;
            }
            _ = SendHttpAsync(roomCode, channel, body, clientMessageId);
        }

        private async System.Threading.Tasks.Task SendHttpAsync(string roomCode, string channel, string body, string clientMessageId)
        {
            int generation = _roomGeneration;
            var token = destroyCancellationToken;
            var result = await _api.SendRoomChatAsync(roomCode, new RoomChatSendRequest
            {
                channel = channel,
                body = body,
                clientMessageId = clientMessageId,
            }, token);
            if (token.IsCancellationRequested) return;
            // 旧房间响应隔离 + 会话代际（审计 §5.3-5）：迟到的确认/失败不写入新会话、不还原草稿
            if (!ChatRoomSession.IsRoom(roomCode) || generation != _roomGeneration || !_running) return;
            if (result.Success && result.Data != null)
            {
                _view?.CommitPendingDraft(); // R9：确认后才真正清草稿
                PushIfNew(new ChatClientMessage
                {
                    Transport = TransportHttp,
                    Epoch = "room",
                    Seq = result.Data.seq,
                    Channel = result.Data.channel,
                    SenderUserId = result.Data.senderUserId ?? 0L,
                    SenderUsername = result.Data.senderUsername,
                    TeamId = result.Data.teamId,
                    Body = result.Data.body,
                    ClientMessageId = result.Data.clientMessageId,
                    AtUtc = ParseUtc(result.Data.sentAtUtc),
                }, "H:" + result.Data.seq);
            }
            else
            {
                // R9 审计修复：发送失败把在途正文还原到输入行（真实草稿不丢），再提示
                _view?.RestorePendingDraft();
                PushLocalSystem("发送失败：" + (result?.Message ?? "未知错误") + "（草稿已还原）");
            }
        }

        private void PushIfNew(ChatClientMessage message, string serverKey)
        {
            // R9：去重与历史持久在 ChatRoomSession（房间作用域，跨场景/返房保留）
            if (!ChatRoomSession.TryRegister(message, serverKey)) return;
            // 2026-09-13 矩阵 D：RPC 回显确认——clientMessageId 是本端生成的全局唯一 GUID，
            // 回显携带它即代表服务器已接受并中继：确认清除在途草稿（用户已输入新内容时只解除标记）
            if (_pendingRpcMessageId != null
                && string.Equals(message.ClientMessageId, _pendingRpcMessageId, StringComparison.Ordinal))
            {
                _pendingRpcMessageId = null;
                _pendingRpcBody = null;
                _view?.CommitRpcDraftIfUnedited();
            }
            _view?.Push(message);
        }

        private void PushLocalSystem(string body)
        {
            _view?.Push(new ChatClientMessage
            {
                Transport = "L",
                Epoch = "local",
                Channel = ChatRules.ChannelSystem,
                Body = body,
                AtUtc = DateTime.UtcNow,
            });
        }

        private void HandleViewClosed() { /* Esc 关闭：草稿已保留在视图内 */ }

        private void HandleChannelChanged() { /* 频道切换：视图侧仅更新标签（草稿共享） */ }

        /// <summary>本端换队（LobbyPresenter.ChangeTeamAsync 调用，Docs/26 §3.3 + R9 审计修复）：
        /// 删除旧队聊显示（历史+最近窗口）并清空草稿/输入行，频道回到全体。</summary>
        public void OnLocalTeamSwitched()
        {
            _view?.PurgeTeamChannel();
            _view?.SetPendingChannel(ChatRules.ChannelAll);
        }

        private static DateTime ParseUtc(string value) =>
            DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var utc) ? utc : DateTime.UtcNow;
    }

    /// <summary>客户端聊天消息（UI 形状；HTTP/RPC/本地系统三来源共用）。</summary>
    public sealed class ChatClientMessage
    {
        public string Transport;
        public string Epoch;
        public ulong Seq;
        public string Channel;
        public long SenderUserId;
        public string SenderUsername;
        public string TeamId;
        public string Body;
        public string ClientMessageId;
        public DateTime AtUtc;
    }
}
