using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>房间终局仅查询 Dedicated Server 权威结果，并通过代际守卫返回等待房间。
    /// 编辑器调试对局不提交账号奖励。</summary>
    public static class MatchSettlementFlow
    {
        /// <summary>最近一次房间对局权威结果（I4：Pending/Final 快照；等待房间页展示；新局开始时清除）。</summary>
        public static RoomMatchResultViewDto LastRoomResult { get; private set; }

        /// <summary>新局开始：清除上局结果展示（LobbyPresenter.OnWaitingStartClicked 调用）。</summary>
        public static void ClearLastRoomResult() => LastRoomResult = null;

        /// <summary>
        /// 等待房间页的结果补刷新入口（R5 审计修复）：结算查询超限仍 Pending 时，等待房间页以此
        /// 有界重拉权威结果——只允许 Final 升级（绝不倒退/覆盖为新局数据）。null 安全。
        /// </summary>
        public static void UpdateRoomResult(RoomMatchResultViewDto dto)
        {
            if (dto == null || LastRoomResult == null) return;
            if (!string.Equals(dto.matchId, LastRoomResult.matchId, StringComparison.Ordinal)) return; // 旧任务结果不得触碰新局
            if (dto.status != "Final" && LastRoomResult.status == "Final") return; // 已 Final 不倒退
            LastRoomResult = dto;
        }

        /// <summary>上局结果文案（R2 模式感知；纯函数供 EditMode 锁定）：TDM 按胜队；KillRace 按
        /// 逐玩家 isWin 取个人胜者；Pending 显示结算中；平局/无胜者显示平局。</summary>
        public static string BuildRoomResultVerdict(RoomMatchResultViewDto result, string mode)
        {
            if (result == null) return null;
            if (result.status != "Final") return "上局：结算中…";
            if (GameModes.IsTeamMode(mode))
            {
                if (result.winnerTeam == TeamId.Red) return "上局：红队获胜";
                if (result.winnerTeam == TeamId.Blue) return "上局：蓝队获胜";
                return "上局：平局";
            }
            RoomMatchResultPlayerViewDto winner = null;
            if (result.players != null)
            {
                foreach (var player in result.players)
                {
                    if (player == null || !player.isWin) continue;
                    if (winner == null || player.kills > winner.kills) winner = player;
                }
            }
            return winner != null ? "上局：" + winner.username + " 获胜" : "上局：平局";
        }

        /// <summary>最近一次结算流程错误文案（Results 页展示）。</summary>
        public static string LastError { get; private set; }

        private static bool subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSubscribe() => EnsureSubscribed();

        /// <summary>订阅终局事件（幂等；RuntimeInitializeOnLoad 自动调用）。</summary>
        public static void EnsureSubscribed()
        {
            if (subscribed) return;
            subscribed = true;
            NetworkCombatAuthority.OnMatchEvent += HandleMatchEvent;
        }

        private static void HandleMatchEvent(MatchEventKind kind, string payload)
        {
            if (kind != MatchEventKind.Ended) return;
            // Explicit leave owns navigation; the room session is being removed.
            if (MatchExitState.VoluntaryLeaveRequested) return;
            _ = SubmitFromEndedPayloadAsync(payload);
        }

        private static async Task SubmitFromEndedPayloadAsync(string payload)
        {
            var ended = JsonUtility.FromJson<MatchLifecycle.MatchEndedPayload>(payload);
            var local = FindLocalAuthority();
            if (ended == null || ended.players == null || ended.players.Length == 0 || local == null)
            {
                LastError = "终局数据不完整，无法结算";
                Debug.LogWarning("[MatchSettlementFlow] " + LastError);
                return;
            }

            // 自条目匹配（Phase C 补强）：优先按稳定 playerId（服务器载荷与 kill feed 同源）；
            // 无 playerId 的旧载荷回退 (kills, deaths) 对齐
            var myId = MatchLifecycle.PlayerId(local);
            MatchLifecycle.MatchPlayerResult mine = null;
            foreach (var entry in ended.players)
            {
                if (entry == null) continue;
                bool isMine = !string.IsNullOrEmpty(entry.playerId)
                    ? entry.playerId == myId
                    : entry.kills == local.Kills && entry.deaths == local.Deaths && mine == null;
                if (isMine && mine == null) mine = entry;
            }
            if (mine == null)
            {
                LastError = "终局载荷未含本地条目，无法结算";
                Debug.LogWarning("[MatchSettlementFlow] " + LastError);
                return;
            }

            // ---- CF 房间对局分支（C3/Q05 + A04/V0 + R5 审计修复，Docs/27 §7.2/§5.7）----
            // 终局载荷带权威 matchId = 房间对局：发奖责任统一归 DS（幂等键 ds-{matchId}-{userId}），
            // TDM 与 KillRace 一致——客户端一律只查询权威结果，不再带 matchId 自报
            //（玩家自报奖励入口已退役，统一返回 410）。
            // 返房事务（R5）：查询→发布→ack→断战斗→清上下文→导航整条链共用捕获的
            // room/match/generation，每个 await 后校验——被顶替（切房/新战斗连接）即终止，
            // 旧任务绝不可能写新结果、ack 新房间或断开新连接。
            if (!string.IsNullOrEmpty(ended.matchId))
            {
                var api = AppRoot.Instance != null ? AppRoot.Instance.ApiClient : null;
                var session = AppRoot.Instance != null ? AppRoot.Instance.Session : null;
                var roomCode = session?.Room?.RoomId;
                var generationAtStart = session != null ? session.ConnectionGeneration : 0L;
                Debug.Log($"[MatchSettlementFlow] 房间对局（mode={ended.mode}）：结果由服务器权威上报，客户端查询 matchId={ended.matchId}");

                if (api == null || session == null || string.IsNullOrEmpty(roomCode))
                {
                    LastError = "会话/ApiClient 不可用，无法进入返房链";
                    Debug.LogWarning("[MatchSettlementFlow] " + LastError);
                    return;
                }

                // Only claim navigation after the payload and return session are valid.
                MatchExitState.SettlementNavigationPending = true;
                MatchLoadingScreen.Begin(false);
                var sequence = new MatchReturnSequence();
                await sequence.RunAsync(roomCode, ended.matchId, generationAtStart, new MatchReturnSequence.Deps
                {
                    SnapshotSession = () => new MatchReturnSequence.SessionSnapshot(
                        session.Room != null ? session.Room.RoomId : null, session.ConnectionGeneration),
                    QueryResultOnce = async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var result = await api.GetRoomMatchResultAsync(roomCode, ended.matchId, timeout.Token);
                        return (result.Success && result.Data != null, result.Data);
                    },
                    Delay = () => Task.Delay(TimeSpan.FromSeconds(MatchReturnSequence.PollIntervalSeconds)),
                    // F3：HTTP ack 委托只返回数据；不能在旧请求 await 内提前写 AccountSession。
                    AckReturnWithData = async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var ack = await api.ReturnRoomAsync(roomCode, ended.matchId, timeout.Token);
                        return (ack.Success, ack.Data);
                    },
                    ApplyAckSnapshot = snapshot =>
                    {
                        // RunAsync 已在 await 返回后做过守卫；这里再做一次同步身份检查，
                        // 封住连接代际不变但玩家已切到另一房间的旧响应污染路径。
                        var liveRoom = session.Room;
                        if (liveRoom == null
                            || !string.Equals(liveRoom.RoomId, roomCode, StringComparison.Ordinal)
                            || session.ConnectionGeneration != generationAtStart
                            || snapshot?.room == null
                            || !string.Equals(snapshot.room.roomId.ToString(), roomCode, StringComparison.Ordinal))
                            return;
                        session.RefreshRoomSnapshot(snapshot);
                    },
                    PublishResult = dto => LastRoomResult = dto,
                    StopBattleConnection = StopBattleConnectionIfCurrent,
                    ClearLaunchContext = () => NetworkLaunchContext.Clear(),
                    NavigateToLobby = () => _ = LoadLobbySceneAsync(),
                });
                if (sequence.Superseded)
                {
                    // 被顶替：本流放弃结算导航权（新会话/新流程接管；MatchExitState 仲裁位归还）
                    MatchExitState.SettlementNavigationPending = false;
                    return;
                }
                // 回到大厅：不 Navigate（LobbyPresenter 引导检测 session.Room 自动恢复等待房间页）
                return;
            }

            // Editor-only/non-room battles never submit account rewards.
            MatchExitState.SettlementNavigationPending = false;
            LastError = "调试对局不产生账号奖励";
        }

        // ---- 私有工具 ----

        private static NetworkCombatAuthority FindLocalAuthority()
        {
            foreach (var player in UnityEngine.Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
                if (player.IsOwnerPlayer) return player;
            return null;
        }

        /// <summary>断开战斗连接（退战斗 ≠ 退房；R5：仅返房事务未被顶替时调用——绝不断新会话连接）。</summary>
        private static void StopBattleConnectionIfCurrent()
        {
            var networkManager = FishNet.InstanceFinder.NetworkManager;
            if (networkManager != null && networkManager.IsClientStarted)
                networkManager.ClientManager.StopConnection();
        }

        /// <summary>加载大厅场景（R5：返房事务导航步；等待房间页由大厅引导自动恢复）。</summary>
        private static async Task LoadLobbySceneAsync()
        {
            await MatchLoadingScreen.LoadAsync("Lobby", false);
        }

        // ---- I4：房间对局权威结果查询已迁入 MatchReturnSequence（R5：整条返房链统一代际守卫） ----

    }
}
