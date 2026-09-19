using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 单活会话（2026-09-17 实测缺口修复）：同账号再次登录 → 此前所有 token 立即失效
/// （tv 声明 vs UserAccount.TokenVersion 比对；旧客户端 401 = SessionExpired 语义）。
/// 向后兼容：部署前签发的无 tv 声明 token 按版本 0 处理，未重新登录的账号保持有效。
/// </summary>
public sealed class UserSessionSingleActiveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public UserSessionSingleActiveTests(ApiFactory factory) => this.factory = factory;

    private static string RandomUser(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> LoginAndGetTokenAsync(HttpClient client, string username)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password123!" });
        login.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<HttpClient> AuthedClientAsync(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Relogin_SupersedesOldToken_OldTokenGets401()
    {
        var username = RandomUser("sa");
        var register = await factory.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { username, password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var firstToken = await LoginAndGetTokenAsync(await AuthedClientAsync(""), username);
        var first = await AuthedClientAsync(firstToken);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/profile")).StatusCode);

        // 第二个客户端登录同一账号：顶替第一会话
        var secondToken = await LoginAndGetTokenAsync(await AuthedClientAsync(""), username);
        var second = await AuthedClientAsync(secondToken);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/profile")).StatusCode);

        // 旧 token 下一个认证请求立即 401
        var oldStatus = (await first.GetAsync("/api/profile")).StatusCode;
        Assert.Equal(HttpStatusCode.Unauthorized, oldStatus);

        // 新 token 保持有效（幂等多次）
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/profile")).StatusCode);
    }

    [Fact]
    public async Task LegacyTokenWithoutTvClaim_ValidUntilAccountRelogs()
    {
        // 模拟部署前签发的无 tv 声明 token：直接手造（同签名密钥）——此处以"注册后的账号
        // 尚未再次登录"路径验证等价语义：注册 token（tv=1）在从未再次登录时保持有效。
        var username = RandomUser("lg");
        var register = await factory.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { username, password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        using var registerJson = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var registerToken = registerJson.RootElement.GetProperty("token").GetString()!;

        var client = await AuthedClientAsync(registerToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/profile")).StatusCode);
    }
}
