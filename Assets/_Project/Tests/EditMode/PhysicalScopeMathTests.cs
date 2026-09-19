using System.Collections.Generic;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// P4 实体镜（I4b）直接风险用例：AdsFovMath.ScopeFov 共享公式（计划
    /// scopeFov = 2·atan(tan(worldFov/2)/mag)）、OpticLensMath 镜片推导（项目派生几何）
    /// 与 LensHalfAngle 钳制、OpticAimContext 放大率传递（配件/内置双源）与 IsPhysicalScope 门。
    /// </summary>
    public sealed class PhysicalScopeMathTests
    {
        // ---------------------------------------------------------------- ScopeFov

        [Test]
        public void ScopeFov_CompressesTanHalfAngleByMagnification()
        {
            float fov = AdsFovMath.ScopeFov(60f, 4f);
            float expected = 2f * Mathf.Atan(Mathf.Tan(30f * Mathf.Deg2Rad) / 4f) * Mathf.Rad2Deg;
            Assert.AreEqual(expected, fov, 0.001f);
            // 倍率语义：灵敏度缩放（焦距比）恰为 1/mag
            Assert.AreEqual(1f / 4f, AdsFovMath.SensitivityScale(60f, fov), 0.0001f);
        }

        [Test]
        public void ScopeFov_NoMagnification_PassesThrough()
        {
            Assert.AreEqual(60f, AdsFovMath.ScopeFov(60f, 0f), 0.001f);
            Assert.AreEqual(60f, AdsFovMath.ScopeFov(60f, 1f), 0.001f);
        }

        [Test]
        public void ScopeFov_HigherMagnification_NarrowerFov()
        {
            float threeX = AdsFovMath.ScopeFov(60f, 3f);
            float eightX = AdsFovMath.ScopeFov(60f, 8f);
            Assert.Greater(threeX, eightX);
            Assert.Less(eightX, threeX * 0.5f);
        }

        // ---------------------------------------------------------------- LensHalfAngle

        [Test]
        public void LensHalfAngle_MatchesAtanGeometry()
        {
            float angle = OpticLensMath.LensHalfAngleDegrees(0.05f, 0.016f);
            Assert.AreEqual(Mathf.Atan(0.016f / 0.05f) * Mathf.Rad2Deg, angle, 0.001f);
        }

        [Test]
        public void LensHalfAngle_ClampsExtremeDistance()
        {
            Assert.AreEqual(OpticLensMath.MaxLensHalfAngleDeg, OpticLensMath.LensHalfAngleDegrees(0.0001f, 0.016f), 0.001f);
            Assert.AreEqual(OpticLensMath.MinLensHalfAngleDeg, OpticLensMath.LensHalfAngleDegrees(10f, 0.016f), 0.001f);
            Assert.AreEqual(OpticLensMath.MaxLensHalfAngleDeg, OpticLensMath.LensHalfAngleDegrees(-1f, 0.016f), 0.001f);
        }

        // ---------------------------------------------------------------- 镜片推导

        [Test]
        public void TryDeriveLensFrame_DerivesRearFaceAxisAndRadius()
        {
            var bounds = new Bounds(new Vector3(-0.05f, 0.01f, 0f), new Vector3(0.1f, 0.03f, 0.032f));
            var eye = new Vector3(0.06f, 0.01f, 0f);
            bool ok = OpticLensMath.TryDeriveLensFrame(bounds, eye, out Vector3 lensPos, out float radius);
            Assert.IsTrue(ok);
            Assert.AreEqual(bounds.max.x + OpticLensMath.GlassSurfaceOffset, lensPos.x, 0.0001f);
            Assert.AreEqual(eye.y, lensPos.y, 0.0001f);
            Assert.AreEqual(eye.z, lensPos.z, 0.0001f);
            Assert.AreEqual(Mathf.Clamp(bounds.size.z * 0.45f, OpticLensMath.MinLensRadius, OpticLensMath.MaxLensRadius),
                radius, 0.0001f);
        }

        [Test]
        public void TryDeriveLensFrame_RejectsInvalidGeometry()
        {
            var bounds = new Bounds(new Vector3(-0.05f, 0.01f, 0f), new Vector3(0.1f, 0.03f, 0.032f));
            // 眼点横向脱出光轴截面 → 拒绝（脏校准不产生错位镜片）
            Assert.IsFalse(OpticLensMath.TryDeriveLensFrame(bounds, new Vector3(0.06f, 0.2f, 0f), out _, out _));
            // 眼点在镜体前方（挂点 -X 侧）→ 拒绝
            Assert.IsFalse(OpticLensMath.TryDeriveLensFrame(bounds, new Vector3(-0.2f, 0.01f, 0f), out _, out _));
            // 退化包围盒 → 拒绝
            Assert.IsFalse(OpticLensMath.TryDeriveLensFrame(
                new Bounds(Vector3.zero, new Vector3(0.001f, 0.001f, 0.001f)),
                new Vector3(0.01f, 0f, 0f), out _, out _));
        }

        // ---------------------------------------------------------------- 情境传递

        private static AttachmentAssetEntry Optic(OpticAimTier tier, float magnification)
            => new AttachmentAssetEntry
            {
                itemId = "attach.test.optic",
                slot = AttachmentSlotType.Optic,
                aimTier = tier,
                adsFovOverride = 28f,
                magnification = magnification,
            };

        [Test]
        public void Context_CarriesMagnificationFromEquipped()
        {
            var ctx = OpticAimContext.FromEquipped(new List<AttachmentAssetEntry> { Optic(OpticAimTier.LowZoom, 4f) });
            Assert.AreEqual(4f, ctx.Magnification);
            Assert.IsTrue(ctx.IsPhysicalScope);
        }

        [Test]
        public void Context_WithoutMagnification_NotPhysicalScope()
        {
            var ctx = OpticAimContext.FromEquipped(new List<AttachmentAssetEntry> { Optic(OpticAimTier.Holo, 0f) });
            Assert.AreEqual(0f, ctx.Magnification);
            Assert.IsFalse(ctx.IsPhysicalScope);
            Assert.IsFalse(OpticAimContext.None.IsPhysicalScope);
        }

        [Test]
        public void Context_BuiltInCarriesMagnification()
        {
            var builtIn = new BuiltInOpticDefinition
            {
                opticId = "builtin.weapon.sniper01",
                aimTier = OpticAimTier.HighZoom,
                adsFovOverride = 12f,
                magnification = 8f,
            };
            var ctx = OpticAimContext.Resolve(new List<AttachmentAssetEntry>(), builtIn);
            Assert.AreEqual(8f, ctx.Magnification);
            Assert.IsTrue(ctx.IsPhysicalScope);
            // 配件瞄具（任意非 None 档）覆盖内置
            var equipped = OpticAimContext.Resolve(new List<AttachmentAssetEntry> { Optic(OpticAimTier.Holo, 0f) }, builtIn);
            Assert.AreEqual(0f, equipped.Magnification);
        }
    }
}
