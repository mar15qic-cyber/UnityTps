using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Data;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 2026-09-09 复审计划 R01–R04 关闭用例（先红后修）：
/// R01 详情票据落库+签发比赛身份绑定+补人 roster+轮询不积累；
/// R02 TDM 玩家不可自报（带/不带 matchId 的降级绕过都封死，KillRace 兼容路径保留）；
/// R03 return 不得提前终局/释放活跃 DS，幂等 ack 之前必须校验比赛身份，全员 ack 立即回 Waiting；
/// R04 终局重放来源一致性 + 内容冲突拒绝 + 奖励只消费持久结果 + roster 校验。
/// </summary>
public sealed class CFReviewFixTests
{
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

    private static async Task<JsonElement> GetDetailAsync(HttpClient client, string token, string roomCode)
    {
        var detail = await ServerTest.Authorized(client, token).GetAsync($"/api/rooms/{roomCode}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return await detail.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task RewindRoomStateClockAsync(ServerApiFactory f, string roomCode, TimeSpan back)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = await db.GameRooms.SingleAsync(x => x.RoomCode == roomCode);
        room.StateChangedAtUtc -= back;
        await db.SaveChangesAsync();
    }

    private static long UserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var remainder = payload.Length % 4;
        var padded = remainder switch { 2 => payload + "==", 3 => payload + "=", _ => payload };
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(
            Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))));
        return long.Parse(json.RootElement.GetProperty("sub").GetString()!);
    }

    // ===== R01 =====

    [Fact]
    public async Task R01_DetailTicket_IsPersisted_AndConsumableByNonHost()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, _) = await ServerTest.CreateStartedRoomAsync(client);

        // 非房主经详情轮询取票（Docs/27 §5.3：每次 GET 重取票）
        var detail = await GetDetailAsync(client, guestToken, roomCode);
        var ticket = detail.GetProperty("connection").GetProperty("joinTicket").GetString()!;

        // 票据必须真实落库：跨 HTTP 请求可被 DS 消费一次
        var consume = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(consume.GetProperty("valid").GetBoolean(),
            "详情签发的票据必须落库可消费，实际: " + consume.GetProperty("errorCode").GetString());
        Assert.Equal(roomCode, consume.GetProperty("roomCode").GetString());

        // 一次性：重复消费按重放拒绝
        var replay = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.False(replay.GetProperty("valid").GetBoolean());
        _ = hostToken;
    }

    [Fact]
    public async Task R01_DetailPolling_DoesNotAccumulateUnconsumedTickets()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, _, guestToken, _) = await ServerTest.CreateStartedRoomAsync(client);
        var guestId = UserIdFromToken(guestToken);

        for (var i = 0; i < 5; i++)
            await GetDetailAsync(client, guestToken, roomCode);

        using var scope = isolated.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unconsumed = await db.ServerJoinTickets
            .Where(x => x.RoomCode == roomCode && x.UserId == guestId && x.ConsumedAtUtc == null)
            .CountAsync();
        Assert.Equal(1, unconsumed); // 新签发顶替旧未消费票据，轮询不无界积累
    }

    [Fact]
    public async Task R01_TicketBoundToIssuedMatch_RejectedAcrossNextMatch()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, guestToken, firstStart) = await ServerTest.CreateStartedRoomAsync(client);
        var firstMatchId = firstStart.GetProperty("matchId").GetString()!;

        // 第一局签发的票据未消费
        var staleTicket = (await GetDetailAsync(client, guestToken, roomCode))
            .GetProperty("connection").GetProperty("joinTicket").GetString()!;

        // 第一局作废回 Waiting（Starting 超时，开局作废清空全员准备）→ 重新准备 → 开局（第二局）
        await RewindRoomStateClockAsync(isolated, roomCode, TimeSpan.FromSeconds(120));
        await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var secondStart = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        Assert.NotEqual(firstMatchId, secondStart.GetProperty("matchId").GetString());

        // 旧票不允许被解释为新局票：严格按签发比赛身份拒绝
        var consume = await ServerTest.ConsumeTicketAsync(client, GetFirstInstanceId(isolated), staleTicket);
        Assert.False(consume.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task R01_InMatchReinforcement_GetsRosterRow()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        var (token3, _) = await ServerTest.RegisterUserAsync(client);
        var join = await ServerTest.JoinRoomAsync(client, token3, roomCode);
        var guest3Id = UserIdFromToken(token3);
        Assert.NotNull(join.GetProperty("connection").GetProperty("joinTicket").GetString());

        using var scope = isolated.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rosterRow = await db.RoomMatchRosters.SingleOrDefaultAsync(
            x => x.MatchId == matchId && x.UserId == guest3Id);
        Assert.NotNull(rosterRow); // 补入者必须进入有效名单（终局资格/队伍判定锚点）
        Assert.Equal("Red", rosterRow!.TeamId); // 红队 1<4，自动分队应入红队
    }

    [Fact]
    public async Task R01_ReturningRejoin_GivesSnapshotWithoutBattleTicket()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var hostId = UserIdFromToken(hostToken);
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        var report = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[] { new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = false } },
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        // Returning：roster 成员重进只拿快照，不签战斗票（Waiting/Returning 不签战斗票）
        var rejoin = await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        Assert.Equal("Returning", rejoin.GetProperty("room").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rejoin.GetProperty("connection").ValueKind);
    }

    // ===== R02 =====

    [Fact]
    public async Task R02_TdmMember_CannotSelfReportWithMatchId()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;

        // TDM 成员带 matchId 自报 → 拒绝且不产生奖励
        var submit = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "tdm-self-" + Guid.NewGuid().ToString("N")[..12],
            kills = 99, deaths = 0, durationSeconds = 500, isWin = true, matchId,
        });
        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
        using var scope = isolated.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Matches.CountAsync(x => x.MatchId == matchId));
    }

    [Fact]
    public async Task R02_TdmMember_CannotBypassViaLegacyNoMatchIdSubmission()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        _ = matchId;

        // 对局进行中不带 matchId 走旧路径 → 拒绝（封死降级绕过）
        var legacy = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "tdm-legacy-" + Guid.NewGuid().ToString("N")[..12],
            kills = 25, deaths = 2, durationSeconds = 300, isWin = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
    }

    [Fact]
    public async Task R02_RoomMatchSubmissionRejected_LegacyPathIsolated()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client, mode: "KillRace", capacity: 8);
        var matchId = start.GetProperty("matchId").GetString()!;
        _ = roomCode;

        // A04（V0）：房间比赛（TDM 与 KillRace 一致）发奖责任统一归 DS——玩家带 matchId 自报 409，仅可查询
        var submit = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "kr-compat-" + Guid.NewGuid().ToString("N")[..12],
            kills = 45, deaths = 7, durationSeconds = 580, isWin = true, matchId,
        });
        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);

        // 旧非房间路径明确隔离：不在任何房间的新玩家无 matchId 自报仍合法（30 杀上限语义）
        var (freshToken, _) = await ServerTest.RegisterUserAsync(client);
        var legacy = await ServerTest.Authorized(client, freshToken).PostAsJsonAsync("/api/matches", new
        {
            clientMatchId = "legacy-" + Guid.NewGuid().ToString("N")[..12],
            kills = 10, deaths = 3, durationSeconds = 300, isWin = false,
        });
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }

    // ===== R03 =====

    [Fact]
    public async Task R03_InMatchMemberReturn_Rejected_InstanceNotReleased()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        // 比赛仍在进行：普通成员 return ack 不得创建终局/释放实例
        var ack = await ServerTest.Authorized(client, guestToken)
            .PostAsJsonAsync($"/api/rooms/{roomCode}/return", new { matchId });
        Assert.Equal(HttpStatusCode.Conflict, ack.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Equal("InMatch", list!.Single(r => r.GetProperty("roomCode").GetString() == roomCode)
            .GetProperty("status").GetString());
        var pool = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get,
            "/api/server-instances/pool?requestedCapacity=8", ServerTest.ServerKey, payload: null);
        using var poolJson = JsonDocument.Parse(await pool.Content.ReadAsStringAsync());
        Assert.Equal(0, poolJson.RootElement.GetProperty("summary").GetProperty("readyFresh").GetInt32());
    }

    [Fact]
    public async Task R03_ReturningAck_RequiresMatchIdentity_BeforeIdempotentReturn()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var hostId = UserIdFromToken(hostToken);
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        var report = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[] { new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = false } },
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        // Returning 状态下携带错误 matchId 的 ack 不得命中幂等快路径
        var badAck = await ServerTest.Authorized(client, guestToken)
            .PostAsJsonAsync($"/api/rooms/{roomCode}/return", new { matchId = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Conflict, badAck.StatusCode);
    }

    [Fact]
    public async Task R03_AllMembersAcked_RealDisconnectFacts_ThenRearm_CanRestart()
    {
        // F1/R1 契约（推翻 Docs/27 v1.2 §13.3 旧"全员 ack 立即回 Waiting"）：返房 ack 只写
        // ReturnedAtUtc，Waiting 由全员真实战斗断线（players/disconnect 写 LeftAtUtc）驱动；
        // 实例 Draining 等待 DS Ready+0 重臂心跳回池——本测试锁定
        // "终局→ack→真实断线→Waiting→重臂→再开局"全链。
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = await ServerTest.RegisterInstanceAsync(client).ContinueWith(t =>
            t.Result.GetProperty("instanceId").GetString()!);
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var firstMatchId = start.GetProperty("matchId").GetString()!;

        // 终局须由服务器事实驱动（DS 上报；TDM 队伍：host=Red、guest=Blue 自动分队）
        var hostId = UserIdFromToken(hostToken);
        var guestId = UserIdFromToken(guestToken);
        var report = await ReportResultAsync(client, instanceId, new
        {
            matchId = firstMatchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 45, deaths = 3, assists = 0, participationSeconds = 300, rewardEligible = false },
                new { userId = guestId, teamId = "Blue", kills = 12, deaths = 9, assists = 0, participationSeconds = 300, rewardEligible = false },
            },
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var listAfterReport = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Equal("Returning", listAfterReport!.Single(r => r.GetProperty("roomCode").GetString() == roomCode)
            .GetProperty("status").GetString());

        // 全员 ack → 200（ReturnedAtUtc 落库），但状态保持 Returning——ack 不得代替真实断线事实（F1）
        var ackHost = await ServerTest.Authorized(client, hostToken)
            .PostAsJsonAsync($"/api/rooms/{roomCode}/return", new { matchId = firstMatchId });
        Assert.Equal(HttpStatusCode.OK, ackHost.StatusCode);
        var ackGuest = await ServerTest.Authorized(client, guestToken)
            .PostAsJsonAsync($"/api/rooms/{roomCode}/return", new { matchId = firstMatchId });
        Assert.Equal(HttpStatusCode.OK, ackGuest.StatusCode);
        var detailAfterAck = await GetDetailAsync(client, hostToken, roomCode);
        Assert.Equal("Returning", detailAfterAck.GetProperty("room").GetProperty("status").GetString());

        // 全员真实战斗断线（DS 上报）：最后一人触发返房窗口收口 → Waiting + 实例 Draining
        var disconnectHost = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/players/disconnect", ServerTest.ServerKey,
            new { roomCode, userId = hostId });
        Assert.Equal(HttpStatusCode.OK, disconnectHost.StatusCode);
        var disconnectGuest = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/players/disconnect", ServerTest.ServerKey,
            new { roomCode, userId = guestId });
        Assert.Equal(HttpStatusCode.OK, disconnectGuest.StatusCode);

        var detail = await GetDetailAsync(client, hostToken, roomCode);
        Assert.Equal("Waiting", detail.GetProperty("room").GetProperty("status").GetString());
        Assert.All(detail.GetProperty("members").EnumerateArray(),
            m => Assert.False(m.GetProperty("isReady").GetBoolean()));

        // 迟到的第三次 ack（已回 Waiting）幂等安全
        var lateAck = await ServerTest.Authorized(client, guestToken)
            .PostAsJsonAsync($"/api/rooms/{roomCode}/return", new { matchId = firstMatchId });
        Assert.Equal(HttpStatusCode.OK, lateAck.StatusCode);

        // 多局复用：A03/R1 实例退役后须 DS 重臂心跳（Ready+0）权威回池，再开第二局
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "Ready", 0);
        await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var second = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        Assert.Equal(2, second.GetProperty("matchGeneration").GetInt32());
    }

    private static string GetFirstInstanceId(ServerApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.ServerInstances.OrderBy(x => x.Id).First().InstanceId;
    }

    // ===== R04 =====

    [Fact]
    public async Task R04_ResultReplay_FromDifferentInstance_Rejected()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var hostId = UserIdFromToken(hostToken);
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        var payload = new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[] { new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = true } },
        };
        var first = await ReportResultAsync(client, instanceId, payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 另一注册实例重放同一比赛 → 拒绝（server-key 不等于任意实例可改任何比赛）
        var otherInstanceId = (await ServerTest.RegisterInstanceAsync(client, address: "10.1.0.9")).GetProperty("instanceId").GetString()!;
        var replay = await ReportResultAsync(client, otherInstanceId, payload);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task R04_ResultReplay_WithTamperedRewards_GrantsNothingBeyondStoredResult()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var hostId = UserIdFromToken(hostToken);
        var guestId = UserIdFromToken(guestToken);
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        // 首次登记：guest 不合格（rewardEligible=false）
        var first = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = true },
                new { userId = guestId, teamId = "Blue", kills = 5, deaths = 9, assists = 0, participationSeconds = 300, rewardEligible = false },
            },
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 篡改重放：把 guest 改成 eligible → 不得依据请求补发奖励（奖励只消费持久结果）
        var tampered = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = true },
                new { userId = guestId, teamId = "Blue", kills = 5, deaths = 9, assists = 0, participationSeconds = 300, rewardEligible = true },
            },
        });
        using var scope = isolated.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Matches.CountAsync(x => x.MatchId == matchId && x.UserId == guestId));
        Assert.Equal(1, await db.Matches.CountAsync(x => x.MatchId == matchId && x.UserId == hostId)); // 主奖励恰一次
        _ = tampered;
    }

    [Fact]
    public async Task R04_ResultReport_RejectsNonRoster_Duplicate_AndWrongTeam()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var hostId = UserIdFromToken(hostToken);
        var guestId = UserIdFromToken(guestToken);
        await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);

        // 非 roster 用户
        var (outsiderToken, _) = await ServerTest.RegisterUserAsync(client);
        var outsiderId = UserIdFromToken(outsiderToken);
        var withOutsider = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = true },
                new { userId = outsiderId, teamId = "Blue", kills = 1, deaths = 9, assists = 0, participationSeconds = 300, rewardEligible = true },
            },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withOutsider.StatusCode);

        // 重复用户
        var duplicated = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Red",
            players = new object[]
            {
                new { userId = hostId, teamId = "Red", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = true },
                new { userId = hostId, teamId = "Blue", kills = 2, deaths = 9, assists = 0, participationSeconds = 300, rewardEligible = true },
            },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicated.StatusCode);

        // 队伍与名单不符（host 在红队，上报蓝队）
        var wrongTeam = await ReportResultAsync(client, instanceId, new
        {
            matchId, durationSeconds = 300, winnerTeam = (string?)"Blue",
            players = new object[]
            {
                new { userId = hostId, teamId = "Blue", kills = 10, deaths = 1, assists = 0, participationSeconds = 300, rewardEligible = true },
            },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongTeam.StatusCode);

        // 三个非法上报都不产生结果/奖励
        using var scope = isolated.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await db.RoomMatchResults.SingleOrDefaultAsync(x => x.MatchId == matchId));
        Assert.Equal(0, await db.Matches.CountAsync(x => x.MatchId == matchId));
        _ = guestToken;
    }
}
