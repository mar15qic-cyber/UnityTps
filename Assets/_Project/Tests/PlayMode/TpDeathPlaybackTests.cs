using System.Collections;
using Animancer;
using Game.Presentation.Animation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Gameplay.PlayModeTests
{
    public sealed class TpDeathPlaybackTests
    {
        [UnityTest]
        public IEnumerator DeathAdvancesBonesHoldsLastFrameAndCanReplayAfterRespawn()
        {
            var prefab = Resources.Load<GameObject>("UI/LobbyCharacter");
            Assert.IsNotNull(prefab);
            var root = Object.Instantiate(prefab);
            try
            {
                var animator = root.GetComponentInChildren<Animator>();
                Assert.IsTrue(animator.isHuman);
                var graph = animator.GetComponent<AnimancerComponent>() ?? animator.gameObject.AddComponent<AnimancerComponent>();
                graph.Animator = animator;
                var driver = animator.gameObject.AddComponent<TPAnimDriver>();
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                driver.PlayDeathPose(0);
                yield return null;
                var initial = hips.localRotation;
                var initialHeight = hips.position.y;
                yield return new WaitForSeconds(.7f);
                Assert.Greater(Quaternion.Angle(initial, hips.localRotation), 1f, "Actual humanoid bones must move during death");
                Assert.IsFalse(driver.DeathPoseComplete);
                driver.PlayDeathPose(0); // Duplicate event must not restart playback.
                yield return new WaitForSeconds(1.4f);
                Assert.IsTrue(driver.DeathPoseComplete);
                Assert.Less(hips.position.y, initialHeight - .25f, "Death must lower the body to the ground");
                var final = hips.localRotation;
                yield return new WaitForSeconds(.2f);
                Assert.Less(Quaternion.Angle(final, hips.localRotation), .1f);
                driver.RecoverPoseAfterRespawn();
                driver.PlayDeathPose(0);
                yield return null;
                Assert.IsFalse(driver.DeathPoseComplete);
                driver.RecoverPoseAfterRespawn();
                driver.PlayDeathPose(10f); // Late observer sees the final pose immediately.
                yield return null;
                Assert.IsTrue(driver.DeathPoseComplete);
                Assert.IsFalse(animator.applyRootMotion);
            }
            finally { Object.Destroy(root); }
        }
    }
}
