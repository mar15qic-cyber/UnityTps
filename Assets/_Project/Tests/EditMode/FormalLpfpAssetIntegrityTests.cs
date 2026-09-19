using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// D4-A.3（Gate A 复审 §2.3）参数化资产门禁：正式可购买/可装备 LPFP 武器的
    /// Definition TP 引用、Catalog preview 引用、Muzzle/LeftHandTarget 挂点与
    /// AttachmentSocket 逐把可解析。背景：handgun03/smg03/smg04/smg05/sniper03 与 P30
    /// 同源断裂（引用 fileID 不在 wrapper 内 → Unity-null），已按 P30 方法重写为 wrapper
    /// 根；本套件防止 Editor 侧 previewPrefabPath 降级加载掩盖 Player Build 断链。
    /// 断言只读，不重建资产。
    /// </summary>
    public sealed class FormalLpfpAssetIntegrityTests
    {
        private static readonly string[] FormalItemIds =
        {
            "weapon.handgun03",
            "weapon.handgun04",
            "weapon.smg03",
            "weapon.smg04",
            "weapon.smg05",
            "weapon.sniper03",
        };

        private static WeaponAssetCatalog LoadCatalog()
        {
            // DS build 解析前提：目录本体必须能从 Resources 加载（LoadOrDefault 的主路径）
            var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(catalog, Is.Not.Null, "WeaponAssetCatalog 必须在 Resources 下可加载");
            return catalog;
        }

        [TestCaseSource(nameof(FormalItemIds))]
        public void FormalWeapon_DefinitionAndCatalog_ResolveTpWithRigNodes(string itemId)
        {
            var catalog = LoadCatalog();
            Assert.That(catalog.TryGet(itemId, out var entry) && entry != null, Is.True,
                $"{itemId} 必须在目录中（后端正式商品同源）");
            Assert.That(entry.previewPrefabPath, Is.Not.Empty, $"{itemId} previewPrefabPath 缺失");

            // ① Catalog previewPrefab：序列化引用本身可解析（Editor 降级加载掩盖不了 Player Build）
            Assert.That(entry.previewPrefab, Is.Not.Null, $"{itemId} 目录 previewPrefab 引用断链");
            Assert.That(UnityEditor.AssetDatabase.GetAssetPath(entry.previewPrefab),
                Is.EqualTo(entry.previewPrefabPath), $"{itemId} previewPrefab 必须等于 previewPrefabPath 指向的 wrapper");

            // ② Definition TP 引用：同一 wrapper、同一根锚点
            Assert.That(catalog.TryResolveDefinition(itemId, out var definition) && definition != null, Is.True,
                $"{itemId} 定义必须可解析");
            Assert.That(definition.ThirdPersonViewPrefab, Is.Not.Null, $"{itemId} 定义 thirdPersonViewPrefab 断链");
            Assert.That(UnityEditor.AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab),
                Is.EqualTo(entry.previewPrefabPath), $"{itemId} 定义与目录必须指向同一 TP wrapper");

            // ③ 挂点可见性契约：TPWeaponMeshSwapper.transform.Find 与附件表现消费
            var tp = definition.ThirdPersonViewPrefab;
            Assert.That(tp.transform.Find("Muzzle"), Is.Not.Null, $"{itemId} TP Muzzle 挂点缺失");
            Assert.That(tp.transform.Find("LeftHandTarget"), Is.Not.Null, $"{itemId} TP LeftHandTarget 缺失");
            var socket = tp.GetComponentInChildren<AttachmentSocket>(true);
            Assert.That(socket, Is.Not.Null, $"{itemId} TP AttachmentSocket 缺失");
            Assert.That(entry.slotCapabilities, Is.Not.Empty, $"{itemId} slotCapabilities 缺失（后端矩阵同源）");
            // 已声明能力的槽位必须有对应挂点（stat-only 槽位除外）
            foreach (var capability in entry.slotCapabilities)
            {
                if (capability == "Magazine") continue; // 纯数值配件，无挂点（项目规则）
                bool hasSocketForSlot = false;
                foreach (var s in tp.GetComponentsInChildren<AttachmentSocket>(true))
                    if (s != null && s.Slot.ToString() == capability) hasSocketForSlot = true;
                Assert.That(hasSocketForSlot, Is.True,
                    $"{itemId} 声明槽位 {capability} 但 TP wrapper 无对应 AttachmentSocket");
            }
        }
    }
}
