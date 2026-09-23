using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnityFps.Api.Data.Migrations;

/// <summary>
/// 阶段 A 数据清理：LPW 八款瞄具与旧手枪瞄具已经从正式目录下线。
/// 这是单向数据迁移；回滚不会凭空恢复已删除的目录/库存/购买历史。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260921090000_RemoveRetiredOpticRows")]
public partial class RemoveRetiredOpticRows : Migration
{
    private const string RetiredIds =
        "'attach.lpw.optic.01','attach.lpw.optic.02','attach.lpw.optic.03','attach.lpw.optic.04'," +
        "'attach.lpw.optic.05','attach.lpw.optic.06','attach.lpw.optic.07','attach.lpw.optic.08'," +
        "'attach.pistol.optic'";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 仅删除当前库中实际存在的行；迁移不会假定有外部备份或编造删除计数。
        migrationBuilder.Sql($"DELETE FROM `PlayerLoadoutAttachment` WHERE `AttachmentItemId` IN ({RetiredIds});");
        migrationBuilder.Sql($"DELETE FROM `AttachmentCompat` WHERE `AttachmentItemId` IN ({RetiredIds});");
        migrationBuilder.Sql($"DELETE FROM `PlayerInventoryItem` WHERE `ItemId` IN ({RetiredIds});");
        migrationBuilder.Sql($"DELETE FROM `ShopPurchase` WHERE `ItemId` IN ({RetiredIds});");
        migrationBuilder.Sql("DELETE FROM `PlayerPassRewardGrant` WHERE `SeasonId` = 'S1' AND `PassLevel` = 8;");
        migrationBuilder.Sql("DELETE FROM `PassReward` WHERE `SeasonId` = 'S1' AND `PassLevel` = 8;");
        migrationBuilder.Sql($"DELETE FROM `CatalogItem` WHERE `ItemId` IN ({RetiredIds});");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 单向清理迁移：旧目录项及玩家数据需要由正式业务重新授予，不能伪造回填。
    }
}
