using System.Collections.Generic;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 瞄具开镜情境 + FOV/灵敏度缩放（2026-09-03，真瞄准镜 ①② 步）：
    /// OpticAimContext.FromEquipped 解析语义（Optic 槽+分档才生效）+ EffectiveAdsFov 覆盖回退 +
    /// AdsFovMath 共享公式（FOV 插值端点/焦距比灵敏度）+ 目录 4 款变焦镜 FOV 覆盖接线。
    /// </summary>
    public sealed class OpticAimContextTests
    {
        private static AttachmentAssetEntry Optic(OpticAimTier tier, float fov = 0f)
            => new AttachmentAssetEntry { itemId = "attach.test.optic", slot = AttachmentSlotType.Optic, aimTier = tier, adsFovOverride = fov };

        [Test]
        public void Resolve_EquippedOpticOverridesBuiltIn()
        {
            var builtIn = new BuiltInOpticDefinition { opticId = "builtin.sniper", aimTier = OpticAimTier.HighZoom, adsFovOverride = 12f };
            var resolved = OpticAimContext.Resolve(new List<AttachmentAssetEntry> { Optic(OpticAimTier.Holo, 0f) }, builtIn);
            Assert.AreEqual(OpticAimTier.Holo, resolved.Tier);
            Assert.AreEqual("attach.test.optic", resolved.ItemId);
        }

        [Test]
        public void Resolve_UsesBuiltInWhenNoOpticEquipped()
        {
            var builtIn = new BuiltInOpticDefinition { opticId = "builtin.sniper", aimTier = OpticAimTier.HighZoom, adsFovOverride = 12f };
            var resolved = OpticAimContext.Resolve(new List<AttachmentAssetEntry>(), builtIn);
            Assert.AreEqual(OpticAimTier.HighZoom, resolved.Tier);
            Assert.AreEqual(12f, resolved.AdsFovOverride);
            Assert.AreEqual("builtin.sniper", resolved.ItemId);
        }

        private static AttachmentAssetEntry Muzzle()
            => new AttachmentAssetEntry { itemId = "attach.test.muzzle", slot = AttachmentSlotType.Muzzle };

        [Test]
        public void FromEquipped_NoOptic_ReturnsNone()
        {
            Assert.AreEqual(OpticAimTier.None, OpticAimContext.FromEquipped(null).Tier);
            Assert.AreEqual(OpticAimTier.None, OpticAimContext.FromEquipped(new List<AttachmentAssetEntry>()).Tier);
            Assert.AreEqual(OpticAimTier.None, OpticAimContext.FromEquipped(new List<AttachmentAssetEntry> { Muzzle() }).Tier);
        }

        [Test]
        public void FromEquipped_OpticWithTier_PickedWithFovOverride()
        {
            var ctx = OpticAimContext.FromEquipped(new List<AttachmentAssetEntry> { Muzzle(), Optic(OpticAimTier.LowZoom, 22f) });
            Assert.AreEqual(OpticAimTier.LowZoom, ctx.Tier);
            Assert.AreEqual(22f, ctx.AdsFovOverride);
        }

        [Test]
        public void FromEquipped_OpticTierNone_Ignored()
        {
            // 瞄准镜条目但分档 None = 不参与开镜情境（防御性）
            var ctx = OpticAimContext.FromEquipped(new List<AttachmentAssetEntry> { Optic(OpticAimTier.None) });
            Assert.AreEqual(OpticAimTier.None, ctx.Tier);
        }

        [Test]
        public void EffectiveAdsFov_OverrideWins_ZeroFallsBack()
        {
            Assert.AreEqual(22f, new OpticAimContext(OpticAimTier.LowZoom, 22f).EffectiveAdsFov(50f));
            Assert.AreEqual(50f, new OpticAimContext(OpticAimTier.RedDot, 0f).EffectiveAdsFov(50f));
            Assert.AreEqual(50f, OpticAimContext.None.EffectiveAdsFov(50f));
        }

        [Test]
        public void EvaluateCurrentFov_EndpointsAndClamp()
        {
            Assert.AreEqual(60f, AdsFovMath.EvaluateCurrentFov(60f, 22f, 0f), 1e-4f);
            Assert.AreEqual(22f, AdsFovMath.EvaluateCurrentFov(60f, 22f, 1f), 1e-4f);
            Assert.AreEqual(41f, AdsFovMath.EvaluateCurrentFov(60f, 22f, 0.5f), 1e-4f);
            // ads01 超界钳制（与 Mathf.Lerp 旧行为对齐：旧代码未钳，但 Ads01 由 MoveTowards 保证 [0,1]，钳制为防御）
            Assert.AreEqual(22f, AdsFovMath.EvaluateCurrentFov(60f, 22f, 2f), 1e-4f);
        }

        [Test]
        public void SensitivityScale_HipIsOne_ZoomSlowsDown()
        {
            Assert.AreEqual(1f, AdsFovMath.SensitivityScale(60f, 60f), 1e-4f);
            // 60° → 22°（4x 档）：tan(11°)/tan(30°) ≈ 0.3367
            float scale4x = AdsFovMath.SensitivityScale(60f, 22f);
            Assert.AreEqual(Mathf.Tan(11f * Mathf.Deg2Rad) / Mathf.Tan(30f * Mathf.Deg2Rad), scale4x, 1e-4f);
            // 60° → 12°（高倍档）≈ 0.182，比 4x 更慢；单调性
            float scale8x = AdsFovMath.SensitivityScale(60f, 12f);
            Assert.Less(scale8x, scale4x);
            Assert.Greater(scale8x, 0.05f);
            // 配置异常（currentFov > hipFov）不变快
            Assert.AreEqual(1f, AdsFovMath.SensitivityScale(60f, 90f));
        }

        [Test]
        public void Catalog_ZoomOptics_HaveFovOverride_OthersDont()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(
                "Assets/_Project/Resources/AttachmentAssetCatalog.asset");
            Assert.NotNull(catalog, "配件目录缺失");
            var expected = new Dictionary<string, float>
            {
                { "attach.lpw.optic.04", 28f },   // 4 倍战术瞄准镜（低倍统一 28°）
                { "attach.lpw.optic.07", 12f },   // 高倍狙击镜
                { "attach.lpw.optic.08", 12f },   // 远射狙击镜
                { "attach.lpfp.optic.01", 28f },  // 3 倍战术瞄镜
            };
            foreach (var kv in expected)
            {
                var entry = catalog.Find(kv.Key);
                Assert.NotNull(entry, $"{kv.Key} 缺失");
                Assert.AreEqual(kv.Value, entry.adsFovOverride, $"{kv.Key} FOV 覆盖");
                Assert.That(entry.aimTier == OpticAimTier.LowZoom || entry.aimTier == OpticAimTier.HighZoom,
                    $"{kv.Key} 变焦档必须在 LowZoom/HighZoom");
            }
            foreach (var e in catalog.Entries)
            {
                if (e == null || e.slot != AttachmentSlotType.Optic) continue;
                if (e.aimTier == OpticAimTier.RedDot || e.aimTier == OpticAimTier.Holo)
                    Assert.LessOrEqual(e.adsFovOverride, 1f, $"{e.itemId} 红点/全息不得有 FOV 覆盖");
            }
        }
    }
}
