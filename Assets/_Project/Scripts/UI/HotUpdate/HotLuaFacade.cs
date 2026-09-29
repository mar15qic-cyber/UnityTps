using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using UnityEngine;
using XLua;

namespace Game.UI
{
    /// <summary>
    /// Lua 面向 C# 的稳定接口（热更页注册 + 数据通道）。Lua 调用形如
    /// CS.Game.UI.HotLuaFacade.RegisterPage("career", "战绩", function(root) ... end)。
    /// ★ seam 纪律：只增不改既有方法签名——签名变更会静默破坏线上热更脚本，
    /// 任何变更必须记录在交接文档并同步热更脚本模板。
    /// 数据契约（GetMatchHistory 的 onSuccess LuaTable 形状，Lua 侧按键名只读）：
    ///   { page=int, totalCount=int, totalPages=int,
    ///     summary={ totalMatches, wins, totalKills, totalDeaths, totalXp, totalCoins, winRate },
    ///     rows={ [1..n]={ playedAt, isWin, kills, deaths, score, xp, coins } } }  -- 1-based，配 ipairs
    /// </summary>
    public static class HotLuaFacade
    {
        private sealed class PendingRequest
        {
            public Action<LuaTable> Success;
            public Action<string> Error;
            public LuaEnv Env;
            public readonly CancellationTokenSource Cancellation = new();
            public void Release() { Success = null; Error = null; Env = null; }
        }
        private static readonly System.Collections.Generic.HashSet<PendingRequest> pending = new();

        internal static void CancelPendingRequests()
        {
            var snapshot = new System.Collections.Generic.List<PendingRequest>(pending);
            pending.Clear();
            foreach (var request in snapshot)
            {
                request.Release();
                request.Cancellation.Cancel();
            }
        }
        public static void RegisterPage(string id, string label, Action<RectTransform> render) =>
            HotPageRegistry.Register(id, label, render);

        /// <summary>战绩历史（P3）：回调式异步（Task→主线程回调封包），数据以 LuaTable 交付。</summary>
        public static void GetMatchHistory(int page, int pageSize,
            Action<LuaTable> onSuccess, Action<string> onError)
        {
            var api = AppRoot.Instance != null ? AppRoot.Instance.ApiClient : null;
            if (api == null)
            {
                SafeError(onError, "客户端未就绪");
                return;
            }
            var request = new PendingRequest { Success = onSuccess, Error = onError,
                Env = HotUpdateRuntime.Instance != null ? HotUpdateRuntime.Instance.Env : null };
            pending.Add(request);
            _ = FetchHistoryAsync(api, page, pageSize, request);
        }

        private static async Task FetchHistoryAsync(IApiClient api, int page, int pageSize,
            PendingRequest request)
        {
            try
            {
                var result = await api.GetMatchHistoryAsync(page, pageSize, request.Cancellation.Token);
                if (request.Cancellation.IsCancellationRequested) return;
                if (!result.Success)
                {
                    SafeError(request.Error, result.Code ?? "UNKNOWN");
                    return;
                }
                var env = request.Env;
                if (env == null)
                {
                    SafeError(request.Error, "LuaEnv 未初始化");
                    return;
                }
                var dto = result.Data ?? new MatchHistoryPageDto();
                using var table = env.NewTable();
                table.Set("page", dto.page);
                table.Set("totalCount", dto.totalCount);
                table.Set("totalPages", dto.totalPages);

                using var summary = env.NewTable();
                var s = dto.summary ?? new CareerSummaryDto();
                summary.Set("totalMatches", s.totalMatches);
                summary.Set("wins", s.wins);
                summary.Set("totalKills", s.totalKills);
                summary.Set("totalDeaths", s.totalDeaths);
                summary.Set("totalXp", s.totalXp);
                summary.Set("totalCoins", s.totalCoins);
                summary.Set("winRate", s.winRate);
                table.Set("summary", summary);

                using var rows = env.NewTable();
                if (dto.matches != null)
                {
                    var rowIndex = 1; // Lua 1-based，配 ipairs
                    foreach (var m in dto.matches)
                    {
                        if (m == null) continue;
                        using var row = env.NewTable();
                        row.Set("playedAt", m.playedAtUtc ?? string.Empty);
                        row.Set("isWin", m.isWin);
                        row.Set("kills", m.kills);
                        row.Set("deaths", m.deaths);
                        row.Set("score", m.score);
                        row.Set("xp", m.xpEarned);
                        row.Set("coins", m.coinsEarned);
                        rows.Set(rowIndex, row);
                        rowIndex++;
                    }
                }
                table.Set("rows", rows);
                request.Success?.Invoke(table);
            }
            catch (Exception e)
            {
                if (!request.Cancellation.IsCancellationRequested) SafeError(request.Error, e.Message);
            }
            finally { pending.Remove(request); request.Release(); request.Cancellation.Dispose(); }
        }

        private static void SafeError(Action<string> onError, string message)
        {
            try
            {
                onError?.Invoke(message);
            }
            catch (Exception e)
            {
                Debug.LogError("[HotUpdate] lua error callback threw: " + e.Message);
            }
        }
    }
}
