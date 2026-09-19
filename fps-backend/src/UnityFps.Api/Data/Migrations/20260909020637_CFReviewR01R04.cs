using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CFReviewR01R04 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MatchGeneration",
                table: "ServerJoinTicket",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchId",
                table: "ServerJoinTicket",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedAtUtc",
                table: "RoomMatchRoster",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedByInstanceId",
                table: "RoomMatchResult",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchGeneration",
                table: "ServerJoinTicket");

            migrationBuilder.DropColumn(
                name: "MatchId",
                table: "ServerJoinTicket");

            migrationBuilder.DropColumn(
                name: "ReturnedAtUtc",
                table: "RoomMatchRoster");

            migrationBuilder.DropColumn(
                name: "ReportedByInstanceId",
                table: "RoomMatchResult");
        }
    }
}
