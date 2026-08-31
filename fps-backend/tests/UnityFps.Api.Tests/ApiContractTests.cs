using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class ApiContractTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient client;

    public ApiContractTests(ApiFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task RegisterLoginAndProtectedProfileRoundTrip()
    {
        var username = "user_" + Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var profile = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(username, (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());
    }

    [Fact]
    public async Task UnauthenticatedProfileIsRejected()
    {
        using var isolated = new ApiFactory().CreateClient();
        var response = await isolated.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PassAndAchievementsEndpointsExposeSeededContract()
    {
        var username = "pass_" + Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("token").GetString());

        var pass = await client.GetAsync("/api/pass");
        Assert.Equal(HttpStatusCode.OK, pass.StatusCode);
        using var passJson = JsonDocument.Parse(await pass.Content.ReadAsStringAsync());
        var root = passJson.RootElement;
        Assert.Equal("S1", root.GetProperty("seasonId").GetString());
        Assert.Equal(1, root.GetProperty("level").GetInt32());
        Assert.Equal(15, root.GetProperty("rewards").GetArrayLength());
        Assert.Equal(10, root.GetProperty("achievements").GetArrayLength());
        Assert.Equal("Coins", root.GetProperty("rewards")[0].GetProperty("rewardType").GetString());
        Assert.Equal(200, root.GetProperty("rewards")[0].GetProperty("coinsAmount").GetInt32());
        Assert.False(root.GetProperty("rewards")[0].GetProperty("granted").GetBoolean());

        var achievements = await client.GetAsync("/api/achievements");
        Assert.Equal(HttpStatusCode.OK, achievements.StatusCode);
        using var achJson = JsonDocument.Parse(await achievements.Content.ReadAsStringAsync());
        Assert.Equal(10, achJson.RootElement.GetArrayLength());
        Assert.Equal(300, achJson.RootElement[0].GetProperty("passXpReward").GetInt32());
    }

    [Fact]
    public async Task MatchSettlementRoundTripAwardsThreeCurrencies()
    {
        var username = "settle_" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("token").GetString());

        var payload = new { clientMatchId = "contract-settle-0001", kills = 20, deaths = 5, durationSeconds = 400, isWin = true };
        var first = await client.PostAsJsonAsync("/api/matches", payload);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstRoot = firstJson.RootElement;
        Assert.Equal(700, firstRoot.GetProperty("xpEarned").GetInt32());
        Assert.Equal(350, firstRoot.GetProperty("coinsEarned").GetInt32());
        Assert.Equal(1000, firstRoot.GetProperty("passXpEarned").GetInt32());
        Assert.Equal(3, firstRoot.GetProperty("passLevel").GetInt32());
        Assert.False(firstRoot.GetProperty("replayed").GetBoolean());

        var replay = await client.PostAsJsonAsync("/api/matches", payload);
        Assert.True(replay.IsSuccessStatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());

        var overload = await client.PostAsJsonAsync("/api/matches",
            new { clientMatchId = "contract-settle-0002", kills = 99, deaths = 0, durationSeconds = 100, isWin = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overload.StatusCode);
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddDebug());
        Environment.SetEnvironmentVariable("Database__AllowInMemoryFallback", "true");
    }
}
