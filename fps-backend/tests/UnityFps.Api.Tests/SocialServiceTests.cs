using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class SocialServiceTests
{
    private static AppDbContext Database()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var id in new long[] { 1, 2, 3 }) db.Users.Add(new UserAccount { Id = id, Username = "User"+id, NormalizedUsername = "USER"+id, IdentityTag = "000"+id, LastSeenUtc = DateTime.UtcNow });
        db.Friendships.AddRange(new Friendship { UserId=1,FriendId=2 },new Friendship {UserId=2,FriendId=1});
        db.SaveChanges(); return db;
    }
    private static SendDirectMessageRequest Request(string body="你好") => new() { ClientMessageId=Guid.NewGuid().ToString(), Body=body };

    [Fact] public async Task OfflineMessagePersistsUnreadAndReadPositionAcrossServiceInstances()
    {
        await using var db=Database(); db.Users.Find(2L)!.LastSeenUtc=null;await db.SaveChangesAsync();
        var service=new SocialService(db,null!);
        var sent=await service.Send(1,2,Request(),default);
        var other=new SocialService(db,null!);
        Assert.Equal(1,Assert.Single((await other.Inbox(2,default)).Conversations).Unread);
        Assert.Equal(sent.Id,Assert.Single((await other.History(2,1,0,0,default)).Messages).Id);
        await other.MarkRead(2,1,sent.Id,default);
        Assert.Equal(0,Assert.Single((await service.Inbox(2,default)).Conversations).Unread);
    }
    [Fact] public async Task RetriesAreIdempotentButCannotReuseIdentityForDifferentBody()
    {
        await using var db=Database();var service=new SocialService(db,null!);var request=Request();
        var first=await service.Send(1,2,request,default);var replay=await service.Send(1,2,request,default);
        Assert.Equal(first.Id,replay.Id);Assert.Single(db.Set<DirectMessage>());
        request.Body="different";await Assert.ThrowsAsync<ApiException>(()=>service.Send(1,2,request,default));
    }
    [Fact] public async Task NonParticipantCannotReadAndRemovedFriendCannotSend()
    {
        await using var db=Database();var service=new SocialService(db,null!);await service.Send(1,2,Request(),default);
        Assert.Empty((await service.History(3,1,0,0,default)).Messages);
        db.Friendships.RemoveRange(db.Friendships);await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(()=>service.Send(1,2,Request(),default));
        Assert.Single((await service.History(1,2,0,0,default)).Messages);
        Assert.False(Assert.Single((await service.Inbox(1,default)).Conversations).CanSend);
    }
    [Fact] public async Task PaginationDoesNotSkipMessagesAndReadCannotAdvanceIntoAnotherConversation()
    {
        await using var db=Database();var service=new SocialService(db,null!);
        for(int i=0;i<105;i++)db.Add(new DirectMessage { SenderId=1,RecipientId=2,ClientMessageId=Guid.NewGuid().ToString(),Body="message "+i });
        db.Add(new DirectMessage { SenderId=3,RecipientId=2,ClientMessageId=Guid.NewGuid().ToString(),Body="other" });await db.SaveChangesAsync();
        var latest=await service.History(2,1,0,0,default);Assert.Equal(50,latest.Messages.Length);Assert.True(latest.HasMore);
        var previous=await service.History(2,1,latest.Messages[0].Id,0,default);Assert.Equal(50,previous.Messages.Length);
        Assert.Empty(latest.Messages.Select(m=>m.Id).Intersect(previous.Messages.Select(m=>m.Id)));
        var incremental=await service.History(2,1,0,1,default);Assert.Equal(2,incremental.Messages[0].Id);
        await service.MarkRead(2,1,long.MaxValue,default);
        Assert.Equal(105,(await db.Set<DirectMessageRead>().FindAsync(2L,1L))!.LastReadId);
    }
    [Fact] public async Task InviteRequiresFriendAndMembershipAndWaitingStateAndIsDeduplicated()
    {
        await using var db=Database();var service=new SocialService(db,null!);
        var room=new GameRoom{Id=10,RoomCode="SECRET",HostUserId=3,HostUsername="Host",Status=RoomStatus.Waiting,Members=new(){new GameRoomMember{UserId=1},new GameRoomMember{UserId=3}}};
        db.GameRooms.Add(room);await db.SaveChangesAsync();
        var request=new SendRoomInvitationRequest{RoomId=10,FriendId=2};
        await service.Invite(1,request,default);await service.Invite(1,request,default);Assert.Single(db.Set<RoomInvitation>());
        var card=Assert.Single((await service.Inbox(2,default)).Invitations);Assert.Equal("Host",card.LeaderUsername);
        Assert.DoesNotContain("SECRET",System.Text.Json.JsonSerializer.Serialize(card));
        room.Status=RoomStatus.InMatch;await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(()=>service.Invite(1,request,default));
        await Assert.ThrowsAsync<ApiException>(()=>service.Accept(2,card.Id,null,default));
        Assert.Empty((await service.Inbox(2,default)).Invitations);
    }
    [Fact] public async Task ExpiredAndForeignInvitationsCannotBeAccepted()
    {
        await using var db=Database();var service=new SocialService(db,null!);
        db.Add(new RoomInvitation{Id=1,RoomId=1,SenderId=1,RecipientId=2,ExpiresAtUtc=DateTime.UtcNow.AddSeconds(-1)});await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(()=>service.Accept(3,1,null,default));
        await Assert.ThrowsAsync<ApiException>(()=>service.Accept(2,1,null,default));
    }
}
