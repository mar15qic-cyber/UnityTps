using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// P0-A 应用协议代际门（2026-09-15 一枪终局审计）：
/// ① DS register 申报 protocolId（空串归一 null——旧 DS）；池诊断行携带 protocolId；
/// ② 建房冻结 clientProtocolId → 入房申报必须一致，否则 409 PROTOCOL_MISMATCH（混版本不进同一房）；
/// ③ 实例租用按房间冻结协议筛选：只有旧（null）DS 时新协议房开局 409 NO_SERVER_AVAILABLE；
///    新 DS 就绪后同房开局成功；
/// ④ 旧协议房（null 期望）只可被旧客户端（null 申报）入房；
/// ⑤ ClassifyNoServer 纯函数：有新鲜可用容量但协议全不匹配 → protocol-mismatch 归因。
/// </summary>
public sealed class ProtocolGateTests
{
    private const string NewProtocol = "fps-net-2";
    private const string OldProtocol = "fps-net-1";

    [Fact]
    public async Task Register_StoresProtocolId_EmptyNormalizesNull_PoolShowsIt()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();

        await ServerTest.RegisterInstanceAsync(client, instanceId: "proto-a", protocolId: NewProtocol);
        await ServerTest.RegisterInstanceAsync(client, instanceId: "proto-b", protocolId: "  "); // 空白归一 null（旧 DS）
        await ServerTest.RegisterInstanceAsync(client, instanceId: "proto-c"); // 未申报字段（旧 DS）

        var pool = await ServerTest.SendWithKeyAsync(client, HttpMethod.Get, "/api/server-instances/pool", ServerTest.ServerKey, null);
        Assert.Equal(HttpStatusCode.OK, pool.StatusCode);
        using var json = JsonDocument.Parse(await pool.Content.ReadAsStringAsync());
        var instances = json.RootElement.GetProperty("instances").EnumerateArray().ToDictionary(
            x => x.GetProperty("instanceId").GetString()!, x => x.GetProperty("protocolId").GetString());
        Assert.Equal(NewProtocol, instances["proto-a"]);
        Assert.Null(instances["proto-b"]);
        Assert.Null(instances["proto-c"]);
    }

    [Fact]
    public async Task Join_WithMismatchedProtocol_IsRejected409()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client, protocolId: NewProtocol);

        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, clientProtocolId: NewProtocol);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        // 旧协议客户端 → 409 PROTOCOL_MISMATCH
        var (guestOld, _) = await ServerTest.RegisterUserAsync(client);
        var oldJoin = await ServerTest.Authorized(client, guestOld)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { clientProtocolId = OldProtocol });
        Assert.Equal(HttpStatusCode.Conflict, oldJoin.StatusCode);
        Assert.Equal("PROTOCOL_MISMATCH", await CodeOfAsync(oldJoin));

        // 未申报（旧客户端 null）→ 同样拒绝
        var silentJoin = await ServerTest.Authorized(client, guestOld)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { });
        Assert.Equal(HttpStatusCode.Conflict, silentJoin.StatusCode);
        Assert.Equal("PROTOCOL_MISMATCH", await CodeOfAsync(silentJoin));

        // 一致协议 → 200
        var (guestNew, _) = await ServerTest.RegisterUserAsync(client);
        var okJoin = await ServerTest.Authorized(client, guestNew)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { clientProtocolId = NewProtocol });
        Assert.Equal(HttpStatusCode.OK, okJoin.StatusCode);
    }

    [Fact]
    public async Task LegacyRoom_RejectsNewClient_OnlyLegacyClientsMayJoin()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        await ServerTest.RegisterInstanceAsync(client, protocolId: null);

        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken); // 未申报（旧客户端建房）
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        // 新客户端申报非空协议 → 旧房拒绝（无法为其租到协议匹配的 DS）
        var (guestNew, _) = await ServerTest.RegisterUserAsync(client);
        var newJoin = await ServerTest.Authorized(client, guestNew)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { clientProtocolId = NewProtocol });
        Assert.Equal(HttpStatusCode.Conflict, newJoin.StatusCode);
        Assert.Equal("PROTOCOL_MISMATCH", await CodeOfAsync(newJoin));

        // 旧客户端（不申报）→ 200
        var (guestLegacy, _) = await ServerTest.RegisterUserAsync(client);
        var legacyJoin = await ServerTest.Authorized(client, guestLegacy)
            .PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/join", new { });
        Assert.Equal(HttpStatusCode.OK, legacyJoin.StatusCode);
    }

    [Fact]
    public async Task Start_LeasesOnlyProtocolMatchingInstance()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();

        // 只有旧（null）DS 在池：新协议房开局必须 409 NO_SERVER_AVAILABLE（保持 Waiting 可重试）
        await ServerTest.RegisterInstanceAsync(client, protocolId: null);
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, clientProtocolId: NewProtocol);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode, new { clientProtocolId = NewProtocol });
        await ServerTest.ReadyAsync(client, guestToken, roomCode);

        var noServer = await ServerTest.Authorized(client, hostToken).PostAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, noServer.StatusCode);
        Assert.Equal("NO_SERVER_AVAILABLE", await CodeOfAsync(noServer));

        // 新协议 DS 注册后（直接注册即入池）：同一房开局成功（connection 票据已签发）
        await ServerTest.RegisterInstanceAsync(client, protocolId: NewProtocol);
        var start = await ServerTest.StartRoomAsync(client, hostToken, roomCode);
        Assert.True(start.TryGetProperty("connection", out _), "协议匹配的实例必须可被租用并签发票据");
    }

    [Fact]
    public void ClassifyNoServer_ReportsProtocolMismatch_WhenAllFreshCandidatesMismatch()
    {
        // 有新鲜且容量足够的 Ready，但协议全部不匹配 → protocol-mismatch（P0-A 新归因）
        var mismatch = new ServerInstancePoolSummaryDto(1, 1, 0, 0, 0, 0, 0, 0, 8, ReadyFreshProtocolMismatch: 1);
        Assert.Equal("protocol-mismatch", ServerInstanceService.ClassifyNoServer(mismatch));

        // 部分匹配 → race-transient（存在可租实例，租用竞态）
        var partial = new ServerInstancePoolSummaryDto(2, 2, 0, 0, 0, 0, 0, 0, 8, ReadyFreshProtocolMismatch: 1);
        Assert.Equal("race-transient", ServerInstanceService.ClassifyNoServer(partial));

        // 无协议视角（旧调用）→ 行为不变
        var legacy = new ServerInstancePoolSummaryDto(1, 1, 0, 0, 0, 0, 0, 0, 8);
        Assert.Equal("race-transient", ServerInstanceService.ClassifyNoServer(legacy));
        var none = new ServerInstancePoolSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 8);
        Assert.Equal("no-process", ServerInstanceService.ClassifyNoServer(none));
    }

    [Fact]
    public void SummarizePool_CountsProtocolMismatch_OnlyAmongFreshCapacityOkReady()
    {
        ServerInstanceDiagnosticDto Row(string id, string state, bool fresh, int capacity, string? protocol) =>
            new(id, state, null, 0, capacity, fresh ? 0 : 9999, fresh, protocol);

        var summary = ServerInstanceService.SummarizePool(new[]
        {
            Row("match", "Ready", true, 16, NewProtocol),   // 匹配
            Row("wrong", "Ready", true, 16, OldProtocol),   // 容量足够但协议不匹配
            Row("wrongshort", "Ready", true, 2, OldProtocol), // 容量不足不计协议
            Row("stalewrong", "Ready", false, 16, OldProtocol), // 不新鲜不计
            Row("wrongreserved", "Reserved", true, 16, OldProtocol), // 非 Ready 不计
        }, 8, expectedProtocol: NewProtocol);

        Assert.Equal(3, summary.ReadyFresh);
        Assert.Equal(1, summary.ReadyFreshCapacityShort);
        Assert.Equal(1, summary.ReadyFreshProtocolMismatch);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
