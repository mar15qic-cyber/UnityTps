using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Gate A-5（2026-09-08 P0 追加复审 §1/§2）定向资产验证：P30（weapon.handgun04）TP 视图
    /// 资产链——此前报告按「prefab 未补」归因，复审确认 prefab/meta 已在盘且被双向引用，
    /// 但必须锁定引用可解析与挂点可见性（TPWeaponMeshSwapper 经 transform.Find("Muzzle")
    /// /Find("LeftHandTarget") 消费；配件表现经 AttachmentSocket 消费）。只读断言，不重建资产。
    /// 交付纪律：prefab/meta/Day3_Handgun04.asset/WeaponAssetCatalog.asset 均为未跟踪新资产，
    /// 收编时必须全部纳入版本管理（引用 GUID 依赖 meta）。
    /// </summary>
    public sealed class P30Handgun04AssetTests
    {
        private const string TpPrefabPath = "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_04.prefab";
        private const string DefinitionPath = "Assets/_Project/ScriptableObjects/Weapons/Day3_Handgun04.asset";

        [Test]
        public void TpPrefab_IsOnDisk_WithMuzzleLeftHandTargetAndAttachmentSocket()
        {
            Assert.That(System.IO.File.Exists(TpPrefabPath), Is.True, "TP_Weapon_Handgun_04.prefab 必须在盘");
            Assert.That(System.IO.File.Exists(TpPrefabPath + ".meta"), Is.True, "meta 必须随交付（GUID 引用依赖 meta）");

            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(TpPrefabPath);
            Assert.That(prefab, Is.Not.Null, "TP prefab 必须可加载（P30 TP 断链事故回归锁定）");
            Assert.That(prefab.transform.Find("Muzzle"), Is.Not.Null, "Muzzle 直接子节点缺失（TPWeaponFX/枪口特效依赖）");
            Assert.That(prefab.transform.Find("LeftHandTarget"), Is.Not.Null, "LeftHandTarget 直接子节点缺失（TPLeftHandIK 依赖）");

            var socket = prefab.GetComponentInChildren<AttachmentSocket>(true);
            Assert.That(socket, Is.Not.Null, "AttachmentSocket 缺失（配件表现依赖）");
            Assert.That(socket.Slot, Is.EqualTo(AttachmentSlotType.Muzzle),
                "P30 目录能力只有 Muzzle，TP 挂点槽位必须一致");
        }

        [Test]
        public void DefinitionAndCatalog_ResolveP30_ToTheSameTpPrefab()
        {
            var definition = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponDefinition>(DefinitionPath);
            Assert.That(definition, Is.Not.Null, "Day3_Handgun04 资产必须可加载");
            Assert.That(definition.CatalogItemId, Is.EqualTo("weapon.handgun04"));
            Assert.That(definition.ThirdPersonViewPrefab, Is.Not.Null,
                "thirdPersonViewPrefab 引用断链（TP 没枪事故类）");
            Assert.That(UnityEditor.AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab), Is.EqualTo(TpPrefabPath),
                "定义必须引用包装 prefab 本体（GUID b13d0bb8e364f80b5d12fbc4f0bedcad）");

            // 目录侧（DS build 解析前提：WeaponAssetCatalog 已迁入 Resources）
            var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(catalog, Is.Not.Null, "WeaponAssetCatalog 必须在 Resources 下可加载");
            Assert.That(catalog.TryGet("weapon.handgun04", out var entry) && entry != null, Is.True,
                "目录必须有 weapon.handgun04 条目");
            // D4-A.1（Gate A 复审 §2.2）：previewPrefab 引用本身必须可解析——Editor 下
            // FindPreviewPrefab 凭 previewPrefabPath 降级加载会掩盖断链，Player Build 无此降级。
            Assert.That(entry.previewPrefab, Is.Not.Null,
                "目录 previewPrefab 引用断链（旧 fileID 6573219461991808529 已重写为 wrapper 根）");
            Assert.That(UnityEditor.AssetDatabase.GetAssetPath(entry.previewPrefab), Is.EqualTo(TpPrefabPath),
                "目录 previewPrefab 必须与定义指向同一 wrapper（同一 GUID + 有效根锚点）");
            Assert.That(catalog.TryResolveDefinition("weapon.handgun04", out var resolved) && resolved != null, Is.True);
            Assert.That(UnityEditor.AssetDatabase.GetAssetPath(resolved.ThirdPersonViewPrefab), Is.EqualTo(TpPrefabPath),
                "目录解析出的定义与 Day3_Handgun04 必须指向同一 TP prefab（同一 GUID，同一根锚点）");
        }
    }
}
