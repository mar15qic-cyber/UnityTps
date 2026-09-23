using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// CF 复审 F1 定向回归：返房 ack、真实战斗连接退场、DS 重臂三种事实分离。
/// 这里只覆盖后端 HTTP/持久状态；真实 FishNet 连接关闭与心跳调度仍需实机验收。
/// </summary>
public sealed class CFReviewF1FixTests
{
    private sealed record Fixture(
        ServerApiFactory Factory,
        HttpClient Client,
        string InstanceId,
        string RoomCode,
        string HostToken,
        string GuestToken,
        string MatchId,
        IReadOnlyList<(long UserId, long SessionId)> Sessions);

    private static async Task<Fixture> StartFixtureAsync()
    {
        var factory = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = factory.CreateClient();
        var instance = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = instance.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "TDM");
        var matchId = start.GetProperty("matchId").GetString()!;
        var sessions = new List<(long UserId, long SessionId)>();
        foreach (var token in new[] { hostToken, guestToken })
        {
            using var detail = await ServerTest.Authorized(client, token).GetAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}");
            detail.EnsureSuccessStatusCode();
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var ticket = detailJson.RootElement.GetProperty("connection").GetProperty("joinTicket").GetString()!;
            var consumed = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
            sessions.Add((consumed.GetProperty("userId").GetInt64(), consumed.GetProperty("sessionId").GetInt64()));
        }
        return new Fixture(factory, client, instanceId, roomCode, hostToken, guestToken, matchId, sessions);
    }

    private static object Result(Fixture f)
        => new
        {
            matchId = f.MatchId,
            durationSeconds = 30,
            winnerTeam = "Red",
            players = f.Sessions.Select((x, i) => new
            {
                userId = x.UserId,
                teamId = i == 0 ? "Red" : "Blue",
                kills = 1,
                deaths = 0,
                assists = 0,
                participationSeconds = 30,
                rewardEligible = false,
            }).ToArray(),
        };

    private static Task<HttpResponseMessage> DisconnectAsync(Fixture f, (long UserId, long SessionId) session)
        => ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/players/disconnect", ServerTest.ServerKey,
            new { roomCode = f.RoomCode, userId = session.UserId, sessionId = session.SessionId });

    private static Task<HttpResponseMessage> RearmAsync(Fixture f)
        => ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = InstanceState.Ready });

    private static async Task<(RoomStatusSnapshot Room, InstanceStateSnapshot Instance)> ReadStateAsync(Fixture f)
    {
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = await db.GameRooms.AsNoTracking().SingleAsync(x => x.RoomCode == f.RoomCode);
        var instance = await db.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == f.InstanceId);
        return (new RoomStatusSnapshot(room.Status, room.CurrentMatchId),
            new InstanceStateSnapshot(instance.State, instance.RoomCode));
    }

    private readonly record struct RoomStatusSnapshot(string Status, string? MatchId);
    private readonly record struct InstanceStateSnapshot(string State, string? RoomCode);

    private static async Task AckAsync(Fixture f, string token)
    {
        var response = await ServerTest.Authorized(f.Client, token)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(f.RoomCode)}/return", new { matchId = f.MatchId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllAckThenDisconnect_StaysDrainingUntilRearm_AndRoomCanRestart()
    {
        var f = await StartFixtureAsync();
        var result = await ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/match-result", ServerTest.ServerKey, Result(f));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await AckAsync(f, f.HostToken);
        await AckAsync(f, f.GuestToken);

        var beforeDisconnect = await ReadStateAsync(f);
        Assert.Equal(RoomStatus.Returning, RoomStatus.Normalize(beforeDisconnect.Room.Status));
        Assert.Equal(InstanceState.Draining, beforeDisconnect.Instance.State);

        Assert.Equal(HttpStatusCode.OK, (await DisconnectAsync(f, f.Sessions[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisconnectAsync(f, f.Sessions[1])).StatusCode);
        var beforeRearm = await ReadStateAsync(f);
        Assert.Equal(RoomStatus.Waiting, RoomStatus.Normalize(beforeRearm.Room.Status));
        Assert.Equal(InstanceState.Draining, beforeRearm.Instance.State);

        Assert.Equal(HttpStatusCode.NoContent, (await RearmAsync(f)).StatusCode);
        var afterRearm = await ReadStateAsync(f);
        Assert.Equal(InstanceState.Ready, afterRearm.Instance.State);
        Assert.Null(afterRearm.Instance.RoomCode);

        await ServerTest.ReadyAsync(f.Client, f.GuestToken, f.RoomCode);
        var secondStart = await ServerTest.Authorized(f.Client, f.HostToken)
            .PostAsync($"/api/rooms/{ServerTest.PublicRoomId(f.RoomCode)}/start", null);
        Assert.Equal(HttpStatusCode.OK, secondStart.StatusCode);
    }

    [Fact]
    public async Task MixedAckAndDisconnect_DoesNotArchiveUntilRemainingConnectionLeaves()
    {
        var f = await StartFixtureAsync();
        var result = await ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/match-result", ServerTest.ServerKey, Result(f));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await AckAsync(f, f.HostToken);

        Assert.Equal(HttpStatusCode.OK, (await DisconnectAsync(f, f.Sessions[1])).StatusCode);
        var mixed = await ReadStateAsync(f);
        Assert.Equal(RoomStatus.Returning, RoomStatus.Normalize(mixed.Room.Status));
        Assert.Equal(f.MatchId, mixed.Room.MatchId);
        Assert.Equal(InstanceState.Draining, mixed.Instance.State);
        Assert.Equal(2, await CountMembersAsync(f));

        Assert.Equal(HttpStatusCode.OK, (await DisconnectAsync(f, f.Sessions[0])).StatusCode);
        var final = await ReadStateAsync(f);
        Assert.Equal(RoomStatus.Waiting, RoomStatus.Normalize(final.Room.Status));
        Assert.Equal(InstanceState.Draining, final.Instance.State);
    }

    [Fact]
    public async Task ReturningTimeoutWithLiveConsumedSession_RemainsDraining_AndReadyHeartbeatIsRejected()
    {
        var f = await StartFixtureAsync();
        var result = await ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/match-result", ServerTest.ServerKey, Result(f));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await AckAsync(f, f.HostToken);
        await AckAsync(f, f.GuestToken);

        using (var scope = f.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var room = await db.GameRooms.SingleAsync(x => x.RoomCode == f.RoomCode);
            room.StateChangedAtUtc -= TimeSpan.FromMinutes(2);
            await db.SaveChangesAsync();
        }
        _ = await f.Client.GetFromJsonAsync<JsonElement[]>("/api/rooms");

        var held = await ReadStateAsync(f);
        Assert.Equal(RoomStatus.Returning, RoomStatus.Normalize(held.Room.Status));
        Assert.Equal(InstanceState.Draining, held.Instance.State);
        var premature = await RearmAsync(f);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Equal(2, await CountLiveRosterAsync(f));
    }

    [Fact]
    public async Task DuplicateFinalDisconnect_ReplaysReleasePermitWithoutReleasingEarly()
    {
        var f = await StartFixtureAsync();
        var result = await ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/match-result", ServerTest.ServerKey, Result(f));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await AckAsync(f, f.HostToken);
        await AckAsync(f, f.GuestToken);

        Assert.Equal(HttpStatusCode.OK, (await DisconnectAsync(f, f.Sessions[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisconnectAsync(f, f.Sessions[1])).StatusCode);
        var beforeRetry = await ReadStateAsync(f);
        Assert.Equal(RoomStatus.Waiting, RoomStatus.Normalize(beforeRetry.Room.Status));
        Assert.Equal(InstanceState.Draining, beforeRetry.Instance.State);

        // 最后一条 disconnect 的 HTTP 响应丢失后，DS 用同一 session 重试：重复许可可重放，
        // 但后端不得在收到 Ready+0 心跳前提前翻回 Ready。
        using var duplicate = await DisconnectAsync(f, f.Sessions[1]);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var body = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("remainingPlayers").GetInt32());
        Assert.Equal(InstanceState.Ready, body.RootElement.GetProperty("instanceState").GetString());
        var afterRetry = await ReadStateAsync(f);
        Assert.Equal(InstanceState.Draining, afterRetry.Instance.State);
        Assert.Equal(HttpStatusCode.NoContent, (await RearmAsync(f)).StatusCode);
    }

    [Fact]
    public async Task HttpLeaveBeforeDisconnect_RecyclesOnlyInstanceAcrossTenMatches()
    {
        using var factory = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        using var client = factory.CreateClient();
        var instance = await ServerTest.RegisterInstanceAsync(client);
        string instanceId = instance.GetProperty("instanceId").GetString()!;
        for (int round = 0; round < 10; round++)
        {
            var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
            var sessions = new List<(long UserId, long SessionId)>();
            foreach (string token in new[] { hostToken, guestToken })
            {
                var detail = await ServerTest.Authorized(client, token).GetFromJsonAsync<JsonElement>($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}");
                var consumed = await ServerTest.ConsumeTicketAsync(client, instanceId,
                    detail.GetProperty("connection").GetProperty("joinTicket").GetString()!);
                sessions.Add((consumed.GetProperty("userId").GetInt64(), consumed.GetProperty("sessionId").GetInt64()));
            }
            var f = new Fixture(factory, client, instanceId, roomCode, hostToken, guestToken,
                start.GetProperty("matchId").GetString()!, sessions);
            using var heartbeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
                $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
                new { state = InstanceState.InMatch, roomCode, currentPlayers = 2 });
            Assert.Equal(HttpStatusCode.NoContent, heartbeat.StatusCode);
            foreach (string token in new[] { hostToken, guestToken })
                Assert.Equal(HttpStatusCode.NoContent,
                    (await ServerTest.Authorized(client, token).PostAsync("/api/rooms/leave", null)).StatusCode);
            // HTTP intent alone must not put the still-connected server back in the pool.
            Assert.Equal(InstanceState.InMatch, (await ReadStateAsync(f)).Instance.State);
            foreach (var session in sessions)
            {
                using var disconnected = await DisconnectAsync(f, session);
                Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
                var data = await disconnected.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(InstanceState.Ready, data.GetProperty("instanceState").GetString());
            }
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.False(await db.GameRooms.AnyAsync(x => x.RoomCode == roomCode));
                Assert.Equal(InstanceState.Draining, (await db.ServerInstances.SingleAsync(x => x.InstanceId == instanceId)).State);
            }
            Assert.Equal(HttpStatusCode.NoContent, (await RearmAsync(f)).StatusCode);
        }
    }

    [Fact]
    public async Task StrandedEmptyRoomRequiresZeroTransportOccupancyBeforeCleanup()
    {
        var f = await StartFixtureAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/heartbeat", ServerTest.ServerKey,
            new { state = InstanceState.InMatch, roomCode = f.RoomCode, currentPlayers = 2 })).StatusCode);
        foreach (var token in new[] { f.HostToken, f.GuestToken })
            Assert.Equal(HttpStatusCode.NoContent, (await ServerTest.Authorized(f.Client, token).PostAsync("/api/rooms/leave", null)).StatusCode);
        _ = await f.Client.GetAsync("/api/rooms");
        Assert.Equal(InstanceState.InMatch, (await ReadStateAsync(f)).Instance.State);
        Assert.Equal(HttpStatusCode.NoContent, (await ServerTest.SendWithKeyAsync(f.Client, HttpMethod.Post,
            $"/api/server-instances/{f.InstanceId}/heartbeat", ServerTest.ServerKey,
            new { state = InstanceState.InMatch, roomCode = f.RoomCode, currentPlayers = 0 })).StatusCode);
        _ = await f.Client.GetAsync("/api/rooms");
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GameRooms.AnyAsync(x => x.RoomCode == f.RoomCode));
        Assert.Equal(InstanceState.Draining, (await db.ServerInstances.SingleAsync(x => x.InstanceId == f.InstanceId)).State);
        Assert.Equal(HttpStatusCode.NoContent, (await RearmAsync(f)).StatusCode);
    }

    [Fact]
    public async Task LegacyEmptyWaitingRoomDisappearsAndItsLeaseRequiresRearm()
    {
        var f = await StartFixtureAsync();
        using (var scope = f.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var room = await db.GameRooms.Include(x => x.Members).Include(x => x.ServerInstance)
                .SingleAsync(x => x.RoomCode == f.RoomCode);
            db.GameRoomMembers.RemoveRange(room.Members);
            room.Members.Clear();
            room.Status = RoomStatus.Waiting;
            room.ServerInstance!.CurrentPlayers = 0;
            await db.SaveChangesAsync();
        }
        var listed = await f.Client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Empty(listed!);
        using (var scope = f.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.GameRooms.AnyAsync(x => x.RoomCode == f.RoomCode));
            Assert.Equal(InstanceState.Draining, (await db.ServerInstances.SingleAsync(x => x.InstanceId == f.InstanceId)).State);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await RearmAsync(f)).StatusCode);
    }

    private static async Task<int> CountMembersAsync(Fixture f)
    {
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roomId = await db.GameRooms.Where(x => x.RoomCode == f.RoomCode).Select(x => x.Id).SingleAsync();
        return await db.GameRoomMembers.CountAsync(x => x.RoomId == roomId);
    }

    private static async Task<int> CountLiveRosterAsync(Fixture f)
    {
        using var scope = f.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RoomMatchRosters.CountAsync(x => x.MatchId == f.MatchId && x.LeftAtUtc == null);
    }
}
