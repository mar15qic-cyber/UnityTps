using System;
using System.Reflection;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 阶段 B socket/原厂镜门禁：Sniper01-03 的出厂镜根可被外镜抑制，
    /// 共享枪体 renderer 与外镜子树保持激活，卸下后恢复原 active 状态。
    /// </summary>
    public sealed class SniperStockScopeSuppressionTests
    {
        private GameObject root;
        private GameObject opticPrefab;

        [TearDown]
        public void TearDown()
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (opticPrefab != null) UnityEngine.Object.DestroyImmediate(opticPrefab);
        }

        [TestCase("sniper_01_scope")]
        [TestCase("sniper_02_scope")]
        [TestCase("sniper_03_scope")]
        public void SniperStockScope_OpticInstallHidesOnlyStockRootAndUnloadRestores(string stockScopeName)
        {
            var view = BuildFakeSniper(stockScopeName, out var stockScope, out var sharedGun);
            var optic = MakeExternalOptic(stockScopeName);

            view.ApplyAttachments(null, "weapon.sniper.test", new[] { optic });

            Assert.That(stockScope.activeInHierarchy, Is.False, stockScopeName + " 应隐藏出厂镜视觉根");
            Assert.That(sharedGun.activeInHierarchy, Is.True, "共享枪体 renderer 不得被出厂镜抑制误伤");
            Assert.That(view.Spawned, Has.Count.EqualTo(1));
            var spawnedScope = view.Spawned[0].transform.Find(stockScopeName);
            Assert.That(spawnedScope, Is.Not.Null, "外镜内部测试 scope 子树缺失");
            Assert.That(spawnedScope.gameObject.activeInHierarchy, Is.True, "外镜内部 scope 不得被隐藏");

            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("Destroy may not be called from edit mode"));
            view.ApplyAttachments(null, "weapon.sniper.test", Array.Empty<AttachmentAssetEntry>());

            Assert.That(stockScope.activeInHierarchy, Is.True, "卸下外镜必须恢复出厂镜 active 状态");
            Assert.That(sharedGun.activeInHierarchy, Is.True);
            Assert.That(view.Spawned, Is.Empty);
        }

        [Test]
        public void StockScopeName_IsRestrictedToSniper01To03Roots()
        {
            Assert.That(WeaponAttachmentView.IsStockScopeName("sniper_01_scope"), Is.True);
            Assert.That(WeaponAttachmentView.IsStockScopeName("SNIPER_02_SCOPE"), Is.True);
            Assert.That(WeaponAttachmentView.IsStockScopeName("Sniper_03_Scope"), Is.True);
            Assert.That(WeaponAttachmentView.IsStockScopeName("Scope_04_Model"), Is.False);
            Assert.That(WeaponAttachmentView.IsStockScopeName("shared_scope_renderer"), Is.False);
            Assert.That(WeaponAttachmentView.IsStockScopeName("Att_attach.lpfp.optic.01"), Is.False);
        }

        private WeaponAttachmentView BuildFakeSniper(string stockScopeName,
            out GameObject stockScope, out GameObject sharedGun)
        {
            root = new GameObject("SniperView");
            sharedGun = new GameObject("SniperSharedGun", typeof(MeshRenderer));
            sharedGun.transform.SetParent(root.transform, false);
            stockScope = new GameObject(stockScopeName, typeof(MeshRenderer));
            stockScope.transform.SetParent(root.transform, false);

            var socketObject = new GameObject("Attach_Optic");
            socketObject.transform.SetParent(root.transform, false);
            var socket = socketObject.AddComponent<AttachmentSocket>();
            var slot = typeof(AttachmentSocket).GetField("slot",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(slot, Is.Not.Null);
            slot.SetValue(socket, AttachmentSlotType.Optic);

            return root.AddComponent<WeaponAttachmentView>();
        }

        private AttachmentAssetEntry MakeExternalOptic(string stockScopeName)
        {
            opticPrefab = new GameObject("ExternalOptic");
            var internalScope = new GameObject(stockScopeName, typeof(MeshRenderer));
            internalScope.transform.SetParent(opticPrefab.transform, false);
            return new AttachmentAssetEntry
            {
                itemId = "attach.lpfp.optic.01",
                slot = AttachmentSlotType.Optic,
                prefab = opticPrefab,
            };
        }
    }
}
