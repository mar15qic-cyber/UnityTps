using System;
using Microsoft.Extensions.Options;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 复审 R05 关闭用例（RoomChatService 直接单元驱动，零 HTTP/零睡眠）：
/// ①去重先于扣桶（有效重试不消耗额度）；②同 id 不同内容冲突拒绝；
/// ③Ring 淘汰后近窗重试仍命中原消息（去重独立于 Ring）；④换队可见水位（旧队/入队前队聊隔离）。
/// </summary>
public sealed class CFChatR05Tests
{
    private static RoomChatService NewService(int ringCapacity = 100, int tokenCapacity = 3)
    {
        return new RoomChatService(Options.Create(new ServerInstanceOptions
        {
            ChatRingCapacity = ringCapacity,
            ChatTokenCapacity = tokenCapacity,
            ChatTokenRefillSeconds = 2,
        }));
    }

    private static (GameRoom Room, GameRoomMember Member) Member(long userId, string teamId = Teams.None)
    {
        var room = new GameRoom { Id = 1, RoomCode = "R55R05", MaxPlayers = 8, Mode = GameModes.Tdm };
        var member = new GameRoomMember { UserId = userId, TeamId = teamId, Room = room };
        room.Members.Add(member);
        return (room, member);
    }

    private static ChatSendRequest Send(string body, string id, string channel = "All") =>
        new() { Channel = channel, Body = body, ClientMessageId = id };

    [Fact]
    public void Retry_SameId_ReturnsOriginalMessage_AndDoesNotConsumeTokens()
    {
        var service = NewService(); // 令牌容量 3
        var (room, member) = Member(1);

        var first = service.Send(room, member, Send("hello", "id-aaaa"));
        Assert.Equal(1UL, first.Seq);

        // 有效重试 ×3：同 seq 同正文（不重发不计费）
        for (var i = 0; i < 3; i++)
        {
            var retry = service.Send(room, member, Send("hello", "id-aaaa"));
            Assert.Equal(first.Seq, retry.Seq);
            Assert.Equal("hello", retry.Body);
        }

        // 关键断言：重试没扣桶——3 容量下新消息仍可连发 2 条（第一条已扣 1）
        Assert.Equal(2UL, service.Send(room, member, Send("b", "id-bbbb")).Seq);
        Assert.Equal(3UL, service.Send(room, member, Send("c", "id-cccc")).Seq);
        // 第 4 条新消息才撞限频
        Assert.Throws<UnityFps.Api.Common.ApiException>(() => service.Send(room, member, Send("d", "id-dddd")));
    }

    [Fact]
    public void Retry_SameId_DifferentBody_IsRejected()
    {
        var service = NewService();
        var (room, member) = Member(1);
        service.Send(room, member, Send("hello", "id-aaaa"));

        var conflict = Assert.Throws<UnityFps.Api.Common.ApiException>(
            () => service.Send(room, member, Send("evil", "id-aaaa")));
        Assert.Equal(ApiErrorCodes.ChatRejected, conflict.Code);
    }

    [Fact]
    public void Dedup_SurvivesRingEviction_AndDoesNotRebroadcast()
    {
        // Ring 容量收紧到 4：发 6 条后最早 2 条被挤出 Ring，但去重记录仍在
        var service = NewService(ringCapacity: 4, tokenCapacity: 100);
        var (room, member) = Member(1);
        var first = service.Send(room, member, Send("first", "id-aaaa"));
        for (var i = 0; i < 5; i++)
            service.Send(room, member, Send("msg-" + i, "id-evict-" + i));

        var retry = service.Send(room, member, Send("first", "id-aaaa"));
        Assert.Equal(first.Seq, retry.Seq);
        Assert.Equal("first", retry.Body);
    }

    [Fact]
    public void TeamSwitch_Watermark_HidesOldTeamMessages()
    {
        var service = NewService();
        var (room, red) = Member(1, Teams.Red);
        var (_, blue) = Member(2, Teams.Blue);
        room.Members.Add(blue);

        // 队友 red2 在 red 换队前发的历史队聊
        var (_, red2) = Member(3, Teams.Red);
        room.Members.Add(red2);
        service.Send(room, red2, Send("red-history", "id-hist", "Team"));

        // red：红→蓝→红（两次切换各记录水位）
        service.MarkTeamSwitch(room.RoomCode, red.UserId, Teams.Blue);
        service.MarkTeamSwitch(room.RoomCode, red.UserId, Teams.Red);

        // 换队后 red 不再看到"本次入队之前"的红队历史
        var feed = service.Fetch(room, red, 0);
        Assert.DoesNotContain(feed.Messages, m => m.Body == "red-history");

        // 新入队之后的红队消息照常可见
        var fresh = service.Send(room, red2, Send("red-fresh", "id-fresh", "Team"));
        var feed2 = service.Fetch(room, red, 0);
        Assert.Contains(feed2.Messages, m => m.Seq == fresh.Seq);

        // 蓝队消息对 red 不可见（投递层过滤）
        service.Send(room, blue, Send("blue-secret", "id-blue", "Team"));
        var feed3 = service.Fetch(room, red, 0);
        Assert.DoesNotContain(feed3.Messages, m => m.Body == "blue-secret");
    }

    [Fact]
    public void Retry_ExpiredWindow_ReprocessesNormally()
    {
        // 超窗/淘汰路径：同 id 允许作为新消息重新处理。dedupCapacityPerUser 下限钳 8（实现约束），
        // 以"记录被 FIFO 淘汰"等价覆盖"记录不存在 → 全新消息"路径（显式 8：发 8 条 filler 挤出第一条记录）
        var service = new RoomChatService(Options.Create(new ServerInstanceOptions
        {
            ChatRingCapacity = 100,
            ChatTokenCapacity = 100,
            ChatDedupCapacityPerUser = 8,
        }));
        var (room, member) = Member(1);
        var first = service.Send(room, member, Send("one", "id-aaaa"));
        for (var i = 0; i < 8; i++)
            service.Send(room, member, Send("filler-" + i, "id-fill-" + i)); // FIFO 淘汰 id-aaaa 记录

        // 记录被淘汰后同 id 重试 = 全新消息（新 seq），不是重放
        var again = service.Send(room, member, Send("one", "id-aaaa"));
        Assert.NotEqual(first.Seq, again.Seq);
        Assert.Equal("one", again.Body);
    }
}
