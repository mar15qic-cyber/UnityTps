using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentCompat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttachmentCompat",
                columns: table => new
                {
                    WeaponItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachmentItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SlotType = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsImplemented = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CalibrationKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentCompat", x => new { x.WeaponItemId, x.AttachmentItemId });
                    table.ForeignKey(
                        name: "FK_AttachmentCompat_CatalogItem_AttachmentItemId",
                        column: x => x.AttachmentItemId,
                        principalTable: "CatalogItem",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttachmentCompat_CatalogItem_WeaponItemId",
                        column: x => x.WeaponItemId,
                        principalTable: "CatalogItem",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentCompat_AttachmentItemId",
                table: "AttachmentCompat",
                column: "AttachmentItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentCompat");
        }
    }
}
