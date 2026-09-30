using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// F13 回归（同房重进幂等，Docs/27 v1 适配）：JoinAsync 的"一人一房"清理不得作用于目标房本身——
/// ① 房主同房重进保房主、保 JoinedAtUtc（经 leader 转移次序证明）；② 普通成员同房重进不产生重复成员行、容量不泄漏；
/// ③ 开局/重连票据规则不变（一次性/REPLAYED/旧票据因仍是成员而继续有效）；
/// ④ 跨房切换仍清理旧房；⑤ 并发重进（含并发首连）不产生重复成员。
/// 各用例用隔离工厂（独立存储），注册已知地址的单/双实例以便 consume 反查 instanceId。
/// </summary>
public sealed class RoomSameRoomRejoinTests
{
    [Fact]
    public async Task HostSameRoomRejoin_KeepsLeader_Count_AndIssuesNewTicketOnStart()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client, null, "10.1.0.5")).GetProperty("instanceId").GetString()!;
        var (hostToken, hostName) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        // 房主同房重进（Waiting）：零变化（人数/房主/成员行不动）
        await ServerTest.JoinRoomAsync(client, hostToken, roomCode);
        var room = await GetRoomAsync(client, roomCode);
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(hostName, room.GetProperty("leaderUsername").GetString());

        // 票据规则（复审 R01 顶替策略）：开局补发第一张；Starting 重进补发第二张并顶替第一张——
        // 一次性 + 单活票据：新票可消费，被顶替的旧票即时作废（TICKET_INVALID），不再依赖 TTL
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        await ServerTest.ReadyAsync(client, guestToken, roomCode);
        var firstStart = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        var firstTicket = ServerTest.ConnectionTicket(firstStart);
        var rejoin = await ServerTest.JoinRoomAsync(client, hostToken, roomCode); // Starting 重进=重连补票
        var rejoinTicket = rejoin.GetProperty("connection").GetProperty("joinTicket").GetString()!;
        Assert.NotEqual(firstTicket, rejoinTicket);

        var consumeNew = await ServerTest.ConsumeTicketAsync(client, instanceId, rejoinTicket);
        Assert.True(consumeNew.GetProperty("valid").GetBoolean());
        var replay = await ServerTest.ConsumeTicketAsync(client, instanceId, rejoinTicket);
        Assert.Equal("TICKET_REPLAYED", replay.GetProperty("errorCode").GetString());
        var consumeSuperseded = await ServerTest.ConsumeTicketAsync(client, instanceId, firstTicket);
        Assert.False(consumeSuperseded.GetProperty("valid").GetBoolean()); // 被新票顶替，即时失效
    }

    [Fact]
    public async Task OrdinaryMemberSameRoomRejoin_NoDuplicateRow_NoCapacityLeak()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client, null, "10.1.0.5");
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        Assert.Equal(2, (await GetRoomAsync(client, roomCode)).GetProperty("joinedPlayers").GetInt32());

        // 普通成员同房重进：不重复成员行
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        Assert.Equal(2, (await GetRoomAsync(client, roomCode)).GetProperty("joinedPlayers").GetInt32());

        // 容量不泄漏：重进未占用新席位，第三名用户仍可加入
        var (thirdToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, thirdToken, roomCode);
        Assert.Equal(3, (await GetRoomAsync(client, roomCode)).GetProperty("joinedPlayers").GetInt32());
    }

    [Fact]
    public async Task HostRejoin_PreservesJoinOrder_ForLeaderTransfer()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client, null, "10.1.0.5");
        var (hostToken, hostName) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, guestName) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);   // JoinedAtUtc T1
        var (kToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, kToken, roomCode);       // JoinedAtUtc T2 (晚于 T1)

        // 房主同房重进：若实现错误（purge+重加），房主 JoinedAtUtc 会重置为晚于 K
        await ServerTest.JoinRoomAsync(client, hostToken, roomCode);
        Assert.Equal(hostName, (await GetRoomAsync(client, roomCode)).GetProperty("leaderUsername").GetString());

        // 房主随后离开 → 转移给最早加入的剩余成员：应是最初 Guest（T1），而非 K（T2）
        await ServerTest.Authorized(client, hostToken).PostAsync("/api/rooms/leave", null);
        var room = await GetRoomAsync(client, roomCode);
        Assert.Equal(guestName, room.GetProperty("leaderUsername").GetString());
        Assert.Equal(2, room.GetProperty("joinedPlayers").GetInt32());
    }

    [Fact]
    public async Task SwitchingRooms_StillCleansUpOldRoom()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();

        var (host1Token, _) = await ServerTest.RegisterUserAsync(client);
        var roomASnapshot = await ServerTest.CreateRoomAsync(client, host1Token);
        var roomA = ServerTest.HostRoomCode(roomASnapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomA);

        var (host2Token, _) = await ServerTest.RegisterUserAsync(client);
        var roomBSnapshot = await ServerTest.CreateRoomAsync(client, host2Token);
        var roomB = ServerTest.HostRoomCode(roomBSnapshot.GetProperty("room"));

        // 跨房切换：旧房清理（减员），新房正常加入（实例租用唯一性由 ConcurrentStartLeasesDistinctInstances 覆盖）
        await ServerTest.JoinRoomAsync(client, guestToken, roomB);
        Assert.Equal(1, (await GetRoomAsync(client, roomA)).GetProperty("joinedPlayers").GetInt32());
        Assert.Equal(2, (await GetRoomAsync(client, roomB)).GetProperty("joinedPlayers").GetInt32());
        _ = host1Token;
    }

    [Fact]
    public async Task ConcurrentRejoin_ProducesNoDuplicateMember()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client, null, "10.1.0.5");
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        var joinedBefore = (await GetRoomAsync(client, roomCode)).GetProperty("joinedPlayers").GetInt32();

        // 已是成员的并发重进：无成员行插入 → 双 200 且人数零变化
        var rejoinA = ServerTest.Authorized(client, guestToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", null);
        var rejoinB = ServerTest.Authorized(client, guestToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", null);
        var rejoins = await Task.WhenAll(rejoinA, rejoinB);
        Assert.All(rejoins, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(joinedBefore, (await GetRoomAsync(client, roomCode)).GetProperty("joinedPlayers").GetInt32());

        // 并发首连的重复成员由 MySQL GameRoomMember.UserId 唯一索引兜底（迁移 unique: true）+ JoinAsync 重试；
        // InMemory 测试提供方不强制唯一索引，无法在本文件覆盖（与既有纪律③一致）。
    }

    private static async Task<JsonElement> GetRoomAsync(HttpClient client, string roomCode)
    {
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        return list!.Single(r => r.GetProperty("roomId").GetInt64() == ServerTest.PublicRoomId(roomCode));
    }
}
