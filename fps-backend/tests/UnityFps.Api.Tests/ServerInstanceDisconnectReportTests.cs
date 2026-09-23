using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// Day2 掉线成员清理（server-key 权威端点 POST /api/server-instances/{id}/players/disconnect）
/// + P0 实例租约闭环契约（2026-09-08）：成功一律 200 + 权威事实 { roomCode, remainingPlayers, instanceState }——
/// DS 只认 instanceState=Ready 且 remainingPlayers=0 为"实例已释放"信号：
/// ① 鉴权与 register/heartbeat/consume 完全一致（缺 key 401 / 错 key 403，玩家 JWT 不被接受）；
/// ② 绑定校验：未知实例 404 / 未绑定或绑定其他房间 409（404/409 无契约 body，DS 不得猜 Ready）；
/// ③ 幂等：成员已不存在 → 200 + 当前权威事实，状态零变化；
/// ④ 普通成员掉线仅减员（200 + Reserved/N 保留绑定）；leader 掉线按 JoinedAtUtc 最早 → UserId 最小转移；
/// ⑤ 最后成员掉线即使 InMatch 也删房+释放实例（200 + Ready/0——DS 唯一允许清绑定的信号）；
/// ⑥ 并发重连保护：通知事务开始后才创建的成员关系不被旧连接迟到通知误删（服务级锁定）；
/// ⑦ 释放后同一实例可被新房连续开局租用（Room A → 退出 → Room B，无需重启进程）；
/// ⑧ 实例池开发诊断端点（GET /api/server-instances/pool，RequireServerKey 保护）。
/// Docs/27 v1 适配：实例绑定从 create 移到 start，需要 Reserved/InMatch 前提的用例走开局链路。
/// </summary>
public sealed class ServerInstanceDisconnectReportTests : IClassFixture<ServerApiFactory>
{
    private readonly ServerApiFactory factory;

    public ServerInstanceDisconnectReportTests(ServerApiFactory factory) => this.factory = factory;

    private HttpClient NewClient() => factory.CreateClient();

    private static async Task<HttpResponseMessage> ReportDisconnectAsync(HttpClient client, string instanceId,
        object payload, string? key = ServerTest.ServerKey)
    {
        if (key is null)
            return await client.PostAsJsonAsync($"/api/server-instances/{instanceId}/players/disconnect", payload);
        return await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/players/disconnect", key, payload);
    }

    /// <summary>解析掉线上报 200 响应并断言权威事实三字段（P0 契约：DS 只认 Ready+0 为释放信号）。</summary>
    private static async Task AssertDisconnectFactsAsync(HttpResponseMessage response,
        string roomCode, int remainingPlayers, string instanceState)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(roomCode, root.GetProperty("roomCode").GetString());
        Assert.Equal(remainingPlayers, root.GetProperty("remainingPlayers").GetInt32());
        Assert.Equal(instanceState, root.GetProperty("instanceState").GetString());
    }

    [Fact]
    public async Task DisconnectReportWithoutServerKeyIsUnauthorized()
    {
        var client = NewClient();
        var response = await ReportDisconnectAsync(client, ServerTest.NewInstanceId(),
            new { roomCode = "ROOMAA", userId = "1" }, key: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DisconnectReportWithWrongServerKeyIsForbidden()
    {
        var client = NewClient();
        var response = await ReportDisconnectAsync(client, ServerTest.NewInstanceId(),
            new { roomCode = "ROOMAA", userId = "1" }, key: "totally-wrong-key");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DisconnectUnknownInstanceIs404()
    {
        var client = NewClient();
        var response = await ReportDisconnectAsync(client, ServerTest.NewInstanceId("nope"),
            new { roomCode = "ROOMAA", userId = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SERVER_INSTANCE_NOT_FOUND", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DisconnectWithRoomMismatchIs409_AndKeepsMembership()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (hostToken, hostName) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        _ = instanceId;

        // 绑定 roomCode != 上报 roomCode → 409，成员不受影响（Waiting 房未绑定实例同样拒绝认领）
        var mismatch = await ReportDisconnectAsync(client, instanceId,
            new { roomCode = "ZZZZ99", userId = 1 });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        using var problem = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
        Assert.Equal("SERVER_INSTANCE_STATE_CONFLICT", problem.RootElement.GetProperty("code").GetString());

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(hostName, room.GetProperty("leaderUsername").GetString());
    }

    [Fact]
    public async Task DisconnectOnUnboundInstanceIs409()
    {
        var client = NewClient();
        var unbound = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var response = await ReportDisconnectAsync(client, unbound, new { roomCode = "ROOMAA", userId = 1 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DisconnectOrdinaryMemberIsIdempotent_AndKeepsRoomAndLeader()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var hostName = (await client.GetFromJsonAsync<JsonElement[]>("/api/rooms"))!
            .Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode)).GetProperty("leaderUsername").GetString();
        var guestId = GetUserIdFromToken(guestToken);

        // 普通成员掉线：200 + 权威事实（Reserved/1——绑定保留），仅该成员移除，房间/leader/实例绑定保持
        var first = await ReportDisconnectAsync(client, instanceId, new { roomCode, userId = guestId.ToString() });
        await AssertDisconnectFactsAsync(first, roomCode, remainingPlayers: 1, instanceState: "Reserved");

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(hostName, room.GetProperty("leaderUsername").GetString());

        // 重复通知：成员已不存在 → 200 幂等 + 当前权威事实（状态零变化）
        var repeat = await ReportDisconnectAsync(client, instanceId, new { roomCode, userId = guestId.ToString() });
        await AssertDisconnectFactsAsync(repeat, roomCode, remainingPlayers: 1, instanceState: "Reserved");
        var listAfterRepeat = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Equal(1, listAfterRepeat!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode))
            .GetProperty("joinedPlayers").GetInt32());

        // 掉线成员已不是房间成员：其票据不可再消费；留房者的票据不受影响（对照）
        var hostTicket = ServerTest.ConnectionTicket(start);
        var hostConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, hostTicket);
        Assert.True(hostConsume.GetProperty("valid").GetBoolean());
        _ = hostToken;
    }

    [Fact]
    public async Task DisconnectLeaderTransfersToEarliestRemaining()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        // 两名客人先于开局加入（Starting 拒绝新成员，成员必须在 Waiting 期进齐）
        var (bToken, bName) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, bToken, roomCode);
        var (cToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, cToken, roomCode);
        await ServerTest.ReadyAsync(client, bToken, roomCode);
        await ServerTest.ReadyAsync(client, cToken, roomCode);
        await ServerTest.StartRoomAsync(client, hostToken, roomCode);

        // leader（创建者）掉线 → 转移给最早加入的剩余成员 B；200 + 权威事实（Reserved/2 绑定保留）
        var report = await ReportDisconnectAsync(client, instanceId, new { roomCode, userId = 1 });
        await AssertDisconnectFactsAsync(report, roomCode, remainingPlayers: 2, instanceState: "Reserved");

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
        Assert.Equal(2, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(bName, room.GetProperty("leaderUsername").GetString());

        // 实例绑定保持（心跳 Reserved + 原房仍为合法转换）
        var beat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 2, state = "Reserved" });
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);
    }

    [Fact]
    public async Task DisconnectLastMemberReleasesInstance_EvenWhenRoomInMatch()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, _) = await ServerTest.CreateStartedRoomAsync(client);

        // 客人先退出（Starting 期 leave），房主独行时房间进入 InMatch：
        // 服务器上报的最后成员掉线仍须删房+释放（与玩家主动 Leave 的 InMatch 保留语义不同）
        await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);
        var inMatchBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 1, state = "InMatch" });
        Assert.Equal(HttpStatusCode.NoContent, inMatchBeat.StatusCode);

        var report = await ReportDisconnectAsync(client, instanceId, new { roomCode, userId = GetUserIdFromToken(hostToken) });
        // P0/R1 契约核心：最后成员掉线 → 200 + { roomCode, remainingPlayers=0, instanceState=Ready }
        // ——Ready/0 是 DS 清空本地绑定转 Ready 的重臂许可信号（DB 侧状态见下方三段式断言）
        await AssertDisconnectFactsAsync(report, roomCode, remainingPlayers: 0, instanceState: "Ready");

        // 房间已删除
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.DoesNotContain(list!, r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));

        // R1/A03 三段式第二段：释放许可 ≠ 数据库已回池——实例保持 Draining（旧绑定保留），
        // 心跳过期前绝不可被新房间租用（防止 DS 旧连接未清时被复用）。
        var poolDuringDraining = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get,
            "/api/server-instances/pool?requestedCapacity=4", ServerTest.ServerKey, payload: null);
        using (var poolJson = JsonDocument.Parse(await poolDuringDraining.Content.ReadAsStringAsync()))
        {
            var summary = poolJson.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("total").GetInt32());
            Assert.Equal(0, summary.GetProperty("readyFresh").GetInt32()); // 重臂前实例必须保持 Draining，不得计入可租
        }

        // R1/A03 三段式第三段：DS 重臂心跳（Ready+0，房间已删）→ 权威清绑定翻 Ready 回池
        var rearmBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 0, state = "Ready" });
        Assert.Equal(HttpStatusCode.NoContent, rearmBeat.StatusCode);

        // 实例池诊断可见重臂事实（readyFresh=1；同地址实例行 state=Ready/未绑定）
        var pool = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get,
            "/api/server-instances/pool?requestedCapacity=4", ServerTest.ServerKey, payload: null);
        Assert.Equal(HttpStatusCode.OK, pool.StatusCode);
        using var poolRearmed = JsonDocument.Parse(await pool.Content.ReadAsStringAsync());
        Assert.Equal(1, poolRearmed.RootElement.GetProperty("summary").GetProperty("readyFresh").GetInt32());

        // 实例可被新房复用（同一地址开局租用）
        var (_, _, _, reused) = await ServerTest.CreateStartedRoomAsync(client);
        Assert.Equal("10.1.0.5", ServerTest.ConnectionAddress(reused));

        // 释放后的重复通知：实例已解绑 → 409（DS 应视为该通知已终态，不再重试）
        var staleRepeat = await ReportDisconnectAsync(client, instanceId, new { roomCode, userId = 1 });
        Assert.Equal(HttpStatusCode.Conflict, staleRepeat.StatusCode);
    }

    [Fact]
    public async Task DisconnectUserIdAcceptsNumericAndStringForms()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, _) = await ServerTest.CreateStartedRoomAsync(client);
        var guestId = GetUserIdFromToken(guestToken);

        // 字符串形态（DS 身份档案以字符串承载后端数字 id）
        var asString = await ReportDisconnectAsync(client, instanceId, new { roomCode, userId = guestId.ToString() });
        await AssertDisconnectFactsAsync(asString, roomCode, remainingPlayers: 1, instanceState: "Reserved");

        // 数字形态（host 移除 → 最后成员 → 删房+释放：200 + Ready/0）
        var asNumber = await ReportDisconnectAsync(client, instanceId,
            new { roomCode, userId = GetUserIdFromToken(hostToken) });
        await AssertDisconnectFactsAsync(asNumber, roomCode, remainingPlayers: 0, instanceState: "Ready");
    }

    [Fact]
    public async Task PoolDiagnosticsEndpoint_RequiresServerKey_AndReportsBreakdown()
    {
        var client = NewClient();

        // 无 key → 401（控制面鉴权纪律：玩家 JWT 也不被接受）
        var unauthorized = await client.GetAsync("/api/server-instances/pool");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var pool = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get,
            "/api/server-instances/pool?requestedCapacity=16", ServerTest.ServerKey, payload: null);
        Assert.Equal(HttpStatusCode.OK, pool.StatusCode);
        using var json = JsonDocument.Parse(await pool.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.True(root.GetProperty("summary").GetProperty("total").GetInt32() >= 0, "汇总字段必须存在");
        Assert.True(root.GetProperty("instances").GetArrayLength() >= 0, "逐实例行必须存在");

        // 响应不得泄露密钥（受保护端点，仅状态/绑定/人数/容量/心跳年龄）
        var raw = await pool.Content.ReadAsStringAsync();
        Assert.False(raw.Contains(ServerTest.ServerKey, StringComparison.Ordinal), "池诊断响应不得包含服务器密钥");
    }

    /// <summary>
    /// 连续租用闭环（审计 §4 第一段 5，R1/A03 三段式更新）：Room A 最后成员退出 →
    /// 释放许可（Ready/0）→ DS 重臂心跳权威回池 → 同一实例立即被 Room B 开局租用
    /// ——DS 无需重启进程即可再次被新房租用（真实部署中重臂心跳由 DS 自动发出，
    /// 测试以显式心跳模拟）。
    /// </summary>
    [Fact]
    public async Task ReleasedInstance_IsImmediatelyReleasableByNextRoom()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomACode, hostAToken, guestAToken, _) = await ServerTest.CreateStartedRoomAsync(client);

        // Room A 客人退出后，最后成员（创建者）掉线 → 释放许可（200 + Ready/0）
        await ServerTest.Authorized(client, guestAToken).PostAsync("/api/rooms/leave", null);
        var report = await ReportDisconnectAsync(client, instanceId,
            new { roomCode = roomACode, userId = GetUserIdFromToken(hostAToken) });
        await AssertDisconnectFactsAsync(report, roomACode, remainingPlayers: 0, instanceState: "Ready");

        // DS 重臂心跳（房间已删 → roomArchiveReady）：权威清绑定回池
        var rearm = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = roomACode, currentPlayers = 0, state = "Ready" });
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);

        // Room B 立即开局成功：同一实例被再次租用（同地址）
        var (roomBCode, hostBToken, guestBToken, roomB) = await ServerTest.CreateStartedRoomAsync(client);
        Assert.Equal("10.1.0.5", ServerTest.ConnectionAddress(roomB));
        Assert.NotEqual(roomACode, roomBCode);

        // Room B 的最后成员掉线同样收敛（连续两轮闭环）
        await ServerTest.Authorized(client, guestBToken).PostAsync("/api/rooms/leave", null);
        var release = await ReportDisconnectAsync(client, instanceId,
            new { roomCode = roomBCode, userId = GetUserIdFromToken(hostBToken) });
        await AssertDisconnectFactsAsync(release, roomBCode, remainingPlayers: 0, instanceState: "Ready");
    }

    private static long GetUserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var remainder = payload.Length % 4;
        var padded = remainder switch
        {
            2 => payload + "==",
            3 => payload + "=",
            _ => payload,
        };
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))));
        // JwtRegisteredClaimNames.Sub 经 ClaimsIdentity 序列化为 JSON 字符串
        return long.Parse(json.RootElement.GetProperty("sub").GetString()!);
    }
}

/// <summary>
/// 并发重连保护（服务级锁定）：通知事务开始后才创建的成员关系（JoinedAtUtc &gt; noticeStartedAtUtc）
/// 不得被旧连接的迟到通知误删；通知开始前已存在的成员正常移除。InMemory 提供方 + 直调服务。
/// </summary>
public sealed class DisconnectReportReconnectGuardTests
{
    [Fact]
    public async Task MembershipCreatedAfterNoticeStart_SurvivesLateNotice()
    {
        await using var db = CreateDb();
        var baseTime = DateTime.UtcNow.AddMinutes(-1);

        var instance = new ServerInstance
        {
            InstanceId = "arena-guard", State = "Reserved", RoomCode = "ROOMAA",
            CurrentPlayers = 2, RegisteredAtUtc = baseTime, LastHeartbeatUtc = baseTime,
        };
        var room = new GameRoom
        {
            RoomCode = "ROOMAA", HostUserId = 1, HostUsername = "a", MaxPlayers = 4, JoinedPlayers = 2,
            Status = RoomStatus.InMatch, CreatedAtUtc = baseTime, ServerInstance = instance,
            Members =
            [
                new GameRoomMember { UserId = 1, JoinedAtUtc = baseTime.AddSeconds(1) },
                new GameRoomMember { UserId = 2, JoinedAtUtc = baseTime.AddSeconds(5) }, // 通知开始后重连创建
            ],
        };
        db.AddRange(instance, room);
        db.SaveChanges();

        var service = CreateService(db);

        // 通知开始于成员创建之前 → 保护：迟到通知不得删除重连后的成员
        await service.ReportPlayerDisconnectAsync("arena-guard",
            new ServerPlayerDisconnectReportRequest { RoomCode = "ROOMAA", UserId = 2 },
            noticeStartedAtUtc: baseTime);
        Assert.Equal(2, await db.GameRoomMembers.CountAsync());

        // 通知开始于成员创建之后（真实断线时序）→ 正常移除
        await service.ReportPlayerDisconnectAsync("arena-guard",
            new ServerPlayerDisconnectReportRequest { RoomCode = "ROOMAA", UserId = 2 },
            noticeStartedAtUtc: baseTime.AddSeconds(10));
        Assert.Equal(1, await db.GameRoomMembers.CountAsync());
    }

    [Fact]
    public async Task AbsentMemberNotice_IsIdempotentNoOp()
    {
        await using var db = CreateDb();
        var baseTime = DateTime.UtcNow.AddMinutes(-1);
        var instance = new ServerInstance
        {
            InstanceId = "arena-guard", State = "Reserved", RoomCode = "ROOMAA",
            CurrentPlayers = 1, RegisteredAtUtc = baseTime, LastHeartbeatUtc = baseTime,
        };
        var room = new GameRoom
        {
            // 真实时序：disconnect 上报只发生在战斗票签发后（Starting/InMatch）——
            // R1 语义下 Waiting 房无 DS 连接，收到 disconnect 属 no-op（removalPermitted 闭集）
            RoomCode = "ROOMAA", HostUserId = 1, HostUsername = "a", MaxPlayers = 4, JoinedPlayers = 1,
            Status = RoomStatus.Starting, CreatedAtUtc = baseTime, ServerInstance = instance,
            Members = [new GameRoomMember { UserId = 1, JoinedAtUtc = baseTime }],
        };
        db.AddRange(instance, room);
        db.SaveChanges();

        var service = CreateService(db);

        // 不存在的成员：幂等 no-op，房间与实例零变化
        await service.ReportPlayerDisconnectAsync("arena-guard",
            new ServerPlayerDisconnectReportRequest { RoomCode = "ROOMAA", UserId = 999 },
            noticeStartedAtUtc: baseTime.AddMinutes(1));
        Assert.Equal(1, await db.GameRoomMembers.CountAsync());
        Assert.NotNull(await db.GameRooms.SingleOrDefaultAsync(x => x.RoomCode == "ROOMAA"));

        // 真实成员掉线（最后成员，房间未 InMatch）→ 删房 + 释放许可；R1/A03：DB 实例转入
        // Draining 且保留旧绑定（等待 DS 重臂），不立即回池
        await service.ReportPlayerDisconnectAsync("arena-guard",
            new ServerPlayerDisconnectReportRequest { RoomCode = "ROOMAA", UserId = 1 },
            noticeStartedAtUtc: baseTime.AddMinutes(1));
        Assert.Null(await db.GameRooms.SingleOrDefaultAsync(x => x.RoomCode == "ROOMAA"));
        var archived = await db.ServerInstances.SingleAsync(x => x.InstanceId == "arena-guard");
        Assert.Equal("Draining", archived.State);
        Assert.Equal("ROOMAA", archived.RoomCode);
        Assert.Equal(1, archived.CurrentPlayers); // 归档保留旧值：重臂心跳才权威写入 0

        // DS 重臂心跳（Ready+0，房间已删）→ 权威清绑定翻 Ready 回池（三段式闭环）
        var instanceService = new ServerInstanceService(db,
            Options.Create(new ServerInstanceOptions { ServerKey = "test" }),
            NullLogger<ServerInstanceService>.Instance);
        await instanceService.HeartbeatAsync("arena-guard",
            new ServerInstanceHeartbeatRequest { RoomCode = "ROOMAA", State = "Ready", CurrentPlayers = 0 },
            default);
        var released = await db.ServerInstances.SingleAsync(x => x.InstanceId == "arena-guard");
        Assert.Equal("Ready", released.State);
        Assert.Null(released.RoomCode);
        Assert.Equal(0, released.CurrentPlayers);
    }

    [Fact]
    public async Task OlderConsumedSessionNotice_DoesNotRemoveReconnectedMember()
    {
        await using var db = CreateDb();
        var baseTime = DateTime.UtcNow.AddMinutes(-1);
        var instance = new ServerInstance
        {
            InstanceId = "arena-guard", State = "Reserved", RoomCode = "ROOMAA",
            CurrentPlayers = 1, RegisteredAtUtc = baseTime, LastHeartbeatUtc = baseTime,
        };
        var room = new GameRoom
        {
            RoomCode = "ROOMAA", HostUserId = 1, HostUsername = "a", MaxPlayers = 4, JoinedPlayers = 1,
            Status = RoomStatus.InMatch, CreatedAtUtc = baseTime, ServerInstance = instance,
            Members = [new GameRoomMember { UserId = 1, JoinedAtUtc = baseTime }],
        };
        db.AddRange(instance, room);
        await db.SaveChangesAsync();

        var oldSession = new ServerJoinTicket
        {
            TicketHash = new string('a', 64), Instance = instance, RoomCode = "ROOMAA", UserId = 1,
            Username = "a", IssuedAtUtc = baseTime, ExpiresAtUtc = baseTime.AddMinutes(2),
            ConsumedAtUtc = baseTime.AddSeconds(1),
        };
        var reconnectedSession = new ServerJoinTicket
        {
            TicketHash = new string('b', 64), Instance = instance, RoomCode = "ROOMAA", UserId = 1,
            Username = "a", IssuedAtUtc = baseTime.AddSeconds(10), ExpiresAtUtc = baseTime.AddMinutes(2),
            ConsumedAtUtc = baseTime.AddSeconds(11),
        };
        db.AddRange(oldSession, reconnectedSession);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ReportPlayerDisconnectAsync("arena-guard",
            new ServerPlayerDisconnectReportRequest
            {
                RoomCode = "ROOMAA", UserId = 1, SessionId = oldSession.Id,
            },
            noticeStartedAtUtc: baseTime.AddSeconds(20));

        Assert.Equal(1, await db.GameRoomMembers.CountAsync());
        Assert.NotNull(await db.GameRooms.SingleOrDefaultAsync(x => x.RoomCode == "ROOMAA"));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static RoomService CreateService(AppDbContext db)
    {
        var options = Options.Create(new ServerInstanceOptions { ServerKey = "test" });
        var instances = new ServerInstanceService(db, options, NullLogger<ServerInstanceService>.Instance);
        var matches = new MatchService(db, new DemoProgressionRules());
        return new RoomService(db, instances, matches, new RoomChatService(Options.Create(options.Value)), NullLogger<RoomService>.Instance);
    }
}

/// <summary>
/// NO_SERVER_AVAILABLE 归因分类（P0 开发诊断）：审计五类必须可区分——
/// 无进程 no-process / 心跳过期 heartbeat-stale / 容量不足 capacity-insufficient /
/// 全部被占用 all-occupied（进程在线但被房间占住的僵尸 Reserved 即此类）/ 状态闭环异常 loop-anomaly；
/// 诊断瞬间出现可租实例（租用竞态）→ race-transient，不算池枯竭。
/// </summary>
public sealed class ServerInstancePoolClassificationTests
{
    private static ServerInstanceDiagnosticDto Row(string state, bool fresh, int capacity = 8) =>
        new("arena-x", state, null, 0, capacity, 0, fresh);

    private static string Classify(params ServerInstanceDiagnosticDto[] rows) =>
        ServerInstanceService.ClassifyNoServer(ServerInstanceService.SummarizePool(rows, requestedCapacity: 8));

    [Fact]
    public void EmptyPool_IsNoProcess()
        => Assert.Equal("no-process", Classify());

    [Fact]
    public void FreshReadyWithSufficientCapacity_IsRaceTransient()
        => Assert.Equal("race-transient", Classify(Row("Ready", fresh: true, capacity: 8)));

    [Fact]
    public void FreshReadyAllCapacityShort_IsCapacityInsufficient()
        => Assert.Equal("capacity-insufficient", Classify(
            Row("Ready", fresh: true, capacity: 2),
            Row("Ready", fresh: true, capacity: 4)));

    [Fact]
    public void ReservedOnly_IsAllOccupied()
        => Assert.Equal("all-occupied", Classify(Row("Reserved", fresh: true)));

    [Fact]
    public void StaleReadyOnly_IsHeartbeatStale()
        => Assert.Equal("heartbeat-stale", Classify(Row("Ready", fresh: false)));

    [Fact]
    public void FreshOfflineOnly_IsLoopAnomaly()
        => Assert.Equal("loop-anomaly", Classify(Row("Offline", fresh: true)));

    [Fact]
    public void Summary_CountsStatesAndFreshness()
    {
        var summary = ServerInstanceService.SummarizePool(
        [
            Row("Ready", fresh: true),
            Row("Ready", fresh: false),
            Row("Reserved", fresh: true),
            Row("InMatch", fresh: false),
            Row("Offline", fresh: true),
        ], requestedCapacity: 16);
        Assert.Equal(5, summary.Total);
        Assert.Equal(1, summary.ReadyFresh);
        Assert.Equal(1, summary.ReadyFreshCapacityShort); // capacity 8 < requested 16
        Assert.Equal(1, summary.ReadyStale);
        Assert.Equal(1, summary.Reserved);
        Assert.Equal(1, summary.InMatch);
        Assert.Equal(1, summary.Offline);
        Assert.Equal(2, summary.StaleTotal);
    }
}
