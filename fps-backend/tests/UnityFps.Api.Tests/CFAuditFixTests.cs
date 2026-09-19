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
/// 2026-09-09 CF 集中收口独立审计（R1/R2/R3）直接回归：
/// R1 正常结算返房【不删房间成员】（战斗退场与成员资格分离）；全员战斗退场/ack → 返房窗口结束、
///   Draining 实例权威回池（Ready/0 响应握手）→ 同房连开两局、成员/队长保留；
///   迟到旧会话上报不得触碰成员与下一局。
/// R2 KillRace 个人胜者保留（winnerTeam=null + 逐玩家 isWin → 发奖 IsWin 与结果卡）；
///   TDM 离队终局胜队随上报行一致。
/// R3 已持久结果的重放/补偿脱离活跃房间（房间删除/多局后仍可入口）；首次部分发奖失败
///   （rewardsApplied=false）→ 修复后重试补发，每人恰一条结算记录。
/// </summary>
public sealed class CFAuditFixTests
{
    private static async Task<JsonElement> ReportResultAsync(HttpClient client, string instanceId, object payload)
        => await (await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/match-result", ServerTest.ServerKey, payload))
            .Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>终局上报载荷（isWin 可选：R2 逐玩家胜负行）。</summary>
    private static object BuildReport(string matchId, string? winnerTeam,
        IReadOnlyList<(long UserId, string TeamId, bool IsWin)> roster)
        => new
        {
            matchId,
            durationSeconds = 60,
            winnerTeam,
            players = roster.Select(x => new
            {
                userId = x.UserId,
                teamId = x.TeamId,
                kills = 3,
                deaths = 1,
                assists = 0,
                participationSeconds = 60,
                rewardEligible = true,
                isWin = x.IsWin,
            }).ToArray(),
        };

    private static async Task<(AppDbContext Db, IServiceScope Scope)> OpenDbAsync(ServerApiFactory factory)
    {
        var scope = factory.Services.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope);
    }

    /// <summary>房间全员票据消费（拿 sessionId 供战斗退场上报）；票据从详情/开局响应签发。</summary>
    private static async Task<List<(long UserId, long SessionId)>> ConsumeTicketsAsync(
        HttpClient client, string instanceId, string roomCode, IReadOnlyList<string> tokens)
    {
        var sessions = new List<(long UserId, long SessionId)>();
        foreach (var token in tokens)
        {
            var detail = await ServerTest.Authorized(client, token).GetAsync($"/api/rooms/{roomCode}");
            detail.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var ticket = json.RootElement.GetProperty("connection").GetProperty("joinTicket").GetString()!;
            var consumed = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
            sessions.Add((consumed.GetProperty("userId").GetInt64(), consumed.GetProperty("sessionId").GetInt64()));
        }
        return sessions;
    }

    private static async Task<HttpResponseMessage> BattleExitAsync(
        HttpClient client, string instanceId, string roomCode, long userId, long sessionId)
        => await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/players/disconnect", ServerTest.ServerKey,
            new { roomCode, userId, sessionId });

    [Fact]
    public async Task R1_SettlementReturnKeepsMembers_ArchiveReleasesInstance_AndSameRoomRestarts()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "TDM");
        var matchId = start.GetProperty("matchId").GetString()!;

        var sessions = await ConsumeTicketsAsync(client, instanceId, roomCode, new[] { hostToken, guestToken });
        Assert.Equal(2, sessions.Count);

        // DS 权威终局上报（Red 胜）
        var (db0, scope0) = await OpenDbAsync(isolated);
        var roster = await db0.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope0.Dispose();
        var report = await ReportResultAsync(client, instanceId, BuildReport(matchId, "Red",
            roster.Select(x => (x.UserId, x.TeamId, x.TeamId == "Red")).ToList()));
        Assert.Equal("Final", report.GetProperty("status").GetString());

        // 两名玩家的战斗连接依次退场（正常结算返房 = 连接断开）
        var first = await BattleExitAsync(client, instanceId, roomCode, sessions[0].UserId, sessions[0].SessionId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var facts = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            // 第一名退场：窗口未结束 → 不释放（fail closed 保留绑定）
            Assert.True(facts.RootElement.GetProperty("instanceState").GetString() != InstanceState.Ready
                        || facts.RootElement.GetProperty("remainingPlayers").GetInt32() != 0,
                "未全员退场时不得返回 Ready/0");
        }

        // 第二名退场：全员战斗退场 → 返房窗口结束 → 权威释放 Ready/0（DS 清绑定/重臂信号）
        var last = await BattleExitAsync(client, instanceId, roomCode, sessions[1].UserId, sessions[1].SessionId);
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        using (var facts = JsonDocument.Parse(await last.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, facts.RootElement.GetProperty("remainingPlayers").GetInt32());
            Assert.Equal(InstanceState.Ready, facts.RootElement.GetProperty("instanceState").GetString());
        }

        // Ready/0 是 DS 重臂许可，不是数据库已回池；在真实重臂心跳前仍不可租。
        var (dbBeforeRearm, scopeBeforeRearm) = await OpenDbAsync(isolated);
        Assert.Equal(InstanceState.Draining,
            (await dbBeforeRearm.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == instanceId)).State);
        scopeBeforeRearm.Dispose();
        var rearm = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = InstanceState.Ready });
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);

        // 成员/队长保留、房间回 Waiting（可再开下一局）
        var (db, scope) = await OpenDbAsync(isolated);
        var room = await db.GameRooms.AsNoTracking().SingleAsync(x => x.RoomCode == roomCode);
        Assert.Equal(RoomStatus.Waiting, RoomStatus.Normalize(room.Status));
        Assert.Equal(2, await db.GameRoomMembers.CountAsync(x => x.RoomId == room.Id));
        Assert.True(await db.GameRoomMembers.AnyAsync(x => x.RoomId == room.Id && x.UserId == room.HostUserId),
            "房主成员行保留");
        Assert.Null(room.CurrentMatchId);
        Assert.Equal(matchId, room.LastMatchId);
        var instanceRow = await db.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == instanceId);
        Assert.Equal(InstanceState.Ready, instanceRow.State);
        Assert.Null(instanceRow.RoomCode);
        scope.Dispose();

        // 同房连开两局：第二局可租到同一实例
        await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var restart = await ServerTest.Authorized(client, hostToken).PostAsync($"/api/rooms/{roomCode}/start", null);
        Assert.Equal(HttpStatusCode.OK, restart.StatusCode);
        using var restartJson = JsonDocument.Parse(await restart.Content.ReadAsStringAsync());
        Assert.NotEqual(matchId, restartJson.RootElement.GetProperty("matchId").GetString());
        var (db2, scope2) = await OpenDbAsync(isolated);
        var room2 = await db2.GameRooms.AsNoTracking().SingleAsync(x => x.RoomCode == roomCode);
        Assert.Equal(instanceRow.Id, room2.ServerInstanceId);
        scope2.Dispose();
    }

    [Fact]
    public async Task R1_StaleBattleSessionReport_NeverTouchesMembersOrNextMatch()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "TDM");
        var matchId1 = start.GetProperty("matchId").GetString()!;

        var sessions = await ConsumeTicketsAsync(client, instanceId, roomCode, new[] { hostToken, guestToken });
        var (db0, scope0) = await OpenDbAsync(isolated);
        var hostUserId = await db0.GameRoomMembers
            .Where(m => m.Room.RoomCode == roomCode && m.Room.HostUserId == m.UserId)
            .Select(m => m.UserId).SingleAsync();
        scope0.Dispose();
        var hostSession = sessions.Single(s => s.UserId == hostUserId);

        // 第一局完整走完（终局上报 + 全员战斗退场 → 归档回 Waiting + 实例回池）
        var (db1, scope1) = await OpenDbAsync(isolated);
        var roster1 = await db1.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId1)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope1.Dispose();
        await ReportResultAsync(client, instanceId, BuildReport(matchId1, "Red",
            roster1.Select(x => (x.UserId, x.TeamId, x.TeamId == "Red")).ToList()));
        foreach (var (userId, sessionId) in sessions)
        {
            var exit = await BattleExitAsync(client, instanceId, roomCode, userId, sessionId);
            Assert.Equal(HttpStatusCode.OK, exit.StatusCode);
        }
        var rearm = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = InstanceState.Ready });
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);
        var (db2, scope2) = await OpenDbAsync(isolated);
        Assert.Equal(RoomStatus.Waiting, RoomStatus.Normalize(
            (await db2.GameRooms.AsNoTracking().SingleAsync(x => x.RoomCode == roomCode)).Status));
        scope2.Dispose();

        // 第二局开起来后，第一局的【旧会话】迟到掉线上报到达：
        // 票据 matchId ≠ 当前局 → 战斗退场 no-op——成员不得被移除、第二局名单不得被污染
        await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var start2 = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        var matchId2 = start2.GetProperty("matchId").GetString()!;
        Assert.NotEqual(matchId1, matchId2);

        var stale = await BattleExitAsync(client, instanceId, roomCode, hostUserId, hostSession.SessionId);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);

        var (db, scope) = await OpenDbAsync(isolated);
        var room = await db.GameRooms.AsNoTracking().SingleAsync(x => x.RoomCode == roomCode);
        Assert.True(await db.GameRoomMembers.AsNoTracking()
            .AnyAsync(x => x.RoomId == room.Id && x.UserId == hostUserId), "迟到旧会话上报不得删除成员");
        var staleRosterRow = await db.RoomMatchRosters.AsNoTracking()
            .SingleAsync(x => x.RoomId == room.Id && x.UserId == hostUserId && x.MatchId == matchId2);
        Assert.Null(staleRosterRow.LeftAtUtc);
        Assert.Null(staleRosterRow.ReturnedAtUtc);
        Assert.Equal(matchId2, room.CurrentMatchId);
        scope.Dispose();
    }

    [Fact]
    public async Task R2_KillRacePersonalWinner_IsPreserved_InRewardsAndView()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "KillRace");
        var matchId = start.GetProperty("matchId").GetString()!;

        var (db0, scope0) = await OpenDbAsync(isolated);
        var roster = await db0.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .OrderBy(x => x.UserId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope0.Dispose();
        Assert.Equal(2, roster.Count);

        // KillRace：winnerTeam=null，个人胜者 = roster[0]（isWin=true）
        var report = await ReportResultAsync(client, instanceId, BuildReport(matchId, null,
            new[] { (roster[0].UserId, roster[0].TeamId, true), (roster[1].UserId, roster[1].TeamId, false) }));
        Assert.Equal("Final", report.GetProperty("status").GetString());
        Assert.True(report.GetProperty("rewardsApplied").GetBoolean());

        var (db, scope) = await OpenDbAsync(isolated);
        var key = "ds-" + matchId + "-";
        var records = await db.Matches.AsNoTracking()
            .Where(x => x.ClientMatchId!.StartsWith(key))
            .ToDictionaryAsync(x => x.UserId, x => x.IsWin);
        Assert.True(records[roster[0].UserId], "KillRace 个人胜者必须按胜者奖励（R2）");
        Assert.False(records[roster[1].UserId]);
        scope.Dispose();

        // 结果视图逐玩家 isWin（结果卡数据源）
        var detail = await ServerTest.Authorized(client, hostToken)
            .GetAsync($"/api/rooms/{roomCode}/match-result?matchId={matchId}");
        detail.EnsureSuccessStatusCode();
        using var view = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var rows = view.RootElement.GetProperty("players");
        Assert.Equal(2, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var expected = row.GetProperty("userId").GetInt64() == roster[0].UserId;
            Assert.Equal(expected, row.GetProperty("isWin").GetBoolean());
        }
    }

    [Fact]
    public async Task R2_TdmLeaveEndWinnerTeam_RewardsFollowReportedRows()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "TDM");
        var matchId = start.GetProperty("matchId").GetString()!;

        var (db0, scope0) = await OpenDbAsync(isolated);
        var roster = await db0.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope0.Dispose();
        var blue = roster.Single(x => x.TeamId == "Blue");
        var red = roster.Where(x => x.TeamId == "Red").ToList();

        // TDM 离队终局（DS 侧计算胜队后上报）：Blue 让位、Red 获胜
        var report = await ReportResultAsync(client, instanceId, BuildReport(matchId, "Red",
            roster.Select(x => (x.UserId, x.TeamId, x.TeamId == "Red")).ToList()));
        Assert.Equal("Final", report.GetProperty("status").GetString());

        var (db, scope) = await OpenDbAsync(isolated);
        var key = "ds-" + matchId + "-";
        var records = await db.Matches.AsNoTracking()
            .Where(x => x.ClientMatchId!.StartsWith(key))
            .ToDictionaryAsync(x => x.UserId, x => x.IsWin);
        Assert.True(records[blue.UserId] == false, "离队方不得判胜");
        Assert.All(red, x => Assert.True(records[x.UserId], "存活方按胜队奖励"));
        scope.Dispose();
    }

    [Fact]
    public async Task R3_ReplayEntersCompensation_AfterRoomDeleted_WithSingleRecordPerPlayer()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "KillRace");
        var matchId = start.GetProperty("matchId").GetString()!;

        var (db0, scope0) = await OpenDbAsync(isolated);
        var roster = await db0.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope0.Dispose();
        var payload = BuildReport(matchId, null,
            roster.Select((x, i) => (x.UserId, x.TeamId, IsWin: i == 0)).ToList());

        var first = await ReportResultAsync(client, instanceId, payload);
        Assert.Equal("Final", first.GetProperty("status").GetString());

        // 房间删除（成员全部离开；终局后最后成员退房同时归档释放实例）
        await ServerTest.Authorized(client, hostToken).PostAsync("/api/rooms/leave", null);
        await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);
        var rearm = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = InstanceState.Ready });
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);
        var (db1, scope1) = await OpenDbAsync(isolated);
        Assert.False(await db1.GameRooms.AnyAsync(x => x.RoomCode == roomCode), "房间已删除");
        var instanceRow = await db1.ServerInstances.AsNoTracking().SingleAsync(x => x.InstanceId == instanceId);
        Assert.Equal(InstanceState.Ready, instanceRow.State);
        Assert.Null(instanceRow.RoomCode);
        scope1.Dispose();

        // 房间已删除：历史比赛重放仍可进入补偿（R3 脱离活跃房间）
        var replay = await ReportResultAsync(client, instanceId, payload);
        Assert.Equal("Final", replay.GetProperty("status").GetString());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.True(replay.GetProperty("rewardsApplied").GetBoolean(), "房间删除后的重放补偿必须成功");

        var (db, scope) = await OpenDbAsync(isolated);
        var key = "ds-" + matchId + "-";
        var records = await db.Matches.AsNoTracking()
            .Where(x => x.ClientMatchId!.StartsWith(key))
            .ToListAsync();
        Assert.Equal(roster.Count, records.Count);
        Assert.Equal(roster.Select(x => x.UserId).OrderBy(x => x), records.Select(x => x.UserId).OrderBy(x => x));
        scope.Dispose();
    }

    [Fact]
    public async Task R3_PartialRewardFailure_IsRetriedToCompletion_ExactlyOncePerPlayer()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "KillRace");
        var matchId = start.GetProperty("matchId").GetString()!;

        var (db0, scope0) = await OpenDbAsync(isolated);
        var roster = await db0.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope0.Dispose();

        // 首次上报前拆掉一名玩家的钱包 → 该玩家发奖失败 → rewardsApplied=false
        var (dbDel, scopeDel) = await OpenDbAsync(isolated);
        var victim = roster[0];
        var wallet = await dbDel.Wallets.SingleAsync(x => x.UserId == victim.UserId);
        dbDel.Wallets.Remove(wallet);
        await dbDel.SaveChangesAsync();
        scopeDel.Dispose();

        var first = await ReportResultAsync(client, instanceId, BuildReport(matchId, null,
            roster.Select((x, i) => (x.UserId, x.TeamId, IsWin: i == 0)).ToList()));
        Assert.Equal("Final", first.GetProperty("status").GetString());
        Assert.False(first.GetProperty("rewardsApplied").GetBoolean(), "部分发奖失败必须显式回报（R3）");

        // 修复环境（重建钱包行）→ 重试同内容上报 → 补发成功；每人恰一条结算记录
        var (dbFix, scopeFix) = await OpenDbAsync(isolated);
        dbFix.Wallets.Add(new PlayerWallet
        {
            UserId = victim.UserId, Coins = 0, UpdatedAtUtc = DateTime.UtcNow,
            User = await dbFix.Users.SingleAsync(x => x.Id == victim.UserId),
        });
        await dbFix.SaveChangesAsync();
        scopeFix.Dispose();

        var retry = await ReportResultAsync(client, instanceId, BuildReport(matchId, null,
            roster.Select((x, i) => (x.UserId, x.TeamId, IsWin: i == 0)).ToList()));
        Assert.True(retry.GetProperty("replayed").GetBoolean());
        Assert.True(retry.GetProperty("rewardsApplied").GetBoolean(), "重放补发必须完成");

        var (db, scope) = await OpenDbAsync(isolated);
        var key = "ds-" + matchId + "-";
        var records = await db.Matches.AsNoTracking()
            .Where(x => x.ClientMatchId!.StartsWith(key))
            .ToListAsync();
        Assert.True(records.Count == roster.Count, $"每人恰一条结算记录（补偿不重复发）：实际 {records.Count} 期望 {roster.Count}");
        scope.Dispose();
    }

    [Fact]
    public async Task R2_WinClaimInconsistency_IsRejected422_BeforeRegistration()
    {
        var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var register = await ServerTest.RegisterInstanceAsync(client);
        var instanceId = register.GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "KillRace");
        var matchId = start.GetProperty("matchId").GetString()!;

        var (db0, scope0) = await OpenDbAsync(isolated);
        var roster = await db0.RoomMatchRosters.AsNoTracking()
            .Where(x => x.MatchId == matchId)
            .Select(x => new { x.UserId, x.TeamId })
            .ToListAsync();
        scope0.Dispose();

        // 无胜队对局出现两名"个人胜者" → 422 拒绝（登记前零副作用）
        var bad = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/match-result", ServerTest.ServerKey, BuildReport(matchId, null,
                roster.Select(x => (x.UserId, x.TeamId, true)).ToList()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        using (var problem = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()))
            Assert.Equal(ApiErrorCodes.MatchPayloadRejected, problem.RootElement.GetProperty("code").GetString());

        var (db, scope) = await OpenDbAsync(isolated);
        Assert.False(await db.RoomMatchResults.AnyAsync(x => x.MatchId == matchId), "被拒绝的上报不得登记");
        scope.Dispose();
    }
}
