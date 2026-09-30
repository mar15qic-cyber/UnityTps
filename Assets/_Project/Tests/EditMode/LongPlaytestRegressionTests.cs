using System.Linq;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class LongPlaytestRegressionTests
    {
        [Test]
        public void GrenadeExplosionUsesUrpParticleMaterials()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Throwables/FragExplosion.prefab");
            foreach (var renderer in prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                Assert.NotNull(renderer.sharedMaterial, renderer.name);
                foreach (var material in renderer.sharedMaterials.Where(m => m != null))
                    Assert.That(material.shader.name, Does.StartWith("GameFX/Particle"));
            }
        }

        [Test]
        public void AllNativeThrowsAreExportedBuildClips()
        {
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            foreach (var weapon in weapons.Entries.Where(e => e.IsLpfp && e.definition != null))
            {
                var clip = weapon.definition.FirstPersonAnimations.ThrowGrenade;
                Assert.NotNull(clip, weapon.itemId);
                Assert.False(clip.name.StartsWith("__preview__"), weapon.itemId);
                Assert.Greater(AnimationUtility.GetCurveBindings(clip).Length, 100, weapon.itemId);
            }
        }

        [TestCase("weapon.service_pistol")]
        [TestCase("weapon.handgun02")]
        [TestCase("weapon.handgun03")]
        [TestCase("weapon.handgun04")]
        public void PistolsAcceptBothNewOneTimesOptics(string id)
        {
            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            foreach (var item in new[] { "attach.rifle.optic", "attach.lpfp.optic.02" })
                Assert.True(AttachmentCompatibilityPolicy.IsAllowed(id, catalog.Find(item)), item);
            foreach (var item in new[] { "attach.lpfp.optic.01", "attach.lpfp.optic.03" })
                Assert.True(AttachmentCompatibilityPolicy.IsAllowed(id, catalog.Find(item)), item);
        }

        [Test]
        public void Mp5GripIsRejectedAtCatalogAndRuntime()
        {
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.False(weapons.Entries.Single(e => e.itemId == "weapon.smg05").HasSlot("Underbarrel"));
            Assert.False(AttachmentCompatibilityPolicy.IsAllowed("weapon.smg05",
                Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog").Find("attach.lpw.grip.01")));
        }

        [Test]
        public void AkRetainsIronSightsAndNoViewShowsDemoKnife()
        {
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var attachments = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            foreach (var weapon in weapons.Entries.Where(e => e.IsLpfp && e.definition != null))
            {
                var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(weapon.definition.FirstPersonViewPrefab));
                try
                {
                    var view = root.GetComponent<WeaponAttachmentView>() ?? root.AddComponent<WeaponAttachmentView>();
                    view.ApplyAttachments(attachments, weapon.itemId, new[] { attachments.Find("attach.lpfp.optic.01") }, false);
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == "knife" || t.name == "knife 1") Assert.False(t.gameObject.activeSelf, weapon.itemId);
                        if (weapon.itemId == "weapon.m4" && WeaponAttachmentView.IsStockIronSightsName(t.name))
                            Assert.True(t.gameObject.activeSelf, "AK iron sight suppressed");
                    }
                    view.Clear();
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        [Test]
        public void TacticalClampsFaceRailAndSeatsTouchSocket()
        {
            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            foreach (var id in new[] { "attach.lpw.tactical.laser", "attach.lpw.tactical.light" })
            {
                var e = catalog.Find(id);
                Assert.Less(Vector3.Dot(e.MountRotation * Vector3.up, Vector3.up), -.99f, id);
                var min = e.prefab.GetComponentsInChildren<MeshFilter>(true).SelectMany(m => m.sharedMesh.vertices.Select(v =>
                    e.MountRotation * e.prefab.transform.InverseTransformPoint(m.transform.TransformPoint(v)) + e.mountOffset)).Min(v => v.y);
                Assert.That(min, Is.EqualTo(0f).Within(.0002f), id);
            }
        }

        [Test]
        public void LobbyAllSixteenWeaponsTrackWristAcrossRelaxedAnimation()
        {
            var source = Resources.Load<GameObject>("UI/LobbyCharacter");
            var character = (GameObject)PrefabUtility.InstantiatePrefab(source);
            try
            {
                var animator = character.GetComponentInChildren<Animator>();
                var clip = Resources.Load<AnimationClip>("UI/LobbyRelaxed");
                var catalog = Resources.Load<LobbyWeaponGripCatalog>("UI/LobbyWeaponGripCatalog");
                Assert.AreEqual(16, catalog.grips.Length);
                foreach (var grip in catalog.grips)
                {
                    var weapon = new GameObject("GripProbe");
                    try
                    {
                        foreach (float time in new[] { 0f, 1f, 2f, 4f })
                        {
                            clip.SampleAnimation(character, time);
                            LobbyWeaponGripCatalog.Apply(grip, animator, weapon.transform);
                            var wrist = animator.GetBoneTransform(HumanBodyBones.RightHand);
                            Assert.Less(Vector3.Distance(wrist.position, weapon.transform.TransformPoint(grip.wrist)), .0001f, grip.weaponId);
                        }
                    }
                    finally { Object.DestroyImmediate(weapon); }
                }
            }
            finally { Object.DestroyImmediate(character); }
        }
    }
}
