using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace UnityFps.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260930060000_AddThrowableSlots")]
public class AddThrowableSlots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("PlayerLoadoutThrowable", columns: table => new
        {
            LoadoutId = table.Column<long>(type: "bigint", nullable: false),
            SlotIndex = table.Column<int>(type: "int", nullable: false),
            ItemId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_PlayerLoadoutThrowable", x => new { x.LoadoutId, x.SlotIndex });
            table.ForeignKey("FK_PlayerLoadoutThrowable_PlayerLoadout_LoadoutId", x => x.LoadoutId, "PlayerLoadout", "Id", onDelete: ReferentialAction.Cascade);
        });
        // Three persisted NULL rows distinguish an intentionally empty bag from a legacy record.
        migrationBuilder.Sql("""
            INSERT INTO PlayerLoadoutThrowable (LoadoutId, SlotIndex, ItemId)
            SELECT p.Id, s.SlotIndex, CASE
                WHEN p.ThrowableId = 'throwable.frag_assault' THEN 'throwable.frag'
                WHEN p.ThrowableId = 'throwable.standard' AND s.SlotIndex = 0 THEN 'throwable.frag'
                WHEN p.ThrowableId = 'throwable.standard' AND s.SlotIndex = 1 THEN 'throwable.flash'
                WHEN p.ThrowableId = 'throwable.standard' AND s.SlotIndex = 2 THEN 'throwable.smoke'
                ELSE NULL END
            FROM PlayerLoadout p CROSS JOIN (SELECT 0 SlotIndex UNION ALL SELECT 1 UNION ALL SELECT 2) s;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PlayerLoadoutThrowable");
}
