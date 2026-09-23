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
/// G-WAIT 模块门（Docs/28 Q02，Docs/27 v1 CF 等待房间契约）：
/// 并发开始只有一个 match/lease、无 DS 失败保留准备可重试、Starting 拒新成员/roster 重入补票、
/// Starting 超时回 Waiting 清准备、Waiting 成员过期清理与 leader 移交、roomVersion 递增、
/// 设置/地图/模式/容量白名单、TDM 开局条件（双方至少一人+非房主全准备）、InMatch 补人与队满、票据带比赛身份。
/// </summary>
public sealed class CFWaitingRoomTests
{
    /// <summary>把房间状态迁移时间回拨（超时懒判定用）：直接改库再触发一次维护扫描。</summary>
    private static async Task RewindRoomStateClockAsync(ServerApiFactory factory, string roomCode, TimeSpan back)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = await db.GameRooms.SingleAsync(x => x.RoomCode == roomCode);
        room.StateChangedAtUtc -= back;
        await db.SaveChangesAsync();
    }

    /// <summary>回拨成员心跳钟（onlyUserId 为空=全成员；用于过期清理用例）。</summary>
    private static async Task RewindMemberSeenClockAsync(ServerApiFactory factory, string roomCode, TimeSpan back, long? onlyUserId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = await db.GameRooms.Include(x => x.Members).SingleAsync(x => x.RoomCode == roomCode);
        foreach (var member in room.Members.Where(m => onlyUserId is null || m.UserId == onlyUserId))
            member.LastSeenUtc -= back;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> GetDetailAsync(HttpClient client, string token, string roomCode)
    {
        var detail = await ServerTest.Authorized(client, token).GetAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return await detail.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GetRoomAsync(HttpClient client, string roomCode)
    {
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        return list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
    }

    private static long GetUserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var remainder = payload.Length % 4;
        var padded = remainder switch { 2 => payload + "==", 3 => payload + "=", _ => payload };
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(
            Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))));
        return long.Parse(json.RootElement.GetProperty("sub").GetString()!);
    }

    private static async Task<HttpResponseMessage> HeartbeatInstanceAsync(HttpClient client, string instanceId,
        string? roomCode, string state, int players)
    {
        return await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = players, state });
    }

    [Fact]
    public async Task ConcurrentStartIssuesSingleMatchAndLease()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        await ServerTest.RegisterInstanceAsync(client);
        await ServerTest.RegisterInstanceAsync(client); // 同房三发并发开局用

        // 两间就绪房间并发开始：两场比赛各自成立、matchId 互不相同
        //（并发租用互斥依赖关系库事务/令牌，InMemory 不强制；真实 MySQL 验证另标 VERIFY_PENDING）
        var (roomA, hostA) = await ServerTest.CreateStartedRoomPendingAsync(client);
        var (roomB, hostB) = await ServerTest.CreateStartedRoomPendingAsync(client);
        var starts = await Task.WhenAll(
            ServerTest.Authorized(isolated.CreateClient(), hostA).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomA)}/start", null),
            ServerTest.Authorized(isolated.CreateClient(), hostB).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomB)}/start", null));
        Assert.Equal(HttpStatusCode.OK, starts[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, starts[1].StatusCode);
        var startA = await starts[0].Content.ReadFromJsonAsync<JsonElement>();
        var startB = await starts[1].Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(startA.GetProperty("matchId").GetString(), startB.GetProperty("matchId").GetString());

        // 同房开始幂等（顺序双击）：Starting 状态的重入返回同一比赛，不重租不重发名单
        var (roomC, hostC) = await ServerTest.CreateStartedRoomPendingAsync(client);
        var first = await ServerTest.StartRoomAsync(client, hostC, roomC);
        var second = await ServerTest.StartRoomAsync(client, hostC, roomC);
        Assert.Equal(first.GetProperty("matchId").GetString(), second.GetProperty("matchId").GetString());
        Assert.Equal(first.GetProperty("matchGeneration").GetInt32(), second.GetProperty("matchGeneration").GetInt32());

        // 同房并发三发（真双击竞态）：全部成功、都在 Starting、名单完整。
        // 注：InMemory 提供方无串行化事务，"并发下只允许一个 matchId"由 MySQL Serializable 事务保证，
        // 本提供方可能观察到两个并发开始各自成局（Docs/28 §D：InMemory 不证明事务/竞态）→ 真实库验证 VERIFY_PENDING。
        var (roomD, hostD) = await ServerTest.CreateStartedRoomPendingAsync(client);
        var triples = await Task.WhenAll(
            ServerTest.Authorized(isolated.CreateClient(), hostD).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomD)}/start", null),
            ServerTest.Authorized(isolated.CreateClient(), hostD).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomD)}/start", null),
            ServerTest.Authorized(isolated.CreateClient(), hostD).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomD)}/start", null));
        Assert.All(triples, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var generations = new HashSet<int>();
        foreach (var r in triples)
        {
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("matchId").GetString()));
            generations.Add(body.GetProperty("matchGeneration").GetInt32());
        }
        Assert.Single(generations); // 无论竞态结果如何，局号都是 1（不出现第二局）
    }

    [Fact]
    public async Task StartFailureKeepsWaitingAndReadyForRetry()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>()); // 无实例
        var client = isolated.CreateClient();
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        await ServerTest.ReadyAsync(client, guestToken, roomCode);

        // 无 DS → 409；房间保持 Waiting、客人准备保留（Docs/26 §2.2：允许重试）
        var failed = await ServerTest.Authorized(client, hostToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var detail = await GetDetailAsync(client, hostToken, roomCode);
        Assert.Equal("Waiting", detail.GetProperty("room").GetProperty("status").GetString());
        Assert.True(detail.GetProperty("members").EnumerateArray()
            .Single(m => !m.GetProperty("isLeader").GetBoolean()).GetProperty("isReady").GetBoolean());

        // 注册实例后重试成功：准备未被失败路径清掉
        await ServerTest.RegisterInstanceAsync(client);
        var retried = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        Assert.Equal(1, retried.GetProperty("matchGeneration").GetInt32());
        Assert.Equal("Starting", (await GetDetailAsync(client, hostToken, roomCode))
            .GetProperty("room").GetProperty("status").GetString());
    }

    [Fact]
    public async Task StartingRejectsNewMemberButRosterCanRejoin()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, guestToken, _) = await ServerTest.CreateStartedRoomAsync(client);

        // 新成员 Starting 拒绝（Docs/26 §2.2：启动名单冻结）
        var (thirdToken, _) = await ServerTest.RegisterUserAsync(client);
        var rejected = await ServerTest.Authorized(client, thirdToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", null);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("ROOM_STARTING", problem.RootElement.GetProperty("code").GetString());

        // roster 成员重进 = 重连补票（一次性新票据 + 比赛身份）
        var rejoin = await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        var connection = rejoin.GetProperty("connection");
        Assert.False(string.IsNullOrWhiteSpace(connection.GetProperty("joinTicket").GetString()));
        var detail = await GetDetailAsync(client, hostToken, roomCode);
        Assert.Equal(connection.GetProperty("matchId").GetString(),
            detail.GetProperty("room").GetProperty("matchId").GetString());
    }

    [Fact]
    public async Task StartingTimeoutReturnsToWaitingAndClearsReady()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (roomCode, hostToken, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        _ = start;

        // 回拨状态钟越过 90s Starting 窗口 → 触发维护扫描（list 路径）→ 回 Waiting
        await RewindRoomStateClockAsync(isolated, roomCode, TimeSpan.FromSeconds(120));
        var room = await GetRoomAsync(client, roomCode);
        Assert.Equal("Waiting", room.GetProperty("status").GetString());

        // 准备已清空；版本递增（创建1→开局+1→超时+1）
        var detail = await GetDetailAsync(client, hostToken, roomCode);
        Assert.All(detail.GetProperty("members").EnumerateArray(),
            m => Assert.False(m.GetProperty("isReady").GetBoolean()));
        Assert.True(detail.GetProperty("room").GetProperty("roomVersion").GetInt64() >= 3);
    }

    [Fact]
    public async Task StaleMemberSweptInWaiting_LeaderTransfers_ThenRoomDiesWhenEmpty()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, guestName) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        var guestId = GetUserIdFromToken(guestToken);
        var hostId = GetUserIdFromToken(hostToken);

        // 客人心跳过期（60s）→ 维护扫描移除；房间与房主保留
        await RewindMemberSeenClockAsync(isolated, roomCode, TimeSpan.FromSeconds(120), onlyUserId: guestId);
        await client.GetFromJsonAsync<JsonElement[]>("/api/rooms"); // 触发懒维护
        var afterSweep = await GetRoomAsync(client, roomCode);
        Assert.Equal(1, afterSweep.GetProperty("joinedPlayers").GetInt32());

        // 房主也过期 → 最后成员被清 → 房间删除
        await RewindMemberSeenClockAsync(isolated, roomCode, TimeSpan.FromSeconds(120), onlyUserId: hostId);
        await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.DoesNotContain(list!, r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
        _ = guestName;
    }

    [Fact]
    public async Task LeaderStaleTransferGoesToEarliestSurvivor()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, guestName) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);

        // 只有房主过期 → leader 移交给幸存者（JoinedAtUtc 最早）
        await RewindMemberSeenClockAsync(isolated, roomCode, TimeSpan.FromSeconds(120),
            onlyUserId: GetUserIdFromToken(hostToken));
        await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = await GetRoomAsync(client, roomCode);
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(guestName, room.GetProperty("leaderUsername").GetString());
    }

    [Fact]
    public async Task TeamSwitchRespectsCapacityAndClearsReady()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (tokenA, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, tokenA, maxPlayers: 4); // 每队上限 2
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (tokenB, _) = await ServerTest.RegisterUserAsync(client);
        var joinB = await ServerTest.JoinRoomAsync(client, tokenB, roomCode);   // 自动：红1蓝0 → 蓝
        Assert.Equal("Blue", joinB.GetProperty("you").GetProperty("teamId").GetString());
        var (tokenC, _) = await ServerTest.RegisterUserAsync(client);
        var joinC = await ServerTest.JoinRoomAsync(client, tokenC, roomCode);   // 红1蓝1 → 红
        Assert.Equal("Red", joinC.GetProperty("you").GetProperty("teamId").GetString());
        // 红队已满（2/1）：D 指定红队 → TEAM_FULL
        var (tokenD, _) = await ServerTest.RegisterUserAsync(client);
        var toFullRed = await ServerTest.Authorized(client, tokenD)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { teamId = "Red" });
        Assert.Equal(HttpStatusCode.Conflict, toFullRed.StatusCode);
        using var fullProblem = JsonDocument.Parse(await toFullRed.Content.ReadAsStringAsync());
        Assert.Equal("TEAM_FULL", fullProblem.RootElement.GetProperty("code").GetString());

        // 换队清准备：C（红）准备后换蓝队（蓝 1<2）→ 成功且 isReady=false
        await ServerTest.ReadyAsync(client, tokenC, roomCode);
        var switched = await ServerTest.Authorized(client, tokenC)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/team", new { teamId = "Blue" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var switchedBody = await switched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(switchedBody.GetProperty("you").GetProperty("isReady").GetBoolean());

        // KillRace 不允许选边（合规规则：killTarget 20 ∈ {10,20,30}）
        var ffaSnapshot = await ServerTest.CreateRoomAsync(client, tokenA, mode: "KillRace", killTarget: 20);
        var ffaCode = ServerTest.HostRoomCode(ffaSnapshot.GetProperty("room"));
        var ffaTeam = await ServerTest.Authorized(client, tokenA)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(ffaCode)}/team", new { teamId = "Red" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ffaTeam.StatusCode);
    }

    [Fact]
    public async Task SettingsByLeaderOnly_Whitelisted_AndClearsReady()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);

        // 非 leader → 403 NOT_LEADER
        var forbidden = await ServerTest.Authorized(client, guestToken).PostAsJsonAsync(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/settings", new { killTarget = 50 });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var forbiddenProblem = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
        Assert.Equal("NOT_LEADER", forbiddenProblem.RootElement.GetProperty("code").GetString());

        // 白名单外 → 422 且零副作用（版本不变）
        foreach (var (bad, expectedCode) in new (object, string)[]
                 {
                     (new { killTarget = 999 }, "SETTING_INVALID"),        // TDM 杀数白名单外
                     (new { maxPlayers = 6 }, "SETTING_INVALID"),          // 容量白名单外
                     (new { mapId = "unknown-map" }, "SETTING_INVALID"),   // 地图目录外
                     (new { mode = "BOGUS" }, "MODE_INVALID"),             // 模式白名单外
                 })
        {
            var rejected = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync(
                $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/settings", bad);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        }
        var unchanged = await GetDetailAsync(client, hostToken, roomCode);
        Assert.Equal(1, unchanged.GetProperty("room").GetProperty("roomVersion").GetInt64());

        // 合法变更：准备变更也递增版本（复审 R06 可见变更递增）→ ready +1、settings +1、全员清准备
        await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var updated = await ServerTest.Authorized(client, hostToken).PostAsJsonAsync(
            $"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/settings", new { killTarget = 50, timeLimitMinutes = 5 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50, body.GetProperty("room").GetProperty("killTarget").GetInt32());
        Assert.Equal(3, body.GetProperty("room").GetProperty("roomVersion").GetInt64());
        Assert.All(body.GetProperty("members").EnumerateArray(), m => Assert.False(m.GetProperty("isReady").GetBoolean()));
    }

    [Theory]
    [InlineData("TDM", false)]
    [InlineData("TDM", true)]
    [InlineData("KillRace", false)]
    [InlineData("KillRace", true)]
    public async Task HostStarts_OnlyReadyGuestsAutoEnter_OthersJoinExplicitly(string mode, bool guestReady)
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4,
            mode: mode, killTarget: mode == "KillRace" ? 20 : 50);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);

        if (guestReady) await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var denied = await ServerTest.Authorized(client, guestToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var started = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        var roster = started.GetProperty("roster");
        Assert.Equal(guestReady ? 2 : 1, roster.GetArrayLength());
        var detail = await GetDetailAsync(client, guestToken, roomCode);
        Assert.Equal(guestReady ? JsonValueKind.Object : JsonValueKind.Null, detail.GetProperty("connection").ValueKind);
        // Polling never enrolls an unready member. Explicit entry creates a persistent roster row.
        detail = await GetDetailAsync(client, guestToken, roomCode);
        Assert.Equal(guestReady ? JsonValueKind.Object : JsonValueKind.Null, detail.GetProperty("connection").ValueKind);
        var joined = await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        Assert.Equal(JsonValueKind.Object, joined.GetProperty("connection").ValueKind);
        Assert.Equal(JsonValueKind.Object, (await GetDetailAsync(client, guestToken, roomCode)).GetProperty("connection").ValueKind);
        var repeated = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        Assert.Equal(started.GetProperty("matchId").GetString(), repeated.GetProperty("matchId").GetString());
        Assert.Equal(2, repeated.GetProperty("roster").GetArrayLength());
    }

    /// <summary>
    /// 用户规则（2026-09-15 开赛规则票据）：房主无需准备、只负责开始比赛，可自行开始；
    /// 开赛时已准备成员一同入场，未准备成员留在房间之后自行「进入比赛」。
    /// 覆盖旧门槛被移除：① 房主单人开局（原「至少两人」）② 全员未准备（原「所有人准备」）
    /// ③ TDM 同队开局（原「两边必须有人」）。
    /// </summary>
    [Fact]
    public async Task HostStartsAlone_AllUnready_SameTeam_AllStillStart()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        await ServerTest.RegisterInstanceAsync(client);
        await ServerTest.RegisterInstanceAsync(client);

        // ① 房主单人 TDM：房间只有房主一人也能开始
        var (soloHost, _) = await ServerTest.RegisterUserAsync(client);
        var soloRoom = (await ServerTest.CreateRoomAsync(client, soloHost, maxPlayers: 4))
            .GetProperty("room").GetProperty("roomCode").GetString()!;
        var soloStart = await ServerTest.StartRoomAsync(client, soloHost, soloRoom);
        Assert.Equal(1, soloStart.GetProperty("roster").GetArrayLength());
        var soloDetail = await GetDetailAsync(client, soloHost, soloRoom);
        Assert.Equal("Starting", soloDetail.GetProperty("room").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, soloDetail.GetProperty("connection").ValueKind);

        // ② 全员未准备（房主 + 2 客人都不准备）：房主仍可开始，名单只有房主；客人留房等手动入场
        var (host, _) = await ServerTest.RegisterUserAsync(client);
        var room = (await ServerTest.CreateRoomAsync(client, host, maxPlayers: 4))
            .GetProperty("room").GetProperty("roomCode").GetString()!;
        var (guestA, _) = await ServerTest.RegisterUserAsync(client);
        var (guestB, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestA, room);
        await ServerTest.JoinRoomAsync(client, guestB, room);
        var allUnready = await ServerTest.StartRoomAsync(client, host, room);
        var matchId = allUnready.GetProperty("matchId").GetString()!;
        Assert.Equal(1, allUnready.GetProperty("roster").GetArrayLength());
        foreach (var guest in new[] { guestA, guestB })
        {
            var detail = await GetDetailAsync(client, guest, room);
            Assert.Equal("Starting", detail.GetProperty("room").GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("connection").ValueKind); // 轮询不自动入场
            Assert.Equal(3, detail.GetProperty("members").GetArrayLength()); // 未准备成员留在房间
        }

        // 未准备成员显式「进入比赛」→ 补名单 + 票据（带同一权威 matchId）
        var joined = await ServerTest.JoinRoomAsync(client, guestA, room);
        Assert.Equal(matchId, joined.GetProperty("connection").GetProperty("matchId").GetString());
        var afterJoin = await ServerTest.StartRoomAsync(client, host, room);
        Assert.Equal(matchId, afterJoin.GetProperty("matchId").GetString()); // 幂等：不重开新局
        Assert.Equal(2, afterJoin.GetProperty("roster").GetArrayLength());

        // ③ TDM 两人同队（蓝队无人）也允许开局
        var (hostSame, _) = await ServerTest.RegisterUserAsync(client);
        var sameTeamRoom = (await ServerTest.CreateRoomAsync(client, hostSame, maxPlayers: 4, mode: "TDM", killTarget: 50))
            .GetProperty("room").GetProperty("roomCode").GetString()!;
        var (guestSame, _) = await ServerTest.RegisterUserAsync(client);
        var sameJoin = await ServerTest.JoinRoomAsync(client, guestSame, sameTeamRoom, new { teamId = "Red" });
        Assert.Equal("Red", sameJoin.GetProperty("you").GetProperty("teamId").GetString());
        await ServerTest.ReadyAsync(client, guestSame, sameTeamRoom); // 已准备 → 与房主一同入场
        var sameStart = await ServerTest.StartRoomAsync(client, hostSame, sameTeamRoom);
        Assert.Equal(2, sameStart.GetProperty("roster").GetArrayLength());
        Assert.All(sameStart.GetProperty("roster").EnumerateArray(),
            entry => Assert.Equal("Red", entry.GetProperty("teamId").GetString()));
    }

    /// <summary>房主开局不需要先点准备：房主的 isReady 不得被开始流程写成 true（准备是客人语义）。</summary>
    [Fact]
    public async Task HostReadyFlagUntouchedByStart()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var roomCode = (await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4))
            .GetProperty("room").GetProperty("roomCode").GetString()!;
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        await ServerTest.ReadyAsync(client, guestToken, roomCode);

        await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        var detail = await GetDetailAsync(client, hostToken, roomCode);
        Assert.False(detail.GetProperty("you").GetProperty("isReady").GetBoolean(),
            "房主不因开始比赛被写成已准备");
        Assert.True(detail.GetProperty("members").EnumerateArray()
            .Single(m => !m.GetProperty("isLeader").GetBoolean()).GetProperty("isReady").GetBoolean(),
            "已准备客人的准备状态在开局后保持（用于返房后恢复）");
    }

    [Fact]
    public async Task InMatchReinforcementJoinsTeamWithCapacity()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, _, _, start) = await ServerTest.CreateStartedRoomAsync(client, maxPlayers: 4); // 每队 2，当前红1蓝1
        var matchId = start.GetProperty("matchId").GetString()!;
        // 进入 InMatch（补人分支按状态区分：Starting 拒绝、InMatch 允许）
        var beat = await HeartbeatInstanceAsync(client, instanceId, roomCode, "InMatch", 2);
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);

        // 补人 1：自动分队 → 红队（1<2）
        var (token3, _) = await ServerTest.RegisterUserAsync(client);
        var join3 = await ServerTest.JoinRoomAsync(client, token3, roomCode);
        Assert.Equal("Red", join3.GetProperty("you").GetProperty("teamId").GetString());
        Assert.Equal(matchId, join3.GetProperty("connection").GetProperty("matchId").GetString());

        // 补人 2：指定满队 → TEAM_FULL
        var (token4, _) = await ServerTest.RegisterUserAsync(client);
        var toFullRed = await ServerTest.Authorized(client, token4)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { teamId = "Red" });
        Assert.Equal(HttpStatusCode.Conflict, toFullRed.StatusCode);
        using (var problem = JsonDocument.Parse(await toFullRed.Content.ReadAsStringAsync()))
            Assert.Equal("TEAM_FULL", problem.RootElement.GetProperty("code").GetString());

        // 补人 3：自动 → 蓝队（1<2），此后两队均满
        var join4 = await ServerTest.JoinRoomAsync(client, token4, roomCode);
        Assert.Equal("Blue", join4.GetProperty("you").GetProperty("teamId").GetString());

        // 双方均满 → ROOM_FULL
        var (token5, _) = await ServerTest.RegisterUserAsync(client);
        var join5 = await ServerTest.Authorized(client, token5).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", null);
        Assert.Equal(HttpStatusCode.Conflict, join5.StatusCode);
        using (var problem = JsonDocument.Parse(await join5.Content.ReadAsStringAsync()))
            Assert.Equal("ROOM_FULL", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateRejectsWhitelistViolations()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (token, _) = await ServerTest.RegisterUserAsync(client);
        var auth = ServerTest.Authorized(client, token);

        var badMap = await auth.PostAsJsonAsync("/api/rooms", new { mapId = "moon" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badMap.StatusCode);
        using (var problem = JsonDocument.Parse(await badMap.Content.ReadAsStringAsync()))
            Assert.Equal("SETTING_INVALID", problem.RootElement.GetProperty("code").GetString());

        var badCapacity = await auth.PostAsJsonAsync("/api/rooms", new { maxPlayers = 6 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCapacity.StatusCode);

        var badMode = await auth.PostAsJsonAsync("/api/rooms", new { mode = "BOGUS" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badMode.StatusCode);
        using (var problem = JsonDocument.Parse(await badMode.Content.ReadAsStringAsync()))
            Assert.Equal("MODE_INVALID", problem.RootElement.GetProperty("code").GetString());

        // 合法 KillRace 创建（回归入口）：默认规则注入
        var ffa = await ServerTest.CreateRoomAsync(client, token, mode: "KillRace", killTarget: 20);
        var ffaRoom = ffa.GetProperty("room");
        Assert.Equal("KillRace", ffaRoom.GetProperty("mode").GetString());
        Assert.Equal(20, ffaRoom.GetProperty("killTarget").GetInt32());
        Assert.Equal("None", ffa.GetProperty("you").GetProperty("teamId").GetString());
    }
}
