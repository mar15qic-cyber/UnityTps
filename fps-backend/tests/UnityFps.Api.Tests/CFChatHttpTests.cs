using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// G-CHAT HTTP 后端切片（Docs/28 Q07 HTTP 部分，Docs/27 §8）：
/// 发送/游标拉取、100 code point 上限（emoji 计 1）、换行/控制字符拒绝、System 无客户端发送路径、
/// 令牌桶 429、clientMessageId 去重、Team 投递层隔离、入房水位（不补发旧历史）、InMatch HTTP 关闭。
/// 聊天在 Waiting 窗口进行（CreateStartedRoomPendingAsync 产生的是就绪未开局的房间）。
/// </summary>
public sealed class CFChatHttpTests
{
    private static async Task<(HttpClient Client, string RoomCode, string HostToken, string GuestToken)>
        NewWaitingRoomAsync(ServerApiFactory factory, string mode = "TDM")
    {
        var client = factory.CreateClient();
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken, maxPlayers: 4, mode: mode,
            killTarget: mode == "KillRace" ? 20 : null);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));
        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        return (client, roomCode, hostToken, guestToken);
    }

    private static Task<HttpResponseMessage> TrySendAsync(HttpClient client, string token, string roomCode,
        string body, string channel = "All", string? clientMessageId = null) =>
        ServerTest.Authorized(client, token).PostAsJsonAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/chat", new
        {
            channel,
            body,
            clientMessageId = clientMessageId ?? "cm-" + Guid.NewGuid().ToString("N")[..16],
        });

    private static async Task<JsonElement> SendOkAsync(HttpClient client, string token, string roomCode,
        string body, string channel = "All", string? clientMessageId = null)
    {
        var response = await TrySendAsync(client, token, roomCode, body, channel, clientMessageId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> FetchAsync(HttpClient client, string token, string roomCode, ulong after = 0)
    {
        var response = await ServerTest.Authorized(client, token).GetAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/chat?after={after}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task SendAndFetchRoundtrip_WithSystemMessageOnJoin()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var (client, roomCode, hostToken, guestToken) = await NewWaitingRoomAsync(isolated);

        var sent = await SendOkAsync(client, hostToken, roomCode, "大家好");
        Assert.Equal("All", sent.GetProperty("channel").GetString());
        Assert.Equal("大家好", sent.GetProperty("body").GetString());
        Assert.Equal("Red", sent.GetProperty("teamId").GetString());
        Assert.True(sent.GetProperty("seq").GetUInt64() > 0);

        var feed = await FetchAsync(client, guestToken, roomCode);
        var bodies = feed.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("body").GetString()).ToArray();
        Assert.Contains("大家好", bodies);
        Assert.Contains(bodies, b => b!.Contains("加入了房间")); // 入房系统消息
        _ = guestToken;
    }

    [Fact]
    public async Task BodyLimitCountsCodePoints_EmojiCountsOne()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var (client, roomCode, hostToken, _) = await NewWaitingRoomAsync(isolated);

        var hundredEmoji = string.Join("", Enumerable.Repeat("🎮", 100)); // 100 code point = 200 UTF-16 单元
        var ok = await TrySendAsync(client, hostToken, roomCode, hundredEmoji);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var hundredOneEmoji = string.Join("", Enumerable.Repeat("🎮", 101));
        var rejected = await TrySendAsync(client, hostToken, roomCode, hundredOneEmoji);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("CHAT_REJECTED", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RejectsNewline_ControlChars_SystemChannel_Blank_AndBadCursor()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var (client, roomCode, hostToken, _) = await NewWaitingRoomAsync(isolated);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await TrySendAsync(client, hostToken, roomCode, "第一行\n第二行")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await TrySendAsync(client, hostToken, roomCode, "x\u0001y")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await TrySendAsync(client, hostToken, roomCode, "   ")).StatusCode);

        // System 频道无客户端发送路径
        var system = await TrySendAsync(client, hostToken, roomCode, "伪造系统消息", channel: "System");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, system.StatusCode);
        using var systemProblem = JsonDocument.Parse(await system.Content.ReadAsStringAsync());
        Assert.Equal("CHAT_REJECTED", systemProblem.RootElement.GetProperty("code").GetString());

        // 游标越界 → 422 CHAT_CURSOR_INVALID
        var badCursor = await ServerTest.Authorized(client, hostToken).GetAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/chat?after=99999");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCursor.StatusCode);
        using var problem = JsonDocument.Parse(await badCursor.Content.ReadAsStringAsync());
        Assert.Equal("CHAT_CURSOR_INVALID", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TokenBucketLimitsFourthQuickMessage_ButOtherMemberUnaffected()
    {
        // 回填放慢到 60s：测试服务器单请求耗时可达秒级，1/2s 的生产回填会跟上消耗无法触发 429
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>
        {
            ["ServerInstances:ChatTokenRefillSeconds"] = "60",
        });
        var (client, roomCode, hostToken, guestToken) = await NewWaitingRoomAsync(isolated);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await TrySendAsync(client, hostToken, roomCode, $"消息 {i}")).StatusCode);

        var limited = await TrySendAsync(client, hostToken, roomCode, "第四条");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var problem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("CHAT_RATE_LIMITED", problem.RootElement.GetProperty("code").GetString());

        // 桶按账号隔离：客人首条不受房主桶影响
        Assert.Equal(HttpStatusCode.OK, (await TrySendAsync(client, guestToken, roomCode, "客人发言")).StatusCode);
    }

    [Fact]
    public async Task DedupByClientMessageId_ReturnsOriginalMessage()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var (client, roomCode, hostToken, _) = await NewWaitingRoomAsync(isolated);
        var clientMessageId = "cm-dedup-" + Guid.NewGuid().ToString("N")[..12];

        var first = await SendOkAsync(client, hostToken, roomCode, "只发一次", clientMessageId: clientMessageId);
        var second = await SendOkAsync(client, hostToken, roomCode, "只发一次", clientMessageId: clientMessageId);
        Assert.Equal(first.GetProperty("seq").GetUInt64(), second.GetProperty("seq").GetUInt64()); // 同 seq = 原消息

        var feed = await FetchAsync(client, hostToken, roomCode);
        Assert.Equal(1, feed.GetProperty("messages").EnumerateArray()
            .Count(m => m.GetProperty("clientMessageId").GetString() == clientMessageId));
    }

    [Fact]
    public async Task TeamChannelIsFilteredAtDelivery()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var (client, roomCode, hostToken, guestToken) = await NewWaitingRoomAsync(isolated);

        var team = await SendOkAsync(client, hostToken, roomCode, "红队集合", channel: "Team");
        Assert.Equal("Team", team.GetProperty("channel").GetString());
        Assert.Equal("Red", team.GetProperty("teamId").GetString());

        // 房主（红队）可见；客人（蓝队）投递层过滤
        var hostFeed = await FetchAsync(client, hostToken, roomCode);
        Assert.Contains(hostFeed.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("body").GetString() == "红队集合");
        var guestFeed = await FetchAsync(client, guestToken, roomCode);
        Assert.DoesNotContain(guestFeed.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("body").GetString() == "红队集合");

        // 未选边（KillRace None）不能使用队聊
        var (ffaClient, ffaCode, ffaHost, _) = await NewWaitingRoomAsync(isolated, mode: "KillRace");
        var ffaTeam = await TrySendAsync(ffaClient, ffaHost, ffaCode, "无队队聊", channel: "Team");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ffaTeam.StatusCode);
    }

    [Fact]
    public async Task JoinWatermark_HidesPreJoinHistory()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var (hostToken, _) = await ServerTest.RegisterUserAsync(client);
        var snapshot = await ServerTest.CreateRoomAsync(client, hostToken);
        var roomCode = ServerTest.HostRoomCode(snapshot.GetProperty("room"));

        await SendOkAsync(client, hostToken, roomCode, "入房前的历史");

        var (guestToken, _) = await ServerTest.RegisterUserAsync(client);
        await ServerTest.JoinRoomAsync(client, guestToken, roomCode);
        await SendOkAsync(client, hostToken, roomCode, "入房后的新消息");

        var guestFeed = await FetchAsync(client, guestToken, roomCode);
        var bodies = guestFeed.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("body").GetString()).ToArray();
        Assert.DoesNotContain(bodies, b => b == "入房前的历史");
        Assert.Contains("入房后的新消息", bodies);
        Assert.Contains(bodies, b => b!.Contains("加入了房间"));
    }

    [Fact]
    public async Task InMatchHttpChatIsClosed()
    {
        using var isolated = ServerApiFactory.WithConfig(new Dictionary<string, string?>());
        var client = isolated.CreateClient();
        var instanceId = (await ServerTest.RegisterInstanceAsync(client)).GetProperty("instanceId").GetString()!;
        var (roomCode, hostToken, _, _) = await ServerTest.CreateStartedRoomAsync(client);
        // 进入 InMatch（Starting 窗口按契约 §12 允许 HTTP，系统消息为主）
        var beat = await ServerTest.SendWithKeyAsync(client, HttpMethod.Post,
            $"/api/server-instances/{instanceId}/heartbeat", ServerTest.ServerKey,
            new { roomCode, currentPlayers = 2, state = "InMatch" });
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);

        var send = await TrySendAsync(client, hostToken, roomCode, "局内 HTTP 消息");
        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode); // 局内只允许 Owner RPC
        var fetch = await ServerTest.Authorized(client, hostToken).GetAsync($"/api/rooms/{ServerTest.PublicRoomId(roomCode)}/chat?after=0");
        Assert.Equal(HttpStatusCode.Conflict, fetch.StatusCode);
    }
}
