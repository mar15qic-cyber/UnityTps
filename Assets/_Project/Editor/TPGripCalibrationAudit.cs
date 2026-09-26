using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Animancer;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools
{
    public static class TPGripCalibrationAudit
    {
        private const string PlayerPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static string Capture(string itemId, string label, bool attachedGrip = false,
            Vector3? wristLocal = null, Quaternion? wristRotationLocal = null, float pitch = 0f,
            string motion = "idle", float phase = .3f, bool nearWall = false)
        {
            var entry = WeaponAssetCatalog.LoadOrDefault();
            var definition = entry.FindDefinition(itemId);
            if (definition == null) throw new InvalidOperationException("Missing definition " + itemId);
            string folder = "Captures/CurrentAudit/IK/" + label + "/" + itemId;
            Directory.CreateDirectory(folder);
            var player = PrefabUtility.LoadPrefabContents(PlayerPath);
            GameObject cameraObject = null, lightObject = null, wallObject = null;
            RenderTexture render = null;
            Texture2D image = null;
            try
            {
                UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(player.scene, 1UL << 60);
                if (nearWall) player.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var model = player.transform.Find("TP_Model");
                var animator = model.GetComponent<Animator>();
                var animation = model.GetComponent<AnimancerComponent>();
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var state = animation.Play(motion == "walk" ? definition.ThirdPersonLocomotion.WalkForward
                    : definition.ThirdPersonLocomotion.Idle);
                state.Time = phase;
                if (motion == "fire")
                {
                    var mask = new SerializedObject(model.GetComponent<TPAnimDriver>()).FindProperty("upperBodyMask").objectReferenceValue as AvatarMask;
                    if (mask != null) animation.Layers.SetMask(1, mask);
                    state = animation.Layers[1].Play(definition.ThirdPersonActions.Fire);
                    animation.Layers[1].Weight = 1f;
                    state.Time = phase;
                }
                animation.Evaluate(0f);
                var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                var gunAsset = definition.ThirdPersonViewPrefab;
                var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunAsset, player.scene);
                gun.transform.SetParent(animator.GetBoneTransform(HumanBodyBones.RightHand), false);
                gun.transform.localPosition = gunAsset.transform.localPosition;
                gun.transform.localRotation = gunAsset.transform.localRotation;
                gun.transform.localScale = gunAsset.transform.localScale;
                Transform target = gun.transform.Find("LeftHandTarget");
                if (attachedGrip)
                {
                    var catalog = AttachmentAssetCatalog.LoadOrDefault();
                    var grip = catalog.Find("attach.lpw.grip.01");
                    var view = gun.AddComponent<WeaponAttachmentView>();
                    view.ApplyAttachments(catalog, itemId, new List<AttachmentAssetEntry> { grip }, laserBeamEnabled: false);
                    gun.GetComponent<TPGripPose>()?.CalibrateAttachmentTargets(view);
                    target = TPWeaponMeshSwapper.ResolveLeftHandTarget(view, target);
                }
                var report = new StringBuilder();
                if (wristLocal.HasValue) target.position = gun.transform.TransformPoint(wristLocal.Value);
                if (wristRotationLocal.HasValue) target.rotation = gun.transform.rotation * wristRotationLocal.Value;
                report.AppendLine("item=" + itemId + " name=" + definition.DisplayName + " clip=" + state.Clip.name);
                report.AppendLine("beforeWrist=" + gun.transform.InverseTransformPoint(hand.position).ToString("F5"));
                report.AppendLine("beforeRotation=" + (Quaternion.Inverse(gun.transform.rotation) * hand.rotation).ToString("F5"));
                report.AppendLine("target=" + gun.transform.InverseTransformPoint(target.position).ToString("F5"));
                report.AppendLine("targetRotation=" + (Quaternion.Inverse(gun.transform.rotation) * target.rotation).ToString("F5"));
                // Run the battle frame writers, including aim followed by the
                // swapper's actual IK entry, instead of a second preview solver.
                var swapper = model.GetComponent<TPWeaponMeshSwapper>();
                typeof(TPWeaponMeshSwapper).GetField("_baseLeftHandTarget", Private).SetValue(swapper, target);
                typeof(TPWeaponMeshSwapper).GetProperty("CurrentMuzzle").SetValue(swapper, gun.transform.Find("Muzzle"));
                var aim = model.GetComponent<TPAimDriver>();
                typeof(TPAimDriver).GetMethod("Awake", Private).Invoke(aim, null);
                typeof(TPAimDriver).GetMethod("ApplyDirectionalPose", Private).Invoke(aim,
                    new object[] { Quaternion.Euler(-pitch, 0, 0) * Vector3.forward, 0f });
                typeof(TPWeaponMeshSwapper).GetMethod("LateUpdate", Private).Invoke(swapper, null);
                var muzzle = gun.transform.Find("Muzzle");
                report.AppendLine("requestedPitch=" + pitch + " muzzlePitch=" +
                    (Mathf.Asin(muzzle.forward.y) * Mathf.Rad2Deg).ToString("F3"));
                report.AppendLine("wristErrorMm=" + (Vector3.Distance(hand.position, target.position) * 1000f).ToString("F3"));
                report.AppendLine("wristAngleError=" + Quaternion.Angle(hand.rotation, target.rotation).ToString("F3"));
                if (nearWall)
                {
                    wallObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(wallObject, player.scene);
                    wallObject.name = "AuditWall";
                    wallObject.layer = 31;
                    wallObject.transform.position = new Vector3(0, 1.5f, 2f);
                    wallObject.transform.localScale = new Vector3(4, 3, .2f);
                    var capsule = player.GetComponent<CharacterController>();
                    Physics.SyncTransforms();
                    for (int step = 0; step < 60; step++) capsule.Move(Vector3.forward * .05f);
                    Physics.SyncTransforms();
                    report.AppendLine("wallFaceZ=1.9 rootZ=" + player.transform.position.z.ToString("F5"));
                    // Measure visible torso/head vertices, excluding the gun and extended arms.
                    float front = float.NegativeInfinity;
                    foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        var baked = new Mesh();
                        try
                        {
                            skin.BakeMesh(baked);
                            var vertices = baked.vertices;
                            var weights = skin.sharedMesh.boneWeights;
                            var bones = skin.bones;
                            for (int v = 0; v < vertices.Length; v++)
                            {
                                var w = weights[v];
                                int index = w.boneIndex0;
                                float weight = w.weight0;
                                if (w.weight1 > weight) { index = w.boneIndex1; weight = w.weight1; }
                                if (w.weight2 > weight) { index = w.boneIndex2; weight = w.weight2; }
                                if (w.weight3 > weight) index = w.boneIndex3;
                                string boneName = bones[index].name.ToLowerInvariant();
                                if (!boneName.Contains("spine") && !boneName.Contains("chest")
                                    && !boneName.Contains("head") && !boneName.Contains("neck")) continue;
                                front = Mathf.Max(front, skin.transform.TransformPoint(vertices[v]).z);
                            }
                        }
                        finally { UnityEngine.Object.DestroyImmediate(baked); }
                    }
                    report.AppendLine("visibleTorsoHeadFrontZ=" + front.ToString("F5")
                        + " clearanceMeters=" + (1.9f - front).ToString("F5"));
                }
                foreach (var bone in new[] { HumanBodyBones.LeftHand, HumanBodyBones.LeftThumbProximal,
                    HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal,
                    HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal })
                {
                    var t = animator.GetBoneTransform(bone);
                    if (t != null) report.AppendLine(bone + "=" + gun.transform.InverseTransformPoint(t.position).ToString("F5"));
                }
                foreach (var renderer in gun.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (renderer.sharedMesh == null) continue;
                    var bounds = new Bounds(); bool any = false;
                    foreach (var v in renderer.sharedMesh.vertices)
                    {
                        var p = gun.transform.InverseTransformPoint(renderer.transform.TransformPoint(v));
                        if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                    }
                    report.AppendLine("mesh=" + renderer.name + " min=" + bounds.min.ToString("F5") + " max=" + bounds.max.ToString("F5"));
                }
                File.WriteAllText(folder + "/measurements.txt", report.ToString());
                foreach (var t in player.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
                cameraObject = new GameObject("GripAuditCamera");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, player.scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.cameraType = CameraType.Preview;
                camera.scene = player.scene; camera.overrideSceneCullingMask = 1UL << 60;
                camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.14f, .17f, .21f);
                camera.fieldOfView = 32f; camera.nearClipPlane = .01f; camera.farClipPlane = 15f;
                lightObject = new GameObject("GripAuditLight");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightObject, player.scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 2f;
                light.transform.rotation = Quaternion.Euler(35, -35, 0);
                render = new RenderTexture(1000, 1000, 24);
                image = new Texture2D(1000, 1000, TextureFormat.RGB24, false);
                render.Create();
                Vector3 center = nearWall ? player.transform.position + new Vector3(0, 1, .35f)
                    : (hand.position + animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal).position) * .5f;
                var views = new[] { new Vector3(-.75f, .12f, .2f), new Vector3(.7f, .08f, .3f),
                    new Vector3(-.35f, .18f, .7f), new Vector3(-.28f, .65f, -.15f) };
                if (nearWall) views = new[] { new Vector3(-.75f,.12f,-.3f), new Vector3(.75f,.12f,-.3f),
                    new Vector3(-.45f,.2f,-.7f), new Vector3(-.28f,.65f,-.35f) };
                var names = new[] { "left", "right", "front", "top" };
                for (int i = 0; i < views.Length; i++)
                {
                    camera.transform.position = center + player.transform.TransformDirection(views[i] * (nearWall ? 4f : 1f));
                    camera.transform.LookAt(center, Vector3.up);
                    RenderPipeline.SubmitRenderRequest(camera,
                        new UniversalRenderPipeline.SingleCameraRequest { destination = render });
                    RenderTexture.active = render;
                    image.ReadPixels(new Rect(0, 0, 1000, 1000), 0, 0); image.Apply();
                    File.WriteAllBytes(folder + "/" + names[i] + ".png", image.EncodeToPNG());
                }
                return report.ToString();
            }
            finally
            {
                RenderTexture.active = null;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (render != null) { render.Release(); UnityEngine.Object.DestroyImmediate(render); }
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (lightObject != null) UnityEngine.Object.DestroyImmediate(lightObject);
                if (wallObject != null) UnityEngine.Object.DestroyImmediate(wallObject);
                PrefabUtility.UnloadPrefabContents(player);
            }
        }

        public static void RepairTargets()
        {
            var vertical = new Quaternion(.07523985f, .6240461f, .7569626f, -.1786448f);
            var support = Quaternion.AngleAxis(90f, Vector3.forward) * vertical;
            var specs = new (string item, Vector3 wrist, bool vertical)[] {
                ("weapon.m4", new Vector3(-.008f,-.055f,.075f), false),
                ("weapon.ak", new Vector3(-.064f,-.040f,.130f), true),
                ("weapon.rifle03", new Vector3(-.0635f,-.042f,.097f), true),
                ("weapon.smg01", new Vector3(-.063f,-.040f,.096f), true),
                ("weapon.smg02", new Vector3(-.064f,-.040f,.055f), true),
                ("weapon.smg03", new Vector3(-.008f,-.078f,.035f), false),
                ("weapon.smg04", new Vector3(-.064f,-.059f,.064f), true),
                ("weapon.smg05", new Vector3(-.008f,-.085f,.100f), false),
                ("weapon.shotgun01", new Vector3(-.008f,-.076f,.120f), false),
                ("weapon.sniper01", new Vector3(-.008f,-.078f,.160f), false),
                ("weapon.sniper02", new Vector3(-.008f,-.085f,.110f), false),
                ("weapon.sniper03", new Vector3(-.008f,-.080f,.110f), false),
            };
            var catalog = WeaponAssetCatalog.LoadOrDefault();
            foreach (var spec in specs)
            {
                var asset = catalog.FindDefinition(spec.item).ThirdPersonViewPrefab;
                string path = AssetDatabase.GetAssetPath(asset);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var target = root.transform.Find("LeftHandTarget");
                    target.localPosition = spec.wrist;
                    target.localRotation = spec.vertical ? vertical : support;
                    if (root.GetComponent<TPGripPose>() == null) root.AddComponent<TPGripPose>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            foreach (string item in new[] { "weapon.service_pistol", "weapon.handgun02", "weapon.handgun03", "weapon.handgun04" })
            {
                var def = catalog.FindDefinition(item);
                var player = PrefabUtility.LoadPrefabContents(PlayerPath);
                string path = AssetDatabase.GetAssetPath(def.ThirdPersonViewPrefab);
                var gun = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var animator = player.transform.Find("TP_Model").GetComponent<Animator>();
                    var animation = animator.GetComponent<AnimancerComponent>();
                    var state = animation.Play(def.ThirdPersonLocomotion.Idle);
                    state.Time = .3f; animation.Evaluate(0f);
                    var right = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    var left = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                    var rootMatrix = Matrix4x4.TRS(right.position, right.rotation, right.lossyScale)
                        * Matrix4x4.TRS(gun.transform.localPosition, gun.transform.localRotation, gun.transform.localScale);
                    var target = gun.transform.Find("LeftHandTarget");
                    target.localPosition = rootMatrix.inverse.MultiplyPoint3x4(left.position)
                        + new Vector3(-.00311f, .05194f, -.06553f);
                    target.localRotation = Quaternion.Inverse(right.rotation * gun.transform.localRotation) * left.rotation;
                    PrefabUtility.SaveAsPrefabAsset(gun, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(gun); PrefabUtility.UnloadPrefabContents(player); }
            }
        }
    }
}
