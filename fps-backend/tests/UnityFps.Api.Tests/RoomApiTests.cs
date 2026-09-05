using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 房间注册表 API 契约测试（Docs/19 N4）：创建/列表/加入/心跳/退出/解散。
/// 房间码语义：6 位无易混淆字符；心跳懒清理不在本测试范围（30s TTL）。
/// </summary>
public sealed class RoomApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient client;

    public RoomApiTests(ApiFactory factory) => client = factory.CreateClient();

    private async Task<(string Token, string Username)> RegisterAsync()
    {
        var username = "room_" + Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        using var json = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("token").GetString()!, username);
    }

    private HttpClient AuthorizedClient(string token)
    {
        var c = factoryClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpClient factoryClient() => new ApiFactory().CreateClient();

    [Fact]
    public async Task CreateRoomReturnsCodeAndAddress()
    {
        var (token, username) = await RegisterAsync();
        var auth = AuthorizedClient(token);

        var create = await auth.PostAsJsonAsync("/api/rooms", new { hostAddress = "192.168.1.10", hostPort = 7770, maxPlayers = 4 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var room = await create.Content.ReadFromJsonAsync<JsonElement>();
        var code = room.GetProperty("roomCode").GetString()!;
        Assert.Equal(6, code.Length);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", code); // 无 I/O/0/1
        Assert.Equal(username, room.GetProperty("hostUsername").GetString());
        Assert.Equal("192.168.1.10", room.GetProperty("hostAddress").GetString());
        Assert.Equal(4, room.GetProperty("maxPlayers").GetInt32());
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
    }

    [Fact]
    public async Task JoinByCodeIncrementsCountAndReturnsAddress()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "10.0.0.5", hostPort = 7770 });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var (guestToken, _) = await RegisterAsync();
        var guestClient = AuthorizedClient(guestToken);
        var join = await guestClient.PostAsync($"/api/rooms/{code}/join", null);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        var joined = await join.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, joined.GetProperty("joinedPlayers").GetInt32());
        Assert.Equal("10.0.0.5", joined.GetProperty("hostAddress").GetString());
    }

    [Fact]
    public async Task JoinUnknownRoomReturns404()
    {
        var (token, _) = await RegisterAsync();
        var auth = AuthorizedClient(token);
        var join = await auth.PostAsync("/api/rooms/ZZZZZZ/join", null);
        Assert.Equal(HttpStatusCode.NotFound, join.StatusCode);
    }

    [Fact]
    public async Task HostLeaveDissolvesRoom()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "1.2.3.4" });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var leave = await hostClient.PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.OK, leave.StatusCode);

        var list = await hostClient.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.DoesNotContain(list, r => r.GetProperty("roomCode").GetString() == code);
    }

    [Fact]
    public async Task HeartbeatKeepsRoomVisible()
    {
        var (token, _) = await RegisterAsync();
        var auth = AuthorizedClient(token);
        var create = await auth.PostAsJsonAsync("/api/rooms", new { hostAddress = "5.6.7.8" });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var beat = await auth.PostAsync("/api/rooms/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);

        var list = await auth.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Contains(list, r => r.GetProperty("roomCode").GetString() == code);
    }

    [Fact]
    public async Task UnauthenticatedRoomAccessIsRejected()
    {
        var response = await client.GetAsync("/api/rooms");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 退出对局 Phase E：Leave 幂等 / 人数真相 / 并发 ----

    [Fact]
    public async Task MemberLeaveInTwoPlayerRoomReducesToOneAndKeepsRoom()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "10.0.0.9", maxPlayers = 4 });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var (guestToken, _) = await RegisterAsync();
        var guestClient = AuthorizedClient(guestToken);
        await guestClient.PostAsync($"/api/rooms/{code}/join", null);

        var leave = await guestClient.PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.OK, leave.StatusCode);

        var list = await hostClient.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = Assert.Single(list, r => r.GetProperty("roomCode").GetString() == code);
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32());
        Assert.True(room.GetProperty("isOpen").GetBoolean());
    }

    [Fact]
    public async Task MemberLeaveInThreePlayerRoomOnlyDecrements()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "10.0.0.10", maxPlayers = 8 });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var guestTokens = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var (t, _) = await RegisterAsync();
            guestTokens.Add(t);
            await AuthorizedClient(t).PostAsync($"/api/rooms/{code}/join", null);
        }
        var before = await hostClient.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        Assert.Equal(3, before.Single(r => r.GetProperty("roomCode").GetString() == code).GetProperty("joinedPlayers").GetInt32());

        await AuthorizedClient(guestTokens[0]).PostAsync("/api/rooms/leave", null);

        var after = await hostClient.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = after.Single(r => r.GetProperty("roomCode").GetString() == code);
        Assert.Equal(2, room.GetProperty("joinedPlayers").GetInt32());
        Assert.True(room.GetProperty("isOpen").GetBoolean(), ">2 人局非房主退出仅减员，房间继续开放");
    }

    [Fact]
    public async Task LeaveReplayIsIdempotent()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "10.0.0.11", maxPlayers = 4 });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var (guestToken, _) = await RegisterAsync();
        var guestClient = AuthorizedClient(guestToken);
        await guestClient.PostAsync($"/api/rooms/{code}/join", null);

        var first = await guestClient.PostAsync("/api/rooms/leave", null);
        var replay = await guestClient.PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // 重复离开=幂等成功

        var list = await hostClient.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list.Single(r => r.GetProperty("roomCode").GetString() == code);
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32()); // 重放不得二次减员
    }

    [Fact]
    public async Task ConcurrentMemberLeavesAreAllApplied()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "10.0.0.12", maxPlayers = 8 });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var clients = new List<HttpClient>();
        for (var i = 0; i < 3; i++)
        {
            var (t, _) = await RegisterAsync();
            var c = AuthorizedClient(t);
            clients.Add(c);
            await c.PostAsync($"/api/rooms/{code}/join", null);
        }

        var leaves = await Task.WhenAll(clients.Select(c => c.PostAsync("/api/rooms/leave", null)));
        Assert.All(leaves, l => Assert.Equal(HttpStatusCode.OK, l.StatusCode));

        var list = await hostClient.GetFromJsonAsync<JsonElement[]>("/api/rooms");
        var room = list.Single(r => r.GetProperty("roomCode").GetString() == code);
        Assert.Equal(1, room.GetProperty("joinedPlayers").GetInt32()); // 并发退出后 Members.Count 为真相
    }

    [Fact]
    public async Task DuplicateJoinIsIdempotent()
    {
        var (hostToken, _) = await RegisterAsync();
        var hostClient = AuthorizedClient(hostToken);
        var create = await hostClient.PostAsJsonAsync("/api/rooms", new { hostAddress = "10.0.0.13", maxPlayers = 4 });
        var code = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roomCode").GetString()!;

        var (guestToken, _) = await RegisterAsync();
        var guestClient = AuthorizedClient(guestToken);
        var first = await guestClient.PostAsync($"/api/rooms/{code}/join", null);
        var replay = await guestClient.PostAsync($"/api/rooms/{code}/join", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // 重复加入（重入）=幂等返回当前房间

        var joined = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, joined.GetProperty("joinedPlayers").GetInt32()); // 重复加入不得重复计数
    }

    [Fact]
    public async Task LeaveWithoutTokenIsRejected()
    {
        var response = await client.PostAsync("/api/rooms/leave", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
