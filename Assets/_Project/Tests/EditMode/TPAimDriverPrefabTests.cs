using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Game.Presentation.Animation;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-16 需求3：联网生成的 Player_Day2_Rebuilt.prefab 必须自带 TPAimDriver——
    /// 远端玩家的抬头/低头俯仰由它消费（数据链 _aimPitch SyncVar → AimDirectionWorld 已存在，
    /// 此前该组件只挂在场景作者玩家上，联网生成的远端玩家没有执行者）。
    /// </summary>
    public sealed class TPAimDriverPrefabTests
    {
        private const string PlayerPrefabPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";

        private static GameObject LoadPlayerPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            Assert.That(prefab, Is.Not.Null, "玩家 prefab 必须存在");
            return prefab;
        }

        [Test]
        public void PlayerPrefab_TpModel_HasEnabledTPAimDriver()
        {
            var prefab = LoadPlayerPrefab();
            var tpModel = prefab.transform.Find("TP_Model");
            Assert.That(tpModel, Is.Not.Null, "TP_Model 节点必须存在");
            var driver = tpModel.GetComponent<TPAimDriver>();
            Assert.That(driver, Is.Not.Null, "TP_Model 必须挂 TPAimDriver，否则远端玩家无 TP 俯仰");
            Assert.That(driver.enabled, Is.True, "TPAimDriver 必须为启用状态");
        }

        [Test]
        public void PlayerPrefab_TpAimDriver_PitchWeightsAreSane()
        {
            var prefab = LoadPlayerPrefab();
            var driver = prefab.transform.Find("TP_Model").GetComponent<TPAimDriver>();
            Assert.That(driver, Is.Not.Null);

            var so = new SerializedObject(driver);
            float spine = so.FindProperty("spineWeight").floatValue;
            float chest = so.FindProperty("chestWeight").floatValue;
            float neck = so.FindProperty("neckWeight").floatValue;
            float head = so.FindProperty("headWeight").floatValue;
            Assert.That(spine + chest + neck + head, Is.EqualTo(1f).Within(0.001f),
                "脊柱/胸/颈/头俯仰权重之和应≈1（与场景作者玩家参考配置一致）");
            Assert.That(spine + chest, Is.EqualTo(1f).Within(0.001f),
                "持枪手臂跟随脊柱和胸骨；若把俯仰留给颈/头，TP 枪与 FP 视角会系统性错角");
            Assert.That(so.FindProperty("maxPitchDegrees").floatValue, Is.GreaterThan(0f));
            Assert.That(so.FindProperty("pitchSmoothSeconds").floatValue, Is.GreaterThanOrEqualTo(0f));
        }

        [Test]
        public void BoreCorrection_ClosesIdleBiasAndStaysBounded()
        {
            var down = Quaternion.Euler(8, 0, 0) * Vector3.forward;
            var correction = TPAimDriver.BoreDirectionCorrection(down, Vector3.forward);
            Assert.That(Vector3.Angle(correction * down, Vector3.forward), Is.LessThan(.01f));
            correction = TPAimDriver.BoreDirectionCorrection(Vector3.down, Vector3.up);
            Assert.That(Quaternion.Angle(Quaternion.identity, correction), Is.EqualTo(20f).Within(.01f));
        }

        [Test]
        public void PlayerCapsule_ContainsAuthoredTorsoAtWall()
        {
            var controller = LoadPlayerPrefab().GetComponent<CharacterController>();
            Assert.That(controller, Is.Not.Null);
            // The bound pose is only .590 m deep, but the animated torso/head
            // reaches .967 m while looking down. Include CharacterController's
            // skin contact distance, as verified by the actual Move wall probe.
            Assert.That(controller.center.z + controller.radius + controller.skinWidth,
                Is.GreaterThanOrEqualTo(1.0f));
            Assert.That(controller.radius, Is.GreaterThanOrEqualTo(.50f),
                "shoulders must stay inside the lateral movement envelope");
        }

        [Test]
        public void PlayerPrefab_TpModelRig_ResolvesAimBones()
        {
            var prefab = LoadPlayerPrefab();
            var tpModel = prefab.transform.Find("TP_Model");
            var animator = tpModel.GetComponentInChildren<Animator>(true);
            Assert.That(animator, Is.Not.Null, "TP_Model 必须有 Animator（Humanoid）");
            // TPAimDriver.Awake 的骨骼解析路径：四根骨都必须可从 Humanoid Avatar 解析
            Assert.That(animator.GetBoneTransform(HumanBodyBones.Spine), Is.Not.Null, "Spine 骨骼缺失");
            Assert.That(animator.GetBoneTransform(HumanBodyBones.Chest), Is.Not.Null, "Chest 骨骼缺失");
            Assert.That(animator.GetBoneTransform(HumanBodyBones.Neck), Is.Not.Null, "Neck 骨骼缺失");
            Assert.That(animator.GetBoneTransform(HumanBodyBones.Head), Is.Not.Null, "Head 骨骼缺失");
        }
    }
}
