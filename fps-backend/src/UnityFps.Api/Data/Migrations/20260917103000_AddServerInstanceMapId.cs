using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    // Phase 8：手写迁移（仅加一列，模式同 AddProtocolIdColumns 先例）。
    // [Migration] 必须与 [DbContext] 成对出现（F02，2026-09-19 审计）：MigrationsAssembly
    // 只发现「带匹配 [DbContext(typeof(AppDbContext))] 的 [Migration]」类型，缺 DbContext
    // 标注时 Database.GetMigrations()/Migrate() 静默跳过该迁移 → 新库缺 MapId 列。
    [DbContext(typeof(AppDbContext))]
    [Migration("20260917103000_AddServerInstanceMapId")]
    public partial class AddServerInstanceMapId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MapId",
                table: "ServerInstance",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MapId",
                table: "ServerInstance");
        }
    }
}
