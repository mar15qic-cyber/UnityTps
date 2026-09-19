using System;
using Game.Account;
using Game.UI;
using Game.UI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>R9 审计修复：房间聊天会话（消息/去重/游标的房间作用域持久持有者）+
    /// 结算结果文案模式感知（R2）纯函数。</summary>
    public sealed class ChatRoomSessionTests
    {
        [SetUp]
        public void SetUp() => ChatRoomSession.Reset("ROOM01");

        [TearDown]
        public void TearDown() => ChatRoomSession.Reset(string.Empty);

        private static ChatClientMessage Msg(string body, string clientMessageId = null, string channel = "all")
            => new ChatClientMessage
            {
                Transport = "H",
                Epoch = "room",
                Seq = 1,
                Channel = channel,
                SenderUserId = 1,
                SenderUsername = "A",
                Body = body,
                ClientMessageId = clientMessageId,
            };

        [Test]
        public void History_IsBoundedAt30()
        {
            for (int i = 0; i < 40; i++)
                Assert.That(ChatRoomSession.TryRegister(Msg("m" + i), "k" + i), Is.True);
            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(ChatRoomSession.MaxHistory));
            Assert.That(ChatRoomSession.Messages[ChatRoomSession.Messages.Count - 1].Body, Is.EqualTo("m39"), "保留最新");
            Assert.That(ChatRoomSession.Messages[0].Body, Is.EqualTo("m10"), "旧的被裁剪");
        }

        [Test]
        public void Dedup_ByClientMessageId_AndServerKey()
        {
            Assert.That(ChatRoomSession.TryRegister(Msg("hi", "c1"), "H:1"), Is.True);
            Assert.That(ChatRoomSession.TryRegister(Msg("hi", "c1"), "H:1"), Is.False, "端到端 id 重复");
            Assert.That(ChatRoomSession.TryRegister(Msg("hi2", null), "H:1"), Is.False, "服务端身份重复（重放）");
            Assert.That(ChatRoomSession.TryRegister(Msg("hi3", null), "H:2"), Is.True);
            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(2));
        }

        [Test]
        public void RoomSwitch_ResetsSession_CrossSceneStateSurvivesSameRoom()
        {
            ChatRoomSession.TryRegister(Msg("in-match msg", "c1"), "R:1:1");
            ChatRoomSession.HttpCursor = 42;

            // 同房间：返房/切场景重建后数据保留
            ChatRoomSession.EnsureRoom("ROOM01");
            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(1));
            Assert.That(ChatRoomSession.HttpCursor, Is.EqualTo((ulong)42));

            // 换房/退房：新会话清空
            ChatRoomSession.EnsureRoom("ROOM02");
            Assert.That(ChatRoomSession.Messages.Count, Is.EqualTo(0));
            Assert.That(ChatRoomSession.HttpCursor, Is.EqualTo((ulong)0));
        }
    }

    /// <summary>R2 审计修复：等待房间结果卡模式感知文案（TDM 胜队 / KillRace 个人胜者 / 平局）。</summary>
    public sealed class RoomResultVerdictTests
    {
        private static RoomMatchResultViewDto Dto(string status, string winnerTeam, RoomMatchResultPlayerViewDto[] players)
        {
            var dto = new RoomMatchResultViewDto
            {
                matchId = "match-1",
                status = status,
                winnerTeam = winnerTeam,
                durationSeconds = 60,
                players = players,
            };
            return dto;
        }

        [Test]
        public void Tdm_WinnerTeam_IsShown()
        {
            var players = new[] { new RoomMatchResultPlayerViewDto { userId = 1, username = "A" } };
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(Dto("Final", "Red", players), GameModes.Tdm), Is.EqualTo("上局：红队获胜"));
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(Dto("Final", "Blue", players), GameModes.Tdm), Is.EqualTo("上局：蓝队获胜"));
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(Dto("Final", null, players), GameModes.Tdm), Is.EqualTo("上局：平局"));
        }

        [Test]
        public void KillRace_PersonalWinner_IsShownByName()
        {
            var players = new[]
            {
                new RoomMatchResultPlayerViewDto { userId = 1, username = "Alice", kills = 8, isWin = false },
                new RoomMatchResultPlayerViewDto { userId = 2, username = "Bob", kills = 12, isWin = true },
                new RoomMatchResultPlayerViewDto { userId = 3, username = "Cid", kills = 3, isWin = false },
            };
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(Dto("Final", null, players), GameModes.KillRace),
                Is.EqualTo("上局：Bob 获胜"), "KillRace 个人胜者不再被写作平局");
        }

        [Test]
        public void KillRace_Draw_AndPending_AreHonest()
        {
            var players = new[] { new RoomMatchResultPlayerViewDto { userId = 1, username = "A", isWin = false } };
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(Dto("Final", null, players), GameModes.KillRace), Is.EqualTo("上局：平局"));
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(Dto("Pending", null, players), GameModes.KillRace), Is.EqualTo("上局：结算中…"));
            Assert.That(MatchSettlementFlow.BuildRoomResultVerdict(null, GameModes.KillRace), Is.Null);
        }
    }
}
