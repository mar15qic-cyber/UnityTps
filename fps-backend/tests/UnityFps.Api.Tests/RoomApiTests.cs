using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Data;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 房间 API 契约测试（Docs/27 v1 CF 等待房间语义）：创建/加入只进 Waiting（无票据/无实例租用）；
/// 租 DS/签票在 start（票据断言见 ServerInstanceApiTests 与 CFWaitingRoomTests）；
/// leader 离开转移管理权而非解散房间；最后一名成员离开删除房间。
/// 旧 CreateRoomReturnsConnectionWithTicket 已改写为等待语义；开始签票断言由 CreateStartedRoom* 链路保留（覆盖不删减）。
/// </summary>
public sealed class RoomApiTests : IClassFixture<ServerApiFactory>
{
    private readonly ServerApiFactory factory;

    public RoomApiTests(ServerApiFactory factory) => this.factory = factory;

    private HttpClient NewClient() => factory.CreateClient();

    [Fact]
    public async Task CreateRoomReturnsWaitingSnapshotWithoutConnection()
    {
        var client = NewClient();
        var (token, username) = await ServerTest.RegisterUserAsync(client);
        var auth = ServerTest.Authorized(client, token);

        var create = await auth.PostAsJsonAsync("/api/rooms", new { maxPlayers = 4 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var snapshot = await create.Content.ReadFromJsonAsync<JsonElement>();
        var room = snapshot.GetProperty("room");
        var code = ServerTest.HostRoomCode(room);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", code); // 无 I/O/0/1
        Assert.Equal(username, room.GetProperty("leaderUsername").GetString());
        Assert.Equal(4, room.GetProperty("maxPlayers").GetInt32());
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal("Waiting", room.GetProperty("status").GetString());
        Assert.Equal("TDM", room.GetProperty("mode").GetString());      // 默认模式
        Assert.Equal("arena", room.GetProperty("mapId").GetString());   // 默认地图
        Assert.Equal(1, room.GetProperty("roomVersion").GetInt64());
        Assert.False(room.TryGetProperty("matchId", out var matchId) && matchId.ValueKind == JsonValueKind.String); // 未开局无比赛（null 序列化为 Null 字面量）
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("connection").ValueKind); // 等待房间连接信息为 null（不携带票据）
    }

    [Fact]
    public async Task PlayerReportedAddressIsIgnored()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client); // 默认地址 10.1.0.5
        var (token, _) = await ServerTest.RegisterUserAsync(client);
        // 旧客户端请求体带 hostAddress/hostPort：解析兼容，值一律忽略
        var create = await ServerTest.Authorized(client, token).PostAsJsonAsync("/api/rooms",
            new { maxPlayers = 4, hostAddress = "evil.example.com", hostPort = 1234 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var snapshot = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(snapshot.TryGetProperty("serverAddress", out _)); // 创建响应不再包含连接地址

        // 直连地址以服务器实例注册上报为准：开局连接信息的地址来自实例
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        await ServerTest.JoinRoomAsync(client, guestToken, code);
        await ServerTest.ReadyAsync(client, guestToken, code);
        var start = await ServerTest.StartRoomAsync(client, token, code);
        Assert.Equal("10.1.0.5", ServerTest.ConnectionAddress(start));
    }

    [Fact]
    public async Task ListExposesRoomsWithoutSensitiveFields()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (token, username) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, token);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var list = await client.GetAsync("/api/rooms");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(code, raw);
        Assert.DoesNotContain("roomCode", raw);
        Assert.Contains(username, raw);
        Assert.DoesNotContain("joinTicket", raw);   // 列表绝不序列化票据
        Assert.DoesNotContain("ticketHash", raw);
        Assert.DoesNotContain("serverKey", raw);
        Assert.DoesNotContain("hostAddress", raw);  // 新 DTO 不再暴露直连地址字段
    }

    [Fact]
    public async Task JoinByCodeAddsMemberWithoutTicket()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        var joined = await ServerTest.JoinRoomAsync(client, guestToken, code);
        Assert.Equal(2, joined.GetProperty("room").GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(JsonValueKind.Null, joined.GetProperty("connection").ValueKind); // Waiting 无票据（null 而非缺席）
        Assert.Equal("Blue", joined.GetProperty("you").GetProperty("teamId").GetString()); // TDM 自动分队：红1→蓝
    }

    [Fact]
    public async Task JoinUnknownRoomReturns404()
    {
        var client = NewClient();
        var (token, _) = await ServerTest.RegisterUserAsync(client);
        var join = await ServerTest.Authorized(client, token).PostAsync("/api/rooms/9223372036854775807/join", null);
        Assert.Equal(HttpStatusCode.NotFound, join.StatusCode);
    }

    /// <summary>Docs/27 §2：leader 离开=确定性转移，房间保留。</summary>
    [Fact]
    public async Task LeaderLeaveTransfersToEarliestJoined()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (leaderToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, leaderToken, maxPlayers: 8);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (firstGuestToken, firstGuestName) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, firstGuestToken, code);
        var (secondGuestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, secondGuestToken, code);

        var leave = await ServerTest.Authorized(client, leaderToken).PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);

        var list = await ServerTest.Authorized(client, leaderToken).GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = Assert.Single(list!, r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(code));
        Assert.Equal(2, room.GetProperty("joinedPlayers").GetInt32());       // 房间仍在，人数 2
        Assert.Equal("Waiting", room.GetProperty("status").GetString());
        Assert.Equal(firstGuestName, room.GetProperty("leaderUsername").GetString()); // 最早加入者接任
    }

    [Fact]
    public async Task LastMemberLeaveDeletesRoomForReuse()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client); // 唯一实例
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var leave = await ServerTest.Authorized(client, hostToken).PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);

        var list = await ServerTest.Authorized(client, hostToken).GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.DoesNotContain(list!, r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(code));

        // 实例可复用：第二个用户不注册新实例也能开局租到同一实例（否则唯一实例被占用会 409）
        var (secondToken, _) = await ServerTest.RegisterUserAsync(client);
        var (_, _, _, started) = await ServerTest.CreateStartedRoomAsync(client);
        Assert.Equal("10.1.0.5", ServerTest.ConnectionAddress(started));
        _ = secondToken;
    }

    [Fact]
    public async Task CreateWithoutInstanceSucceedsAndStartRequiresServer()
    {
        // 独立空存储工厂：保证"无实例"前提不受同类其他测试残留影响
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (token, _) = await ServerTest.RegisterUserAsync(client);
        // CF：创建不再要求实例存在（Waiting 不连 DS）
        var snapshot = await ServerTest.CreateRoomAsync(client, token);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, code);
        await ServerTest.ReadyAsync(client, guestToken, code);

        // 开局才需要实例：无可用 DS → 409 NO_SERVER_AVAILABLE
        var start = await ServerTest.Authorized(client, token).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(code)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        using var problem = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        Assert.Equal("NO_SERVER_AVAILABLE", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MemberLeaveInTwoPlayerRoomReducesToOneAndKeepsRoom()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, code);

        var leave = await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);

        var list = await ServerTest.Authorized(client, hostToken).GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = Assert.Single(list!, r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(code));
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal("Waiting", room.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LeaveReplayIsIdempotent()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, code);

        var first = await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);
        var replay = await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode); // 重复离开=幂等成功

        var list = await ServerTest.Authorized(client, hostToken).GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(code));
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32()); // 重放不得二次减员
    }

    [Fact]
    public async Task ConcurrentMemberLeavesAreAllApplied()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 8);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var guests = new List<HttpClient>();
        for (var i = 0; i < 3; i++)
        {
            var (t, _) = await ServerTest.RegisterUserAsync(client);
            // 每个身份独立 client（共享 client 会互相覆盖 Authorization 头，并发时三发同 token）
            guests.Add(ServerTest.Authorized(factory.CreateClient(), t));
            await guests[^1].PostAsync($"/api/rooms/{ServerTest.PublicRoomId(code)}/join", null);
        }

        var leaves = await Task.WhenAll(guests.Select(g => g.PostAsync("/api/rooms/leave", null)));
        Assert.All(leaves, l => Assert.Equal(HttpStatusCode.NoContent, l.StatusCode));

        var list = await ServerTest.Authorized(client, hostToken).GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(code));
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32()); // 并发退出后 Members.Count 为真相
    }

    [Fact]
    public async Task DuplicateJoinIsIdempotentInWaiting()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        var guest = ServerTest.Authorized(client, guestToken);
        var first = await guest.PostAsync($"/api/rooms/{ServerTest.PublicRoomId(code)}/join", null);
        var replay = await guest.PostAsync($"/api/rooms/{ServerTest.PublicRoomId(code)}/join", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // 重复加入（重入）=幂等返回当前房间

        var joined = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, joined.GetProperty("room").GetProperty("joinedPlayers").GetInt32()); // 重复加入不得重复计数
        Assert.Equal(JsonValueKind.Null, joined.GetProperty("connection").ValueKind); // Waiting 无票据重发（票据断言移交开局链路）
    }

    /// <summary>成员保活心跳（Docs/27 §2）：成员调用成功；非成员无副作用同样成功（兼容旧客户端）。</summary>
    [Fact]
    public async Task MemberHeartbeatRefreshesKeepalive()
    {
        var client = NewClient();
        var (token, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, token);
        var code = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var beat = await ServerTest.Authorized(client, token).PostAsync("/api/rooms/heartbeat", null);
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);

        var detail = await ServerTest.Authorized(client, token).GetFromJsonAsync<JsonElement>($"/api/rooms/{ServerTest.PublicRoomId(code)}");
        Assert.Equal(1, detail.GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task UnauthenticatedRoomAccessIsRejected()
    {
        var client = NewClient();
        var response = await client.GetAsync("/api/rooms");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LeaveWithoutTokenIsRejected()
    {
        var client = NewClient();
        var response = await client.PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 审计 P1-3 等价回归：无实例绑定的空状态历史行（client-hosted 时代的形态）
    /// 不会以 Status="" 出现在列表，且被懒清理删除不残留（Docs/27 v1 维护扫描的未知状态清除分支）。
    /// </summary>
    [Fact]
    public async Task LegacyUnboundRoomsWithBlankStatusArePurgedNotListed()
    {
        var client = NewClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var legacyUser = new UserAccount
            {
                Username = "legacy_" + Guid.NewGuid().ToString("N")[..8],
                NormalizedUsername = "LEGACY_" + Guid.NewGuid().ToString("N")[..8],
                PasswordHash = "legacy",
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(legacyUser);
            await db.SaveChangesAsync(); // 先落用户取 Id

            db.GameRooms.Add(new GameRoom
            {
                RoomCode = "LEGAC1",
                HostUserId = legacyUser.Id,
                HostUsername = legacyUser.Username,
                HostAddress = "192.168.1.1",
                HostPort = 7770,
                MaxPlayers = 8,
                JoinedPlayers = 1,
                IsOpen = true,
                Status = "", // 迁移回填前的历史形态
                CreatedAtUtc = DateTime.UtcNow,
                LastHeartbeatUtc = DateTime.UtcNow,
                Members = { new GameRoomMember { UserId = legacyUser.Id, JoinedAtUtc = DateTime.UtcNow } },
            });
            await db.SaveChangesAsync();
        }

        var list = await ServerTest.Authorized(factory.CreateClient(),
            (await ServerTest.RegisterUserAsync(client)).Token).GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.DoesNotContain(list!, r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId("LEGAC1"));

        // 懒清理兜底：无实例绑定的空状态行必须被删除，不得残留为僵尸数据
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.GameRooms.AnyAsync(x => x.RoomCode == "LEGAC1"));
        }
    }
}
