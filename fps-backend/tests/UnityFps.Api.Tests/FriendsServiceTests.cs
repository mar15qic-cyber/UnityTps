using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 好友系统服务层测试（2026-09-20 需求2）：用户名#编码 双约束查找、请求-同意制、
/// 在线状态（InMatch/InRoom 优先于 LastSeenUtc 在线窗口）、删除对称性。
/// InMemory 宿主：FriendRequest 的唯一索引不被 InMemory 强制（仅 MySQL），防重由服务前置检查承担。
/// </summary>
public sealed class FriendsServiceTests
{
    [Fact]
    public async Task Send_ByUsernameAndTag_CreatesRequest_AndIsCaseInsensitiveOnName()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var b = AddUser(db, "Bobby", tag: "0042");

        var entry = await service.SendAsync(a.Id, new FriendSendRequest { Query = "bobby#0042" }.Query, CancellationToken.None);

        Assert.Equal(b.Id, entry.UserId);
        Assert.Equal("Bobby", entry.Username);
        Assert.Equal("0042", entry.IdentityTag);

        var list = await service.ListAsync(b.Id, CancellationToken.None);
        Assert.Single(list.Incoming);
        Assert.Equal(a.Id, list.Incoming[0].UserId);
        Assert.Empty(list.Outgoing);
    }

    [Fact]
    public async Task Send_RejectsBadFormat_UnknownTarget_Self_AndDuplicates()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var b = AddUser(db, "bobby", tag: "5678");

        await AssertCode(ApiErrorCodes.FriendQueryInvalid, () => service.SendAsync(a.Id, "bobby", CancellationToken.None));
        await AssertCode(ApiErrorCodes.FriendQueryInvalid, () => service.SendAsync(a.Id, "bobby#12a4", CancellationToken.None));
        await AssertCode(ApiErrorCodes.FriendQueryInvalid, () => service.SendAsync(a.Id, "bobby#123", CancellationToken.None));
        await AssertCode(ApiErrorCodes.FriendNotFound, () => service.SendAsync(a.Id, "bobby#0000", CancellationToken.None));
        await AssertCode(ApiErrorCodes.FriendNotFound, () => service.SendAsync(a.Id, "nobody#1234", CancellationToken.None));
        await AssertCode(ApiErrorCodes.FriendSelf, () => service.SendAsync(a.Id, "alice#1234", CancellationToken.None));

        await service.SendAsync(a.Id, "bobby#5678", CancellationToken.None);
        await AssertCode(ApiErrorCodes.FriendRequestExists, () => service.SendAsync(a.Id, "bobby#5678", CancellationToken.None));
        // 反方向同样视为已存在（避免双方各挂一条申请）
        await AssertCode(ApiErrorCodes.FriendRequestExists, () => service.SendAsync(b.Id, "alice#1234", CancellationToken.None));
    }

    [Fact]
    public async Task Accept_CreatesSymmetricFriendship_RemovesRequest_AndRejectsNonRecipient()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var b = AddUser(db, "bobby", tag: "5678");
        var c = AddUser(db, "carol", tag: "9999");
        var sent = await service.SendAsync(a.Id, "bobby#5678", CancellationToken.None);

        await AssertCode(ApiErrorCodes.FriendRequestNotFound, () => service.AcceptAsync(c.Id, sent.RequestId, CancellationToken.None));

        await service.AcceptAsync(b.Id, sent.RequestId, CancellationToken.None);

        var forA = await service.ListAsync(a.Id, CancellationToken.None);
        var forB = await service.ListAsync(b.Id, CancellationToken.None);
        Assert.Single(forA.Friends);
        Assert.Single(forB.Friends);
        Assert.Equal(b.Id, forA.Friends[0].UserId);
        Assert.Equal(a.Id, forB.Friends[0].UserId);
        Assert.Empty(forA.Incoming);
        Assert.Empty(forA.Outgoing);
        Assert.Empty(forB.Incoming);
        Assert.Empty(forB.Outgoing);

        // 已是好友：再发申请 → ALREADY_FRIENDS
        await AssertCode(ApiErrorCodes.AlreadyFriends, () => service.SendAsync(a.Id, "bobby#5678", CancellationToken.None));
        // 已处理的申请不能再次 accept
        await AssertCode(ApiErrorCodes.FriendRequestNotFound, () => service.AcceptAsync(b.Id, sent.RequestId, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveRequest_WorksForRecipientDecline_AndSenderCancel()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var b = AddUser(db, "bobby", tag: "5678");
        var sent = await service.SendAsync(a.Id, "bobby#5678", CancellationToken.None);

        // 撤销（发件人）→ 重发 → 拒绝（收件人）
        await service.RemoveRequestAsync(a.Id, sent.RequestId, CancellationToken.None);
        Assert.Empty((await service.ListAsync(b.Id, CancellationToken.None)).Incoming);

        var resent = await service.SendAsync(a.Id, "bobby#5678", CancellationToken.None);
        await service.RemoveRequestAsync(b.Id, resent.RequestId, CancellationToken.None);
        Assert.Empty((await service.ListAsync(a.Id, CancellationToken.None)).Outgoing);

        // 无关第三人不可动他人申请
        var c = AddUser(db, "carol", tag: "9999");
        var again = await service.SendAsync(a.Id, "bobby#5678", CancellationToken.None);
        await AssertCode(ApiErrorCodes.FriendRequestNotFound, () => service.RemoveRequestAsync(c.Id, again.RequestId, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveFriend_DeletesBothDirections_And404WhenNotFriends()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var b = AddUser(db, "bobby", tag: "5678");
        var sent = await service.SendAsync(a.Id, "bobby#5678", CancellationToken.None);
        await service.AcceptAsync(b.Id, sent.RequestId, CancellationToken.None);

        await service.RemoveFriendAsync(a.Id, b.Id, CancellationToken.None);

        Assert.Empty((await service.ListAsync(a.Id, CancellationToken.None)).Friends);
        Assert.Empty((await service.ListAsync(b.Id, CancellationToken.None)).Friends);
        await AssertCode(ApiErrorCodes.FriendNotFound, () => service.RemoveFriendAsync(a.Id, b.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Presence_InMatchAndInRoom_OverrideOnlineWindow()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var inMatch = AddUser(db, "fighter", tag: "1111", lastSeenUtcUtc: DateTime.UtcNow.AddMinutes(-10));
        var inRoom = AddUser(db, "waiter", tag: "2222", lastSeenUtcUtc: DateTime.UtcNow.AddMinutes(-10));
        var online = AddUser(db, "online", tag: "3333", lastSeenUtcUtc: DateTime.UtcNow.AddSeconds(-30));
        var offline = AddUser(db, "ghost", tag: "4444", lastSeenUtcUtc: DateTime.UtcNow.AddMinutes(-30));
        AddRoomMembership(db, inMatch, RoomStatus.InMatch);
        AddRoomMembership(db, inRoom, RoomStatus.Waiting);
        // 建立好友关系（被测列表是 a 的好友）
        foreach (var friend in new[] { inMatch, inRoom, online, offline })
            db.Friendships.Add(new Friendship { UserId = a.Id, FriendId = friend.Id, CreatedAtUtc = DateTime.UtcNow });
        db.SaveChanges();

        var list = await service.ListAsync(a.Id, CancellationToken.None);
        var presence = list.Friends.ToDictionary(f => f.Username, f => f.Presence);

        Assert.Equal(FriendPresence.InMatch, presence["fighter"]);
        Assert.Equal(FriendPresence.InRoom, presence["waiter"]);
        Assert.Equal(FriendPresence.Online, presence["online"]);
        Assert.Equal(FriendPresence.Offline, presence["ghost"]);
    }

    [Fact]
    public async Task List_FriendRowsSortedByName_WithIdentityTag()
    {
        await using var db = CreateDb();
        var service = new FriendsService(db);
        var a = AddUser(db, "alice", tag: "1234");
        var z = AddUser(db, "zoe", tag: "0001");
        var m = AddUser(db, "mike", tag: "0002");
        foreach (var friend in new[] { z, m })
        {
            var sent = await service.SendAsync(friend.Id, "alice#1234", CancellationToken.None);
            await service.AcceptAsync(a.Id, sent.RequestId, CancellationToken.None);
        }

        var list = await service.ListAsync(a.Id, CancellationToken.None);
        Assert.Equal(new[] { "mike", "zoe" }, list.Friends.Select(f => f.Username).ToArray());
        Assert.All(list.Friends, f => Assert.Matches("^\\d{4}$", f.IdentityTag));
    }

    private static async Task AssertCode(string expected, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserAccount AddUser(AppDbContext db, string name, string tag, DateTime? lastSeenUtcUtc = null)
    {
        var user = new UserAccount
        {
            Username = name,
            NormalizedUsername = name.ToUpperInvariant(),
            IdentityTag = tag,
            PasswordHash = "test",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenUtc = lastSeenUtcUtc,
            Profile = new PlayerProfile { UpdatedAtUtc = DateTime.UtcNow },
            Loadout = new PlayerLoadout { UpdatedAtUtc = DateTime.UtcNow },
            Wallet = new PlayerWallet { Coins = CatalogSeeder.InitialCoins, UpdatedAtUtc = DateTime.UtcNow }
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static void AddRoomMembership(AppDbContext db, UserAccount user, string roomStatus)
    {
        var room = new GameRoom
        {
            RoomCode = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            HostUserId = user.Id,
            HostUsername = user.Username,
            Status = roomStatus,
            StateChangedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
        };
        db.GameRooms.Add(room);
        db.GameRoomMembers.Add(new GameRoomMember
        {
            Room = room,
            User = user,
            JoinedAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}
