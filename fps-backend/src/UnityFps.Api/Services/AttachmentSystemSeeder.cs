using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;

namespace UnityFps.Api.Services;

/// <summary>
/// 配件系统种子 v2（Docs/21 返工，2026-09-02）：
/// 1) 目录精简：12 个消音器口径副本实测为 2 形状×6 拷贝 → 3 个分族消音器（紧凑=手枪/SMG、重型=步枪/霰弹、经典=原生武器）；
/// 2) 兼容矩阵按"武器家族 × 槽位 × 瞄具档位"生成：狙击仅高倍镜、狙击无消音器（M82A1 等大口径制退器）、
///    手枪仅红点、步枪红点/全息/低倍、战术与下挂按护木挂点；
/// 3) 旧 12 个消音器目录项 IsActive=false 下架（Docs/15 §9：禁止物理删除，已购历史保留）。
/// </summary>
public static class AttachmentSystemSeeder
{
    private enum WClass { Pistol, Smg, Rifle, Shotgun, Sniper, Classic }   // Classic=无现代导轨的老枪
    private enum OTier { RedDot, Holo, LowZoom, HighZoom }

    /// <summary>45 把枪的家族与槽位能力（16 把 LPFP + 29 把 LPW；2026-09-02 网格实测重判）.</summary>
    private sealed record Gun(string WeaponId, WClass Class, bool Optic, bool Muzzle, bool Tactical, bool Underbarrel);

    private static readonly Gun[] Guns =
    [
        // —— 16 把 LPFP 原生 ——
        new("weapon.m4",             WClass.Rifle,   Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // AKM
        new("weapon.ak",             WClass.Rifle,   Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // M4A1
        new("weapon.service_pistol", WClass.Pistol,  Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: false), // 格洛克17
        new("weapon.rifle03",        WClass.Rifle,   Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // SCAR-L
        new("weapon.smg01",          WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // Vector
        new("weapon.smg02",          WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: false, Underbarrel: false), // P90
        new("weapon.shotgun01",      WClass.Shotgun, Optic: false, Muzzle: true,  Tactical: true,  Underbarrel: false), // M870
        new("weapon.sniper01",       WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // AWM
        new("weapon.sniper02",       WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // M82A1：无消音器（大口径制退器）
        new("weapon.handgun02",      WClass.Pistol,  Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // M1911
        // —— 新增 LPFP 枪械（沿用正式 FP/TP prefab 的已存在挂点；Magazine 为纯数值槽） ——
        new("weapon.handgun03",      WClass.Pistol,  Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // Handgun 03
        new("weapon.handgun04",      WClass.Pistol,  Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // Handgun 04
        new("weapon.smg03",          WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // SMG 03
        new("weapon.smg04",          WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // SMG 04
        new("weapon.smg05",          WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // SMG 05
        new("weapon.sniper03",       WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // Sniper 03
        // —— 29 把 LPW ——
        new("weapon.lpw.rifle.01",   WClass.Rifle,   Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // AKM II
        new("weapon.lpw.rifle.02",   WClass.Rifle,   Optic: true,  Muzzle: true,  Tactical: false, Underbarrel: false), // AUG
        new("weapon.lpw.rifle.03",   WClass.Rifle,   Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // G36
        new("weapon.lpw.rifle.04",   WClass.Rifle,   Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // M16A4
        new("weapon.lpw.rifle.05",   WClass.Rifle,   Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // M16A1
        new("weapon.lpw.rifle.06",   WClass.Rifle,   Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // M4A1 II
        new("weapon.lpw.pistol.01",  WClass.Pistol,  Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: false), // 格洛克 II
        new("weapon.lpw.pistol.02",  WClass.Pistol,  Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // M1911 II
        new("weapon.lpw.pistol.03",  WClass.Pistol,  Optic: true,  Muzzle: true,  Tactical: false, Underbarrel: false), // 沙漠之鹰
        new("weapon.lpw.pistol.04",  WClass.Pistol,  Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // 格洛克 III
        new("weapon.lpw.pistol.05",  WClass.Classic, Optic: false, Muzzle: false, Tactical: false, Underbarrel: false), // 转轮
        new("weapon.lpw.pistol.06",  WClass.Pistol,  Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // 微型 Uzi
        new("weapon.lpw.shotgun.01", WClass.Classic, Optic: false, Muzzle: false, Tactical: false, Underbarrel: false), // 汤姆森
        new("weapon.lpw.shotgun.02", WClass.Shotgun, Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: false), // M870 战术
        new("weapon.lpw.shotgun.03", WClass.Shotgun, Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // M870 猎鹿
        new("weapon.lpw.shotgun.04", WClass.Classic, Optic: false, Muzzle: false, Tactical: false, Underbarrel: false), // 双管
        new("weapon.lpw.shotgun.05", WClass.Shotgun, Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: false), // AA-12
        new("weapon.lpw.smg.01",     WClass.Smg,     Optic: false, Muzzle: true,  Tactical: false, Underbarrel: false), // MAC-10
        new("weapon.lpw.smg.02",     WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // MP5（原误判 G36C，更正）
        new("weapon.lpw.smg.03",     WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: false, Underbarrel: false), // P90 双弹匣
        new("weapon.lpw.smg.04",     WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: false, Underbarrel: false), // P90 II
        new("weapon.lpw.smg.05",     WClass.Shotgun, Optic: true,  Muzzle: true,  Tactical: false, Underbarrel: false), // KSG
        new("weapon.lpw.smg.06",     WClass.Smg,     Optic: true,  Muzzle: true,  Tactical: true,  Underbarrel: true),  // UMP45
        new("weapon.lpw.sniper.01",  WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // AWM II
        new("weapon.lpw.sniper.02",  WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // SVD
        new("weapon.lpw.sniper.03",  WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // M700
        new("weapon.lpw.sniper.04",  WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // M95
        new("weapon.lpw.sniper.05",  WClass.Classic, Optic: false, Muzzle: false, Tactical: false, Underbarrel: false), // M1887 杠杆
        new("weapon.lpw.sniper.06",  WClass.Sniper,  Optic: true,  Muzzle: false, Tactical: false, Underbarrel: false), // VSS：一体消音，无枪口槽
    ];

    /// <summary>16 个具体配件目录项（商城直购；消音器分族）.</summary>
    private static readonly (string ItemId, string SlotType, string DisplayName, string Description, string AssetKey, long Price, int Level, OTier? Tier)[] ConcreteCatalog =
    [
        // 枪口（3 分族）
        ("attach.lpw.muffler.01",  "Muzzle", "紧凑消音器", "紧凑型消音器；适配手枪与冲锋枪", "lpw/muffler/01", 2200, 3, null),
        ("attach.lpw.muffler.02",  "Muzzle", "重型消音器", "全长重型消音器；适配步枪与霰弹枪", "lpw/muffler/02", 2600, 4, null),
        ("attach.lpfp.muffler.01", "Muzzle", "经典消音器", "经典制式消音器；适配原生武器",   "lpfp/muffler/01", 2000, 1, null),
        // 瞄具（LPW 8 + LPFP 2；档位与 Unity AttachmentAssetCatalog 同源）
        ("attach.lpw.optic.01",   "Optic", "紧凑红点镜",     "开放式反射红点；开镜快、无放大", "lpw/optic/01", 2500, 3, OTier.RedDot),
        ("attach.lpw.optic.02",   "Optic", "全息瞄具 551",   "方形窗口全息瞄具；视野开阔",     "lpw/optic/02", 3000, 4, OTier.Holo),
        ("attach.lpw.optic.03",   "Optic", "紧凑全息瞄具",   "短镜体全息瞄具",                 "lpw/optic/03", 2800, 4, OTier.Holo),
        ("attach.lpw.optic.04",   "Optic", "4 倍战术瞄准镜", "4x 固定倍率棱镜镜；中距离压制",  "lpw/optic/04", 4500, 8, OTier.LowZoom),
        ("attach.lpw.optic.05",   "Optic", "封闭红点镜",     "封闭镜体红点；兼顾机瞄高度",     "lpw/optic/05", 2600, 3, OTier.RedDot),
        ("attach.lpw.optic.06",   "Optic", "微型红点镜",     "微型红点；最轻量化",             "lpw/optic/06", 2400, 3, OTier.RedDot),
        ("attach.lpw.optic.07",   "Optic", "高倍狙击镜",     "高倍率远距离狙击镜",             "lpw/optic/07", 7000, 12, OTier.HighZoom),
        ("attach.lpw.optic.08",   "Optic", "远射狙击镜",     "高倍率远射瞄准镜",               "lpw/optic/08", 6800, 12, OTier.HighZoom),
        ("attach.lpfp.optic.01",  "Optic", "3 倍战术瞄镜",   "3x 战术棱镜瞄镜",                "lpfp/optic/01", 4000, 6, OTier.LowZoom),
        ("attach.lpfp.optic.02",  "Optic", "全息瞄具 553",   "方形窗口全息瞄具（增强版）",     "lpfp/optic/02", 3200, 5, OTier.Holo),
        // 战术
        ("attach.lpw.tactical.laser", "Tactical", "激光指示器", "下挂激光指示模块；开镜对中提示", "lpw/tactical/laser", 1800, 3, null),
        ("attach.lpw.tactical.light", "Tactical", "战术手电",   "下挂照明模块",                   "lpw/tactical/light", 1500, 2, null),
        // 下挂
        ("attach.lpw.grip.01", "Underbarrel", "垂直前握把", "下挂垂直握把；提升操控稳定性", "lpw/grip/01", 3000, 6, null),
    ];

    /// <summary>家族 × 瞄具档位放行表.</summary>
    private static OTier[] AllowedTiers(WClass cls) => cls switch
    {
        WClass.Sniper  => [OTier.HighZoom],
        WClass.Rifle   => [OTier.RedDot, OTier.Holo, OTier.LowZoom],
        WClass.Smg     => [OTier.RedDot, OTier.Holo],
        WClass.Pistol  => [OTier.RedDot],
        WClass.Shotgun => [OTier.RedDot],
        _              => [],
    };

    /// <summary>家族 × 枪口消音器放行（紧凑=手枪/SMG；重型=步枪/霰弹；经典=仅原生枪）.</summary>
    private static string[] AllowedMuzzles(WClass cls, bool isNative) => cls switch
    {
        WClass.Pistol  => isNative ? ["attach.lpfp.muffler.01", "attach.lpw.muffler.01"] : ["attach.lpw.muffler.01"],
        WClass.Smg     => isNative ? ["attach.lpfp.muffler.01", "attach.lpw.muffler.01"] : ["attach.lpw.muffler.01"],
        WClass.Rifle   => isNative ? ["attach.lpfp.muffler.01", "attach.lpw.muffler.02"] : ["attach.lpw.muffler.02"],
        WClass.Shotgun => isNative ? ["attach.lpfp.muffler.01", "attach.lpw.muffler.02"] : ["attach.lpw.muffler.02"],
        _              => [],
    };

    private static bool IsNative(string weaponId) => !weaponId.StartsWith("weapon.lpw.", System.StringComparison.Ordinal);

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await SeedConcreteCatalogAsync(db, cancellationToken);
        await SaveSectionTolerantAsync(db, cancellationToken);
        await SeedCompatMatrixAsync(db, cancellationToken);
        await SaveSectionTolerantAsync(db, cancellationToken);
    }

    private static async Task SaveSectionTolerantAsync(AppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    private static async Task SeedConcreteCatalogAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.CatalogItems.ToDictionaryAsync(x => x.ItemId, ct);
        foreach (var (itemId, slotType, displayName, description, assetKey, price, level, _) in ConcreteCatalog)
        {
            if (!existing.TryGetValue(itemId, out var item))
                db.CatalogItems.Add(new CatalogItem
                {
                    ItemId = itemId, ItemType = "Attachment", SlotType = slotType, Category = "Attachment",
                    DisplayName = displayName, Description = description, AssetKey = assetKey,
                    PriceCoins = price, UnlockLevel = level, IsActive = true,
                    IsImplemented = true, CalibrationKey = assetKey, AcquisitionSource = "Shop"
                });
            else
            {
                item.SlotType = slotType; item.DisplayName = displayName; item.Description = description;
                item.AssetKey = assetKey; item.PriceCoins = price; item.UnlockLevel = level;
                item.IsActive = true; item.IsImplemented = true; item.CalibrationKey = assetKey;
                item.AcquisitionSource = "Shop";
            }
        }
        // 下架 2026-09-01 版的 12 个消音器口径副本（含测试期购买历史，IsActive=false 保留外键完整性）
        foreach (var item in existing.Values)
        {
            if (item.ItemType != "Attachment" || item.AcquisitionSource != "Shop") continue;
            var stillOffered = ConcreteCatalog.Any(x => x.ItemId == item.ItemId);
            if (!stillOffered && item.IsActive) item.IsActive = false;
        }
    }

    /// <summary>矩阵生成：家族 × 槽位 × 档位；全部 IsImplemented=true（挂点已网格实测并目检 5/5 通过，2026-09-02）.</summary>
    private static async Task SeedCompatMatrixAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = BuildMatrix().ToDictionary(x => (x.WeaponItemId, x.AttachmentItemId));
        var existing = await db.AttachmentCompat.ToListAsync(ct);
        foreach (var row in existing.Where(x => !rows.ContainsKey((x.WeaponItemId, x.AttachmentItemId))))
            db.AttachmentCompat.Remove(row);
        foreach (var ((weaponId, attachmentId), row) in rows)
        {
            var current = existing.FirstOrDefault(x => x.WeaponItemId == weaponId && x.AttachmentItemId == attachmentId);
            if (current is null)
                db.AttachmentCompat.Add(new AttachmentCompat
                {
                    WeaponItemId = weaponId, AttachmentItemId = attachmentId, SlotType = row.SlotType,
                    IsImplemented = true, CalibrationKey = row.CalibrationKey
                });
            else
            {
                current.SlotType = row.SlotType;
                current.IsImplemented = true;
                current.CalibrationKey = row.CalibrationKey;
            }
        }
    }

    private static IEnumerable<AttachmentCompat> BuildMatrix()
    {
        var optics = ConcreteCatalog.Where(x => x.SlotType == "Optic").ToArray();
        var tacticals = ConcreteCatalog.Where(x => x.SlotType == "Tactical").Select(x => x.ItemId).ToArray();
        var grips = ConcreteCatalog.Where(x => x.SlotType == "Underbarrel").Select(x => x.ItemId).ToArray();

        foreach (var gun in Guns)
        {
            var native = IsNative(gun.WeaponId);
            var tiers = AllowedTiers(gun.Class);

            if (gun.Optic)
            {
                foreach (var optic in optics.Where(o => o.Tier.HasValue && tiers.Contains(o.Tier.Value)))
                    yield return Row(gun.WeaponId, optic.ItemId, "Optic");
                // 通行证通用瞄具按同档位放行（手枪版仅手枪，步枪版仅长枪）
                if (tiers.Contains(OTier.RedDot))
                    yield return Row(gun.WeaponId, gun.Class == WClass.Pistol ? "attach.pistol.optic" : "attach.rifle.optic", "Optic");
            }

            if (gun.Muzzle)
            {
                foreach (var muzzle in AllowedMuzzles(gun.Class, native))
                    yield return Row(gun.WeaponId, muzzle, "Muzzle");
                if (gun.Class != WClass.Classic)
                    yield return Row(gun.WeaponId, gun.Class == WClass.Pistol ? "attach.pistol.muzzle" : "attach.rifle.muzzle", "Muzzle");
            }

            if (gun.Tactical)
                foreach (var tactical in tacticals)
                    yield return Row(gun.WeaponId, tactical, "Tactical");

            if (gun.Underbarrel)
                foreach (var grip in grips)
                    yield return Row(gun.WeaponId, grip, "Underbarrel");

            // 弹匣：纯数值（不改模型），全枪开放
            yield return Row(gun.WeaponId, gun.Class == WClass.Pistol ? "attach.pistol.magazine" : "attach.rifle.magazine", "Magazine", calibration: "stat-only");
        }
    }

    private static AttachmentCompat Row(string weaponId, string attachmentId, string slotType, string calibration = "socket-v2") =>
        new() { WeaponItemId = weaponId, AttachmentItemId = attachmentId, SlotType = slotType, IsImplemented = true, CalibrationKey = calibration };
}
