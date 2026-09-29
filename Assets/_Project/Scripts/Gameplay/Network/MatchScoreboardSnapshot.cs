using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 战绩快照 DTO 与纯逻辑核心（Docs/23 P1-6 战绩面板）：服务器周期性构建
    /// （MatchLifecycle.BuildScoreboardPayload）经既有 RelayStatic/OnMatchEvent 通道广播，
    /// 客户端 MatchScoreboardView（Presentation）消费。JSON 走 JsonUtility
    /// （与 MatchKillPayload 同通道同序列化器）；纯函数部分与 Unity 解耦，EditMode 可测
    /// （先例同 MatchRules）。
    /// </summary>

    /// <summary>战绩面板单行（服务器权威数据；TDM 附队伍）。</summary>
    [Serializable]
    public sealed class MatchScoreboardEntry
    {
        public string playerId;
        public string displayName;
        public int kills;
        public int deaths;
        public int assists;
        public int pingMs;
        /// <summary>所属队伍（C3/Q04 TDM；None = 无队伍）。</summary>
        public string teamId = MatchRules.TeamNone;
        /// <summary>阵亡标识（Phase 4：服务器权威 _dead SyncVar 投影；面板置灰展示）。
        /// JSON 载荷追加字段——ObserversMatchEvent(kind, string) wire 形状不变，协议不递增。</summary>
        public bool isDead;
    }

    /// <summary>战绩快照载荷（ScoreboardSnapshot 事件 JSON）。</summary>
    [Serializable]
    public sealed class MatchScoreboardPayload
    {
        public string matchId;
        public MatchPhase phase;
        public int timeLimitSeconds;
        /// <summary>比赛剩余秒数（服务器权威；-1 = 无进行中比赛，HUD 隐藏计时）。</summary>
        public long timeLeftSeconds;
        public MatchScoreboardEntry[] entries;
        /// <summary>模式（"KillRace"/"TDM"；旧载荷缺省 = KillRace 语义）。</summary>
        public string mode = MatchRules.ModeKillRace;
        /// <summary>击杀目标（TDM=团队目标）。</summary>
        public int killTarget;
        /// <summary>红队团队击杀（TDM）。</summary>
        public int redKills;
        /// <summary>蓝队团队击杀（TDM）。</summary>
        public int blueKills;

        public bool IsTeamMode => mode == MatchRules.ModeTdm;
    }

    /// <summary>服务器构建快照的原始输入（与 NetworkBehaviour 解耦，纯核心可测）。</summary>
    public struct MatchScoreboardInput
    {
        public string playerId;
        public string displayName;
        public int kills;
        public int deaths;
        public int assists;
        public int pingMs;
        public string teamId;
        public bool isDead;
    }

    /// <summary>战绩面板纯逻辑核心：个人击杀/助攻排序 + 显示分契约 + 格式化。</summary>
    public static class MatchScoreboardSnapshot
    {
        /// <summary>显示分换算（纯展示聚合；终局结算载荷 MatchPlayerResult 不使用，Docs/17 契约不受影响）。</summary>
        public const int ScorePerKill = 100;
        public const int ScorePerAssist = 50;

        /// <summary>显示分 = 击杀×100 + 助攻×50（服务器与客户端同式同值，仅用于面板排序/展示）。</summary>
        public static int ComputeScore(int kills, int assists)
            => kills * ScorePerKill + assists * ScorePerAssist;

        /// <summary>K/D（deaths=0 按 1 兜底，恒有限值）。</summary>
        public static double KillDeathRatio(int kills, int deaths)
            => kills / (double)Math.Max(1, deaths);

        /// <summary>K/D 文本（两位小数，不变文化）。</summary>
        public static string FormatRatio(int kills, int deaths)
            => KillDeathRatio(kills, deaths).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>剩余时间 mm:ss（负值钳 0）。</summary>
        public static string FormatClock(long totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;
            return $"{minutes:00}:{seconds:00}";
        }

        /// <summary>
        /// 纯核心：原始输入 → 面板行，全局按个人击杀、助攻降序，随后按显示名稳定排序。
        /// </summary>
        public static MatchScoreboardEntry[] BuildEntries(List<MatchScoreboardInput> inputs)
        {
            if (inputs == null || inputs.Count == 0) return Array.Empty<MatchScoreboardEntry>();

            var entries = new MatchScoreboardEntry[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                entries[i] = new MatchScoreboardEntry
                {
                    playerId = input.playerId,
                    displayName = string.IsNullOrEmpty(input.displayName) ? input.playerId : input.displayName,
                    kills = Math.Max(0, input.kills),
                    deaths = Math.Max(0, input.deaths),
                    assists = Math.Max(0, input.assists),
                    pingMs = Math.Max(0, input.pingMs),
                    teamId = string.IsNullOrEmpty(input.teamId) ? MatchRules.TeamNone : input.teamId,
                    isDead = input.isDead,
                };
            }
            Array.Sort(entries, CompareRows);
            return entries;
        }

        /// <summary>TDM 团队比分标题（纯函数；TDM 且有目标 → "红 X : Y 蓝"；否则 null）。</summary>
        public static string FormatTeamScoreHeader(MatchScoreboardPayload payload)
        {
            if (payload == null || !payload.IsTeamMode) return null;
            return $"红 {payload.redKills} : {payload.blueKills} 蓝";
        }

        private static int CompareRows(MatchScoreboardEntry a, MatchScoreboardEntry b)
        {
            if (a == null || b == null) return a == null ? -1 : 1;
            if (a.kills != b.kills) return b.kills.CompareTo(a.kills);
            if (a.assists != b.assists) return b.assists.CompareTo(a.assists);
            if (a.deaths != b.deaths) return a.deaths.CompareTo(b.deaths);
            return string.CompareOrdinal(a.displayName ?? a.playerId, b.displayName ?? b.playerId);
        }
    }
}
