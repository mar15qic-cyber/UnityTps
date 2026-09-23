using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 配件数值管线（Docs/21 Phase D）：
    /// MagazineSize 数量型合成（空源直通/Add/Multiply/下限 Clamp）+
    /// AttachmentAssetEntry 修饰符导出 + AttachmentStatModifierSource 聚合语义（Priority=0）。
    /// </summary>
    public sealed class AttachmentStatTests
    {
        private static WeaponStat BaseStat(int magSize = 30) => new WeaponStat
        {
            Damage = 26, Rpm = 600, MagSize = magSize, ReserveAmmo = 120,
            ReloadTime = 2.2f, Spread = 1.2f, MaxRange = 120, AdsFov = 50,
            Recoil = new RecoilProfileData { PitchDeg = 1.1f, YawDeg = 0.3f, FirstShotMultiplier = 1.25f, AdsRecoilMultiplier = 0.6f, ViewModelKickBack = 0.045f, RecoverySpeed = 6, SpringFrequency = 9, SpringDamping = 0.75f },
            Accuracy = new AccuracyProfileData { BaseHipSpread = 1.2f, BaseAdsSpread = 0.3f, BloomRecoverySpeed = 5 },
            Ballistic = new BallisticProfileData { PelletCount = 1 },
        };

        private static AttachmentAssetEntry Entry(string itemId, params (WeaponStatId stat, ModifierOperation op, float value)[] mods)
        {
            var entry = new AttachmentAssetEntry { itemId = itemId, slot = AttachmentSlotType.Magazine };
            foreach (var (stat, op, value) in mods)
                entry.modifiers.Add(new AttachmentModifierEntry { stat = stat, op = op, value = value });
            return entry;
        }

        [Test]
        public void MagazineSize_EmptySources_PassthroughBase()
        {
            var r = WeaponStatResolver.Resolve(BaseStat(30), null);
            Assert.That(r.MagazineSize, Is.EqualTo(30f).Within(1e-5f));
            var empty = WeaponStatResolver.Resolve(BaseStat(6), new List<IWeaponStatModifierSource>());
            Assert.That(empty.MagazineSize, Is.EqualTo(6f).Within(1e-5f));
        }

        [Test]
        public void MagazineSize_ExtendedMag_AddsRounds()
        {
            // 30 + 8 = 38（加长弹匣 Add 语义，数量型）
            var source = new AttachmentStatModifierSource();
            source.Reset(new[] { Entry("attach.rifle.magazine", (WeaponStatId.MagazineSize, ModifierOperation.Add, 8f)) });
            var r = WeaponStatResolver.Resolve(BaseStat(30), new[] { source });
            Assert.That(r.MagazineSize, Is.EqualTo(38f).Within(1e-4f));
        }

        [Test]
        public void MagazineSize_AddThenMultiply_Composes()
        {
            // (30 + 8) × 1.2 = 45.6 —— 与既有"加法先于乘法"规则一致
            var source = new AttachmentStatModifierSource();
            source.Reset(new[]
            {
                Entry("attach.a", (WeaponStatId.MagazineSize, ModifierOperation.Add, 8f),
                                    (WeaponStatId.MagazineSize, ModifierOperation.Multiply, 1.2f))
            });
            var r = WeaponStatResolver.Resolve(BaseStat(30), new[] { source });
            Assert.That(r.MagazineSize, Is.EqualTo(45.6f).Within(1e-4f));
        }

        [Test]
        public void MagazineSize_ClampsToOne()
        {
            // 数量型下限 1：不可能出现 0/负容量
            var source = new AttachmentStatModifierSource();
            source.Reset(new[] { Entry("attach.bad", (WeaponStatId.MagazineSize, ModifierOperation.Multiply, 0f)) });
            var r = WeaponStatResolver.Resolve(BaseStat(30), new[] { source });
            Assert.That(r.MagazineSize, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void AttachmentEntry_ExportsModifiersWithItemIdSource()
        {
            var entry = Entry("attach.lpw.grip.01",
                (WeaponStatId.VerticalRecoil, ModifierOperation.Multiply, 0.88f),
                (WeaponStatId.HorizontalRecoil, ModifierOperation.Multiply, 0.85f));
            var output = new List<WeaponStatModifier>();
            entry.CollectModifiers(output);
            Assert.AreEqual(2, output.Count);
            Assert.That(output.All(m => m.SourceId == "attach.lpw.grip.01"), Is.True);
            Assert.AreEqual(WeaponStatId.VerticalRecoil, output[0].Stat);
            Assert.AreEqual(0.88f, output[0].Value);
            Assert.AreEqual(ModifierOperation.Multiply, output[0].Op);
        }

        [Test]
        public void AttachmentSource_AggregatesAndResets()
        {
            var source = new AttachmentStatModifierSource();
            Assert.AreEqual(0, source.Priority);                 // 配件层固定 Priority=0（< 技能 10 < Buff 20）
            Assert.AreEqual("attachments", source.SourceId);

            source.Reset(new[]
            {
                Entry("attach.a", (WeaponStatId.MagazineSize, ModifierOperation.Add, 8f)),
                Entry("attach.b", (WeaponStatId.Spread, ModifierOperation.Multiply, 0.95f))
            });
            Assert.AreEqual(2, source.Equipped.Count);
            Assert.AreEqual(2, source.GetModifiers().Count);

            source.Reset(null);                                   // 换枪/卸空：整体清空
            Assert.AreEqual(0, source.Equipped.Count);
            Assert.AreEqual(0, source.GetModifiers().Count);

            source.Reset(new[] { null, Entry("attach.c") });    // null 条目安全跳过
            Assert.AreEqual(1, source.Equipped.Count);
        }

        [Test]
        public void AttachmentSource_PipesThroughResolver_NeutralWhenEmpty()
        {
            var source = new AttachmentStatModifierSource();
            source.Reset(new[] { Entry("attach.empty") });        // 无修饰符的配件（如战术手电）不改数值
            var r = WeaponStatResolver.Resolve(BaseStat(30), new[] { source });
            Assert.That(r.MagazineSize, Is.EqualTo(30f).Within(1e-5f));
            Assert.That(r.SpreadScale, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(r.VerticalRecoilDeg, Is.EqualTo(1.1f).Within(1e-5f));
        }

        [Test]
        public void ShippedOptics_DoNotSecretlyChangeBallisticSpread()
        {
            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            Assert.That(catalog, Is.Not.Null, "运行时配件目录必须可加载");

            var optics = catalog.Entries.Where(entry => entry != null && entry.slot == AttachmentSlotType.Optic).ToArray();
            Assert.That(optics, Is.Not.Empty, "目录必须包含基础瞄具");
            foreach (var optic in optics)
                Assert.That(optic.modifiers.Any(modifier => modifier.stat == WeaponStatId.Spread), Is.False,
                    $"瞄具 {optic.itemId} 不得暗改弹道散布；有镜/无镜必须共享同一准确度规则");
        }
    }
}
