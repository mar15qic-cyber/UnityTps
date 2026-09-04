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
            _ = SubmitFromEndedPayloadAsync(payload);
        }

        /// <summary>提交预校验（纯函数，Docs/23 §G.1：超限不发请求，避免后端 422）。</summary>
        public static bool IsValidSubmission(int kills, int durationSeconds)
            => kills >= 0 && kills <= MaxKills && durationSeconds >= 0 && durationSeconds <= MaxDurationSeconds;

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

            // 自条目匹配：按 (kills, deaths) 对齐（平局时双方条目相同，任一匹配等价——
            // MatchRules 保证非平局时双方 (kills,deaths) 必不相同）
            MatchLifecycle.MatchPlayerResult mine = null;
            MatchLifecycle.MatchPlayerResult other = null;
            foreach (var entry in ended.players)
            {
                if (entry == null) continue;
                if (entry.kills == local.Kills && entry.deaths == local.Deaths && mine == null)
                {
                    mine = entry;
                }
                else
                {
                    other = entry;
                }
            }
            if (mine == null)
            {
                LastError = "终局载荷未含本地条目，无法结算";
                Debug.LogWarning("[MatchSettlementFlow] " + LastError);
                return;
            }

            LastVerdictText = mine.isWin ? "VICTORY" : (other != null && other.isWin ? "DEFEAT" : "DRAW");
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
            foreach (var player in Object.FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
                if (player.IsOwnerPlayer) return player;
            return null;
        }

        /// <summary>回大厅并直接进入 Results 页：等场景加载 + LobbyBootstrap.Start（AddComponent 与
        /// Initialize 同步块）完成后再 Navigate，轮询上限 600 帧（约 10s@60fps）。</summary>
        private static async Task ReturnToLobbyAndShowResultsAsync()
        {
            var op = SceneManager.LoadSceneAsync("Lobby", LoadSceneMode.Single);
            while (op != null && !op.isDone) await Task.Yield();

            LobbyPresenter presenter = null;
            for (int i = 0; i < 600 && presenter == null; i++)
            {
                presenter = Object.FindFirstObjectByType<LobbyPresenter>();
                if (presenter == null) await Task.Yield();
            }
            presenter?.Navigate(LobbyPage.Results);
        }
    }
}
