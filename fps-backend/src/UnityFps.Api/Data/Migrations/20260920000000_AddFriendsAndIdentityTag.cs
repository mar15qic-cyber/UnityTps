using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    // 好友系统（2026-09-20 需求2）：UserAccount.IdentityTag（用户名#编码 查找，存量账号随机回填 4 位）、
    // UserAccount.LastSeenUtc（在线状态锚点）+ FriendRequest/Friendship 两表。
    // [DbContext]+[Migration] 成对标注（F02 先例）：缺一会被迁移发现静默跳过。
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920000000_AddFriendsAndIdentityTag")]
    public partial class AddFriendsAndIdentityTag : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityTag",
                table: "UserAccount",
                type: "varchar(8)",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenUtc",
                table: "UserAccount",
                type: "datetime(6)",
                nullable: true);

            // 存量账号回填随机 4 位编码（含前导零）；编码不要求全局唯一（用户名已全局唯一）
            migrationBuilder.Sql("UPDATE UserAccount SET IdentityTag = LPAD(FLOOR(RAND() * 10000), 4, '0');");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccount_NormalizedUsername_IdentityTag",
                table: "UserAccount",
                columns: new[] { "NormalizedUsername", "IdentityTag" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "FriendRequest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn),
                    FromUserId = table.Column<long>(type: "bigint", nullable: false),
                    ToUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FriendRequest", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_FriendRequest_FromUserId_ToUserId",
                table: "FriendRequest",
                columns: new[] { "FromUserId", "ToUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FriendRequest_ToUserId",
                table: "FriendRequest",
                column: "ToUserId");

            migrationBuilder.CreateTable(
                name: "Friendship",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    FriendId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friendship", x => new { x.UserId, x.FriendId });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Friendship_FriendId",
                table: "Friendship",
                column: "FriendId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FriendRequest");

            migrationBuilder.DropTable(
                name: "Friendship");

            migrationBuilder.DropIndex(
                name: "IX_UserAccount_NormalizedUsername_IdentityTag",
                table: "UserAccount");

            migrationBuilder.DropColumn(
                name: "LastSeenUtc",
                table: "UserAccount");

            migrationBuilder.DropColumn(
                name: "IdentityTag",
                table: "UserAccount");
        }
    }
}
