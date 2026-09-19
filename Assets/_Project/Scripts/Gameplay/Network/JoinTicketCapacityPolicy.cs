using System.Collections.Generic;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// TDM Pending 容量终验策略（I3 + R7 审计修复，纯逻辑）：
    /// 占用 = 认证档案中同队连接数（已生成玩家与 Pending 同样占名额），但【排除与本次接入同 userId 的
    /// 既有连接】——该连接会在接受路径被单活接管断开（JoinTicketAuthenticator 既有 USER_TAKEOVER），
    /// 其名额由本次接入顶替而非新增；不排除会让满队（如 8 占位）同账号重连被误判 8+1 超员拒绝，
    /// 破坏既有单活接管。不同用户的 Pending 计入名额（容量红线不变）。
    /// </summary>
    public static class JoinTicketCapacityPolicy
    {
        /// <summary>同队占用计数（纯函数）：排除与 incomingUserId 相同的既有档案（单活接管顶替）。</summary>
        public static int CountTeamOccupants(IEnumerable<TicketConsumeResult> acceptedProfiles,
            string incomingUserId, string teamId)
        {
            int occupied = 0;
            if (acceptedProfiles == null) return 0;
            foreach (var candidate in acceptedProfiles)
            {
                if (candidate == null) continue;
                // 单活接管顶替：与本次接入同 userId 的旧连接名额被本次顶替，不计占用
                if (!string.IsNullOrEmpty(incomingUserId) && candidate.UserId == incomingUserId) continue;
                var candidateTeam = string.IsNullOrEmpty(candidate.TeamId) ? MatchRules.TeamNone : candidate.TeamId;
                if (candidateTeam == teamId) occupied++;
            }
            return occupied;
        }

        /// <summary>接入终验（纯函数）：非 TDM / 旧票（mode 空）/ 容量字段缺失不设限；TDM 按
        /// CanJoinTeam（占用（已生成+Pending，扣除被顶替名额）+ 本次 ≤ 每队上限）判定。</summary>
        public static bool CanAccept(string mode, int maxPlayers, string teamId, string incomingUserId,
            IEnumerable<TicketConsumeResult> acceptedProfiles)
        {
            if (string.IsNullOrEmpty(mode) || mode != MatchRules.ModeTdm) return true;
            if (maxPlayers <= 0) return true;
            var normalizedTeam = string.IsNullOrEmpty(teamId) ? MatchRules.TeamNone : teamId;
            int occupied = CountTeamOccupants(acceptedProfiles, incomingUserId, normalizedTeam);
            return MatchRules.CanJoinTeam(mode, maxPlayers, normalizedTeam, occupied);
        }
    }
}
