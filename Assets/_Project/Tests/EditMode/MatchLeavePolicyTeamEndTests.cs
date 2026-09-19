using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// R6 审计修复：TDM 按队伍剩余人数终局（旧"总数≤2"在 3红1蓝唯一蓝退出时不触发终局）+ R2 离队胜队映射。
    /// </summary>
    public sealed class MatchLeavePolicyTeamEndTests
    {
        [Test]
        public void Tdm_OneTeamWiped_ByOutnumberedLeaver_EndsMatch()
        {
            // 3红1蓝：唯一蓝退出（移除后红 3 蓝 0）→ 终局（旧规则总数 4 > 2 不终局）
            Assert.That(MatchLeavePolicy.ShouldEndMatchOnTeamLeave(redRemaining: 3, blueRemaining: 0), Is.True);
        }

        [Test]
        public void Tdm_BothTeamsAlive_ContinuesMatch()
        {
            Assert.That(MatchLeavePolicy.ShouldEndMatchOnTeamLeave(redRemaining: 3, blueRemaining: 1), Is.False);
            Assert.That(MatchLeavePolicy.ShouldEndMatchOnTeamLeave(redRemaining: 1, blueRemaining: 1), Is.False);
        }

        [Test]
        public void Tdm_SimultaneousWipe_IsDrawRule()
        {
            // 双方归零同语义：终局条件成立（顺序化处理下首个事件触发；胜者映射为 null 平局）
            Assert.That(MatchLeavePolicy.ShouldEndMatchOnTeamLeave(redRemaining: 0, blueRemaining: 0), Is.True);
        }

        [Test]
        public void Tdm_TeamWinnerOnLeaveEnd_OpposingSurvivingTeamWins()
        {
            Assert.That(MatchLeavePolicy.TeamWinnerOnLeaveEnd("Blue", opposingTeamHasRemaining: true), Is.EqualTo("Red"));
            Assert.That(MatchLeavePolicy.TeamWinnerOnLeaveEnd("Red", opposingTeamHasRemaining: true), Is.EqualTo("Blue"));
        }

        [Test]
        public void Tdm_TeamWinnerOnLeaveEnd_BothWipedOrNoTeam_IsDraw()
        {
            Assert.That(MatchLeavePolicy.TeamWinnerOnLeaveEnd("Blue", opposingTeamHasRemaining: false), Is.Null, "对方无存活=平局");
            Assert.That(MatchLeavePolicy.TeamWinnerOnLeaveEnd("None", opposingTeamHasRemaining: true), Is.Null, "离场者无队伍=平局");
            Assert.That(MatchLeavePolicy.TeamWinnerOnLeaveEnd("", opposingTeamHasRemaining: true), Is.Null);
        }

        [Test]
        public void Legacy_TotalCountRule_Preserved()
        {
            // 旧规则保留作兜底：≤2 人仍终局（KillRace 与 TDM 双保险）
            Assert.That(MatchLeavePolicy.ShouldEndMatch(2), Is.True);
            Assert.That(MatchLeavePolicy.ShouldEndMatch(3), Is.False);
        }
    }
}
