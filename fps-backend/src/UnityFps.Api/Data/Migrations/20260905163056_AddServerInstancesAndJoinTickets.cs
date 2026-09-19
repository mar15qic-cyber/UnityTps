using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerInstancesAndJoinTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ServerInstanceId",
                table: "GameRoom",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "GameRoom",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            // 审计 P1-3 历史回填：旧 client-hosted 房间没有实例绑定（ServerInstanceId 为 NULL），
            // 无法在 Dedicated 拓扑运行；冻结四态中只有 Closed 诚实表达"历史房间已终结"
            //（WaitingForServer/WaitingForPlayers 会把永远开不起来的死房挂进列表）。
            // 回填后不存在 Status='' 的历史行；列默认值 '' 仅供 AddColumn 存量初始化，
            // 新行一律由代码显式写入冻结状态。
            migrationBuilder.Sql(
                "UPDATE `GameRoom` SET `Status` = 'Closed' WHERE `Status` = '' OR `Status` IS NULL;");

            migrationBuilder.CreateTable(
                name: "ServerInstance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    InstanceId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Address = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    BuildVersion = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CurrentPlayers = table.Column<int>(type: "int", nullable: false),
                    RoomCode = table.Column<string>(type: "varchar(6)", maxLength: 6, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RegisteredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerInstance", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ServerJoinTicket",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    TicketHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ServerInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    RoomCode = table.Column<string>(type: "varchar(6)", maxLength: 6, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Username = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerJoinTicket", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerJoinTicket_ServerInstance_ServerInstanceId",
                        column: x => x.ServerInstanceId,
                        principalTable: "ServerInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_GameRoom_ServerInstanceId",
                table: "GameRoom",
                column: "ServerInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstance_InstanceId",
                table: "ServerInstance",
                column: "InstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstance_RoomCode",
                table: "ServerInstance",
                column: "RoomCode");

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstance_State_LastHeartbeatUtc",
                table: "ServerInstance",
                columns: new[] { "State", "LastHeartbeatUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ServerJoinTicket_ExpiresAtUtc",
                table: "ServerJoinTicket",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServerJoinTicket_RoomCode",
                table: "ServerJoinTicket",
                column: "RoomCode");

            migrationBuilder.CreateIndex(
                name: "IX_ServerJoinTicket_ServerInstanceId",
                table: "ServerJoinTicket",
                column: "ServerInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerJoinTicket_TicketHash",
                table: "ServerJoinTicket",
                column: "TicketHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GameRoom_ServerInstance_ServerInstanceId",
                table: "GameRoom",
                column: "ServerInstanceId",
                principalTable: "ServerInstance",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GameRoom_ServerInstance_ServerInstanceId",
                table: "GameRoom");

            migrationBuilder.DropTable(
                name: "ServerJoinTicket");

            migrationBuilder.DropTable(
                name: "ServerInstance");

            migrationBuilder.DropIndex(
                name: "IX_GameRoom_ServerInstanceId",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "ServerInstanceId",
                table: "GameRoom");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "GameRoom");
        }
    }
}
