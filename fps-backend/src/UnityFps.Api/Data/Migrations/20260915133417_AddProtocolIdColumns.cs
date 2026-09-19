using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProtocolIdColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtocolId",
                table: "ServerInstance",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedProtocolId",
                table: "GameRoom",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtocolId",
                table: "ServerInstance");

            migrationBuilder.DropColumn(
                name: "ExpectedProtocolId",
                table: "GameRoom");
        }
    }
}
