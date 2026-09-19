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
            _ = FetchHistoryAsync(api, page, pageSize, onSuccess, onError);
        }

        private static async Task FetchHistoryAsync(IApiClient api, int page, int pageSize,
            Action<LuaTable> onSuccess, Action<string> onError)
        {
            try
            {
                var result = await api.GetMatchHistoryAsync(page, pageSize, CancellationToken.None);
                if (!result.Success)
                {
                    SafeError(onError, result.Code ?? "UNKNOWN");
                    return;
                }
                var env = HotUpdateRuntime.Instance != null ? HotUpdateRuntime.Instance.Env : null;
                if (env == null)
                {
                    SafeError(onError, "LuaEnv 未初始化");
                    return;
                }
                var dto = result.Data ?? new MatchHistoryPageDto();
                var table = env.NewTable();
                table.Set("page", dto.page);
                table.Set("totalCount", dto.totalCount);
                table.Set("totalPages", dto.totalPages);

                var summary = env.NewTable();
                var s = dto.summary ?? new CareerSummaryDto();
                summary.Set("totalMatches", s.totalMatches);
                summary.Set("wins", s.wins);
                summary.Set("totalKills", s.totalKills);
                summary.Set("totalDeaths", s.totalDeaths);
                summary.Set("totalXp", s.totalXp);
                summary.Set("totalCoins", s.totalCoins);
                summary.Set("winRate", s.winRate);
                table.Set("summary", summary);

                var rows = env.NewTable();
                if (dto.matches != null)
                {
                    var rowIndex = 1; // Lua 1-based，配 ipairs
                    foreach (var m in dto.matches)
                    {
                        if (m == null) continue;
                        var row = env.NewTable();
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
                onSuccess?.Invoke(table);
            }
            catch (Exception e)
            {
                SafeError(onError, e.Message);
            }
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
