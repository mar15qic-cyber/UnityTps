using System.Reflection;
using Animancer;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class TPGripPoseValidationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void RemoteReloadPose_ReleasesEvenWithoutOwnerCompletionEvent()
        {
            var host = new GameObject("RemoteReloadPose");
            try
            {
                var ik = host.AddComponent<TPLeftHandIK>();
                var suppressed = typeof(TPLeftHandIK).GetField("_reloadSuppressed", Private);
                suppressed.SetValue(ik, true);
                typeof(TPLeftHandIK).GetField("_reloadPoseEndTime", Private).SetValue(ik, 12f);
                ik.ReleaseExpiredReloadPose(11.9f);
                Assert.That((bool)suppressed.GetValue(ik), Is.True);
                ik.ReleaseExpiredReloadPose(12f);
                Assert.That((bool)suppressed.GetValue(ik), Is.False);
            }
            finally { Object.DestroyImmediate(host); }
        }

        [TestCase(-89f)]
        [TestCase(-60f)]
        [TestCase(0f)]
        [TestCase(60f)]
        [TestCase(89f)]
        public void AllPlayableWeapons_BattlePoseReachesGripAndMatchesCameraPitch(float pitch)
        {
            int count = 0;
            foreach (var entry in WeaponAssetCatalog.LoadOrDefault().Entries)
            {
                if (!entry.IsLpfp || entry.definition == null) continue;
                count++;
                var player = PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab");
                try
                {
                    var model = player.transform.Find("TP_Model");
                    var animator = model.GetComponent<Animator>();
                    var animation = model.GetComponent<AnimancerComponent>();
                    var state = animation.Play(entry.definition.ThirdPersonLocomotion.Idle);
                    state.Time = .3f; animation.Evaluate(0f);
                    var source = entry.definition.ThirdPersonViewPrefab;
                    var gun = (GameObject)PrefabUtility.InstantiatePrefab(source, player.scene);
                    gun.transform.SetParent(animator.GetBoneTransform(HumanBodyBones.RightHand), false);
                    gun.transform.localPosition = source.transform.localPosition;
                    gun.transform.localRotation = source.transform.localRotation;
                    gun.transform.localScale = source.transform.localScale;
                    var target = gun.transform.Find("LeftHandTarget");
                    var muzzle = gun.transform.Find("Muzzle");
                    var swapper = model.GetComponent<TPWeaponMeshSwapper>();
                    typeof(TPWeaponMeshSwapper).GetField("_baseLeftHandTarget", Private).SetValue(swapper, target);
                    typeof(TPWeaponMeshSwapper).GetProperty("CurrentMuzzle").SetValue(swapper, muzzle);
                    var aim = model.GetComponent<TPAimDriver>();
                    typeof(TPAimDriver).GetMethod("Awake", Private).Invoke(aim, null);
                    aim.ApplyDirectionalPose(Quaternion.Euler(-pitch, 0, 0) * Vector3.forward, 0);
                    typeof(TPWeaponMeshSwapper).GetMethod("LateUpdate", Private).Invoke(swapper, null);
                    var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                    Assert.That(Vector3.Distance(hand.position, target.position), Is.LessThan(.001f), entry.itemId + " wrist contact");
                    Assert.That(Quaternion.Angle(hand.rotation, target.rotation), Is.LessThan(.1f), entry.itemId + " wrist rotation");
                    Assert.That(Mathf.Asin(muzzle.forward.y) * Mathf.Rad2Deg, Is.EqualTo(pitch).Within(.5f), entry.itemId + " pitch");
                }
                finally { PrefabUtility.UnloadPrefabContents(player); }
            }
            Assert.That(count, Is.EqualTo(16), "coverage must follow the playable catalog");
        }

        [Test]
        public void UziAttachedGrip_UsesWeaponWristFrameAndChangesSupportPose()
        {
            var definition = WeaponAssetCatalog.LoadOrDefault().FindDefinition("weapon.smg03");
            var gun = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab));
            try
            {
                var bare = gun.transform.Find("LeftHandTarget");
                var catalog = AttachmentAssetCatalog.LoadOrDefault();
                var view = gun.AddComponent<WeaponAttachmentView>();
                view.ApplyAttachments(catalog, definition.CatalogItemId,
                    new System.Collections.Generic.List<AttachmentAssetEntry> { catalog.Find("attach.lpw.grip.01") }, false);
                gun.GetComponent<TPGripPose>().CalibrateAttachmentTargets(view);
                var target = TPWeaponMeshSwapper.ResolveLeftHandTarget(view, bare);
                Assert.That(target, Is.Not.SameAs(bare));
                Assert.That(Quaternion.Angle(target.rotation, bare.rotation), Is.EqualTo(90f).Within(.2f),
                    "vertical grip and palm-up handguard need different wrist orientations");
                var position = gun.transform.InverseTransformPoint(target.position);
                Assert.That(position.x, Is.EqualTo(-.0636f).Within(.002f));
                Assert.That(position.z, Is.InRange(.04f, .075f), "wrist must sit behind the grip, with the palm around it");
            }
            finally { PrefabUtility.UnloadPrefabContents(gun); }
        }
    }
}
