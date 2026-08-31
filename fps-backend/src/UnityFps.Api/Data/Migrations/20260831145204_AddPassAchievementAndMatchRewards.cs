using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPassAchievementAndMatchRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientMatchId",
                table: "MatchRecord",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "CoinsEarned",
                table: "MatchRecord",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsWin",
                table: "MatchRecord",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PassXpEarned",
                table: "MatchRecord",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AcquisitionSource",
                table: "CatalogItem",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AchievementDefinition",
                columns: table => new
                {
                    AchievementId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(240)", maxLength: 240, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TargetMetric = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TargetValue = table.Column<int>(type: "int", nullable: false),
                    PassXpReward = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchievementDefinition", x => x.AchievementId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PassReward",
                columns: table => new
                {
                    SeasonId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PassLevel = table.Column<int>(type: "int", nullable: false),
                    RewardType = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CoinsAmount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PassReward", x => new { x.SeasonId, x.PassLevel });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerAchievement",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    AchievementId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Progress = table.Column<int>(type: "int", nullable: false),
                    UnlockedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GrantedPassXp = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerAchievement", x => new { x.UserId, x.AchievementId });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerPass",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    SeasonId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PassXp = table.Column<int>(type: "int", nullable: false),
                    PassLevel = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerPass", x => new { x.UserId, x.SeasonId });
                    table.ForeignKey(
                        name: "FK_PlayerPass_UserAccount_UserId",
                        column: x => x.UserId,
                        principalTable: "UserAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerPassRewardGrant",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    SeasonId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PassLevel = table.Column<int>(type: "int", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerPassRewardGrant", x => new { x.UserId, x.SeasonId, x.PassLevel });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRecord_UserId_ClientMatchId",
                table: "MatchRecord",
                columns: new[] { "UserId", "ClientMatchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AchievementDefinition");

            migrationBuilder.DropTable(
                name: "PassReward");

            migrationBuilder.DropTable(
                name: "PlayerAchievement");

            migrationBuilder.DropTable(
                name: "PlayerPass");

            migrationBuilder.DropTable(
                name: "PlayerPassRewardGrant");

            migrationBuilder.DropIndex(
                name: "IX_MatchRecord_UserId_ClientMatchId",
                table: "MatchRecord");

            migrationBuilder.DropColumn(
                name: "ClientMatchId",
                table: "MatchRecord");

            migrationBuilder.DropColumn(
                name: "CoinsEarned",
                table: "MatchRecord");

            migrationBuilder.DropColumn(
                name: "IsWin",
                table: "MatchRecord");

            migrationBuilder.DropColumn(
                name: "PassXpEarned",
                table: "MatchRecord");

            migrationBuilder.DropColumn(
                name: "AcquisitionSource",
                table: "CatalogItem");
        }
    }
}
