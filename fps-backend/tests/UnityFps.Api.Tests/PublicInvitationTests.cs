using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class PublicInvitationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(PublicTestSecurity.DevelopmentSigningKey, "abcdefghijklmnopqrstuvwxyza123456")]
    [InlineData("short", "short")]
    [InlineData("01234567890123456789012345678901", "01234567890123456789012345678901")]
    public void ProductionRejectsMissingDefaultWeakOrSharedKeys(string? jwt, string? server)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Jwt:SigningKey"] = jwt, ["ServerInstances:ServerKey"] = server }).Build();
        Assert.Throws<InvalidOperationException>(() => PublicTestSecurity.Validate(config, false));
    }

    [Fact]
    public async Task InviteOnlyRejectsRegistrationAndDisabledAccountCannotLoginOrUseExistingToken()
    {
        using var root = new ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string,string?> { ["Access:InviteOnly"] = "true" })));
        using var client = factory.CreateClient();
        var denied = await client.PostAsJsonAsync("/api/auth/register", new { username = "invited", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var session = await auth.CreateInvitedAsync(new RegisterRequest { Username = "invited", Password = "Password123!" }, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/profile")).StatusCode);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = db.Users.Single(u => u.Username == "invited"); user.Disabled = true; user.TokenVersion++;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { username = "invited", password = "Password123!" })).StatusCode);
    }

    [Fact]
    public async Task SelfRegistrationCreatesAccountAndRejectsDuplicateUsername()
    {
        using var root = new ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string,string?> { ["Access:InviteOnly"] = "false" })));
        using var client = factory.CreateClient();
        var request = new { username = "selfregistered", password = "Password123!" };
        var registered = await client.PostAsJsonAsync("/api/auth/register", request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
    }

    [Fact]
    public async Task LoginLimiterRejectsEleventhAttempt()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        for (int i=0;i<10;i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { username="missing", password="bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login", new { username="missing", password="bad" })).StatusCode);
    }
}
