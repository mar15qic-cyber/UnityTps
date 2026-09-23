using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>枪匠预览必须走与 FP/TP 相同的 Attach_* socket 解析路径。</summary>
    public sealed class WeaponPreviewAttachmentSocketTests
    {
        private GameObject controllerObject;
        private GameObject opticPrefab;

        [TearDown]
        public void TearDown()
        {
            if (controllerObject != null) Object.DestroyImmediate(controllerObject);
            if (opticPrefab != null) Object.DestroyImmediate(opticPrefab);
        }

        [Test]
        public void GunsmithPreview_ParentsOpticUnderNativeAttachOpticSocket()
        {
            var weaponPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab");
            Assert.That(weaponPrefab, Is.Not.Null);
            Assert.That(weaponPrefab.transform.Find("Attach_Optic"), Is.Not.Null);

            controllerObject = new GameObject("WeaponPreviewSocketTest", typeof(RectTransform), typeof(RawImage),
                typeof(WeaponPreviewController));
            var controller = controllerObject.GetComponent<WeaponPreviewController>();
            controller.Initialize(weaponPrefab);

            opticPrefab = new GameObject("PreviewOptic", typeof(MeshRenderer));
            var entry = new AttachmentAssetEntry
            {
                itemId = "attach.lpfp.optic.01",
                slot = AttachmentSlotType.Optic,
                prefab = opticPrefab,
            };

            controller.ApplyPreviewAttachments(null, "weapon.m4", new[] { entry });

            var view = controller.ModelInstance.GetComponent<WeaponAttachmentView>();
            Assert.That(view, Is.Not.Null);
            Assert.That(view.Spawned, Has.Count.EqualTo(1));
            Assert.That(view.Spawned[0].transform.parent.name, Is.EqualTo("Attach_Optic"));
            Assert.That(view.GetSocketTransform(AttachmentSlotType.Optic), Is.SameAs(view.Spawned[0].transform.parent));
        }
    }
}
