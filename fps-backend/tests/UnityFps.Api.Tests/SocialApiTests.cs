using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Data;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class SocialApiTests
{
    [Fact]
    public async Task TwoAuthenticatedAccountsExchangeMessagesAndAcceptInviteWithoutCodeExposure()
    {
        using var factory = ServerApiFactory.WithConfig(new Dictionary<string,string?>());
        using var client = factory.CreateClient();
        var (a, an) = await ServerTest.RegisterUserAsync(client);
        var (b, bn) = await ServerTest.RegisterUserAsync(client);
        long aid, bid;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            aid = db.Users.Single(u => u.Username == an).Id;
            bid = db.Users.Single(u => u.Username == bn).Id;
            db.Friendships.AddRange(new Friendship { UserId=aid, FriendId=bid }, new Friendship { UserId=bid, FriendId=aid });
            db.Users.Single(u=>u.Id==bid).LastSeenUtc=DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var payload = new { clientMessageId = Guid.NewGuid().ToString(), body = "离线后仍可读取的消息" };
        var sent = await ServerTest.Authorized(client,a).PostAsJsonAsync($"/api/social/messages/{bid}",payload);
        Assert.Equal(HttpStatusCode.OK,sent.StatusCode);
        var first = await sent.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await ServerTest.Authorized(client,a).PostAsJsonAsync($"/api/social/messages/{bid}",payload);
        Assert.Equal(first.GetProperty("id").GetInt64(),(await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64());
        var inbox = await ServerTest.Authorized(client,b).GetFromJsonAsync<JsonElement>("/api/social/inbox");
        Assert.Equal(1,inbox.GetProperty("conversations")[0].GetProperty("unread").GetInt32());
        var created = await ServerTest.CreateRoomAsync(client,a);
        var roomId = created.GetProperty("room").GetProperty("roomId").GetInt64();
        var invitation = await ServerTest.Authorized(client,a).PostAsJsonAsync("/api/social/invitations",new { roomId, friendId=bid });
        Assert.Equal(HttpStatusCode.NoContent,invitation.StatusCode);
        inbox = await ServerTest.Authorized(client,b).GetFromJsonAsync<JsonElement>("/api/social/inbox");
        var invite = inbox.GetProperty("invitations")[0];
        Assert.DoesNotContain("roomCode",invite.GetRawText());
        var inviteId = invite.GetProperty("id").GetInt64();
        for (var i=0;i<2;i++)
        {
            var accepted = await ServerTest.Authorized(client,b).PostAsJsonAsync($"/api/social/invitations/{inviteId}/accept",new {});
            Assert.Equal(HttpStatusCode.OK,accepted.StatusCode);
            Assert.DoesNotContain("roomCode",await accepted.Content.ReadAsStringAsync());
        }
    }
}
