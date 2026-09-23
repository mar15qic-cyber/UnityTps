using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace UnityFps.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260922090000_AddAccountDisabled")]
public sealed class AddAccountDisabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>("Disabled", "UserAccount", type: "tinyint(1)", nullable: false, defaultValue: false);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("Disabled", "UserAccount");
}
