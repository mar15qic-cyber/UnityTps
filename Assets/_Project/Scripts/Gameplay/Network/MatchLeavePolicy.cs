using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 离开对局策略（Phase C，纯函数可测）：服务器权威判定的唯一真相。
    /// 规则（用户定案）：有效玩家总数 = 2（含防御性的 &lt;=2）时，任一人主动退出或断线
    /// → 以 PlayerLeft 终局，离开者判负、剩余者判胜；有效玩家总数 &gt; 2 → 仅移除退出者，
    /// 其余玩家继续比赛（Phase/比分/计时不变）。
    /// R6：TDM 按队伍剩余人数终局——任一队伍清零即终局（等价性修正：旧"总数≤2"规则在
    /// 3红1蓝唯一蓝退出时不成立，剩 3 红只能干等超时）。
    /// 有效玩家数由服务器在移除玩家【之前】统计（FindPlayers 快照），客户端上报的任何人数
    /// 一律不参与判定（反作弊边界）。
    /// </summary>
    public static class MatchLeavePolicy
    {
        /// <summary>该离开事件是否应终局（effectivePlayerCount 为移除前的服务器权威人数）。</summary>
        public static bool ShouldEndMatch(int effectivePlayerCount)
            => effectivePlayerCount <= 2;

        /// <summary>TDM 离队终局判定（纯函数，R6）：移除离开者后任一队伍剩余 0 → 终局。
        /// 双方同时归零同语义（顺序化处理下首个事件触发，winnerTeam=null 平局）。</summary>
        public static bool ShouldEndMatchOnTeamLeave(int redRemaining, int blueRemaining)
            => redRemaining <= 0 || blueRemaining <= 0;

        /// <summary>TDM 离队终局胜队（纯函数，R2/R6）：离场队伍让位，存活的对方队伍获胜；
        /// 离场队伍非 Red/Blue 或对方无存活（双方归零）→ null = 平局（无胜者）。</summary>
        public static string TeamWinnerOnLeaveEnd(string departingTeam, bool opposingTeamHasRemaining)
        {
            if (string.IsNullOrEmpty(departingTeam) || !opposingTeamHasRemaining) return null;
            if (departingTeam == MatchRules.TeamRed) return MatchRules.TeamBlue;
            if (departingTeam == MatchRules.TeamBlue) return MatchRules.TeamRed;
            return null;
        }

        /// <summary>2 人退出终局时的胜负映射：离开者判负（false），剩余者判胜（true）。</summary>
        public static bool IsWinnerOnLeaveEnd(bool isDepartingPlayer)
            => !isDepartingPlayer;

        /// <summary>离开终局是否需要提交结算：2 人退出终局是正式终局（双方提交，离开者按负）。</summary>
        public static bool ShouldSubmitSettlement(MatchEndReason reason)
            => reason == MatchEndReason.Normal || reason == MatchEndReason.TimeLimit || reason == MatchEndReason.PlayerLeft;
    }

    /// <summary>
    /// 离开处理幂等闸（Phase C）：同一比赛内每个 clientId 只处理一次离开
    /// （重复 RPC / 断线事件重放 / 终局与退出竞态均幂等）。纯逻辑可测。
    /// </summary>
    public sealed class LeaveOnceGuard
    {
        private readonly HashSet<long> _processed = new();

        /// <summary>首次返回 true 并占用；重复返回 false（忽略）。</summary>
        public bool TryBegin(long clientId) => _processed.Add(clientId);

        public int ProcessedCount => _processed.Count;

        public void Clear() => _processed.Clear();
    }
}
