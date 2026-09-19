using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// V0 集中收口（A01–A04）直接回归：
/// A01 合法无胜队（空/空白/null = KillRace、TDM 平局）进入可信终局，非空白非法值仍 422；
/// A02 奖励补偿与当前房间成员资格脱钩（离房后重放上报仍恰一次补发，幂等键 ds-{matchId}-{userId}）；
/// A03 终局实例退役（Draining 不可租）→ DS 重臂 Ready+0 心跳权威回池 → 同实例可复用；
/// A04 房间比赛（TDM 与 KillRace）发奖责任统一归 DS，玩家带 matchId 自报一律 409。
/// </summary>
public sealed class CFV0SettlementAndLeaseTests
{
    private static async Task<JsonElement> ReportResultAsync(HttpClient client, string instanceId, object payload)
        => await (await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/match-result", ServerTest.ServerKey, payload))
            .Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<HttpResponseMessage> TryReportResultAsync(HttpClient client, string instanceId, object payload)
        => await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/match-result", ServerTest.ServerKey, payload);

    private static async Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, string instanceId,
        string state, string? roomCode, int currentPlayers)
        => await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { state, roomCode, currentPlayers });

    private static object BuildReport(string matchId, string? winnerTeam,
        IReadOnlyList<(long UserId, string TeamId)> roster, string? winnerOverride = null)
        => new
        {
            matchId,
            durationSeconds = 60,
            winnerTeam = winnerOverride ?? winnerTeam,
            players = roster.Select(x => new
            {
                userId = x.UserId,
                teamId = x.TeamId,
                kills = 3,
                deaths = 1,
                assists = 0,
                participationSeconds = 60,
                rewardEligible = true,
            }).ToArray(),
        };

    private static async Task<(AppDbContext Db, IServiceScope Scope)> OpenDbAsync(ServerApiFactory factory)
    {
        var scope = factory.Services.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope);
    }

    /// <summary>一站式：注册单实例 + TDM/KillRace 房开局。返回上报与断言所需的全部上下文。</summary>
    private static async Task<(string InstanceId, string RoomCode, string HostToken, string MatchId,
        List<(long UserId, string TeamId)> Roster, ServerApiFactory Factory)> StartRoomWithInstanceAsync(string mode)
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, _, start) =
            await ServerTest.CreateStartedRoomAsync(client, mode: mode);
        var matchId = start.GetProperty("matchId").GetString()!;

        var (db, scope) = await OpenDbAsync(isolated);
        var rows = await db.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope.Dispose();
        var roster = rows.Select(x => (x.UserId, x.TeamId)).ToList();

        return (instanceId, roomCode, hostToken, matchId, roster, isolated);
    }

    [Fact]
    public async Task A01_EmptyWinnerTeamAcceptedAsDraw_AndInvalidTeamStill422()
    {
        var (instanceId, _, _, matchId, roster, isolated) = await StartRoomWithInstanceAsync("KillRace");
        var client = isolated.CreateClient();

        // 非空白非法值 → 422（登记前零副作用）
        var invalid = await TryReportResultAsync(client, instanceId, BuildReport(matchId, null, roster, "Purple"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        // 合法无胜队（空串 = 平局，旧 DS 兼容）→ 200 Final，落库归一为 null
        var report = await ReportResultAsync(client, instanceId, BuildReport(matchId, null, roster, string.Empty));
        Assert.Equal("Final", report.GetProperty("status").GetString());
        Assert.False(report.GetProperty("replayed").GetBoolean());

        var (db, scope) = await OpenDbAsync(isolated);
        var stored = await db.RoomMatchResults.AsNoTracking().SingleAsync(x => x.MatchId == matchId);
        Assert.Null(stored.WinnerTeam);
        scope.Dispose();

        // TDM 平局（null）同样成立：发奖 IsWin = false（无胜队）
        var (tdmInstance, _, _, tdmMatchId, tdmRoster, tdmFactory) = await StartRoomWithInstanceAsync("TDM");
        var tdmReport = await ReportResultAsync(tdmFactory.CreateClient(), tdmInstance,
            BuildReport(tdmMatchId, null, tdmRoster, null));
        Assert.Equal("Final", tdmReport.GetProperty("status").GetString());
        var (tdmDb, tdmScope) = await OpenDbAsync(tdmFactory);
        Assert.Null((await tdmDb.RoomMatchResults.AsNoTracking().SingleAsync(x => x.MatchId == tdmMatchId)).WinnerTeam);
        tdmScope.Dispose();
    }

    [Fact]
    public async Task A02_ReplayCompensationSucceedsAfterMemberLeft_WithExactlyOneRewardPerPlayer()
    {
        var (instanceId, roomCode, hostToken, matchId, roster, isolated) = await StartRoomWithInstanceAsync("KillRace");
        var client = isolated.CreateClient();

        var first = await ReportResultAsync(client, instanceId, BuildReport(matchId, null, roster));
        Assert.False(first.GetProperty("replayed").GetBoolean());
        Assert.True(first.GetProperty("rewardsApplied").GetBoolean(), $"首次上报奖励应成功（探针）");

        // 房主移除（DS 权威掉线上报，Returning 期可用；成员行删除）后重放上报：
        // 补偿不得再依赖当前成员资格（A02）。Returning 期成员自退被 409 拒绝（既有守卫），故走 server-key 链。
        var disconnect = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/players/disconnect", ServerTest.ServerKey,
            new { roomCode, userId = roster[0].UserId });
        Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);

        var replay = await ReportResultAsync(client, instanceId, BuildReport(matchId, null, roster));
        Assert.Equal("Final", replay.GetProperty("status").GetString());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.True(replay.GetProperty("rewardsApplied").GetBoolean(), "离房后重放补偿必须仍成功（A02）");

        // 恰一次：每个名单玩家恰好一条 ds-{matchId}-{userId} 结算记录
        var (db, scope) = await OpenDbAsync(isolated);
        var keyPrefix = "ds-" + matchId + "-";
        var records = await db.Matches.AsNoTracking()
            .Where(x => x.ClientMatchId!.StartsWith(keyPrefix))
            .ToListAsync();
        Assert.Equal(roster.Count, records.Count);
        Assert.Equal(roster.Select(x => x.UserId).OrderBy(x => x), records.Select(x => x.UserId).OrderBy(x => x));
        scope.Dispose();
    }

    [Fact]
    public async Task A03_InstanceDrainsAfterMatchEnd_AndReturnsToPoolOnlyAfterRearmedHeartbeat()
    {
        var (instanceId, roomCode, _, matchId, roster, isolated) = await StartRoomWithInstanceAsync("TDM");
        var client = isolated.CreateClient();
        var report = await ReportResultAsync(client, instanceId, BuildReport(matchId, "Red", roster));
        Assert.Equal("Final", report.GetProperty("status").GetString());

        // 真实连接退场事实（未带 sessionId 的旧 DS 端点仍按当前局身份处理）。
        // 仅终局/ack 不足以让 Ready/0 心跳回池。
        foreach (var player in roster)
        {
            var exit = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
                $"/api/server-instances/{instanceId}/players/disconnect", ServerTest.ServerKey,
                new { roomCode, userId = player.UserId });
            Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        }

        var (db, scope) = await OpenDbAsync(isolated);
        var instanceRow = await db.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == instanceId);
        Assert.Equal(InstanceState.Draining, instanceRow.State); // 退役中：保留绑定
        Assert.Equal(roomCode, instanceRow.RoomCode);
        var instanceEntityId = instanceRow.Id;
        scope.Dispose();

        // 退役中不可租：唯一实例处于 Draining → 新开局 409 NO_SERVER_AVAILABLE
        var (hostToken2, _) = await ServerTest.RegisterUserAsync(client);
        var roomCode2 = (await ServerTest.CreateRoomAsync(client, hostToken2))
            .GetProperty("room").GetProperty("roomCode").GetString()!;
        var (guestToken2, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken2, roomCode2);
        await ServerTest.ReadyAsync(client, guestToken2, roomCode2);
        var blocked = await ServerTest.Authorized(client, hostToken2).PostAsync($"/api/rooms/{roomCode2}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using (var problem = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()))
            Assert.Equal(ApiErrorCodes.NoServerAvailable, problem.RootElement.GetProperty("code").GetString());

        // 迟到的高人数 Ready 不回池（旧连接未清零：维持 Draining）
        var hold = await HeartbeatAsync(client, instanceId, InstanceState.Ready, roomCode, currentPlayers: 2);
        Assert.Equal(HttpStatusCode.NoContent, hold.StatusCode);
        var (dbHold, scopeHold) = await OpenDbAsync(isolated);
        Assert.Equal(InstanceState.Draining,
            (await dbHold.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == instanceId)).State);
        scopeHold.Dispose();

        // DS 重臂确认（Ready + 0 人 + roomCode 缺省）→ 权威回池
        var rearm = await HeartbeatAsync(client, instanceId, InstanceState.Ready, roomCode: null, currentPlayers: 0);
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);
        var (dbReady, scopeReady) = await OpenDbAsync(isolated);
        var released = await dbReady.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == instanceId);
        Assert.Equal(InstanceState.Ready, released.State);
        Assert.Null(released.RoomCode);
        scopeReady.Dispose();

        // 回池后同实例复用：新开局租到同一实例
        var restarted = await ServerTest.Authorized(client, hostToken2).PostAsync($"/api/rooms/{roomCode2}/start", null);
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        var (dbReused, scopeReused) = await OpenDbAsync(isolated);
        var room2 = await dbReused.GameRooms.AsNoTracking().SingleAsync(x => x.RoomCode == roomCode2);
        Assert.Equal(instanceEntityId, room2.ServerInstanceId);
        scopeReused.Dispose();
    }

    [Fact]
    public async Task A04_ClientSubmissionWithRoomMatchIdIsRejected409_ForBothModes()
    {
        foreach (var mode in new[] { "TDM", "KillRace" })
        {
            var (instanceId, _, hostToken, matchId, _, isolated) = await StartRoomWithInstanceAsync(mode);
            var client = isolated.CreateClient();
            var submission = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync("/api/matches", new
            {
                clientMatchId = "kr-" + matchId + "-self",
                kills = 5,
                deaths = 2,
                durationSeconds = 60,
                isWin = true,
                matchId,
                teamId = "Red",
            });
            Assert.Equal(HttpStatusCode.Conflict, submission.StatusCode);
            using var problem = JsonDocument.Parse(await submission.Content.ReadAsStringAsync());
            Assert.Equal(ApiErrorCodes.RoomStateConflict, problem.RootElement.GetProperty("code").GetString());
        }
    }
}
