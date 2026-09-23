using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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
        // S1 level 8 的旧手枪瞄具奖励已下线，奖励轨保留等级空洞。
        Assert.Equal(14, root.GetProperty("rewards").GetArrayLength());
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
    public async Task RetiredClientRewardAndUpgradeEndpointsCannotMutateProfile()
    {
        var username = "settle_" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("token").GetString());

        var before = await client.GetStringAsync("/api/profile");
        var payload = new { clientMatchId = "retired-0001", kills = 20, deaths = 5, durationSeconds = 400, isWin = true };
        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync("/api/matches", payload);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        }
        Assert.Equal(before, await client.GetStringAsync("/api/profile"));
        var upgrade = await client.PutAsJsonAsync("/api/profile/upgrades", new { upDamage = 1 });
        Assert.False(upgrade.IsSuccessStatusCode);
        using var profile = JsonDocument.Parse(before);
        Assert.False(profile.RootElement.TryGetProperty("skillPoints", out _));
        Assert.False(profile.RootElement.TryGetProperty("upgrades", out _));

    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddDebug());
        // 测试宿主隔离（Gate A-4，2026-09-08 复审 §1.4）：与 ServerApiFactory 同范式——经
        // ConfigureAppConfiguration 注入（DbContext 选项构建时可读），普通 `dotnet test` 无需
        // 调用方预设任何隐藏环境变量；不再写进程级环境变量（会泄漏给同进程其它测试/工厂）。
        // 每个工厂实例唯一 InMemory 库名 → 并行工厂互不共享存储，消除跨工厂 Seed 竞争。
        // 生产环境缺 GameDb 连接串仍由 Program 顶层 fail-closed（本配置只在测试工厂注入）。
        var values = new Dictionary<string, string?>
        {
            ["Database:AllowInMemoryFallback"] = "true",
            ["Database:InMemoryName"] = "test-" + Guid.NewGuid().ToString("N"),
        };
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values!));
    }
}
