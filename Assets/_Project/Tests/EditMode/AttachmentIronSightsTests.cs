using System.Collections.Generic;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 配件装配——出厂机械瞄具抑制（2026-09-05 用户需求）：LPFP 枪模自带
    /// *_Iron_Sights 子件，装配拓展瞄准镜（LowZoom/HighZoom，有放大档）时必须隐藏，
    /// 否则机瞄柱遮挡镜内视野；红点/全息（无放大）不隐藏；卸下/换装恢复原状。
    /// 另锁定：挂点上实例化的配件子树绝不参与出厂件抑制（配件模型内部含 scope/iron
    /// 命名的部件不能被误隐藏）。
    /// </summary>
    public sealed class AttachmentIronSightsTests
    {
        private GameObject _root;
        private GameObject _opticPrefab;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_opticPrefab != null) Object.DestroyImmediate(_opticPrefab);
        }

        private (WeaponAttachmentView view, Transform ironsights, Transform stockScope) BuildFakeWeapon()
        {
            _root = new GameObject("FakeWeapon");
            var view = _root.AddComponent<WeaponAttachmentView>();

            var ironsights = new GameObject("Assault_Rifle_01_Iron_Sights");
            ironsights.transform.SetParent(_root.transform, false);
            new GameObject("Front_Post").transform.SetParent(ironsights.transform, false);

            var stockScope = new GameObject("Scope_01"); // 原生出厂瞄具网格（既有抑制路径）
            stockScope.transform.SetParent(_root.transform, false);

            var socketGo = new GameObject("Attach_Optic");
            socketGo.transform.SetParent(_root.transform, false);
            var socket = socketGo.AddComponent<AttachmentSocket>();
            var slotField = typeof(AttachmentSocket).GetField("slot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            slotField.SetValue(socket, AttachmentSlotType.Optic);

            return (view, ironsights.transform, stockScope.transform);
        }

        private AttachmentAssetEntry MakeOptic(OpticAimTier tier)
        {
            // 配件模型内部故意含 scope/iron 命名部件——锁定"配件子树不参与抑制"
            if (_opticPrefab == null)
            {
                _opticPrefab = new GameObject("Scope_08_Model");
                new GameObject("Scope_lense").transform.SetParent(_opticPrefab.transform, false);
                new GameObject("ironsight_shade").transform.SetParent(_opticPrefab.transform, false);
            }
            return new AttachmentAssetEntry
            {
                itemId = "attach.optic.scope_08",
                slot = AttachmentSlotType.Optic,
                aimTier = tier,
                prefab = _opticPrefab,
            };
        }

        private static List<GameObject> ActiveChildrenOf(Transform parent)
        {
            var active = new List<GameObject>();
            foreach (Transform child in parent)
                if (child.gameObject.activeSelf) active.Add(child.gameObject);
            return active;
        }

        // ---- 纯函数：档位判定 ----

        [Test]
        public void MagnifiedTiers_SuppressIronSights()
        {
            Assert.That(WeaponAttachmentView.ShouldSuppressIronSights(OpticAimTier.LowZoom), Is.True, "低倍镜（2-4x）遮挡镜内视野");
            Assert.That(WeaponAttachmentView.ShouldSuppressIronSights(OpticAimTier.HighZoom), Is.True, "高倍狙击镜遮挡镜内视野");
        }

        [Test]
        public void NonMagnifiedTiers_KeepIronSights()
        {
            Assert.That(WeaponAttachmentView.ShouldSuppressIronSights(OpticAimTier.None), Is.False);
            Assert.That(WeaponAttachmentView.ShouldSuppressIronSights(OpticAimTier.RedDot), Is.False, "红点无放大，机瞄不遮挡");
            Assert.That(WeaponAttachmentView.ShouldSuppressIronSights(OpticAimTier.Holo), Is.False, "全息无放大，机瞄不遮挡");
        }

        // ---- 纯函数：名字匹配（LPFP 实测命名变体） ----

        [Test]
        public void IronSightsName_MatchesLpfpVariants()
        {
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("Assault_Rifle_01_Iron_Sights"), Is.True);
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("Grenade_Launcher_01_Front_Iron_Sights"), Is.True);
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("Grenade_Launcher_01_Back_Iron_Sights"), Is.True);
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("sniper_03_iron_sights"), Is.True, "大小写不敏感");
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("ironsights"), Is.True, "无下划线变体");
        }

        [Test]
        public void IronSightsName_RejectsSpawnedAndUnrelated()
        {
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("Att_attach.optic.scope_08"), Is.False, "挂点实例化配件排除");
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("Attach_Optic"), Is.False);
            Assert.That(WeaponAttachmentView.IsStockIronSightsName("Scope_01"), Is.False, "出厂瞄具走 scope 抑制路径，不属机瞄");
            Assert.That(WeaponAttachmentView.IsStockIronSightsName(""), Is.False);
            Assert.That(WeaponAttachmentView.IsStockIronSightsName(null), Is.False);
        }

        // ---- 行为：装配/卸下 ----

        [Test]
        public void Apply_HighZoomOptic_HidesIronsightsAndStockScope_KeepsOpticSubtree()
        {
            var (view, ironsights, stockScope) = BuildFakeWeapon();
            var optic = MakeOptic(OpticAimTier.HighZoom);
            view.ApplyAttachments(null, "weapon.test", new[] { optic });

            Assert.That(ironsights.gameObject.activeSelf, Is.False, "装高倍镜必须隐藏机械瞄具");
            Assert.That(ironsights.Find("Front_Post").gameObject.activeSelf, Is.False);
            Assert.That(stockScope.gameObject.activeSelf, Is.False, "出厂瞄具抑制（既有行为）保持");

            // 配件本体与内部部件（含 scope/iron 命名）必须全部保持激活
            Assert.That(view.Spawned.Count, Is.EqualTo(1));
            var spawned = view.Spawned[0];
            Assert.That(spawned.name, Is.EqualTo("Att_" + optic.itemId));
            Assert.That(spawned.activeSelf, Is.True);
            Assert.That(ActiveChildrenOf(spawned.transform), Has.Count.EqualTo(2), "配件两个子件都应激活");
        }

        [Test]
        public void Apply_RedDotOptic_KeepsIronsights_ButStillHidesStockScope()
        {
            var (view, ironsights, stockScope) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[] { MakeOptic(OpticAimTier.RedDot) });

            Assert.That(ironsights.gameObject.activeSelf, Is.True, "红点无放大：机瞄保持显示");
            Assert.That(stockScope.gameObject.activeSelf, Is.False, "出厂瞄具抑制不区分档位（既有行为）");
        }

        [Test]
        public void Apply_NonOpticAttachment_DoesNotTouchIronsights()
        {
            var (view, ironsights, stockScope) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[]
            {
                new AttachmentAssetEntry { itemId = "attach.muzzle.heavy", slot = AttachmentSlotType.Muzzle, prefab = _opticPrefab != null ? _opticPrefab : new GameObject("MuzzleModel") },
            });
            Assert.That(ironsights.gameObject.activeSelf, Is.True);
            Assert.That(stockScope.gameObject.activeSelf, Is.True);
        }

        [Test]
        public void Reapply_EmptyAttachments_RestoresSuppressedChildren()
        {
            var (view, ironsights, stockScope) = BuildFakeWeapon();
            view.ApplyAttachments(null, "weapon.test", new[] { MakeOptic(OpticAimTier.HighZoom) });
            Assert.That(ironsights.gameObject.activeSelf, Is.False);

            view.ApplyAttachments(null, "weapon.test", System.Array.Empty<AttachmentAssetEntry>()); // 卸下全部
            Assert.That(ironsights.gameObject.activeSelf, Is.True, "卸下瞄具必须恢复机械瞄具");
            Assert.That(stockScope.gameObject.activeSelf, Is.True, "卸下瞄具必须恢复出厂瞄具");
        }
    }
}
