using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class RoomPrivacyTests
{
    [Fact]
    public async Task OnlyLeaderReceivesShareCodeAndOwnershipTransferChangesVisibility()
    {
        using var factory=ServerApiFactory.WithConfig(new Dictionary<string,string?>());
        using var client=factory.CreateClient();
        var (host,_)=await ServerTest.RegisterUserAsync(client);
        var created=await ServerTest.CreateRoomAsync(client,host);
        var room=created.GetProperty("room");var id=room.GetProperty("roomId").GetInt64();var code=room.GetProperty("roomCode").GetString()!;
        var (guest,_)=await ServerTest.RegisterUserAsync(client);
        var listing=await ServerTest.Authorized(client,guest).GetStringAsync("/api/rooms");
        Assert.DoesNotContain("roomCode",listing);Assert.DoesNotContain(code,listing);
        var joined=await ServerTest.Authorized(client,guest).PostAsync($"/api/rooms/{id}/join",null);
        Assert.Equal(HttpStatusCode.OK,joined.StatusCode);Assert.DoesNotContain("roomCode",await joined.Content.ReadAsStringAsync());
        var detail=await ServerTest.Authorized(client,guest).GetStringAsync($"/api/rooms/{id}");Assert.DoesNotContain("roomCode",detail);
        var old=await client.GetAsync($"/api/rooms/{code}");Assert.Equal(HttpStatusCode.NotFound,old.StatusCode);
        await ServerTest.Authorized(client,host).PostAsync("/api/rooms/leave",null);
        var promoted=await ServerTest.Authorized(client,guest).GetFromJsonAsync<JsonElement>($"/api/rooms/{id}");
        Assert.Equal(code,promoted.GetProperty("room").GetProperty("roomCode").GetString());
        var rejoin=await ServerTest.Authorized(client,host).PostAsJsonAsync("/api/rooms/join-by-code",new {roomCode=code});
        Assert.Equal(HttpStatusCode.OK,rejoin.StatusCode);Assert.DoesNotContain("roomCode",await rejoin.Content.ReadAsStringAsync());
    }
}
