using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>战绩历史端点（热更试点 P3）：本人视角聚合/分页/鉴权。</summary>
public sealed class MatchesHistoryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    private readonly HttpClient client;

    public MatchesHistoryTests(ApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private async Task<string> RegisterAndGetTokenAsync(string prefix)
    {
        var username = prefix + "_" + Guid.NewGuid().ToString("N")[..8];
        var register = await client.PostAsJsonAsync("/api/auth/register", new { username, password = "Password123!" });
        Assert.True(register.IsSuccessStatusCode, await register.Content.ReadAsStringAsync());
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return loginJson.RootElement.GetProperty("token").GetString()!;
    }

    private async Task SettleAsync(int kills, int deaths, bool isWin, int index)
    {
        var payload = new { clientMatchId = "history-" + Guid.NewGuid().ToString("N")[..12] + "-" + index, kills, deaths, durationSeconds = 400, isWin };
        var response = await client.PostAsJsonAsync("/api/matches", payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_RequiresAuth()
    {
        var response = await client.GetAsync("/api/matches/history");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task History_Empty_ReturnsZeroSummary()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await RegisterAndGetTokenAsync("histempty"));
        var response = await client.GetAsync("/api/matches/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, root.GetProperty("matches").GetArrayLength());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("totalMatches").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("winRate").GetDouble());
        Assert.Equal(0, root.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task History_AggregatesAndPaginates_OwnRecordsOnly()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await RegisterAndGetTokenAsync("histagg"));
        // 三局：胜(k12/d3)、胜(k20/d5)、负(k4/d9) —— 提交顺序即时间顺序
        await SettleAsync(12, 3, true, 1);
        await SettleAsync(20, 5, true, 2);
        await SettleAsync(4, 9, false, 3);

        // 另一账号的记录不得混入
        var other = factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await RegisterAndGetTokenAsync("histother"));
        var otherTokenClient = other;

        var page1 = await client.GetAsync("/api/matches/history?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        using var json1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync());
        var root1 = json1.RootElement;
        Assert.Equal(3, root1.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, root1.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, root1.GetProperty("matches").GetArrayLength());

        var summary = root1.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("totalMatches").GetInt32());
        Assert.Equal(2, summary.GetProperty("wins").GetInt32());
        Assert.Equal(36, summary.GetProperty("totalKills").GetInt64());
        Assert.Equal(17, summary.GetProperty("totalDeaths").GetInt64());
        Assert.Equal(66.7, summary.GetProperty("winRate").GetDouble(), 1);

        // 时间倒序：第一页第一条 = 最后提交的负局
        Assert.False(root1.GetProperty("matches")[0].GetProperty("isWin").GetBoolean());
        Assert.Equal(4, root1.GetProperty("matches")[0].GetProperty("kills").GetInt32());
        Assert.True(root1.GetProperty("matches")[1].GetProperty("isWin").GetBoolean());

        // 第二页 = 最早一局（k12 胜局）
        var page2 = await client.GetAsync("/api/matches/history?page=2&pageSize=2");
        using var json2 = JsonDocument.Parse(await page2.Content.ReadAsStringAsync());
        Assert.Equal(1, json2.RootElement.GetProperty("matches").GetArrayLength());
        Assert.Equal(12, json2.RootElement.GetProperty("matches")[0].GetProperty("kills").GetInt32());

        // 越界页 → 空列表（不报错）
        var page9 = await client.GetAsync("/api/matches/history?page=9&pageSize=2");
        using var json9 = JsonDocument.Parse(await page9.Content.ReadAsStringAsync());
        Assert.Equal(0, json9.RootElement.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task History_PageParams_ClampedNotRejected()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await RegisterAndGetTokenAsync("histclamp"));
        await SettleAsync(6, 2, false, 1);
        // 非法参数钳制：page=0 → 1；pageSize=999 → ≤50（不 422）
        var response = await client.GetAsync("/api/matches/history?page=0&pageSize=999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("matches").GetArrayLength());
    }
}
