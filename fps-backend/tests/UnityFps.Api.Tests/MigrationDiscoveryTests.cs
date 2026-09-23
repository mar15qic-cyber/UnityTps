using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using Xunit;

namespace UnityFps.Api.Tests;

/// <summary>
/// F02（2026-09-19 四日审计）：两条 2026-09-17 手写迁移只标了 [Migration]，缺
/// [DbContext(typeof(AppDbContext))]——MigrationsAssembly 只发现「带匹配 DbContext
/// 标注」的迁移类型，GetMigrations()/Migrate() 静默跳过 → 全新库缺 ServerInstance.MapId
/// 与 UserAccount.TokenVersion，地图租用/认证查询 500。
/// GetMigrations() 是纯模型+程序集发现，不连接数据库（连接串+固定 ServerVersion 仅为构造选项）。
/// </summary>
public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void GetMigrations_IncludesBothHandwrittenSept17Migrations()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                "Server=localhost;Database=migration_discovery_probe;Uid=probe;Pwd=probe;",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        using var db = new AppDbContext(options);

        var migrations = db.Database.GetMigrations().ToList();

        Assert.Contains("20260917103000_AddServerInstanceMapId", migrations);
        Assert.Contains("20260917112000_AddUserTokenVersion", migrations);
        Assert.Contains("20260921090000_RemoveRetiredOpticRows", migrations);
        // Migrate() 按 ID 升序应用：发现集合必须保持有序（回归加列顺序）。
        Assert.Equal(migrations.OrderBy(x => x, StringComparer.Ordinal), migrations);
        // 两条必须排在既有最后一条 AddProtocolIdColumns 之后（ID 字典序）。
        Assert.True(string.CompareOrdinal(migrations[^1], "20260921090000_RemoveRetiredOpticRows") >= 0);
    }
}
