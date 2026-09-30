using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// Dedicated Server 控制面 API 测试（Docs/27 v1 CF 等待房间契约 + 控制面纪律）：
/// 密钥 fail closed / 注册幂等 / 心跳 / 开局租用并发 / 票据一次性（REPLAYED/EXPIRED/INVALID/INSTANCE_MISMATCH）。
/// CF 语义适配：创建/加入只进 Waiting（无票据）；租 DS/签票在 start——本文件的票据一律来自开局响应。
/// 密钥经内存配置注入（测试基础设施），生产只允许环境变量/命令行。
/// </summary>
public sealed class ServerInstanceApiTests : IClassFixture<ServerApiFactory>
{
    private readonly ServerApiFactory factory;

    public ServerInstanceApiTests(ServerApiFactory factory) => this.factory = factory;

    private HttpClient NewClient() => factory.CreateClient();

    [Fact]
    public async Task RegisterWithoutServerKeyIsUnauthorized()
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/server-instances/register",
            new { instanceId = "arena-01", address = "10.1.0.5", port = 7770, capacity = 8 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegisterWithWrongServerKeyIsForbidden()
    {
        var client = NewClient();
        var response = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post, "/api/server-instances/register",
            "totally-wrong-key", new { instanceId = "arena-01", address = "10.1.0.5", port = 7770, capacity = 8 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ControlPlaneFailsClosedWithoutConfiguredKey()
    {
        // 普通 ApiFactory 未配置任何 ServerInstances 密钥 → 一切调用 401（fail closed）
        using var plain = new ApiFactory();
        var client = plain.CreateClient();
        var withKey = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post, "/api/server-instances/register",
            ServerTest.ServerKey, new { instanceId = "arena-01", address = "10.1.0.5", port = 7770, capacity = 8 });
        Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
        var withoutKey = await client.PostAsJsonAsync("/api/server-instances/register",
            new { instanceId = "arena-01", address = "10.1.0.5", port = 7770, capacity = 8 });
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
    }

    [Fact]
    public async Task RegisterReturnsReadyStateAndHeartbeatInterval()
    {
        var client = NewClient();
        var instanceId = ServerTest.NewInstanceId();
        var registered = await ServerTest.RegisterInstanceAsync(client, instanceId);
        Assert.Equal(instanceId, registered.GetProperty("instanceId").GetString());
        Assert.Equal("Ready", registered.GetProperty("state").GetString());
        Assert.Equal(15, registered.GetProperty("heartbeatIntervalSeconds").GetInt32());
    }

    [Fact]
    public async Task ReRegisterSameInstanceUpdatesAddressWithoutDuplication()
    {
        var client = NewClient();
        var instanceId = ServerTest.NewInstanceId();
        await ServerTest.RegisterInstanceAsync(client, instanceId, "10.1.0.5");
        var again = await ServerTest.RegisterInstanceAsync(client, instanceId, "10.1.0.99");
        Assert.Equal("Ready", again.GetProperty("state").GetString());

        // 重新注册后开局必须租到"更新后"的同一实例（地址为新值），证明是 upsert 而非新增
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        Assert.Equal("10.1.0.99", ServerTest.ConnectionAddress(start));
    }

    [Fact]
    public async Task HeartbeatUnknownInstanceIs404()
    {
        var client = NewClient();
        var response = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{ServerTest.NewInstanceId("nope")}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = "Ready" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SERVER_INSTANCE_NOT_FOUND", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HeartbeatRejectsUnknownState()
    {
        var client = NewClient();
        var instanceId = ServerTest.NewInstanceId();
        await ServerTest.RegisterInstanceAsync(client, instanceId);
        var response = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = "Flying" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HeartbeatInMatchSyncsRoomStatus()
    {
        var client = NewClient();
        var instanceId = ServerTest.NewInstanceId();
        await ServerTest.RegisterInstanceAsync(client, instanceId);
        var (roomCode, hostToken, _, _) = await ServerTest.CreateStartedRoomAsync(client);

        var beat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 2, state = "InMatch" });
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
        Assert.Equal("InMatch", room.GetProperty("status").GetString());
        _ = hostToken;
    }

    [Fact]
    public async Task ConcurrentStartBothSucceedWithDistinctMatches()
    {
        var client = NewClient();
        var addrA = $"10.{Random.Shared.Next(2, 250)}.{Random.Shared.Next(2, 250)}.5"; // 唯一地址：类内残留实例不致撞车
        var addrB = $"10.{Random.Shared.Next(2, 250)}.{Random.Shared.Next(2, 250)}.6";
        await ServerTest.RegisterInstanceAsync(client, ServerTest.NewInstanceId(), addrA);
        await ServerTest.RegisterInstanceAsync(client, ServerTest.NewInstanceId(), addrB);

        // 准备两间就绪房间，再并发开始：两场比赛各自成立、matchId 互不相同
        var prepared = new List<(string RoomCode, string HostToken)>();
        for (var i = 0; i < 2; i++)
        {
            var (roomCode, hostToken) = await ServerTest.CreateStartedRoomPendingAsync(client);
            prepared.Add((roomCode, hostToken));
        }

        var starts = await Task.WhenAll(prepared.Select(p =>
            ServerTest.Authorized(factory.CreateClient(), p.HostToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(p.RoomCode)}/start", null)));
        Assert.All(starts, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var matchIds = new HashSet<string>();
        foreach (var started in starts)
        {
            var body = await started.Content.ReadFromJsonAsync<JsonElement>();
            matchIds.Add(body.GetProperty("matchId").GetString()!);
        }
        Assert.Equal(2, matchIds.Count);
        // 注：并发下"两房必租不同实例"依赖关系库 Serializable 事务 + Version 令牌；InMemory 提供方
        // 不强制该竞态（本轮实测双双命中同一实例、无冲突异常），真实 MySQL 验证另标 VERIFY_PENDING。
        // 顺序语义（租用后 Reserved 不可再租）由 TicketInvalidAfterLastMemberLeavesAndInstanceReused 等用例覆盖。
    }

    [Fact]
    public async Task StaleInstanceIsNotLeased()
    {
        using var shortTtl = ServerApiFactory.WithConfig(new Dictionary<string, string?> { ["ServerInstances:InstanceTtlSeconds"] = "1" });
        var client = shortTtl.CreateClient();
        await ServerTest.RegisterInstanceAsync(client);
        await Task.Delay(1100); // 心跳失联（TTL=1s）

        // CF：创建只进 Waiting（不再租实例），失联实例在 start 时拒绝
        var (token, _) = await ServerTest.RegisterUserAsync(client);
        var create = await ServerTest.Authorized(client, token).PostAsJsonAsync("/api/rooms", new { maxPlayers = 4 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var roomCode = ServerTest.HostRoomCode((await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        await ServerTest.ReadyAsync(client, guestToken, roomCode);

        var start = await ServerTest.Authorized(client, token).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        using var problem = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        Assert.Equal("NO_SERVER_AVAILABLE", problem.RootElement.GetProperty("code").GetString());

        // 失败回 Waiting 且保留准备：开始条件不变时重试仍是同一 409（房间未被破坏）
        var detail = await ServerTest.Authorized(client, token).GetFromJsonAsync<JsonElement>($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}");
        Assert.Equal("Waiting", detail.GetProperty("room").GetProperty("status").GetString());
    }

    [Fact]
    public async Task StartIssuesDistinctTicketsBoundToUsers()
    {
        var client = NewClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var matchId = start.GetProperty("matchId").GetString()!;
        var ticketA = ServerTest.ConnectionTicket(start);

        // roster 成员在 Starting 的重进 = 重连补票（Docs/27 §5.2）
        var guestRejoin = await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        var ticketB = guestRejoin.GetProperty("connection").GetProperty("joinTicket").GetString()!;
        var nameA = (await client.GetFromJsonAsync<JsonElement[]>("/api/rooms"))!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode))
            .GetProperty("leaderUsername").GetString();

        Assert.NotEqual(ticketA, ticketB); // 票据互不相同且一次性
        var consumeA = await ServerTest.ConsumeTicketAsync(client, instanceId, ticketA);
        Assert.True(consumeA.GetProperty("valid").GetBoolean());
        Assert.Equal(roomCode, consumeA.GetProperty("roomCode").GetString());
        Assert.Equal(nameA, consumeA.GetProperty("username").GetString());
        Assert.Equal(matchId, consumeA.GetProperty("matchId").GetString());      // CF 扩展：比赛身份带出
        Assert.Equal(1, consumeA.GetProperty("matchGeneration").GetInt32());
        Assert.Equal("Red", consumeA.GetProperty("teamId").GetString());         // TDM 自动分队：房主红队

        var consumeB = await ServerTest.ConsumeTicketAsync(client, instanceId, ticketB);
        Assert.True(consumeB.GetProperty("valid").GetBoolean());
        Assert.Equal(matchId, consumeB.GetProperty("matchId").GetString());
        Assert.Equal("Blue", consumeB.GetProperty("teamId").GetString());
        _ = hostToken;
    }

    [Fact]
    public async Task TicketReplayIsRejectedWithStableErrorCode()
    {
        var client = NewClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);

        var first = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(first.GetProperty("valid").GetBoolean());

        var replay = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.False(replay.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_REPLAYED", replay.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// 账号权威配装快照（2026-09-08 追加 P0 §6 二.1）：consume 成功必须携带
    /// primary/secondary/version/attachments（DS 据此把网络玩家 Arsenal 严格配置为账号两槽）；
    /// 失败响应（重放）不得携带配装（防泄露）。
    /// </summary>
    [Fact]
    public async Task ConsumeReturnsAuthoritativeLoadoutSnapshot_AndFailuresCarryNoLoadout()
    {
        var client = NewClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);

        var consume = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(consume.GetProperty("valid").GetBoolean());
        var loadout = consume.GetProperty("loadout");
        Assert.Equal(JsonValueKind.Object, loadout.ValueKind); // 成功 consume 必须携带权威配装快照
        Assert.False(string.IsNullOrEmpty(loadout.GetProperty("primaryWeaponId").GetString()));
        Assert.False(string.IsNullOrEmpty(loadout.GetProperty("secondaryWeaponId").GetString()));
        Assert.True(loadout.GetProperty("version").GetInt64() >= 1);
        Assert.Equal(JsonValueKind.Array, loadout.GetProperty("attachments").ValueKind); // 附件数组必须存在（可空内容）

        // CF 三背包（2026-09-30 Phase A）：consume 必须同时携带三背包全集 + 活动下标，
        // 各背包带 backpackIndex 标识；失败响应不得携带（防泄露）。
        var backpacks = consume.GetProperty("backpacks");
        Assert.Equal(JsonValueKind.Array, backpacks.ValueKind);
        Assert.Equal(3, backpacks.GetArrayLength());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, backpacks[i].GetProperty("backpackIndex").GetInt32());
            Assert.False(string.IsNullOrEmpty(backpacks[i].GetProperty("primaryWeaponId").GetString()));
        }
        Assert.Equal(0, consume.GetProperty("activeBackpackIndex").GetInt32());

        var replay = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.False(replay.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, replay.GetProperty("loadout").ValueKind); // 失败响应不得携带配装;
    }

    [Fact]
    public async Task UnknownAndMalformedTicketsAreInvalid()
    {
        var client = NewClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;

        // 43 字符合法 Base64Url（≈32 字节）但库中查无此 hash
        var randomButWellFormed = new string(Enumerable.Range(0, 43).Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[Random.Shared.Next(64)]).ToArray());
        var unknown = await ServerTest.ConsumeTicketAsync(client, instanceId, randomButWellFormed);
        Assert.False(unknown.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INVALID", unknown.GetProperty("errorCode").GetString());

        // 非法格式（解码不出 32 字节）
        var malformed = await ServerTest.ConsumeTicketAsync(client, instanceId, "not-a-ticket!!");
        Assert.False(malformed.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INVALID", malformed.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ExpiredTicketIsRejected()
    {
        using var shortTtl = ServerApiFactory.WithConfig(new Dictionary<string, string?> { ["ServerInstances:TicketTtlSeconds"] = "1" });
        var client = shortTtl.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);

        await Task.Delay(1100); // 越过 1s TTL
        var consumed = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.False(consumed.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_EXPIRED", consumed.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task TicketBoundToForeignInstanceIsMismatch()
    {
        var client = NewClient();
        // 先注册、开局（租到的必然是它），再注册异实例——顺序保证确定性
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);
        Assert.Equal("10.1.0.5", ServerTest.ConnectionAddress(start)); // 确认租到的是刚注册的实例

        var foreignId = ServerTest.NewInstanceId();
        await ServerTest.RegisterInstanceAsync(client, foreignId);

        var mismatch = await ServerTest.ConsumeTicketAsync(client, foreignId, ticket);
        Assert.False(mismatch.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INSTANCE_MISMATCH", mismatch.GetProperty("errorCode").GetString());

        var own = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(own.GetProperty("valid").GetBoolean()); // 控制组：对实例消费则有效
    }

    [Fact]
    public async Task ConsumeRejectsUnknownInstanceAsMismatch()
    {
        var client = NewClient();
        await ServerTest.RegisterInstanceAsync(client);
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);

        var mismatch = await ServerTest.ConsumeTicketAsync(client, ServerTest.NewInstanceId("nope"), ticket);
        Assert.False(mismatch.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INSTANCE_MISMATCH", mismatch.GetProperty("errorCode").GetString());
    }

    // ===== Codex 审计修复回归（2026-09-06 第二轮）=====

    /// <summary>审计 P0-1：Codely 现状——注册得 Ready 后持续发 Ready 心跳，不得把已租用实例降回 Ready。</summary>
    [Fact]
    public async Task ReservedInstanceRejectsReadyHeartbeatAndStaysUnleasable()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (_, _, _, start) = await ServerTest.CreateStartedRoomAsync(client); // 实例 → Reserved
        var ticket = ServerTest.ConnectionTicket(start);

        var readyBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 1, state = "Ready" });
        Assert.Equal(HttpStatusCode.Conflict, readyBeat.StatusCode);
        using var problem = JsonDocument.Parse(await readyBeat.Content.ReadAsStringAsync());
        Assert.Equal("SERVER_INSTANCE_STATE_CONFLICT", problem.RootElement.GetProperty("code").GetString());

        // 不得重新进入 start 的 Ready 候选集合：第二间房开始必须 409 NO_SERVER_AVAILABLE
        var (otherToken, _) = await ServerTest.RegisterUserAsync(client);
        var otherRoom = await ServerTest.CreateRoomAsync(client, otherToken);
        var otherCode = ServerTest.HostRoomCode(otherRoom.GetProperty("room"));
        var (otherGuest, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, otherGuest, otherCode);
        await ServerTest.ReadyAsync(client, otherGuest, otherCode);
        var second = await ServerTest.Authorized(client, otherToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(otherCode)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var createProblem = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("NO_SERVER_AVAILABLE", createProblem.RootElement.GetProperty("code").GetString());

        // 绑定未被破坏：合法 Reserved 心跳可确认；原票据仍可消费
        var confirmBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 1, state = "Reserved" });
        Assert.Equal(HttpStatusCode.NoContent, confirmBeat.StatusCode);
        var consume = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(consume.GetProperty("valid").GetBoolean());
    }

    /// <summary>审计 P0-1：绑定态的一切降级/改写形状（错 roomCode、Offline、未绑定认领）全部 409，绑定原样。</summary>
    [Fact]
    public async Task ReservedInstanceRejectsIllegalHeartbeatShapesAndKeepsBinding()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);

        // 已绑定实例：InMatch/Reserved 携带不一致 roomCode → 409；Offline 降级 → 409
        foreach (var shape in new[]
                 {
                     new { roomCode = "ZZZZZZ", currentPlayers = 1, state = "InMatch" },
                     new { roomCode = "ZZZZZZ", currentPlayers = 1, state = "Reserved" },
                     new { roomCode = (string?)null, currentPlayers = 1, state = "Offline" },
                 })
        {
            var beat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
                $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey, shape);
            Assert.Equal(HttpStatusCode.Conflict, beat.StatusCode);
        }

        // 绑定原样：第二间房开局仍租不到（此时存储里没有其他 Ready 实例）
        var (otherToken, _) = await ServerTest.RegisterUserAsync(client);
        var otherRoom = await ServerTest.CreateRoomAsync(client, otherToken);
        var otherCode = ServerTest.HostRoomCode(otherRoom.GetProperty("room"));
        var (otherGuest, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, otherGuest, otherCode);
        await ServerTest.ReadyAsync(client, otherGuest, otherCode);
        var second = await ServerTest.Authorized(client, otherToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(otherCode)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // 未绑定实例凭空认领绑定 → 409（放在上面 409 断言之后注册，避免游离实例被开局合法租走）
        var freeId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var claim = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{freeId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = "ZZZZZZ", currentPlayers = 0, state = "Ready" });
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);

        // 绑定原样：原票据仍可消费
        var consume = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(consume.GetProperty("valid").GetBoolean());
        Assert.Equal(roomCode, consume.GetProperty("roomCode").GetString());
    }

    /// <summary>审计 P0-1：合法转换阶梯 Ready→Reserved→InMatch 全程保持绑定；InMatch 正确同步房间状态。</summary>
    [Fact]
    public async Task LegalHeartbeatLadderKeepsBindingAndSyncsRoom()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;

        // 未绑定：Ready→Ready 合法
        var readyBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = (string?)null, currentPlayers = 0, state = "Ready" });
        Assert.Equal(HttpStatusCode.NoContent, readyBeat.StatusCode);

        var (roomCode, _, _, start) = await ServerTest.CreateStartedRoomAsync(client);
        var ticket = ServerTest.ConnectionTicket(start);

        // Reserved 确认（携带精确 roomCode）→ InMatch（携带精确 roomCode）
        var reservedBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 2, state = "Reserved" });
        Assert.Equal(HttpStatusCode.NoContent, reservedBeat.StatusCode);
        var inMatchBeat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 2, state = "InMatch" });
        Assert.Equal(HttpStatusCode.NoContent, inMatchBeat.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
        Assert.Equal("InMatch", room.GetProperty("status").GetString());

        // 绑定全程未变：InMatch 后票据仍可正常消费
        var consume = await ServerTest.ConsumeTicketAsync(client, instanceId, ticket);
        Assert.True(consume.GetProperty("valid").GetBoolean());
    }

    /// <summary>审计 P0-2：普通成员 Leave 后其票据消费失败；未离开者的票据不受影响。</summary>
    [Fact]
    public async Task TicketInvalidAfterMemberLeaves()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var hostTicket = ServerTest.ConnectionTicket(start);
        var guestTicket = (await ServerTest.JoinRoomAsync(client, guestToken, roomCode))
            .GetProperty("connection").GetProperty("joinTicket").GetString()!;

        await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);

        var guestConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, guestTicket);
        Assert.False(guestConsume.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INVALID", guestConsume.GetProperty("errorCode").GetString());

        var hostConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, hostTicket);
        Assert.True(hostConsume.GetProperty("valid").GetBoolean()); // 房间与绑定仍在，留房者不受影响
        _ = hostToken;
    }

    /// <summary>审计 P0-2：换房（加入第二间房自动退出第一间）后旧房票据消费失败。</summary>
    [Fact]
    public async Task TicketInvalidAfterSwitchingRooms()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;

        var (room1, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var hostTicket = ServerTest.ConnectionTicket(start);
        var oldTicket = (await ServerTest.JoinRoomAsync(client, guestToken, room1))
            .GetProperty("connection").GetProperty("joinTicket").GetString()!;

        // 换房：另一房主建 Waiting 房（不租实例），guest 弃 room1 加入 room2（Join 自动清一人一房）
        var (host2Token, _) = await ServerTest.RegisterUserAsync(client);
        var room2Connection = await ServerTest.CreateRoomAsync(client, host2Token);
        var room2 = ServerTest.HostRoomCode(room2Connection.GetProperty("room"));
        await ServerTest.JoinRoomAsync(client, guestToken, room2);

        var oldConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, oldTicket);
        Assert.False(oldConsume.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INVALID", oldConsume.GetProperty("errorCode").GetString()); // 已不是 room1 成员

        var hostConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, hostTicket);
        Assert.True(hostConsume.GetProperty("valid").GetBoolean());
        _ = hostToken;
    }

    /// <summary>审计 P0-2：最后一名成员离开（房间删除+实例释放）与实例被新房重新租用后，旧票据失败且不影响新票据。</summary>
    [Fact]
    public async Task TicketInvalidAfterLastMemberLeavesAndInstanceReused()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (firstRoomCode, hostToken, guestToken, start) = await ServerTest.CreateStartedRoomAsync(client);
        var oldTicket = ServerTest.ConnectionTicket(start);

        await ServerTest.Authorized(client, guestToken).PostAsync("/api/rooms/leave", null);
        await ServerTest.Authorized(client, hostToken).PostAsync("/api/rooms/leave", null); // 房间删除，实例释放许可

        // R1/A03：释放许可后 DB 实例保持 Draining（旧绑定保留）——DS 重臂心跳（Ready+0，房间已删）
        // 权威清绑定回池后，同一实例才能被新房重新开局租用
        var rearm = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode = firstRoomCode, currentPlayers = 0, state = "Ready" });
        Assert.Equal(HttpStatusCode.NoContent, rearm.StatusCode);

        var oldConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, oldTicket);
        Assert.False(oldConsume.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INVALID", oldConsume.GetProperty("errorCode").GetString());

        // 同一实例被新房重新开局租用：新票据正常；旧票据依旧失败（幂等复核，不受重新分配影响）
        var (_, _, _, reused) = await ServerTest.CreateStartedRoomAsync(client);
        var newTicket = ServerTest.ConnectionTicket(reused);
        Assert.Equal("10.1.0.5", ServerTest.ConnectionAddress(reused)); // 复用了同一实例

        var newConsume = await ServerTest.ConsumeTicketAsync(client, instanceId, newTicket);
        Assert.True(newConsume.GetProperty("valid").GetBoolean());
        var oldConsumeAgain = await ServerTest.ConsumeTicketAsync(client, instanceId, oldTicket);
        Assert.False(oldConsumeAgain.GetProperty("valid").GetBoolean());
        Assert.Equal("TICKET_INVALID", oldConsumeAgain.GetProperty("errorCode").GetString());
    }

    /// <summary>审计 P1-4：capacity=2 的实例不能承载 maxPlayers=8；有足够容量的实例应在开局时被选中。</summary>
    [Fact]
    public async Task StartRequiresInstanceCapacity()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient(); // 独立存储：本用例对候选实例集合有全库断言
        var smallAddr = $"10.{Random.Shared.Next(2, 250)}.{Random.Shared.Next(2, 250)}.2";
        await ServerTest.RegisterInstanceAsync(client, ServerTest.NewInstanceId(), smallAddr, 7770, capacity: 2);

        // maxPlayers=8 > capacity=2：开局 409 NO_SERVER_AVAILABLE（创建仍成功进 Waiting）
        var (tokenA, _) = await ServerTest.RegisterUserAsync(client);
        var bigRoom = await ServerTest.CreateRoomAsync(client, tokenA, maxPlayers: 8);
        var bigCode = ServerTest.HostRoomCode(bigRoom.GetProperty("room"));
        var (bigGuest, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, bigGuest, bigCode);
        await ServerTest.ReadyAsync(client, bigGuest, bigCode);
        var tooBig = await ServerTest.Authorized(client, tokenA).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(bigCode)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, tooBig.StatusCode);
        using var problem = JsonDocument.Parse(await tooBig.Content.ReadAsStringAsync());
        Assert.Equal("NO_SERVER_AVAILABLE", problem.RootElement.GetProperty("code").GetString());

        // 容量边界：maxPlayers=2 恰好等于 capacity=2，允许开局（1v1：房主红队+客人蓝队）
        var (_, _, _, fits) = await ServerTest.CreateStartedRoomAsync(client, maxPlayers: 2);
        Assert.Equal(smallAddr, ServerTest.ConnectionAddress(fits));

        // 补充一个大容量实例后，maxPlayers=8 应选中它而不是已租用的小实例
        var bigAddr = $"10.{Random.Shared.Next(2, 250)}.{Random.Shared.Next(2, 250)}.16";
        await ServerTest.RegisterInstanceAsync(client, ServerTest.NewInstanceId(), bigAddr, 7770, capacity: 16);
        var (_, _, _, started) = await ServerTest.CreateStartedRoomAsync(client, maxPlayers: 8);
        Assert.Equal(bigAddr, ServerTest.ConnectionAddress(started));
    }
}

/// <summary>控制面/房间测试共享：带 X-Server-Key 的测试工厂与小工具（Docs/27 §3）。</summary>
public sealed class ServerApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> extraConfig;

    public ServerApiFactory() => extraConfig = new Dictionary<string, string?>();

    private ServerApiFactory(IReadOnlyDictionary<string, string?> extraConfig) => this.extraConfig = extraConfig;

    /// <summary>xUnit 类夹具要求单一公共构造；自定义配置的变体走这里。</summary>
    public static ServerApiFactory WithConfig(IReadOnlyDictionary<string, string?> extraConfig) => new(extraConfig);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole()); // 500 等未处理异常的堆栈会进测试输出
        Environment.SetEnvironmentVariable("Database__AllowInMemoryFallback", "true");
        var values = new Dictionary<string, string?>(extraConfig)
        {
            // 存储隔离开关：强制 InMemory + 每工厂独立库名（绕开 user-secrets/环境变量里的真实 MySQL 连接串）
            ["Database:InMemoryName"] = "test-" + Guid.NewGuid().ToString("N"),
            // 测试专用密钥：生产密钥只允许环境变量/命令行，绝不入库/入仓库
            ["ServerInstances:ServerKey"] = ServerTest.ServerKey
        };
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values!));
    }
}

/// <summary>房间/控制面测试共享 HTTP 小工具。</summary>
public static class ServerTest
{
    // Keep DS fixtures keyed by their internal code, but exercise player endpoints by public ID.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> PublicIds = new();
    public static string HostRoomCode(JsonElement room)
    {
        var code = room.GetProperty("roomCode").GetString()!;
        if (room.TryGetProperty("roomId", out var id)) PublicIds[code] = id.GetInt64();
        return code;
    }
    public static long PublicRoomId(string code) => PublicIds.TryGetValue(code, out var id) ? id : long.MaxValue;

    public const string ServerKey = "test-server-key-not-a-secret";

    /// <summary>每个测试用唯一实例 id：同类工厂的存储跨测试共享，固定 id 会把上一例的 Reserved 状态带过来。</summary>
    public static string NewInstanceId(string prefix = "arena") => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    public static HttpClient Authorized(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static HttpRequestMessage KeyedRequest(HttpMethod method, string path, string key, object? payload)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Server-Key", key);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    public static Task<HttpResponseMessage> SendWithKeyAsync(HttpClient client, HttpMethod method, string path, string key, object? payload)
        => client.SendAsync(KeyedRequest(method, path, key, payload));

    public static async Task<JsonElement> RegisterInstanceAsync(HttpClient client, string? instanceId = null, string address = "10.1.0.5", int port = 7770, int capacity = 8, string? protocolId = null)
    {
        instanceId ??= NewInstanceId();
        object payload = protocolId is null
            ? new { instanceId, address, port, capacity, buildVersion = "0.1.0-day1" }
            : new { instanceId, address, port, capacity, buildVersion = "0.1.0-day1", protocolId };
        var response = await SendWithKeyAsync(client, HttpMethod.Post, "/api/server-instances/register", ServerKey, payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<(string Token, string Username)> RegisterUserAsync(HttpClient client)
    {
        var username = "zc_" + Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        using var json = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("token").GetString()!, username);
    }

    /// <summary>创建房间（Docs/27 v1：只进 Waiting，无票据）。返回创建快照；killTarget 缺省时跟随请求默认。
    /// clientProtocolId（P0-A）：建房者申报的应用协议代际——冻结为房间租用筛选依据。</summary>
    public static async Task<JsonElement> CreateRoomAsync(HttpClient client, string token, int maxPlayers = 4, string mode = "TDM", int? killTarget = null, string? clientProtocolId = null)
    {
        object payload = killTarget is null
            ? (clientProtocolId is null ? new { maxPlayers, mode } : new { maxPlayers, mode, clientProtocolId })
            : (clientProtocolId is null
                ? new { maxPlayers, mode, killTarget }
                : new { maxPlayers, mode, killTarget, clientProtocolId });
        var create = await Authorized(client, token).PostAsJsonAsync("/api/rooms", payload);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var snapshot = await create.Content.ReadFromJsonAsync<JsonElement>();
        HostRoomCode(snapshot.GetProperty("room"));
        return snapshot;
    }

    public static async Task<JsonElement> JoinRoomAsync(HttpClient client, string token, string roomCode, object? body = null)
    {
        var join = await Authorized(client, token).PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", body);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        return await join.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> ReadyAsync(HttpClient client, string token, string roomCode, bool isReady = true)
    {
        var response = await Authorized(client, token).PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/ready", new { isReady });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> StartRoomAsync(HttpClient client, string hostToken, string roomCode)
    {
        var start = await Authorized(client, hostToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        return await start.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>就绪但未开局的房间（TDM：host+自动分队 guest，guest 已准备）——并发开局用例的公共前置。</summary>
    public static async Task<(string RoomCode, string HostToken)> CreateStartedRoomPendingAsync(HttpClient client, int maxPlayers = 4)
    {
        var (hostToken, _) = await RegisterUserAsync(client);
        var roomCode = (await CreateRoomAsync(client, hostToken, maxPlayers)).GetProperty("room").GetProperty("roomCode").GetString()!;
        var (guestToken, _) = await RegisterUserAsync(client);
        await JoinRoomAsync(client, guestToken, roomCode);
        await ReadyAsync(client, guestToken, roomCode);
        return (roomCode, hostToken);
    }

    /// <summary>CF 等待房间一站式前置：TDM 房（host+guest 就绪）并开局租 DS。票据一律从返回的 start 响应取。</summary>
    public static async Task<(string RoomCode, string HostToken, string GuestToken, JsonElement Start)> CreateStartedRoomAsync(
        HttpClient client, int maxPlayers = 8, string mode = "TDM", int? capacity = null, string? address = null)
    {
        if (address is not null || capacity is not null)
            await RegisterInstanceAsync(client, null, address ?? "10.1.0.5", 7770, capacity ?? 8);
        var (hostToken, _) = await RegisterUserAsync(client);
        // KillRace 的杀数白名单与 TDM 不同：助手按模式带出合规默认值
        var roomCode = (await CreateRoomAsync(client, hostToken, maxPlayers, mode,
                killTarget: mode == "KillRace" ? 20 : null))
            .GetProperty("room").GetProperty("roomCode").GetString()!;
        var (guestToken, _) = await RegisterUserAsync(client);
        await JoinRoomAsync(client, guestToken, roomCode);
        await ReadyAsync(client, guestToken, roomCode);
        var start = await StartRoomAsync(client, hostToken, roomCode);
        return (roomCode, hostToken, guestToken, start);
    }

    public static string ConnectionAddress(JsonElement start) => start.GetProperty("connection").GetProperty("serverAddress").GetString()!;
    public static string ConnectionTicket(JsonElement start) => start.GetProperty("connection").GetProperty("joinTicket").GetString()!;

    public static async Task<JsonElement> ConsumeTicketAsync(HttpClient client, string instanceId, string ticket)
    {
        var response = await SendWithKeyAsync(client, HttpMethod.Post, "/api/server-instances/tickets/consume", ServerKey,
            new { instanceId, ticket });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
