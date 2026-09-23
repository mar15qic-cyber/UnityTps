using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 瞄具眼光轴校准数据（AttachmentCalibration.OpticAimRows，校准工具写入）：
    /// 默认行（weaponItemId 空）兜底 / 组合行优先 / 无记录 false（机瞄降级）/ 删除回退 /
    /// 与配件贴合校准（rows）互不影响。
    /// </summary>
    public sealed class OpticAimCalibrationTests
    {
        private const string Optic = "attach.lpfp.optic.01";
        private const string WeaponA = "weapon.m4";
        private const string WeaponB = "weapon.ak";

        private static AttachmentCalibration NewCalibration()
            => ScriptableObject.CreateInstance<AttachmentCalibration>();

        [Test]
        public void EyePoint_NoRows_ReturnsFalse()
        {
            var cal = NewCalibration();
            Assert.IsFalse(cal.TryGetOpticEyePoint(WeaponA, Optic, out var v));
            Assert.AreEqual(Vector3.zero, v);
            Assert.IsFalse(cal.TryGetOpticEyePoint(null, Optic, out _));
            Assert.IsFalse(cal.TryGetOpticEyePoint(WeaponA, null, out _));
            Object.DestroyImmediate(cal);
        }

        [Test]
        public void EyePoint_DefaultRow_AppliesToAnyWeapon()
        {
            var cal = NewCalibration();
            var expected = new Vector3(0.08f, 0.035f, 0f);
            cal.SetOpticEyePoint(string.Empty, Optic, expected);

            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponA, Optic, out var gotA));
            Assert.AreEqual(expected, gotA);
            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponB, Optic, out var gotB));
            Assert.AreEqual(expected, gotB);
            Assert.IsTrue(cal.TryGetOpticEyePoint(null, Optic, out _));
            Object.DestroyImmediate(cal);
        }

        [Test]
        public void EyePoint_ComboRow_OverridesDefault()
        {
            var cal = NewCalibration();
            var dflt = new Vector3(0.08f, 0.035f, 0f);
            var combo = new Vector3(0.09f, 0.040f, 0.001f);
            cal.SetOpticEyePoint(string.Empty, Optic, dflt);
            cal.SetOpticEyePoint(WeaponA, Optic, combo);

            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponA, Optic, out var gotCombo));
            Assert.AreEqual(combo, gotCombo);
            // 其他武器仍走默认
            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponB, Optic, out var gotDefault));
            Assert.AreEqual(dflt, gotDefault);
            Object.DestroyImmediate(cal);
        }

        [Test]
        public void EyePoint_SetTwice_UpdatesInPlace()
        {
            var cal = NewCalibration();
            cal.SetOpticEyePoint(WeaponA, Optic, Vector3.one);
            var updated = new Vector3(0.05f, 0.02f, 0f);
            cal.SetOpticEyePoint(WeaponA, Optic, updated);

            Assert.AreEqual(1, cal.OpticAimRows.Count, "同键重复写入不得新增行");
            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponA, Optic, out var got));
            Assert.AreEqual(updated, got);
            Object.DestroyImmediate(cal);
        }

        [Test]
        public void EyePoint_RemoveCombo_FallsBackToDefault()
        {
            var cal = NewCalibration();
            cal.SetOpticEyePoint(string.Empty, Optic, Vector3.one);
            cal.SetOpticEyePoint(WeaponA, Optic, Vector3.one * 2f);

            Assert.IsTrue(cal.RemoveOpticEyePoint(WeaponA, Optic));
            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponA, Optic, out var got));
            Assert.AreEqual(Vector3.one, got);

            Assert.IsTrue(cal.RemoveOpticEyePoint(string.Empty, Optic));
            Assert.IsFalse(cal.TryGetOpticEyePoint(WeaponA, Optic, out _));
            Assert.IsFalse(cal.RemoveOpticEyePoint(WeaponA, Optic), "已删除再删返回 false");
            Object.DestroyImmediate(cal);
        }

        [Test]
        public void EyePoint_DoesNotInterfereWithMountCalibration()
        {
            var cal = NewCalibration();
            cal.Set(WeaponA, Optic, new Vector3(0.01f, 0f, 0f), new Vector3(0f, 5f, 0f));
            cal.SetOpticEyePoint(WeaponA, Optic, new Vector3(0.08f, 0.03f, 0f));

            Assert.IsTrue(cal.TryGet(WeaponA, Optic, out var pos, out var euler));
            Assert.AreEqual(new Vector3(0.01f, 0f, 0f), pos);
            Assert.AreEqual(new Vector3(0f, 5f, 0f), euler);
            Assert.IsTrue(cal.TryGetOpticEyePoint(WeaponA, Optic, out var eye));
            Assert.AreEqual(new Vector3(0.08f, 0.03f, 0f), eye);
            Object.DestroyImmediate(cal);
        }

        [Test]
        public void OpticAimContext_CarriesItemId()
        {
            var entry = new AttachmentAssetEntry
            {
                itemId = Optic,
                slot = AttachmentSlotType.Optic,
                aimTier = OpticAimTier.RedDot,
                adsFovOverride = 0f,
            };
            var ctx = OpticAimContext.FromEquipped(new System.Collections.Generic.List<AttachmentAssetEntry> { entry });
            Assert.AreEqual(Optic, ctx.ItemId);
            Assert.IsNull(OpticAimContext.None.ItemId);
        }
    }
}
