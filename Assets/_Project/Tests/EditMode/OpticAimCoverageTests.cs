using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase 5 数据锁（目录覆盖断言）：附件目录中每一款瞄具（Optic 槽 + 有分档）都必须有
    /// 眼点校准行（weaponItemId 空 = 缺省行），内置狙击出厂镜有逐枪组合行——否则
    /// FPWeaponMotion.TryResolveOpticEyeAim 全链"缺少眼点校准"降级机瞄（光轴对位失效）。
    /// 本测试锁死"批量工具不得再写出空 opticItemId 行"的回归（2026-09-17 数据断点事故）。
    /// </summary>
    public sealed class OpticAimCoverageTests
    {
        private static readonly string[] BuiltInSniperWeaponIds =
        {
            "weapon.sniper01", "weapon.sniper02", "weapon.sniper03",
        };

        [Test]
        public void CatalogOptics_AllHaveDefaultEyePoint()
        {
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            Assert.That(catalog, Is.Not.Null, "AttachmentAssetCatalog 缺失");
            Assert.That(catalog.Calibration, Is.Not.Null, "目录未引用 AttachmentCalibration");

            int opticCount = 0;
            foreach (var entry in catalog.Entries)
            {
                if (entry == null || entry.slot != AttachmentSlotType.Optic || entry.aimTier == OpticAimTier.None)
                    continue;
                opticCount++;
                bool found = catalog.Calibration.TryGetOpticEyePoint("weapon.ak", entry.itemId, out var eye);
                Assert.That(found, Is.True, $"瞄具 {entry.itemId} 缺缺省眼点行（opticAimRows 空键失效行回归？）");
                Assert.That(eye.x, Is.GreaterThan(0f), $"瞄具 {entry.itemId} 眼距非法（挂点 -X 前向 → 眼点 +x）");
            }
            Assert.That(opticCount, Is.GreaterThanOrEqualTo(11), "目录瞄具数量异常（应 ≥ 11 款）");
        }

        [Test]
        public void BuiltInSnipers_HaveComboEyePoint()
        {
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Calibration, Is.Not.Null);
            foreach (var weaponId in BuiltInSniperWeaponIds)
            {
                bool found = catalog.Calibration.TryGetOpticEyePoint(weaponId, $"builtin.{weaponId}", out var eye);
                Assert.That(found, Is.True, $"内置狙击 {weaponId} 缺出厂镜组合眼点行");
                Assert.That(eye.x, Is.GreaterThan(0f));
            }
        }
    }
}
