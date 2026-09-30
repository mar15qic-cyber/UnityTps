using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations;

/// <summary>
/// CF 三背包（2026-09-30 背包系统 Phase A）：PlayerLoadout 增 BackpackIndex（存量行默认 0），
/// 唯一约束从 UserId 单列改为 (UserId, BackpackIndex) 复合。纯加列+索引重组，无数据搬迁。
/// 手写迁移（项目 SOP：dotnet-ef 设计时解析失败，迁移落库走手工 SQL + __EFMigrationsHistory 登记）。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260930020000_AddLoadoutBackpacks")]
public partial class AddLoadoutBackpacks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "BackpackIndex",
            table: "PlayerLoadout",
            type: "int",
            nullable: false,
            defaultValue: 0);

        // 顺序不可颠倒：UserId 外键依赖索引覆盖，必须先建复合索引（首列 UserId）再删旧单列索引，
        // 否则 MySQL 报 1553 "Cannot drop index: needed in a foreign key constraint"。
        migrationBuilder.CreateIndex(
            name: "IX_PlayerLoadout_UserId_BackpackIndex",
            table: "PlayerLoadout",
            columns: new[] { "UserId", "BackpackIndex" },
            unique: true);

        migrationBuilder.DropIndex(
            name: "IX_PlayerLoadout_UserId",
            table: "PlayerLoadout");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PlayerLoadout_UserId_BackpackIndex",
            table: "PlayerLoadout");

        migrationBuilder.DropColumn(
            name: "BackpackIndex",
            table: "PlayerLoadout");

        migrationBuilder.CreateIndex(
            name: "IX_PlayerLoadout_UserId",
            table: "PlayerLoadout",
            column: "UserId",
            unique: true);
    }
}
