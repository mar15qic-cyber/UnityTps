using System;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>
    /// 终局结算流（Docs/23 P2，G5）：订阅 NetworkCombatAuthority.OnMatchEvent，
    /// Ended 事件到达后收集全服务器权威值（clientMatchId/kills/deaths/durationSeconds/isWin，
    /// 客户端不自制胜负）→ SubmitMatchAsync → 回大厅 Navigate(Results) 渲染实数据。
    /// 提交失败 → PlayerPrefs 暂存（键 pendingMatch:{clientMatchId}）+ Results 页重试按钮；
    /// 对战中断线（未收到 Ended）不提交（Docs/17 §3.3 首版规则）。
    /// 纯静态类：AppRoot（跨场景存活）提供 api/session； Results 数据经静态属性供 RenderResults 消费。
    /// </summary>
    public static class MatchSettlementFlow
    {
        /// <summary>客户端提交预校验上限（Docs/23 §G.1：kills ≤ 30、duration ≤ 900s，超限不发请求）。</summary>
        public const int MaxKills = 30;
        public const int MaxDurationSeconds = 900;

        private const string PendingKeyPrefix = "pendingMatch:";

        /// <summary>最近一次成功结算的响应（Results 页实数据源）。</summary>
        public static MatchResultDto LastResult { get; private set; }

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

        /// <summary>最近一次收集的服务器权威提交（无论成败；Results 页 K/D 展示用）。</summary>
        public static MatchSubmissionRequest LastRequest { get; private set; }

        /// <summary>胜负文案（VICTORY / DEFEAT / DRAW；按终局载荷逐玩家 isWin 判定）。</summary>
        public static string LastVerdictText { get; private set; }

        /// <summary>最近一次提交失败、待重试的请求（null = 无待重试）。</summary>
        public static MatchSubmissionRequest LastPendingRequest { get; private set; }

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

        /// <summary>提交预校验（纯函数，Docs/23 §G.1：超限不发请求，避免后端 422）。
        /// CF 房间对局放宽（Docs/27 §7.2 模式感知）：带 matchId 上限 200/1200s；旧路径维持 30/900s。</summary>
        public static bool IsValidSubmission(int kills, int durationSeconds)
            => kills >= 0 && kills <= MaxKills && durationSeconds >= 0 && durationSeconds <= MaxDurationSeconds;

        public static bool IsValidSubmission(int kills, int durationSeconds, string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return IsValidSubmission(kills, durationSeconds);
            return kills >= 0 && kills <= RoomMatchMaxKills && durationSeconds >= 0 && durationSeconds <= RoomMatchMaxDurationSeconds;
        }

        /// <summary>CF 房间对局提交上限（Docs/27 §7.2：matchScoped 放宽 200 杀 / 1200s）。</summary>
        public const int RoomMatchMaxKills = 200;
        public const int RoomMatchMaxDurationSeconds = 1200;

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

            // 导航权接管（Phase C）：Ended 已到达 → 本次回大厅由结算流负责，
            // 退出流程/断线观测看到置位后不再重复 LoadScene（MatchExitState 同源仲裁）
            MatchExitState.SettlementNavigationPending = true;

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

            // 胜负文案：本条 isWin → VICTORY；其余任一条 isWin → DEFEAT；否则 DRAW（多人/团队一致）
            bool someoneElseWon = false;
            foreach (var entry in ended.players)
                if (entry != null && entry != mine && entry.isWin) { someoneElseWon = true; break; }
            LastVerdictText = mine.isWin ? "VICTORY" : (someoneElseWon ? "DEFEAT" : "DRAW");

            // ---- CF 房间对局分支（C3/Q05 + A04/V0 + R5 审计修复，Docs/27 §7.2/§5.7）----
            // 终局载荷带权威 matchId = 房间对局：发奖责任统一归 DS（幂等键 ds-{matchId}-{userId}），
            // TDM 与 KillRace 一致——客户端一律只查询权威结果，不再带 matchId 自报
            //（后端对房间比赛自报统一 409；旧 kr- 键双发链路就此关闭）。
            // 返房事务（R5）：查询→发布→ack→断战斗→清上下文→导航整条链共用捕获的
            // room/match/generation，每个 await 后校验——被顶替（切房/新战斗连接）即终止，
            // 旧任务绝不可能写新结果、ack 新房间或断开新连接。
            if (!string.IsNullOrEmpty(ended.matchId))
            {
                LastRequest = null;
                LastPendingRequest = null;
                var api = AppRoot.Instance != null ? AppRoot.Instance.ApiClient : null;
                var session = AppRoot.Instance != null ? AppRoot.Instance.Session : null;
                var roomCode = session?.Room?.RoomCode;
                var generationAtStart = session != null ? session.ConnectionGeneration : 0L;
                Debug.Log($"[MatchSettlementFlow] 房间对局（mode={ended.mode}）：结果由服务器权威上报，客户端查询 matchId={ended.matchId}");

                if (api == null || session == null || string.IsNullOrEmpty(roomCode))
                {
                    LastError = "会话/ApiClient 不可用，无法进入返房链";
                    Debug.LogWarning("[MatchSettlementFlow] " + LastError);
                    return;
                }

                var sequence = new MatchReturnSequence();
                await sequence.RunAsync(roomCode, ended.matchId, generationAtStart, new MatchReturnSequence.Deps
                {
                    SnapshotSession = () => new MatchReturnSequence.SessionSnapshot(
                        session.Room != null ? session.Room.RoomCode : null, session.ConnectionGeneration),
                    QueryResultOnce = async () =>
                    {
                        var result = await api.GetRoomMatchResultAsync(roomCode, ended.matchId);
                        return (result.Success && result.Data != null, result.Data);
                    },
                    Delay = () => Task.Delay(TimeSpan.FromSeconds(MatchReturnSequence.PollIntervalSeconds)),
                    // F3：HTTP ack 委托只返回数据；不能在旧请求 await 内提前写 AccountSession。
                    AckReturnWithData = async () =>
                    {
                        var ack = await api.ReturnRoomAsync(roomCode, ended.matchId);
                        return (ack.Success, ack.Data);
                    },
                    ApplyAckSnapshot = snapshot =>
                    {
                        // RunAsync 已在 await 返回后做过守卫；这里再做一次同步身份检查，
                        // 封住连接代际不变但玩家已切到另一房间的旧响应污染路径。
                        var liveRoom = session.Room;
                        if (liveRoom == null
                            || !string.Equals(liveRoom.RoomCode, roomCode, StringComparison.Ordinal)
                            || session.ConnectionGeneration != generationAtStart
                            || snapshot?.room == null
                            || !string.Equals(snapshot.room.roomCode, roomCode, StringComparison.Ordinal))
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

            var request = new MatchSubmissionRequest
            {
                clientMatchId = MatchLifecycle.ClientMatchId,
                kills = local.Kills,
                deaths = local.Deaths,
                durationSeconds = Mathf.RoundToInt(Mathf.Max(0f, ended.durationSeconds)),
                isWin = mine.isWin
            };
            LastRequest = request;

            if (string.IsNullOrEmpty(request.clientMatchId) || !IsValidSubmission(request.kills, request.durationSeconds))
            {
                // 超限/缺 id：不发送（后端 422 拒绝语义的客户端预闸），留待人工排查
                LastError = $"提交预校验未通过（kills={request.kills}, duration={request.durationSeconds}s, id={request.clientMatchId}）";
                Debug.LogWarning("[MatchSettlementFlow] " + LastError);
                return;
            }

            await SubmitAsync(request);
            await ReturnToLobbyAndShowResultsAsync();
        }

        /// <summary>提交一条结算请求：成功记 LastResult 并清暂存；失败暂存 PlayerPrefs 待重试。</summary>
        private static async Task SubmitAsync(MatchSubmissionRequest request)
        {
            var api = AppRoot.Instance != null ? AppRoot.Instance.ApiClient : null;
            if (api == null)
            {
                LastError = "ApiClient 不可用，结算已暂存";
                PersistPending(request);
                return;
            }
            var result = await api.SubmitMatchAsync(request);
            if (result.Success)
            {
                LastResult = result.Data;
                LastError = null;
                ClearPending(request.clientMatchId);
                Debug.Log($"[MatchSettlementFlow] 结算成功 replayed={result.Data.replayed} xp={result.Data.xpEarned} passXp={result.Data.passXpEarned}");
            }
            else
            {
                LastPendingRequest = request;
                LastError = ApiErrorMessages.ToUserMessage(result);
                PersistPending(request);
                Debug.LogWarning($"[MatchSettlementFlow] 结算提交失败（{result.Code}），已暂存待重试");
            }
        }

        /// <summary>Results 页重试按钮：重提暂存请求；成功后清除暂存并刷新结果。</summary>
        public static async Task RetryPendingAsync()
        {
            if (LastPendingRequest == null) return;
            var request = LastPendingRequest;
            await SubmitAsync(request);
            if (LastPendingRequest == request)
                Debug.LogWarning("[MatchSettlementFlow] 重试后仍失败，暂存保留");
        }

        // ---- 暂存存取（PlayerPrefs；测试覆盖往返） ----

        public static string PendingKey(string clientMatchId) => PendingKeyPrefix + clientMatchId;

        public static void PersistPending(MatchSubmissionRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.clientMatchId)) return;
            PlayerPrefs.SetString(PendingKey(request.clientMatchId), JsonUtility.ToJson(request));
            PlayerPrefs.Save();
        }

        public static MatchSubmissionRequest TryLoadPending(string clientMatchId)
        {
            if (string.IsNullOrEmpty(clientMatchId)) return null;
            var json = PlayerPrefs.GetString(PendingKey(clientMatchId), string.Empty);
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<MatchSubmissionRequest>(json);
        }

        public static void ClearPending(string clientMatchId)
        {
            if (LastPendingRequest != null && LastPendingRequest.clientMatchId == clientMatchId)
                LastPendingRequest = null;
            if (string.IsNullOrEmpty(clientMatchId)) return;
            PlayerPrefs.DeleteKey(PendingKey(clientMatchId));
            PlayerPrefs.Save();
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
            var op = SceneManager.LoadSceneAsync("Lobby", LoadSceneMode.Single);
            while (op != null && !op.isDone) await Task.Yield();
        }

        // ---- I4：房间对局权威结果查询已迁入 MatchReturnSequence（R5：整条返房链统一代际守卫） ----

        /// <summary>回大厅并直接进入 Results 页：等场景加载 + LobbyBootstrap.Start（AddComponent 与
        /// Initialize 同步块）完成后再 Navigate，轮询上限 600 帧（约 10s@60fps）。</summary>
        private static async Task ReturnToLobbyAndShowResultsAsync()
        {
            var op = SceneManager.LoadSceneAsync("Lobby", LoadSceneMode.Single);
            while (op != null && !op.isDone) await Task.Yield();

            LobbyPresenter presenter = null;
            for (int i = 0; i < 600 && presenter == null; i++)
            {
                presenter = UnityEngine.Object.FindFirstObjectByType<LobbyPresenter>();
                if (presenter == null) await Task.Yield();
            }
            presenter?.Navigate(LobbyPage.Results);
        }
    }
}
