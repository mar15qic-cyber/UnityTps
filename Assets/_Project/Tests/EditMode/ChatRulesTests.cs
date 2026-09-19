using System;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// C4/I1+I2 聊天纯规则与 DS 中继核心定向（Docs/26 §3.3 / Docs/27 §8.2 / 复审 R05+R06）：
    /// 校验（100 code point/控制字符/频道/队伍资格）、令牌桶、去重（含 Ring 无关的独立记录语义在
    /// 后端已锁——此处锁 DS 侧行为）、投递层队伍过滤、每局 epoch seq 隔离与重置。
    /// </summary>
    public sealed class ChatRulesTests
    {
        private static readonly DateTime T0 = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

        // ---- 校验 ----

        [Test]
        public void IsValidBody_LengthCountsCodePoints_NotUtf16Units()
        {
            string surrogate = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 100)); // 100 个 emoji = 200 UTF-16 单元
            Assert.That(ChatRules.IsValidBody(surrogate), Is.True, "100 个标量字符（代理对）应合法");
            string overflow = new string('中', 101);
            Assert.That(ChatRules.IsValidBody(overflow), Is.False, "101 个汉字超限");
        }
        [Test]
        public void IsValidBody_RejectsBlank_Newline_AndControl()
        {
            Assert.That(ChatRules.IsValidBody("   "), Is.False);
            Assert.That(ChatRules.IsValidBody("a\nb"), Is.False);
            Assert.That(ChatRules.IsValidBody("a\rb"), Is.False);
            Assert.That(ChatRules.IsValidBody("a\u0001b"), Is.False);
            Assert.That(ChatRules.IsValidBody("正常消息<b>标签</b>"), Is.True, "正文不解析标签，标签按纯文本放行");
        }

        [Test]
        public void ChannelAndTeam_Guards()
        {
            Assert.That(ChatRules.IsValidChannel("All"), Is.True);
            Assert.That(ChatRules.IsValidChannel("System"), Is.False, "System 无客户端发送路径");
            Assert.That(ChatRules.CanUseTeamChannel(MatchRules.TeamRed), Is.True);
            Assert.That(ChatRules.CanUseTeamChannel(MatchRules.TeamNone), Is.False);
        }

        // ---- 令牌桶 ----

        [Test]
        public void TokenBucket_ThreeBursts_ThenLimited_ThenRefills()
        {
            double tokens = ChatRules.TokenCapacity;
            var at = T0;
            var first = ChatRules.TryConsumeToken(tokens, at, at, 3, 2);
            Assert.That(first.Allowed, Is.True);
            tokens = first.Tokens;
            for (var i = 0; i < 2; i++)
            {
                var r = ChatRules.TryConsumeToken(tokens, at, at, 3, 2);
                Assert.That(r.Allowed, Is.True);
                tokens = r.Tokens;
            }
            var blocked = ChatRules.TryConsumeToken(tokens, at, at, 3, 2);
            Assert.That(blocked.Allowed, Is.False);
            Assert.That(blocked.RetryAfterSeconds, Is.GreaterThanOrEqualTo(1));
            var afterRefill = ChatRules.TryConsumeToken(blocked.Tokens, blocked.RefilledAt, T0.AddSeconds(2), 3, 2);
            Assert.That(afterRefill.Allowed, Is.True, "2 秒后回 1 个额度");
        }

        // ---- ChatRelayCore（DS 服务器权威状态）----

        [Test]
        public void RelayCore_Accepts_AssignsSequentialSeq_AndInjectsIdentity()
        {
            ChatRelayCore.ResetAllForTests();
            var a = ChatRelayCore.TryAccept("m1", 11, "alice", MatchRules.TeamRed, "All", "大家好", "id-1", T0);
            Assert.That(a.Outcome, Is.EqualTo(ChatRelayCore.AcceptOutcome.Accepted));
            Assert.That(a.Message.Seq, Is.EqualTo(1UL));
            Assert.That(a.Message.SenderUsername, Is.EqualTo("alice"));
            Assert.That(a.Message.TeamId, Is.EqualTo(MatchRules.TeamRed));
            var b = ChatRelayCore.TryAccept("m1", 22, "bob", MatchRules.TeamBlue, "Team", "队友好", "id-2", T0);
            Assert.That(b.Message.Seq, Is.EqualTo(2UL));
        }

        [Test]
        public void RelayCore_Duplicate_ReturnsOriginal_DifferentBodyRejected()
        {
            ChatRelayCore.ResetAllForTests();
            var first = ChatRelayCore.TryAccept("m1", 11, "alice", MatchRules.TeamRed, "All", "hello", "id-1", T0);
            var dup = ChatRelayCore.TryAccept("m1", 11, "alice", MatchRules.TeamRed, "All", "hello", "id-1", T0);
            Assert.That(dup.Outcome, Is.EqualTo(ChatRelayCore.AcceptOutcome.Duplicate));
            Assert.That(dup.Message.Seq, Is.EqualTo(first.Message.Seq), "重复不重发不占 seq");
            var conflict = ChatRelayCore.TryAccept("m1", 11, "alice", MatchRules.TeamRed, "All", "evil", "id-1", T0);
            Assert.That(conflict.Outcome, Is.EqualTo(ChatRelayCore.AcceptOutcome.Rejected));
        }

        [Test]
        public void RelayCore_TeamChannel_RequiresTeam_RejectsNone()
        {
            ChatRelayCore.ResetAllForTests();
            var result = ChatRelayCore.TryAccept("m1", 11, "alice", MatchRules.TeamNone, "Team", "hi", "id-1", T0);
            Assert.That(result.Outcome, Is.EqualTo(ChatRelayCore.AcceptOutcome.Rejected));
        }

        [Test]
        public void RelayCore_SystemMessages_HaveNoSender_AndConsumeSeq()
        {
            ChatRelayCore.ResetAllForTests();
            var system = ChatRelayCore.BuildSystem("m1", "红队获胜");
            Assert.That(system, Is.Not.Null);
            Assert.That(system.Channel, Is.EqualTo(ChatRules.ChannelSystem));
            Assert.That(system.Seq, Is.EqualTo(1UL));
            Assert.That(system.SenderUserId, Is.EqualTo(0));
        }

        [Test]
        public void RelayCore_EpochsAreIsolated_AndResetClears()
        {
            ChatRelayCore.ResetAllForTests();
            ChatRelayCore.TryAccept("match-a", 11, "a", MatchRules.TeamNone, "All", "x", "id-1", T0);
            var otherEpoch = ChatRelayCore.TryAccept("match-b", 12, "b", MatchRules.TeamNone, "All", "y", "id-2", T0);
            Assert.That(otherEpoch.Message.Seq, Is.EqualTo(1UL), "不同局独立 seq 空间");

            ChatRelayCore.ResetEpoch("match-b");
            Assert.That(ChatRelayCore.CurrentSeq("match-b"), Is.EqualTo(0UL), "重臂清空该局状态");
            Assert.That(ChatRelayCore.CurrentSeq("match-a"), Is.EqualTo(1UL), "其他局不受影响");
        }

        [Test]
        public void DeliverFilter_TeamOnlySameTeam_SystemToEveryone()
        {
            var team = ChatRelayCore.BuildSystem("m", "ignore");
            team.Channel = ChatRules.ChannelTeam;
            team.TeamId = MatchRules.TeamRed;
            Assert.That(ChatRelayCore.IsDeliverable(team, MatchRules.TeamRed), Is.True);
            Assert.That(ChatRelayCore.IsDeliverable(team, MatchRules.TeamBlue), Is.False);
            Assert.That(ChatRelayCore.IsDeliverable(team, MatchRules.TeamNone), Is.False);

            var system = ChatRelayCore.BuildSystem("m", "system");
            Assert.That(ChatRelayCore.IsDeliverable(system, MatchRules.TeamBlue), Is.True);
        }

        [Test]
        public void RespawnAndTeamHelpers_UnchangedRegression()
        {
            // C3 回归锚点：本文件改动不触及 MatchRules；保一条快速哨兵
            Assert.That(MatchRules.IsDamageAllowed(MatchRules.TeamRed, MatchRules.TeamRed), Is.False);
        }
    }
}
