using System.Collections.Generic;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 武器定义的 FP/TP 视图预制体引用完整性（2026-09-05 用户实测回归锁定）：
    /// TP_Weapon_*.prefab 被重构成"无外层根节点"结构（根=嵌套枪模实例）后，
    /// 定义资产里旧的 fileID 子资产引用静默解析为 null——TPWeaponMeshSwapper 收到
    /// null 直接 return（无日志），症状=第三人称没枪。本套件遍历目录全量断言
    /// FirstPersonViewPrefab / ThirdPersonViewPrefab 均可解析，杜绝静默断链。
    /// </summary>
    public sealed class WeaponDefinitionReferenceTests
    {
        [Test]
        public void EveryCatalogDefinition_HasResolvableFpAndTpViewPrefabs()
        {
            var catalog = LoadRuntimeCatalog();
            Assert.That(catalog, Is.Not.Null, "WeaponAssetCatalog 资产缺失");
            var entries = catalog.Entries;
            Assert.That(entries.Count, Is.GreaterThan(0), "目录为空");

            var broken = new List<string>();
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.itemId)) continue;
                if (!catalog.TryResolveDefinition(entry.itemId, out var definition) || definition == null)
                {
                    // 定义缺失的条目（如纯展示行）不在此套件范围内
                    continue;
                }
                if (definition.FirstPersonViewPrefab == null)
                    broken.Add(entry.itemId + " FirstPersonViewPrefab=null");
                if (definition.ThirdPersonViewPrefab == null)
                    broken.Add(entry.itemId + " ThirdPersonViewPrefab=null（TP 没枪事故类）");
            }
            Assert.That(broken, Is.Empty, "存在断链的视图预制体引用：\n" + string.Join("\n", broken));
        }

        private static WeaponAssetCatalog LoadRuntimeCatalog()
        {
            // 大厅运行时目录 = 场景 Bootstrap 引用的资产本体；EditMode 经 AssetDatabase 加载同名资产
            var guids = UnityEditor.AssetDatabase.FindAssets("t:WeaponAssetCatalog");
            Assert.That(guids.Length, Is.GreaterThan(0), "工程内没有任何 WeaponAssetCatalog 资产");
            foreach (var guid in guids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(path);
                if (catalog != null && catalog.Entries != null && catalog.Entries.Count > 0)
                    return catalog;
            }
            return null;
        }
    }
}
