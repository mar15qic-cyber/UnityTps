using System.Reflection;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class ArticulatedHitboxTests
    {
        private const string PlayerPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";

        [Test]
        public void HeadChestArmsAndFeet_FollowTheirAnimatedBones()
        {
            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                var adapter = player.GetComponent<PlayerNetworkAdapter>();
                typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(adapter, null);
                var model = player.transform.Find("TP_Model");
                var animator = model.GetComponent<Animator>();
                var hitbox = model.Find("BodyHitbox");
                var follower = hitbox.GetComponent<ArticulatedHitboxFollower>();
                Assert.That(follower, Is.Not.Null);
                Assert.That(follower.IsBound, Is.True, "production TP avatar must bind articulated volumes");

                AssertFollows(animator, follower, hitbox.Find("HeadHitbox"), HumanBodyBones.Head,
                    HumanBodyBones.Spine, Quaternion.Euler(45f, 0f, 0f));
                AssertFollows(animator, follower, hitbox.Find("UpperHitbox"), HumanBodyBones.Chest,
                    HumanBodyBones.Spine, Quaternion.Euler(35f, 0f, 0f));
                AssertFollows(animator, follower, hitbox.Find("LeftArmHitbox"), HumanBodyBones.LeftUpperArm,
                    HumanBodyBones.LeftUpperArm, Quaternion.Euler(0f, 0f, 40f));
                AssertFollows(animator, follower, hitbox.Find("LeftFootHitbox"), HumanBodyBones.LeftFoot,
                    HumanBodyBones.LeftLowerLeg, Quaternion.Euler(35f, 0f, 0f));
                AssertFollows(animator, follower, hitbox.Find("RightFootHitbox"), HumanBodyBones.RightFoot,
                    HumanBodyBones.RightLowerLeg, Quaternion.Euler(-35f, 0f, 0f));
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
        }

        [TestCase(-1f, 0f)]
        [TestCase(0f, 0f)]
        [TestCase(1f, 90f)]
        public void ProductionCameraEye_IsInsideExposedHeadHitbox(float lean, float yaw)
        {
            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                player.transform.position = new Vector3(6000, 0, 6000);
                player.transform.rotation = Quaternion.Euler(0, yaw, 0);
                var adapter = player.GetComponent<PlayerNetworkAdapter>();
                adapter.AlignEyePivot();
                typeof(PlayerNetworkAdapter).GetMethod("EnsureBodyHitbox", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(adapter, null);
                var body = player.transform.Find("TP_Model/BodyHitbox");
                body.GetComponent<ArticulatedHitboxFollower>().SetLean(lean);
                Vector3 eye = Game.Gameplay.Player.LeanProfile.Eye(player.transform.position, player.transform.rotation, lean);
                Vector3 displayedEye = player.transform.Find("CameraPivot").position
                    + player.transform.right * (lean * Game.Gameplay.Player.LeanProfile.EyeSideMeters);
                Assert.That(Vector3.Distance(eye, displayedEye), Is.LessThan(.001f));
                Physics.SyncTransforms();
                var head = body.Find("HeadHitbox").GetComponent<CapsuleCollider>();
                Assert.That(Vector3.Distance(head.ClosestPoint(eye), eye), Is.LessThan(.005f),
                    "a player exposing their camera must expose a hittable head volume");
                Assert.That(head.Raycast(new Ray(eye + player.transform.forward * 2,
                    -player.transform.forward), out _, 3), Is.True);
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
        }

        private static void AssertFollows(Animator animator, ArticulatedHitboxFollower follower,
            Transform volume, HumanBodyBones trackedBone, HumanBodyBones movedBone, Quaternion delta)
        {
            Assert.That(volume, Is.Not.Null);
            var tracked = animator.GetBoneTransform(trackedBone);
            var moved = animator.GetBoneTransform(movedBone);
            Assert.That(tracked, Is.Not.Null);
            Assert.That(moved, Is.Not.Null);
            var collider = volume.GetComponent<CapsuleCollider>();
            Vector3 before = volume.TransformPoint(collider.center);
            Vector3 boneLocal = tracked.InverseTransformPoint(before);
            Quaternion old = moved.localRotation;
            moved.localRotation = old * delta;
            follower.SyncNow();
            Vector3 after = volume.TransformPoint(collider.center);
            Assert.That(Vector3.Distance(after, tracked.TransformPoint(boneLocal)), Is.LessThan(.001f));
            Assert.That(Vector3.Distance(before, after), Is.GreaterThan(.025f), trackedBone + " should not stay at the standing pose");
            moved.localRotation = old;
            follower.SyncNow();
        }
    }
}
