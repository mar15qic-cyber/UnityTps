using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// CF 等待房间页（Docs/28 Q03，Docs/27 v1.2）：成员/选边/准备/设置/房主开始 + 快照轮询与成员心跳。
    /// 关键纪律：Waiting 不触碰 NetworkLaunchContext、不推进 ConnectionGeneration；
    /// 只有轮询/start ack 拿到合法 connection 才经 EnterBattleAsync 进 Arena；
    /// Returning 自动补发返房 ack（不退房）；404/ROOM_CLOSED 清会话回大厅。
    /// </summary>
    public sealed partial class LobbyPresenter
    {
        private const float RoomHeartbeatIntervalSeconds = 15f;

        private Transform waitingTeamsRoot;
        private TMP_Text waitingStateText;
        private TMP_Text waitingRulesText;
        private TMP_Text waitingResultText;
        private Button waitingActionButton;
        /// <summary>房主"开始比赛"按钮（直接引用）：StyledButton 返回的是按钮 Face 子对象、
        /// 其外层容器由构建器命名为 Btn_N，按名字 Find 找不到 → 按钮对房主永久不可见（2026-09-15 实测）。</summary>
        private Button waitingStartButton;
        private string waitingRoomCode;
        private bool waitingReturnAckSent;
        /// <summary>上局结果补刷新剩余次数（R5：结算查询超限仍 Pending 时，等待房间页有界重拉）。</summary>
        private int waitingResultRefreshLeft;

        /// <summary>进入等待房间页：渲染一次 UI，然后启动轮询/心跳循环（页面切换即随 pageCts 取消）。</summary>
        private void RenderWaitingRoom()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            var room = session.Room;
            if (room == null || string.IsNullOrWhiteSpace(room.RoomCode)) { Navigate(LobbyPage.Lobby); return; }
            SetBackground(UIArt.KeyBackgroundLobby);
            SetNavigationVisible(false); // 房间是独立全屏流程，离开必须显式退房
            waitingRoomCode = room.RoomCode;
            waitingReturnAckSent = false;

            var root = PageRoot("WaitingRoomPage");
            StyledText(root, "等待房间", UITheme.FontHero, UITheme.TextPrimary,
                new Vector2(0.03f, 0.86f), new Vector2(0.4f, 0.97f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(root, waitingRoomCode, 64, UITheme.AccentPrimary,
                new Vector2(0.40f, 0.83f), new Vector2(0.75f, 0.98f), TextAlignmentOptions.Center, FontStyles.Bold);
            StyledText(root, "把房间码告诉好友即可加入", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.75f, 0.88f), new Vector2(0.98f, 0.93f), TextAlignmentOptions.Left);
            waitingStateText = StyledText(root, "同步中…", UITheme.FontCardTitle, UITheme.AccentSecondary,
                new Vector2(0.75f, 0.82f), new Vector2(0.98f, 0.88f), TextAlignmentOptions.Left, FontStyles.Bold);
            waitingRulesText = StyledText(root, string.Empty, UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.03f, 0.77f), new Vector2(0.6f, 0.85f), TextAlignmentOptions.Left);

            // I4：上局结果卡片（Pending=结算中；Final=胜队）；新局开始时清除（OnWaitingStartClicked）。
            // R2：文案按模式感知（TDM 胜队 / KillRace 个人胜者）；R5：Pending 时由轮询循环有界补刷新。
            var lastResult = MatchSettlementFlow.LastRoomResult;
            waitingResultText = null;
            waitingResultRefreshLeft = RoomResultRefreshMaxAttempts;
            if (lastResult != null)
            {
                var resultCard = StyledPanel("LastResultCard", root, UITheme.CardSurface, new Vector2(0.03f, 0.66f), new Vector2(0.6f, 0.765f));
                waitingResultText = StyledText(resultCard.transform,
                    MatchSettlementFlow.BuildRoomResultVerdict(lastResult, session.Room?.Mode), UITheme.FontBody,
                    UITheme.AccentSecondary, new Vector2(0.02f, 0.1f), new Vector2(0.98f, 0.9f),
                    TextAlignmentOptions.Left, FontStyles.Bold);
            }

            // 底部留出聊天 HUD 带（ChatHudView 收起态占画布左下 0~0.34 高）：页面卡片自 0.36 起，
            // 避免聊天面板/系统消息与成员列表、按钮区互相压盖（2026-09-15 用户实测重叠）。
            var teamsPanel = StyledPanel("TeamsPanel", root, UITheme.CardSurface, new Vector2(0.03f, 0.36f), new Vector2(0.71f, 0.755f));
            waitingTeamsRoot = teamsPanel.transform;

            // 自身操作列：选边 / 准备 / 设置 / 开始 / 离开
            var actionPanel = StyledPanel("ActionPanel", root, UITheme.CardSurface, new Vector2(0.725f, 0.36f), new Vector2(0.98f, 0.755f));
            StyledText(actionPanel.transform, "操作", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.90f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            waitingActionButton = StyledButton(actionPanel.transform, "准备", UIComponents.ButtonKind.Primary,
                new Vector2(0.08f, 0.76f), new Vector2(0.92f, 0.87f), OnWaitingReadyClicked);
            // 首轮 detail 回来前不知道自己的身份（房主不显示准备）→ 两个动作按钮先隐藏，
            // 由 UpdateWaitingUi 按权威快照显隐（避免建房后 1~2s 内房主看到"准备/开始比赛"错态）
            waitingActionButton.gameObject.SetActive(false);
            StyledButton(actionPanel.transform, "加入红队", UIComponents.ButtonKind.Secondary,
                new Vector2(0.08f, 0.62f), new Vector2(0.48f, 0.73f), () => _ = ChangeTeamAsync(TeamId.Red));
            StyledButton(actionPanel.transform, "加入蓝队", UIComponents.ButtonKind.Secondary,
                new Vector2(0.52f, 0.62f), new Vector2(0.92f, 0.73f), () => _ = ChangeTeamAsync(TeamId.Blue));
            StyledButton(actionPanel.transform, "规则设置", UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.48f), new Vector2(0.92f, 0.59f), () => _ = CycleKillTarget());
            StyledButton(actionPanel.transform, "时长设置", UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.37f), new Vector2(0.92f, 0.46f), () => _ = CycleTimeLimit());
            StyledButton(actionPanel.transform, "容量设置", UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.26f), new Vector2(0.92f, 0.35f), () => _ = CycleMaxPlayers());
            var startButton = StyledButton(actionPanel.transform, "开始比赛", UIComponents.ButtonKind.Primary,
                new Vector2(0.08f, 0.13f), new Vector2(0.92f, 0.24f), () => _ = OnWaitingStartClicked());
            startButton.name = "Btn_StartMatch";
            waitingStartButton = startButton;
            startButton.gameObject.SetActive(false); // 同准备按钮：仅房主 + Waiting 时由快照开启
            StyledButton(actionPanel.transform, "离开房间", UIComponents.ButtonKind.Danger,
                new Vector2(0.08f, 0.02f), new Vector2(0.92f, 0.11f), () => _ = LeaveWaitingRoomAsync());

            PlayEnter(root.gameObject);
            // C4/I2：等待房间聊天（HTTP 传输；同画布挂载，离开房间时 StopAndClear 清空）
            Chat.ChatController.EnsureRunning(canvas != null ? canvas.GetComponent<Canvas>() : null, api, session);
            _ = RunWaitingRoomLoopAsync(waitingRoomCode, pageCts.Token);
        }

        /// <summary>等待房间主循环：快照轮询（Waiting 2s / Starting 1s / Returning 1s）+ 15s 成员心跳 +
        /// 战斗连接拾取（connection 非空才进 Arena）+ Returning 返房 ack。</summary>
        private async Task RunWaitingRoomLoopAsync(string roomCode, CancellationToken token)
        {
            var nextHeartbeatUtc = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                var detail = await api.GetRoomDetailAsync(roomCode, token);
                if (token.IsCancellationRequested || currentPage != LobbyPage.WaitingRoom) return;
                if (!detail.Success)
                {
                    if (detail.Code == "AUTH_UNAUTHORIZED")
                    {
                        // 会话失效：离开房间页即收口聊天实例（房间上下文随会话一并失效）
                        Chat.ChatController.StopAndClear();
                        Navigate(LobbyPage.SessionExpired);
                        return;
                    }
                    if (detail.Code == "ROOM_NOT_FOUND" || detail.Code == "ROOM_CLOSED")
                    {
                        // 房间已解散/关闭：清会话回大厅（Docs/27 §11 掉线口径）；聊天实例随房间收口
                        //（2026-09-10 审计 §5：已关闭房间的聊天不得残留到大厅；侧栏由 Navigate 不变量恢复）
                        NetworkLaunchContext.Clear();
                        Chat.ChatController.StopAndClear();
                        session.ClearRoom();
                        Navigate(LobbyPage.Lobby);
                        if (status != null) status.text = "房间已解散";
                        return;
                    }
                    if (status != null) status.text = "同步失败：" + ApiErrorMessages.ToUserMessage(detail) + "（将重试）";
                    await DelaySafe(2f, token);
                    continue;
                }

                var snapshot = detail.Data;
                if (snapshot?.room == null) { await DelaySafe(2f, token); continue; }
                session.RefreshRoomSnapshot(snapshot);

                // 战斗连接出现（房主 start 后的 Starting，或 InMatch 重连）→ 唯一进战场路径
                if (snapshot.connection != null)
                {
                    var entered = await EnterBattleAsync(snapshot.connection, snapshot.room.roomCode);
                    if (!entered)
                    {
                        // 票据校验失败不进战场：清掉上下文，留在等待页由下一轮轮询重新取票
                        NetworkLaunchContext.Clear();
                        RoomConnectionGate.Sanitize(snapshot.connection);
                    }
                    if (entered) return;
                    await DelaySafe(1f, token);
                    continue;
                }

                // Returning：自动补发返房 ack（只退战斗不退房；幂等）
                if (RoomStatus.Returning.Equals(snapshot.room.status, StringComparison.Ordinal)
                    && !waitingReturnAckSent && !string.IsNullOrEmpty(snapshot.room.matchId))
                {
                    waitingReturnAckSent = true;
                    _ = AckReturnAsync(roomCode, snapshot.room.matchId, token);
                }

                // R5：结算查询超限仍 Pending → 等待房间页有界补刷新（每轮 5s 一次，Final 即停）
                var lastResult = MatchSettlementFlow.LastRoomResult;
                if (lastResult != null && lastResult.status != "Final"
                    && waitingResultRefreshLeft > 0
                    && string.Equals(lastResult.matchId, snapshot.room.matchId, StringComparison.Ordinal)
                    && DateTime.UtcNow >= _nextRoomResultRefreshUtc)
                {
                    _nextRoomResultRefreshUtc = DateTime.UtcNow.AddSeconds(RoomResultRefreshIntervalSeconds);
                    waitingResultRefreshLeft--;
                    _ = RefreshStaleRoomResultAsync(roomCode, lastResult.matchId, token);
                }

                UpdateWaitingUi(snapshot);
                var interval = snapshot.room.status == RoomStatus.Starting || snapshot.room.status == RoomStatus.Returning ? 1f : 2f;
                if (DateTime.UtcNow >= nextHeartbeatUtc)
                {
                    nextHeartbeatUtc = DateTime.UtcNow.AddSeconds(RoomHeartbeatIntervalSeconds);
                    _ = api.HeartbeatRoomAsync(CancellationToken.None); // 保活失败不影响轮询（过期由服务端判定）
                }
                await DelaySafe(interval, token);
            }
        }

        private static async Task DelaySafe(float seconds, CancellationToken token)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
            catch (OperationCanceledException) { }
        }

        /// <summary>上局结果补刷新参数（R5）：5s 间隔 × 24 次（≈2 分钟窗口，Final 即停）。</summary>
        private const int RoomResultRefreshMaxAttempts = 24;
        private const float RoomResultRefreshIntervalSeconds = 5f;
        private static DateTime _nextRoomResultRefreshUtc;

        private async Task RefreshStaleRoomResultAsync(string roomCode, string matchId, CancellationToken token)
        {
            try
            {
                var result = await api.GetRoomMatchResultAsync(roomCode, matchId, token);
                if (token.IsCancellationRequested || currentPage != LobbyPage.WaitingRoom) return;
                if (!result.Success || result.Data == null) return;
                MatchSettlementFlow.UpdateRoomResult(result.Data);
                if (waitingResultText != null)
                    waitingResultText.text = MatchSettlementFlow.BuildRoomResultVerdict(
                        MatchSettlementFlow.LastRoomResult, session.Room?.Mode);
            }
            catch (OperationCanceledException) { }
        }

        private async Task AckReturnAsync(string roomCode, string matchId, CancellationToken token)
        {
            try
            {
                var result = await api.ReturnRoomAsync(roomCode, matchId, token);
                if (result.Success && result.Data?.room != null && currentPage == LobbyPage.WaitingRoom)
                {
                    session.RefreshRoomSnapshot(result.Data);
                    if (result.Data.room.status == RoomStatus.Waiting)
                        waitingReturnAckSent = false; // 已回 Waiting，允许下一局再 ack
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ChangeTeamAsync(string teamId)
        {
            if (string.IsNullOrEmpty(waitingRoomCode)) return;
            var result = await api.SetRoomTeamAsync(waitingRoomCode, teamId, pageCts.Token);
            if (result.Success && result.Data != null)
            {
                session.RefreshRoomSnapshot(result.Data);
                UpdateWaitingUi(result.Data);
                Chat.ChatController.Instance?.OnLocalTeamSwitched(); // 换队清队伍草稿（Docs/26 §3.3）
            }
            else if (status != null) status.text = ApiErrorMessages.ToUserMessage(result);
        }

        private void OnWaitingReadyClicked()
        {
            if (session.Room?.Status == RoomStatus.Starting || session.Room?.Status == RoomStatus.InMatch)
            {
                _ = JoinRunningMatchAsync();
                return;
            }
            if (session.Room?.IsHost ?? false) return;
            var isReady = !(session.Room?.IsReady ?? false);
            _ = SetReadyAsync(isReady);
        }

        private bool waitingJoinPending;

        private async Task JoinRunningMatchAsync()
        {
            if (waitingJoinPending || string.IsNullOrEmpty(waitingRoomCode)) return;
            waitingJoinPending = true;
            var roomCode = waitingRoomCode;
            var token = pageCts.Token;
            try
            {
                var result = await api.JoinRoomAsync(roomCode, clientProtocolId: Game.Gameplay.Network.GameProtocolIdentity.ProtocolId, cancellationToken: token);
                if (token.IsCancellationRequested || currentPage != LobbyPage.WaitingRoom || waitingRoomCode != roomCode) return;
                if (!result.Success || result.Data?.connection == null)
                {
                    if (status != null) status.text = ApiErrorMessages.ToUserMessage(result);
                    return;
                }
                // 只补名单（join 已把本人写入本局 roster）——入场仍由轮询循环统一完成：join 返回的
                // 票据会被下一轮 detail 补发的新票据顶替，自行 EnterBattleAsync 会用到被顶替的旧票据
                session.RefreshRoomSnapshot(result.Data);
                if (status != null) status.text = "已加入比赛，正在进入战场…";
            }
            catch (OperationCanceledException) { }
            finally { waitingJoinPending = false; }
        }

        private async Task SetReadyAsync(bool isReady)
        {
            if (string.IsNullOrEmpty(waitingRoomCode)) return;
            var result = await api.SetRoomReadyAsync(waitingRoomCode, isReady, pageCts.Token);
            if (result.Success && result.Data != null)
            {
                session.RefreshRoomSnapshot(result.Data);
                UpdateWaitingUi(result.Data);
            }
            else if (status != null) status.text = ApiErrorMessages.ToUserMessage(result);
        }

        /// <summary>房主开始（Docs/27 §5.5）：ack 返回的 connection 即本人票据 → EnterBattleAsync。
        /// 非房主点击无效果（后端 NOT_LEADER 兜底，按钮本身按 IsHost 显隐）。</summary>
        private async Task OnWaitingStartClicked()
        {
            if (string.IsNullOrEmpty(waitingRoomCode) || !(session.Room?.IsHost ?? false)) return;
            var result = await api.StartRoomMatchAsync(waitingRoomCode, pageCts.Token);
            if (!result.Success || result.Data == null)
            {
                if (result.Code == "AUTH_UNAUTHORIZED") { Navigate(LobbyPage.SessionExpired); return; }
                status.text = ApiErrorMessages.ToUserMessage(result); // NO_SERVER_AVAILABLE / ROOM_NOT_READY 明细
                return;
            }
            session.NoteMatchStart(result.Data);
            MatchSettlementFlow.ClearLastRoomResult(); // 新局开始：清上局结果卡片（I4）
            // 入场统一交给等待页轮询循环（快照 connection = 最新票据）：开始 ack 的票据在 Starting
            // 期间会被下一次 detail 轮询补发的新票据顶替（一次性票据同键顶替语义），两条路径各自
            // EnterBattleAsync 会写出已被顶替的旧票据 → DS TICKET_INVALID（2026-09-15 双端实测）。
            if (status != null) status.text = "比赛启动中，正在进入战场…";
        }

        private async Task LeaveWaitingRoomAsync()
        {
            try { await api.LeaveRoomAsync(CancellationToken.None); }
            catch { /* 离开失败仍回大厅：服务端按心跳过期兜底清理 */ }
            Chat.ChatController.StopAndClear(); // 退房清空聊天（Docs/26 §3.3）
            NetworkLaunchContext.Clear();
            session.ClearRoom();
            SetNavigationVisible(true);
            Navigate(LobbyPage.Lobby);
        }

        // ---- 设置（仅房主生效；后端逐字段白名单 + 全员清准备）----

        private async Task CycleKillTarget()
        {
            var room = session.Room;
            if (room == null || !room.IsHost) { status.text = "只有房主可以修改规则设置"; return; }
            int[] whitelist = room.Mode == GameModes.KillRace ? new[] { 10, 20, 30 } : new[] { 50, 100, 150 };
            var next = NextInWhitelist(whitelist, room.KillTarget);
            await ApplySettingsAsync(new RoomSettingsRequest { killTarget = next });
        }

        private async Task CycleTimeLimit()
        {
            var room = session.Room;
            if (room == null || !room.IsHost) { status.text = "只有房主可以修改规则设置"; return; }
            int[] whitelist = room.Mode == GameModes.KillRace ? new[] { 5, 10 } : new[] { 5, 10, 15 };
            var next = NextInWhitelist(whitelist, room.TimeLimitMinutes);
            await ApplySettingsAsync(new RoomSettingsRequest { timeLimitMinutes = next });
        }

        private async Task CycleMaxPlayers()
        {
            var room = session.Room;
            if (room == null || !room.IsHost) { status.text = "只有房主可以修改规则设置"; return; }
            int[] whitelist = { 2, 4, 8, 12, 16 };
            var next = NextInWhitelist(whitelist, room.MaxPlayers);
            await ApplySettingsAsync(new RoomSettingsRequest { maxPlayers = next });
        }

        private static int NextInWhitelist(int[] whitelist, int current)
        {
            for (int i = 0; i < whitelist.Length; i++)
                if (whitelist[i] > current) return whitelist[i];
            return whitelist[0];
        }

        private async Task ApplySettingsAsync(RoomSettingsRequest request)
        {
            var result = await api.UpdateRoomSettingsAsync(waitingRoomCode, request, pageCts.Token);
            if (result.Success && result.Data != null)
            {
                session.RefreshRoomSnapshot(result.Data);
                UpdateWaitingUi(result.Data);
                if (status != null) status.text = "设置已更新，全员准备已清空";
            }
            else if (status != null) status.text = ApiErrorMessages.ToUserMessage(result);
        }

        // ---- UI 刷新 ----

        /// <summary>按快照重建成员列与按钮态（页面内轻量重建；每轮轮询调用）。</summary>
        private void UpdateWaitingUi(RoomSnapshotDto snapshot)
        {
            if (waitingTeamsRoot == null || waitingStateText == null) return;
            var room = snapshot.room;
            var selfUserId = snapshot.you?.userId ?? 0;

            waitingStateText.text = room.status switch
            {
                RoomStatus.Waiting => "等待开始（房主点击开始比赛）",
                RoomStatus.Starting => "比赛启动中…",
                RoomStatus.InMatch => "比赛进行中…",
                RoomStatus.Returning => "结算中，正在返回房间…",
                _ => room.status,
            };
            var modeName = GameModes.IsTeamMode(room.mode) ? "团队死斗" : "击杀竞赛";
            waitingRulesText.text = $"{modeName} · {room.killTarget} 杀 · {room.timeLimitMinutes} 分钟 · 上限 {room.maxPlayers} 人";

            foreach (Transform child in waitingTeamsRoot) Destroy(child.gameObject);
            bool isTeamMode = GameModes.IsTeamMode(room.mode);
            if (isTeamMode)
            {
                BuildTeamColumn(TeamId.Red, "红队", snapshot, selfUserId, 0.02f, 0.49f);
                BuildTeamColumn(TeamId.Blue, "蓝队", snapshot, selfUserId, 0.51f, 0.98f);
            }
            else
            {
                BuildTeamColumn(null, "参赛成员", snapshot, selfUserId, 0.02f, 0.98f);
            }

            // 按钮态：准备文案/设置与开始仅房主且 Waiting 可见
            if (session.Room != null)
            {
                if (waitingActionButton != null)
                {
                    bool running = room.status == RoomStatus.Starting || room.status == RoomStatus.InMatch;
                    waitingActionButton.gameObject.SetActive(running || (!session.Room.IsHost && room.status == RoomStatus.Waiting));
                    waitingActionButton.GetComponentInChildren<TMP_Text>().text = running ? "进入比赛" : session.Room.IsReady ? "取消准备" : "准备";
                    waitingActionButton.interactable = !waitingJoinPending;
                }
                // 开始比赛：仅房主 + Waiting（直接引用按钮——按名字 Find 命中不到 Face 子对象）
                if (waitingStartButton != null)
                    waitingStartButton.gameObject.SetActive(session.Room.IsHost && room.status == RoomStatus.Waiting);
            }
        }

        private void BuildTeamColumn(string teamId, string title, RoomSnapshotDto snapshot, long selfUserId, float x0, float x1)
        {
            var column = StyledPanel("Team_" + title, waitingTeamsRoot,
                new Color(0f, 0f, 0f, 0f), new Vector2(x0, 0.03f), new Vector2(x1, 0.97f));
            var accent = teamId == TeamId.Red ? UITheme.AccentDanger : teamId == TeamId.Blue ? UITheme.AccentSecondary : UITheme.AccentPrimary;
            StyledText(column.transform, title, UITheme.FontCardTitle, accent,
                new Vector2(0.06f, 0.88f), new Vector2(0.94f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            float y = 0.82f;
            foreach (var member in snapshot.members)
            {
                if (teamId != null && !string.Equals(member.teamId, teamId, StringComparison.Ordinal)) continue;
                var tag = member.isLeader ? "[队长] " : string.Empty;
                var readyTag = member.isReady ? "  ✓" : string.Empty;
                var selfTag = member.userId == selfUserId ? "（你）" : string.Empty;
                var color = member.userId == selfUserId ? UITheme.AccentSecondary : UITheme.TextPrimary;
                StyledText(column.transform, $"{tag}{member.username}{selfTag}{readyTag}", UITheme.FontBody, color,
                    new Vector2(0.06f, y - 0.075f), new Vector2(0.94f, y), TextAlignmentOptions.Left);
                y -= 0.085f;
                if (y < 0.05f) break;
            }
        }
    }
}
