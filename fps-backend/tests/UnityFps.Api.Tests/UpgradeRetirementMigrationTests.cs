using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UnityFps.Api.Data.Migrations;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class UpgradeRetirementMigrationTests
{
    [Fact]
    public void MigrationOnlyDropsRetiredAttributes_LeavingProgressionWalletLoadoutAndHistoryUntouched()
    {
        var operations = new RetireAttributeUpgrades().UpOperations;
        Assert.Equal(4, operations.Count);
        var columns = operations.Select(op => Assert.IsType<DropColumnOperation>(op)).ToArray();
        Assert.All(columns, column => Assert.Equal("PlayerProfile", column.Table));
        Assert.Equal(new[] { "SkillPoints", "UpAmmoCap", "UpDamage", "UpMaxHealth" },
            columns.Select(column => column.Name).OrderBy(name => name).ToArray());
    }
}
