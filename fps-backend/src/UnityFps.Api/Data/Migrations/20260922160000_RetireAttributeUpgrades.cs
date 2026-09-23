using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace UnityFps.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260922160000_RetireAttributeUpgrades")]
public sealed class RetireAttributeUpgrades : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var column in new[] { "SkillPoints", "UpDamage", "UpAmmoCap", "UpMaxHealth" })
            migrationBuilder.DropColumn(column, "PlayerProfile");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Structural rollback only; retired point allocations are intentionally not reconstructed.
        foreach (var column in new[] { "SkillPoints", "UpDamage", "UpAmmoCap", "UpMaxHealth" })
            migrationBuilder.AddColumn<int>(column, "PlayerProfile", type: "int", nullable: false, defaultValue: 0);
    }
}
