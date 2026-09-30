using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CFWaitingRoomLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchId",
                table: "MatchRecord",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<ulong>(
                name: "ChatJoinSeq",
                table: "GameRoomMember",
                type: "bigint unsigned",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.AddColumn<bool>(
                name: "IsReady",
                table: "GameRoomMember",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenUtc",
                table: "GameRoomMember",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "TeamId",
                table: "GameRoomMember",
                type: "varchar(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "CurrentMatchId",
                table: "GameRoom",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "KillTarget",
                table: "GameRoom",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastMatchId",
                table: "GameRoom",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MapId",
                table: "GameRoom",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "MatchGeneration",
                table: "GameRoom",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "GameRoom",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "RoomVersion",
                table: "GameRoom",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "StateChangedAtUtc",
                table: "GameRoom",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "TimeLimitMinutes",
                table: "GameRoom",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RoomMatchResult",
                columns: table => new
                {
                    MatchId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RoomId = table.Column<long>(type: "bigint", nullable: false),
                    WinnerTeam = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DurationSeconds = table.Column<int>(type: "int", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PlayersJson = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RewardsAppliedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMatchResult", x => x.MatchId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RoomMatchRoster",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RoomId = table.Column<long>(type: "bigint", nullable: false),
                    MatchId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TeamId = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LeftAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMatchRoster", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoomMatchRoster_GameRoom_RoomId",
                        column: x => x.RoomId,
                        principalTable: "GameRoom",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRecord_MatchId",
                table: "MatchRecord",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_GameRoom_Status_StateChangedAtUtc",
                table: "GameRoom",
                columns: new[] { "Status", "StateChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomMatchResult_RoomId",
                table: "RoomMatchResult",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomMatchRoster_MatchId_UserId",
                table: "RoomMatchRoster",
                columns: new[] { "MatchId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoomMatchRoster_RoomId",
                table: "RoomMatchRoster",
                column: "RoomId");

            // 数据迁移（Docs/27 §9：不清数据、既有行语义连续）：
            // ① 旧状态值归一为 Waiting；② 既有成员不被心跳过期判定立刻清出；
            // ③ 既有房间的规则字段给 KillRace 语义缺省（旧行为即无队伍击杀竞赛）。
            migrationBuilder.Sql("""
                UPDATE `GameRoom` SET `Status` = 'Waiting' WHERE `Status` IN ('WaitingForPlayers', 'WaitingForServer');
                UPDATE `GameRoom` SET `Mode` = 'KillRace', `MapId` = 'arena', `KillTarget` = 20, `TimeLimitMinutes` = 10
                    WHERE `Mode` = '' OR `MapId` = '' OR `KillTarget` = 0 OR `TimeLimitMinutes` = 0;
                UPDATE `GameRoom` SET `StateChangedAtUtc` = `CreatedAtUtc` WHERE `StateChangedAtUtc` < '2000-01-01';
                UPDATE `GameRoomMember` SET `LastSeenUtc` = `JoinedAtUtc` WHERE `LastSeenUtc` < '2000-01-01';
                UPDATE `GameRoomMember` SET `TeamId` = 'None' WHERE `TeamId` = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomMatchResult");

            migrationBuilder.DropTable(
                name: "RoomMatchRoster");

            migrationBuilder.DropIndex(
                name: "IX_MatchRecord_MatchId",
                table: "MatchRecord");

            migrationBuilder.DropIndex(
                name: "IX_GameRoom_Status_StateChangedAtUtc",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "MatchId",
                table: "MatchRecord");

            migrationBuilder.DropColumn(
                name: "ChatJoinSeq",
                table: "GameRoomMember");

            migrationBuilder.DropColumn(
                name: "IsReady",
                table: "GameRoomMember");

            migrationBuilder.DropColumn(
                name: "LastSeenUtc",
                table: "GameRoomMember");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "GameRoomMember");

            migrationBuilder.DropColumn(
                name: "CurrentMatchId",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "KillTarget",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "LastMatchId",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "MapId",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "MatchGeneration",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "RoomVersion",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "StateChangedAtUtc",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "TimeLimitMinutes",
                table: "GameRoom");
        }
    }
}
