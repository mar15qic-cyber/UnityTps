using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWeaponCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ThrowableId",
                table: "PlayerLoadout",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "SecondaryWeaponId",
                table: "PlayerLoadout",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "PrimaryWeaponId",
                table: "PlayerLoadout",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "PlayerLoadout",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "CatalogItem",
                columns: table => new
                {
                    ItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ItemType = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SlotType = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Category = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(240)", maxLength: 240, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AssetKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PriceCoins = table.Column<long>(type: "bigint", nullable: false),
                    UnlockLevel = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsImplemented = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CalibrationKey = table.Column<string>(type: "varchar(96)", maxLength: 96, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogItem", x => x.ItemId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerLoadoutAttachment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    LoadoutId = table.Column<long>(type: "bigint", nullable: false),
                    WeaponSlot = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachmentSlot = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttachmentItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerLoadoutAttachment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerLoadoutAttachment_PlayerLoadout_LoadoutId",
                        column: x => x.LoadoutId,
                        principalTable: "PlayerLoadout",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerWallet",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Coins = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerWallet", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_PlayerWallet_UserAccount_UserId",
                        column: x => x.UserId,
                        principalTable: "UserAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "WalletLedgerEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    DeltaCoins = table.Column<long>(type: "bigint", nullable: false),
                    BalanceAfter = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReferenceId = table.Column<string>(type: "varchar(96)", maxLength: 96, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletLedgerEntry", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PlayerInventoryItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    AcquiredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerInventoryItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerInventoryItem_CatalogItem_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItem",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlayerInventoryItem_UserAccount_UserId",
                        column: x => x.UserId,
                        principalTable: "UserAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ShopPurchase",
                columns: table => new
                {
                    PurchaseId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPriceCoins = table.Column<long>(type: "bigint", nullable: false),
                    TotalPriceCoins = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(96)", maxLength: 96, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopPurchase", x => x.PurchaseId);
                    table.ForeignKey(
                        name: "FK_ShopPurchase_CatalogItem_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItem",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopPurchase_UserAccount_UserId",
                        column: x => x.UserId,
                        principalTable: "UserAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerInventoryItem_ItemId",
                table: "PlayerInventoryItem",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerInventoryItem_UserId_ItemId",
                table: "PlayerInventoryItem",
                columns: new[] { "UserId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerLoadoutAttachment_LoadoutId_WeaponSlot_AttachmentSlot",
                table: "PlayerLoadoutAttachment",
                columns: new[] { "LoadoutId", "WeaponSlot", "AttachmentSlot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopPurchase_ItemId",
                table: "ShopPurchase",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopPurchase_UserId_IdempotencyKey",
                table: "ShopPurchase",
                columns: new[] { "UserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntry_UserId_CreatedAtUtc",
                table: "WalletLedgerEntry",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.Sql("UPDATE `PlayerLoadout` SET `PrimaryWeaponId` = CASE `PrimaryWeaponId` " +
                "WHEN 'rifle.day3' THEN 'weapon.m4' WHEN 'rifle.02' THEN 'weapon.ak' WHEN 'rifle.03' THEN 'weapon.rifle03' " +
                "WHEN 'smg.01' THEN 'weapon.smg01' WHEN 'smg.02' THEN 'weapon.smg02' WHEN 'shotgun.01' THEN 'weapon.shotgun01' " +
                "WHEN 'sniper.01' THEN 'weapon.sniper01' WHEN 'sniper.02' THEN 'weapon.sniper02' ELSE `PrimaryWeaponId` END;");
            migrationBuilder.Sql("UPDATE `PlayerLoadout` SET `SecondaryWeaponId` = CASE `SecondaryWeaponId` " +
                "WHEN 'pistol.day2' THEN 'weapon.service_pistol' WHEN 'handgun.02' THEN 'weapon.handgun02' ELSE `SecondaryWeaponId` END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerInventoryItem");

            migrationBuilder.DropTable(
                name: "PlayerLoadoutAttachment");

            migrationBuilder.DropTable(
                name: "PlayerWallet");

            migrationBuilder.DropTable(
                name: "ShopPurchase");

            migrationBuilder.DropTable(
                name: "WalletLedgerEntry");

            migrationBuilder.DropTable(
                name: "CatalogItem");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "PlayerLoadout");

            migrationBuilder.AlterColumn<string>(
                name: "ThrowableId",
                table: "PlayerLoadout",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "SecondaryWeaponId",
                table: "PlayerLoadout",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "PrimaryWeaponId",
                table: "PlayerLoadout",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
