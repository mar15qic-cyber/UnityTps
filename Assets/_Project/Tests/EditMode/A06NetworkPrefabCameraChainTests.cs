using System.Linq;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// A06（Docs/交接/2026-09-09-联机第一人称相机差异链路审计）：联机生成 prefab 的相机子树
    /// 必须与 Arena 场景手工配置一致——场景实例只服务离线，联机玩家由
    /// Player_Day2_Rebuilt.prefab 生成（GameplayLoadoutBootstrap 对场景实例 SetIsNetworked(false)），
    /// 资产不同步即产生"调场景不生效"的相机差异。只读断言，不重建资产。
    /// 锁定四组不变量：①数值一致（Main Camera z / FP View 位置/FOV/far）；②俯仰单写者
    /// （FPMouseLook 仅在 CameraPivot 且 == aimPivot == 服务器 ApplyRemotePitch 写点）；③相机
    /// 控制链完整（Brain/CM_FP_Camera/HardLock/RotateWithFollow/Breathing/Recoil/Rig 引用闭合）；
    /// ④Owner/Remote 开关覆盖（全部相机新增件位于 localOnlyRoot 子树，AudioListener/相机计数不变）。
    /// </summary>
    public sealed class A06NetworkPrefabCameraChainTests
    {
        private const string PrefabPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";

        private static GameObject LoadPlayerPrefab()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, "Player_Day2_Rebuilt.prefab 必须可加载");
            return prefab;
        }

        private static Component FindByType(GameObject go, string typeName)
        {
            return go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == typeName);
        }

        [Test]
        public void CameraSubTree_Values_MatchArenaHandTunedConfig()
        {
            var root = LoadPlayerPrefab();
            var pivot = root.transform.Find("CameraPivot");
            Assert.That(pivot, Is.Not.Null);
            Assert.That(pivot.Cast<Transform>().Select(t => t.name).OrderBy(n => n).ToArray(),
                Is.EqualTo(new[] { "CM_FP_Camera", "Main Camera" }), "CameraPivot 子节点必须是 Main Camera + CM_FP_Camera");

            var mainCam = pivot.Find("Main Camera");
            Assert.That(mainCam.localPosition.z, Is.EqualTo(0f), "Main Camera z 必须为 0（场景覆盖 -0.33 已同步）");

            var fpView = mainCam.Find("FP View Camera");
            Assert.That(fpView, Is.Not.Null);
            Assert.That(fpView.localPosition, Is.EqualTo(new Vector3(-0.047f, 0.082f, -0.299f)),
                "FP View Camera 位置必须与 Arena 手工值一致（用户调参保护）");
            var cam = fpView.GetComponent<Camera>();
            Assert.That(cam, Is.Not.Null);
            Assert.That(cam.fieldOfView, Is.EqualTo(45f), "FP View Camera FOV 必须为 45");
            Assert.That(cam.farClipPlane, Is.EqualTo(10f), "FP View Camera far clip 必须为 10");
            Assert.That(cam.depth, Is.EqualTo(1f), "FP View overlay depth 不变");
            Assert.That(cam.cullingMask, Is.EqualTo(512), "FP View overlay culling mask 不变（层 9 枪模）");
        }

        [Test]
        public void PitchWriter_IsSingle_OnCameraPivot_AndUnifiedWithAimNodes()
        {
            var root = LoadPlayerPrefab();
            var pivot = root.transform.Find("CameraPivot");
            var looks = root.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && c.GetType().Name == "FPMouseLook").ToArray();
            Assert.That(looks.Length, Is.EqualTo(1), "FPMouseLook 必须全 prefab 唯一（俯仰单写者）");
            Assert.That(looks[0].transform, Is.EqualTo(pivot), "FPMouseLook 必须挂在 CameraPivot（与 aimPivot/服务器远端俯仰同节点）");

            var lookSo = new SerializedObject(looks[0]);
            Assert.That(lookSo.FindProperty("pitchSensitivity").floatValue, Is.EqualTo(0.1f));
            Assert.That(lookSo.FindProperty("minPitch").floatValue, Is.EqualTo(-89f));
            Assert.That(lookSo.FindProperty("maxPitch").floatValue, Is.EqualTo(89f));

            var weapon = root.GetComponent<WeaponController>();
            Assert.That(weapon, Is.Not.Null);
            var wcSo = new SerializedObject(weapon);
            Assert.That(wcSo.FindProperty("aimPivot").objectReferenceValue, Is.EqualTo(pivot),
                "WeaponController.aimPivot 必须指向 CameraPivot（写入节点=权威瞄准节点）");

            var adapter = root.GetComponent<PlayerNetworkAdapter>();
            Assert.That(adapter, Is.Not.Null);
            var naSo = new SerializedObject(adapter);
            Assert.That(naSo.FindProperty("localOnlyRoot").objectReferenceValue, Is.EqualTo(pivot.gameObject),
                "PlayerNetworkAdapter.localOnlyRoot 必须是 CameraPivot（Owner/Remote 开关覆盖整个相机子树）");
        }

        [Test]
        public void CameraControlChain_IsComplete_WithClosedReferences()
        {
            var root = LoadPlayerPrefab();
            var pivot = root.transform.Find("CameraPivot");
            var mainCam = pivot.Find("Main Camera");
            var vcamGo = pivot.Find("CM_FP_Camera");

            var brain = FindByType(mainCam.gameObject, "CinemachineBrain");
            Assert.That(brain, Is.Not.Null, "Main Camera 必须挂 CinemachineBrain（场景相机栈）");
            var brainSo = new SerializedObject(brain);
            Assert.That(brainSo.FindProperty("UpdateMethod").intValue, Is.EqualTo(2), "Brain UpdateMethod=LateUpdate");
            Assert.That(brainSo.FindProperty("CustomBlends").objectReferenceValue, Is.Null);

            var vcam = FindByType(vcamGo.gameObject, "CinemachineCamera");
            Assert.That(vcam, Is.Not.Null, "CM_FP_Camera 必须挂 CinemachineCamera");
            Assert.That(vcamGo.gameObject.activeSelf, Is.True, "CM_FP_Camera 必须激活");
            Assert.That(vcamGo.localPosition, Is.EqualTo(Vector3.zero), "CM_FP_Camera 局部位姿为恒等");
            var vSo = new SerializedObject(vcam);
            Assert.That(vSo.FindProperty("Target.TrackingTarget").objectReferenceValue, Is.EqualTo(pivot),
                "CinemachineCamera.TrackingTarget 必须引用 CameraPivot");
            Assert.That(vSo.FindProperty("Priority.Enabled").boolValue, Is.False, "Priority 关闭（单 vcam 无需抢占）");
            var lensFov = vSo.FindProperty("Lens.FieldOfView");
            Assert.That(lensFov, Is.Not.Null);
            Assert.That(lensFov.floatValue, Is.EqualTo(60f), "CM 腰射 Lens FOV=60（权威 FOV 由 FPCameraRig 每帧驱动）");

            var extNames = vcamGo.GetComponents<Component>().Select(c => c.GetType().Name).ToArray();
            Assert.That(extNames, Does.Contain("CinemachineHardLockToTarget"), "Body=HardLock（硬锁定 Follow）");
            Assert.That(extNames, Does.Contain("CinemachineRotateWithFollowTarget"), "Aim=RotateWithFollowTarget（随 Follow 旋转）");
            Assert.That(extNames, Does.Contain(nameof(CmFPCameraBreathing)), "呼吸扩展（Roll 通道）");
            Assert.That(extNames, Does.Contain(nameof(CmFPCameraRecoil)), "后坐回声扩展（与 FireRay 同源）");
            // Cinemachine 基类程序集未引用：CmFPCamera* 一律经 Component 弱引用读取，
            // 避免 SerializedObject(breath) 在编译期拉入 Unity.Cinemachine 元数据
            var breath = vcamGo.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().Name == nameof(CmFPCameraBreathing));
            Assert.That(breath, Is.Not.Null, "呼吸扩展（Roll 通道）");
            var bSo = new SerializedObject(breath);
            Assert.That(bSo.FindProperty("cyclesPerSecond").floatValue, Is.EqualTo(0.28f));
            Assert.That(bSo.FindProperty("rollDegrees").floatValue, Is.EqualTo(0.1f));
            Assert.That(bSo.FindProperty("settleSeconds").floatValue, Is.EqualTo(0.4f));

            var rig = pivot.GetComponent<FPCameraRig>();
            Assert.That(rig, Is.Not.Null, "CameraPivot 必须挂 FPCameraRig（ADS FOV 驱动）");
            var rSo = new SerializedObject(rig);
            Assert.That(rSo.FindProperty("cinemachineCamera").objectReferenceValue, Is.EqualTo(vcam),
                "FPCameraRig.cinemachineCamera 引用闭合 -> CM_FP_Camera");
            var aimState = rSo.FindProperty("aimState").objectReferenceValue;
            Assert.That(aimState, Is.Not.Null, "FPCameraRig.aimState 引用闭合 -> PlayerAimState");
            Assert.That(aimState.GetType().Name, Is.EqualTo("PlayerAimState"));
            Assert.That(rSo.FindProperty("hipFov").floatValue, Is.EqualTo(60f));

            var weaponRoot = mainCam.Find("FP_Weapon_Root");
            Assert.That(FindByType(weaponRoot.gameObject, nameof(FPWeaponMotion)), Is.Not.Null,
                "FP_Weapon_Root 必须挂 FPWeaponMotion（viewmodel 姿态唯一写者/动态实体镜对位）");
        }

        [Test]
        public void OwnerRemoteSwitch_CoversAllCameraAdditions_WithoutInvariantDrift()
        {
            var root = LoadPlayerPrefab();
            var pivot = root.transform.Find("CameraPivot");

            // 全部相机链组件必须在 localOnlyRoot（CameraPivot）子树内
            var cameraish = root.GetComponentsInChildren<Component>(true).Where(c => c != null);
            var outside = new System.Collections.Generic.List<string>();
            foreach (var c in cameraish)
            {
                var n = c.GetType().Name;
                var isCameraChain = n == "FPMouseLook" || n == "FPCameraRig" || n == "CinemachineBrain"
                    || n == "CinemachineCamera" || n == "CinemachineHardLockToTarget"
                    || n == "CinemachineRotateWithFollowTarget" || n == nameof(CmFPCameraBreathing)
                    || n == nameof(CmFPCameraRecoil) || n == nameof(FPWeaponMotion);
                if (isCameraChain && !c.transform.IsChildOf(pivot))
                    outside.Add(n + "@" + c.transform.name);
            }
            Assert.That(outside, Is.Empty, "相机链组件必须全部位于 CameraPivot 子树（远端 SetActive(false) 一并禁用）");

            Assert.That(root.GetComponentsInChildren<AudioListener>(true).Length, Is.EqualTo(1),
                "AudioListener 必须恰 1（本地玩家不变量探针依赖）");
            Assert.That(root.GetComponentsInChildren<Camera>(true).Length, Is.EqualTo(2),
                "相机必须恰 2（Base + FP View Overlay）");
        }
    }
}
