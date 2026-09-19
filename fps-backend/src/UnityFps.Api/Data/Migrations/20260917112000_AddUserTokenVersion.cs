using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    // 单活会话（2026-09-17 实测缺口）：UserAccount.TokenVersion 列。存量账号回填 0
    //（旧 token 无 tv 声明=版本 0，与库值一致保持有效，直到该账号下次登录顶替）。
    // [DbContext]+[Migration] 成对标注（F02，2026-09-19 审计），否则迁移发现静默跳过。
    [DbContext(typeof(AppDbContext))]
    [Migration("20260917112000_AddUserTokenVersion")]
    public partial class AddUserTokenVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TokenVersion",
                table: "UserAccount",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokenVersion",
                table: "UserAccount");
        }
    }
}
