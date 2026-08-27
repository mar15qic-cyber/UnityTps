using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using UnityFps.Api.Data;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// 可选的真实 MySQL 回归探针。未提供凭据时跳过，不会猜测密码或触碰开发库。
/// </summary>
public sealed class MySqlIntegrationTests
{
    [Fact(Skip = "仅在提供真实 MySQL 测试凭据后手动启用")]
    public async Task TestDatabase_MustHaveTestSuffix_AndSupportMigrationRoundTrip()
    {
        var connection = Environment.GetEnvironmentVariable("UNITY_FPS_MYSQL_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        var builder = new MySqlConnectionStringBuilder(connection);
        Assert.EndsWith("_test", builder.Database, StringComparison.OrdinalIgnoreCase);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection))
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        Assert.True(await db.Database.CanConnectAsync());
    }
}
