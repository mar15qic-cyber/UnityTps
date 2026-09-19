using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// F15（2026-09-19 四日审计）：并发登录不得产生两个同时有效的 token。
/// 旧实现是「读出 TokenVersion → 内存 ++ → SaveChanges」，两个并发请求都读到 v 再各存
/// v+1，两个 token 均通过 tv 比对。修复后为事务内原子自增+回读：UPDATE 持行锁直到提交，
/// 并发登录串行化，各取得唯一递增版本；只有最后提交者签发的 token 与库值一致（即唯一有效）。
/// 载体：SQLite 文件库（真实关系库写锁语义）+ 两个独立 DbContext 真并发；InMemory 无并发
/// 语义不适用。MySQL 生产行锁与该写锁串行化同构；专用 MySQL 并发用例由集成测试环境另行授权。
/// </summary>
public sealed class ConcurrentLoginSingleActiveTests : IDisposable
{
    private readonly string dbPath = Path.Combine(
        Path.GetTempPath(), "unityfps-concurrent-login-" + Guid.NewGuid().ToString("N") + ".db");

    private AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            // Pooling=False：Microsoft.Data.Sqlite 默认池化会在 Dispose 后仍持有文件锁，
            // 建库/种子连接会阻塞后续跨连接写入（SQLITE_BUSY）。
            .UseSqlite("Data Source=" + dbPath + ";Default Timeout=30;Pooling=False")
            .Options);

    private static AuthService CreateService(AppDbContext db) => new(
        db,
        new JwtTokenService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
        new DemoProgressionRules());

    private static long TvClaimOf(string token) =>
        long.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .First(c => c.Type == "tv").Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TwoConcurrentLogins_ReceiveDistinctVersions_OnlyLastTokenMatchesDatabase()
    {
        const string username = "ConcurrentLogin";
        const string password = "Password123!";

        using (var seed = CreateContext())
        {
            await seed.Database.EnsureCreatedAsync();
            // 直接实体播种（不经过 RegisterAsync——其 409 映射会吞掉 SQLite 细节，且注册链路
            // 不是本测试主旨）；仅要求登录与 CreateSession 所需：凭据+Profile/Loadout。
            var user = new UserAccount
            {
                Username = username,
                NormalizedUsername = username.ToUpperInvariant(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4),
                CreatedAtUtc = DateTime.UtcNow,
                TokenVersion = 1,
            };
            user.Profile = new PlayerProfile { User = user, UpdatedAtUtc = DateTime.UtcNow };
            user.Loadout = new PlayerLoadout { User = user, UpdatedAtUtc = DateTime.UtcNow };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        } // 释放建库连接，避免与并发写竞争文件锁

        AuthService CreateFreshService() => CreateService(CreateContext());

        var loginA = Task.Run(() => CreateFreshService().LoginAsync(new LoginRequest { Username = username, Password = password }, default));
        var loginB = Task.Run(() => CreateFreshService().LoginAsync(new LoginRequest { Username = username, Password = password }, default));
        var sessionA = await loginA;
        var sessionB = await loginB;

        var tvA = TvClaimOf(sessionA.Token);
        var tvB = TvClaimOf(sessionB.Token);
        Assert.NotEqual(tvA, tvB); // 串行化自增：两个并发登录取得不同版本

        using var verify = CreateContext();
        // SQLite 的 == 大小写敏感（MySQL 默认 ai_ci 不敏感）：必须按归一化用户名查询。
        var finalVersion = await verify.Users.Where(x => x.NormalizedUsername == username.ToUpperInvariant())
            .Select(x => x.TokenVersion).SingleAsync();

        Assert.Equal(long.Max(tvA, tvB), finalVersion); // 库值 = 最后提交者
        Assert.Equal(1, new[] { tvA, tvB }.Count(tv => tv == finalVersion)); // 只有最后提交者的 token 有效
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }
}
