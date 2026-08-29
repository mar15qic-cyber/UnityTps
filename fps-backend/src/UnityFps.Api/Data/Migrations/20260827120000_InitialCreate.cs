using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260827120000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserAccount",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn),
                Username = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                NormalizedUsername = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                PasswordHash = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                LastLoginAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_UserAccount", x => x.Id));

        migrationBuilder.CreateTable(
            name: "PlayerProfile",
            columns: table => new
            {
                UserId = table.Column<long>(type: "bigint", nullable: false),
                Level = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                Xp = table.Column<int>(type: "int", nullable: false),
                SkillPoints = table.Column<int>(type: "int", nullable: false),
                UpDamage = table.Column<int>(type: "int", nullable: false),
                UpAmmoCap = table.Column<int>(type: "int", nullable: false),
                UpMaxHealth = table.Column<int>(type: "int", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlayerProfile", x => x.UserId);
                table.ForeignKey("FK_PlayerProfile_UserAccount_UserId", x => x.UserId, "UserAccount", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PlayerLoadout",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                PrimaryWeaponId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                SecondaryWeaponId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                ThrowableId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlayerLoadout", x => x.Id);
                table.ForeignKey("FK_PlayerLoadout_UserAccount_UserId", x => x.UserId, "UserAccount", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MatchRecord",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", Microsoft.EntityFrameworkCore.Metadata.MySqlValueGenerationStrategy.IdentityColumn),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                Kills = table.Column<int>(type: "int", nullable: false),
                Deaths = table.Column<int>(type: "int", nullable: false),
                Score = table.Column<int>(type: "int", nullable: false),
                XpEarned = table.Column<int>(type: "int", nullable: false),
                PlayedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MatchRecord", x => x.Id);
                table.ForeignKey("FK_MatchRecord_UserAccount_UserId", x => x.UserId, "UserAccount", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_UserAccount_NormalizedUsername", "UserAccount", "NormalizedUsername", unique: true);
        migrationBuilder.CreateIndex("IX_PlayerLoadout_UserId", "PlayerLoadout", "UserId", unique: true);
        migrationBuilder.CreateIndex("IX_MatchRecord_UserId_PlayedAtUtc", "MatchRecord", new[] { "UserId", "PlayedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MatchRecord");
        migrationBuilder.DropTable(name: "PlayerLoadout");
        migrationBuilder.DropTable(name: "PlayerProfile");
        migrationBuilder.DropTable(name: "UserAccount");
    }
}
