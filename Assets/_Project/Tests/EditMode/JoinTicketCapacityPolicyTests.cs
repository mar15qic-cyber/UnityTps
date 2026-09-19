using System.Collections.Generic;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    /// <summary>R7 审计修复：Pending 容量终验排除"被单活接管顶替的同 userId 旧连接"。
    /// 锁定：8 占位（满队）同账号重连接管成功；第 9 个不同用户超员拒绝；非 TDM/旧票不设限。</summary>
    public sealed class JoinTicketCapacityPolicyTests
    {
        private static TicketConsumeResult Profile(string userId, string teamId, string mode = "TDM", int maxPlayers = 16)
            => TicketConsumeResult.AcceptedFromBackend("ROOM01", userId, "u" + userId,
                matchMode: mode, teamId: teamId, maxPlayers: maxPlayers);

        [Test]
        public void FullTeam_SameUserTakeover_IsAccepted()
        {
            // 8v8 满队（每队 8）：8 个不同用户占满红队，红队某账号用新连接重连（单活接管）
            var accepted = new List<TicketConsumeResult>();
            for (int i = 1; i <= 8; i++)
                accepted.Add(Profile((100 + i).ToString(), "Red"));

            Assert.That(JoinTicketCapacityPolicy.CanAccept("TDM", 16, "Red", "101", accepted),
                Is.True, "满队同账号重连：旧连接名额被新连接顶替，不得误判 8+1 超员");
        }

        [Test]
        public void FullTeam_NinthDifferentUser_IsRejected()
        {
            var accepted = new List<TicketConsumeResult>();
            for (int i = 1; i <= 8; i++)
                accepted.Add(Profile((100 + i).ToString(), "Red"));

            Assert.That(JoinTicketCapacityPolicy.CanAccept("TDM", 16, "Red", "999", accepted),
                Is.False, "第 9 个不同用户必须超员拒绝（容量红线不变）");
        }

        [Test]
        public void SevenOccupied_PlusIncomingDifferentUser_IsAccepted()
        {
            var accepted = new List<TicketConsumeResult>();
            for (int i = 1; i <= 7; i++)
                accepted.Add(Profile((100 + i).ToString(), "Blue"));

            Assert.That(JoinTicketCapacityPolicy.CanAccept("TDM", 16, "Blue", "777", accepted), Is.True);
        }

        [Test]
        public void NonTdmOrLegacyTicket_IsUnrestricted()
        {
            var accepted = new List<TicketConsumeResult> { Profile("1", "Red") };
            Assert.That(JoinTicketCapacityPolicy.CanAccept("KillRace", 16, "None", "2", accepted), Is.True);
            Assert.That(JoinTicketCapacityPolicy.CanAccept("", 16, "Red", "2", accepted), Is.True, "旧票（mode 空）不设限");
            Assert.That(JoinTicketCapacityPolicy.CanAccept("TDM", 0, "Red", "2", accepted), Is.True, "容量字段缺失不设限");
        }

        [Test]
        public void NoneTeam_OccupantsDoNotCountTowardTeamCapacity()
        {
            var accepted = new List<TicketConsumeResult> { Profile("1", "None"), Profile("2", "Blue") };
            // 入队为 Red：None/Blue 占用不计入 Red 名额
            Assert.That(JoinTicketCapacityPolicy.CountTeamOccupants(accepted, "3", "Red"), Is.EqualTo(0));
        }
    }
}
