using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Data;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// G-RETURN 后端切片（Docs/28 Q05，Docs/27 §7）：
/// DS 权威终局上报（TDM）→ Returning + 释放实例 + 幂等奖励；KillRace 客户端提交 + 返房 ack；
/// 45s 超时回 Waiting 清准备；30 杀上限模式感知（带 matchId 放宽、伪造绑定 409）；结果查询 Pending/Final。
/// </summary>
public sealed class CFReturnLifecycleTests : IClassFixture<ServerApiFactory>
{
    private readonly ServerApiFactory factory;

    public CFReturnLifecycleTests(ServerApiFactory factory) => this.factory = factory;

    private HttpClient NewClient() => factory.CreateClient();

    private static async Task<HttpResponseMessage> HeartbeatInstanceAsync(HttpClient client, string instanceId,
        string? roomCode, string state, int players)
    {
        return await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = players, state });
    }

    private static async Task<HttpResponseMessage> ReportResultAsync(HttpClient client, string instanceId, object payload)
    {
        return await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/match-result", ServerTest.ServerKey, payload);
    }

    private static async Task RewindRoomStateClockAsync(ServerApiFactory f, string roomCode, TimeSpan back)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = await db.GameRooms.SingleAsync(x => x.RoomCode == roomCode);
        room.StateChangedAtUtc -= back;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TdmResultReport_TransitionsReturning_ReleasesInstance_AppliesRewards_Idempotently()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var beat = await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);

        // 终局前结果查询 → Pending
        var pending = await ServerTest.Authorized(client, hostToken).GetFromJsonAsync<JsonElement>(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/match-result?matchId={matchId}");
        Assert.Equal("Pending", pending.GetProperty("status").GetString());

        var hostId = UserIdFromToken(hostToken);
        var guestId = UserIdFromToken(guestToken);
        var report = await ReportResultAsync(client, instanceId, new
        {
            matchId,
            durationSeconds = 611,
            winnerTeam = "Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 42, deaths = 10, assists = 3, participationSeconds = 611, rewardEligible = true },
                new { userId = guestId, teamId = "Blue", kills = 38, deaths = 12, assists = 1, participationSeconds = 611, rewardEligible = true },
            },
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var reportBody = await report.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(reportBody.GetProperty("replayed").GetBoolean());
        Assert.True(reportBody.GetProperty("rewardsApplied").GetBoolean());

        // 房间 → Returning；A03（V0）：实例退役（Draining）不可租——DS 重臂 Ready+0 心跳后权威回池
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Equal("Returning", list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode))
            .GetProperty("status").GetString());
        var poolDuringReturn = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get,
            "/api/server-instances/pool?requestedCapacity=8", ServerTest.ServerKey, payload: null);
        using (var poolJson = JsonDocument.Parse(await poolDuringReturn.Content.ReadAsStringAsync()))
            Assert.Equal(0, poolJson.RootElement.GetProperty("summary").GetProperty("readyFresh").GetInt32());
        // F1：Returning 期间 Ready/0 自报也不能越过真实断线事实。
        var prematureRearm = await HeartbeatInstanceAsync(client, instanceId, roomCode, "Ready", 0);
        Assert.Equal(HttpStatusCode.Conflict, prematureRearm.StatusCode);
        foreach (var userId in new[] { hostId, guestId })
        {
            var exit = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
                $"/api/server-instances/{instanceId}/players/disconnect", ServerTest.ServerKey,
                new { roomCode, userId });
            Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        }
        var rearm = await HeartbeatInstanceAsync(client, instanceId, roomCode: null, "Ready", 0);
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);
        var pool = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get,
            "/api/server-instances/pool?requestedCapacity=8", ServerTest.ServerKey, payload: null);
        using var poolAfterRearm = JsonDocument.Parse(await pool.Content.ReadAsStringAsync());
        Assert.Equal(1, poolAfterRearm.RootElement.GetProperty("summary").GetProperty("readyFresh").GetInt32());

        // 结果查询 → Final（TDM 权威快照）
        var view = await ServerTest.Authorized(client, hostToken).GetFromJsonAsync<JsonElement>(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/match-result?matchId={matchId}");
        Assert.Equal("Final", view.GetProperty("status").GetString());
        Assert.Equal("Red", view.GetProperty("winnerTeam").GetString());
        Assert.Equal(2, view.GetProperty("players").GetArrayLength());

        // 奖励落地：胜方（红队 host）有 MatchRecord + 钱包流水； kills=42 > 旧 30 上限被接受
        using (var scope = isolated.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotNull(await db.Matches.SingleOrDefaultAsync(
                x => x.UserId == hostId && x.MatchId == matchId && x.IsWin && x.Kills == 42));
            Assert.True(await db.WalletLedger.AnyAsync(x => x.UserId == hostId && x.Reason == "MatchReward"));
        }

        // 重复上报：幂等（replayed=true），奖励不重复（无第二条 MatchRecord/流水）
        var replay = await ReportResultAsync(client, instanceId, new
        {
            matchId,
            durationSeconds = 611,
            winnerTeam = "Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 42, deaths = 10, assists = 3, participationSeconds = 611, rewardEligible = true },
                new { userId = guestId, teamId = "Blue", kills = 38, deaths = 12, assists = 1, participationSeconds = 611, rewardEligible = true },
            },
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        using (var scope = isolated.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Matches.CountAsync(x => x.UserId == hostId && x.MatchId == matchId));
        }
    }

    [Fact]
    public async Task KillRaceReturnAck_And45sTimeoutBackToWaiting()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "KillRace");
        var matchId = start.GetProperty("matchId").GetString()!;
        var hostId = UserIdFromToken(hostToken);
        var guestId = UserIdFromToken(guestToken);

        // 终局必须由服务器事实驱动（复审 R03：客户端 ack 不能创建终局）——DS 上报 winnerTeam=null（KillRace）
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);
        var report = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 580, winnerTeam = (string?)null,
            players = new object[]
            {
                new { userId = hostId, teamId = "None", kills = 45, deaths = 7, assists = 0, participationSeconds = 580, rewardEligible = false },
                new { userId = guestId, teamId = "None", kills = 12, deaths = 9, assists = 0, participationSeconds = 580, rewardEligible = false },
            },
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        // A04（V0）：KillRace 房间比赛同样归 DS 权威结算——玩家带 matchId 自报 409（仅可查询）
        var submit = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "kr-" + Guid.NewGuid().ToString("N")[..16],
            kills = 45, deaths = 7, durationSeconds = 580, isWin = true, matchId,
        });
        Assert.Equal(HttpStatusCode.Gone, submit.StatusCode);

        var ack = await ServerTest.Authorized(client, guestToken).PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/return", new { matchId });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var ackReplay = await ServerTest.Authorized(client, guestToken).PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/return", new { matchId });
        Assert.Equal(HttpStatusCode.OK, ackReplay.StatusCode); // 幂等
        _ = hostToken;

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Equal("Returning", list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode))
            .GetProperty("status").GetString());

        // 45s 超时 → Waiting，准备清空，比赛归档为 LastMatchId（结果仍可查）
        await RewindRoomStateClockAsync(isolated, roomCode, TimeSpan.FromSeconds(60));
        await client.GetFromJsonAsync<JsonElement[]>("/api/rooms"); // 触发懒维护
        var detail = await ServerTest.Authorized(client, guestToken).GetFromJsonAsync<JsonElement>($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}");
        Assert.Equal("Waiting", detail.GetProperty("room").GetProperty("status").GetString());
        Assert.All(detail.GetProperty("members").EnumerateArray(),
            m => Assert.False(m.GetProperty("isReady").GetBoolean()));

        var view = await ServerTest.Authorized(client, guestToken).GetFromJsonAsync<JsonElement>(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/match-result?matchId={matchId}");
        Assert.Equal("Final", view.GetProperty("status").GetString()); // 权威快照优先于 MatchRecord 聚合
        Assert.Equal(45, view.GetProperty("players")[0].GetProperty("kills").GetInt32());
    }

    [Fact]
    public async Task ReturnAndSubmissionRejectForeignMatchId()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var foreignMatchId = Guid.NewGuid().ToString("N");

        var badReturn = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/return", new { matchId = foreignMatchId });
        Assert.Equal(HttpStatusCode.Conflict, badReturn.StatusCode);
        using var problem = JsonDocument.Parse(await badReturn.Content.ReadAsStringAsync());
        Assert.Equal("ROOM_STATE_CONFLICT", problem.RootElement.GetProperty("code").GetString());

        // 无 matchId 旧路径维持 30 杀上限：31 杀 → 422（回归锚点；复审 R02：对局成员走不到这里，
        // 在局成员无标识上报已被 R02 守卫拒绝——此锚点用不在任何房间/对局中的玩家）
        var (outsiderToken, _) = await ServerTest.RegisterUserAsync(client);
        var legacyCapped = await ServerTest.Authorized(client, outsiderToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "legacy-" + Guid.NewGuid().ToString("N")[..16],
            kills = 31, deaths = 0, durationSeconds = 300, isWin = false,
        });
        Assert.Equal(HttpStatusCode.Gone, legacyCapped.StatusCode);

        // 伪造他人房间比赛：matchId 查无绑定 → 409
        var forged = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "forged-" + Guid.NewGuid().ToString("N")[..16],
            kills = 10, deaths = 0, durationSeconds = 100, isWin = true, matchId = foreignMatchId,
        });
        Assert.Equal(HttpStatusCode.Gone, forged.StatusCode);
        using var forgedProblem = JsonDocument.Parse(await forged.Content.ReadAsStringAsync());
        Assert.Equal("PLAYER_SETTLEMENT_RETIRED", forgedProblem.RootElement.GetProperty("code").GetString());
        _ = start;
    }

    [Fact]
    public async Task ResultQueryRejectsForeignRoomAndNonMember()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;

        // 非成员查结果 → 404（不泄露房间存在性）
        var (outsiderToken, _) = await ServerTest.RegisterUserAsync(client);
        var outsider = await ServerTest.Authorized(client, outsiderToken).GetAsync(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/match-result?matchId={matchId}");
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);

        // 成员查他房 matchId → 409
        var (otherToken, _) = await ServerTest.RegisterUserAsync(client);
        var otherRoom = await ServerTest.CreateRoomAsync(client, otherToken);
        var otherCode = ServerTest.HostRoomCode(otherRoom.GetProperty("room"));
        var cross = await ServerTest.Authorized(client, otherToken).GetAsync(
            $"/api/rooms/{ServerTest.PublicRoomId(otherCode)}/match-result?matchId={matchId}");
        Assert.Equal(HttpStatusCode.Conflict, cross.StatusCode);
    }

    private static long UserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var remainder = payload.Length % 4;
        var padded = remainder switch { 2 => payload + "==", 3 => payload + "=", _ => payload };
        using var json = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))));
        return long.Parse(json.RootElement.GetProperty("sub").GetString()!);
    }
}
