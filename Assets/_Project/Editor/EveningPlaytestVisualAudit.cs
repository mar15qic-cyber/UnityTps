using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using Game.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools
{
    public static class EveningPlaytestVisualAudit
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void CaptureOptics()
        {
            string directory = "Temp/Playtest0923Evening/visual";
            Directory.CreateDirectory(directory);
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>("Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            foreach (var id in new[] { "weapon.m4", "weapon.smg01", "weapon.smg02", "weapon.shotgun01", "weapon.sniper01" })
            {
                var d = weapons.Entries.First(e => e.itemId == id).definition;
                var view = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
                UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(view.scene, 1UL << 60);
                var host = new GameObject("OpticEditorAudit");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, view.scene);
                try
                {
                    var weapon = host.AddComponent<WeaponController>(); weapon.Initialize(d, balance);
                    var optic = catalog.Find("attach.lpfp.optic.01"); weapon.SetAttachments(new[] { optic });
                    var aim = host.AddComponent<PlayerAimState>(); var rig = host.AddComponent<FPWeaponRig>();
                    typeof(FPWeaponRig).GetField("_activeView", Flags).SetValue(rig, view);
                    var scope = host.AddComponent<PhysicalScopeView>();
                    typeof(PhysicalScopeView).GetField("_rig", Flags).SetValue(scope, rig);
                    typeof(PhysicalScopeView).GetField("_controller", Flags).SetValue(scope, weapon);
                    typeof(PhysicalScopeView).GetField("_aimState", Flags).SetValue(scope, aim);
                    var attachments = view.GetComponent<WeaponAttachmentView>() ?? view.AddComponent<WeaponAttachmentView>();
                    attachments.ApplyAttachments(catalog, id, new[] { optic }, false);
                    d.FirstPersonAnimations.Idle.SampleAnimation(view, 0f);
                    typeof(PhysicalScopeView).GetMethod("Update", Flags).Invoke(scope, null);
                    typeof(PhysicalScopeView).GetMethod("SetLensImageVisible", Flags).Invoke(scope, new object[] { true });
                    var lens = ((GameObject)typeof(PhysicalScopeView).GetField("_lensNode", Flags).GetValue(scope)).transform;
                    var camera = host.AddComponent<Camera>(); camera.enabled = false; camera.nearClipPlane = .001f;
                    camera.scene = view.scene;
                    camera.cameraType = CameraType.Preview;
                    camera.overrideSceneCullingMask = UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(view.scene);
                    camera.backgroundColor = new Color(.3f, .35f, .4f); camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.fieldOfView = 24f;
                    camera.transform.position = lens.position + lens.forward * .12f;
                    camera.transform.rotation = Quaternion.LookRotation(-lens.forward, lens.up);
                    Capture(camera, directory + "/optic-" + id + ".png");
                }
                finally { UnityEngine.Object.DestroyImmediate(host); PrefabUtility.UnloadPrefabContents(view); }
            }
        }

        public static void CaptureThrowables()
        {
            const string directory = "Temp/Playtest0923Evening/visual";
            Directory.CreateDirectory(directory);
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var catalog = Resources.Load<Game.Gameplay.Combat.ThrowableCatalog>("ThrowableCatalog");
            foreach (var entry in weapons.Entries.Where(e => e.IsLpfp && e.definition != null))
            {
                var d = entry.definition;
                var view = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
                UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(view.scene, 1UL << 60);
                var host = new GameObject("ThrowableEditorAudit");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, view.scene);
                try
                {
                    foreach (var r in view.GetComponentsInChildren<Renderer>(true)) if (r.name != "arms") r.enabled = false;
                    d.FirstPersonAnimations.ThrowGrenade.SampleAnimation(view, d.FirstPersonAnimations.ThrowGrenade.length * .35f);
                    var hand = view.transform.Find("Armature/arm_L/lower_arm_L/hand_L");
                    var camera = host.AddComponent<Camera>(); camera.enabled = false; camera.nearClipPlane = .001f;
                    camera.scene = view.scene; camera.cameraType = CameraType.Preview; camera.overrideSceneCullingMask = 1UL << 60;
                    camera.backgroundColor = new Color(.3f, .35f, .4f); camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.fieldOfView = 42f;
                    camera.transform.position = hand.TransformPoint(new Vector3(-.3f, .13f, .16f));
                    camera.transform.rotation = Quaternion.LookRotation(hand.TransformPoint(new Vector3(-.025f, .08f, .01f)) - camera.transform.position, hand.up);
                    var light = new GameObject("AuditLight"); light.transform.SetParent(host.transform, false);
                    var illumination = light.AddComponent<Light>(); illumination.type = LightType.Directional; illumination.intensity = 1.5f;
                    foreach (var definition in new[] { catalog.Frag, catalog.Flash, catalog.Smoke })
                    {
                        var model = (GameObject)PrefabUtility.InstantiatePrefab(definition.ModelPrefab, view.scene);
                        model.transform.SetParent(hand, false);
                        model.transform.localPosition = (Vector3)typeof(FPWeaponAnimator).GetMethod("HeldThrowablePosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { model });
                        Capture(camera, directory + "/hold-" + entry.itemId + "-" + definition.Type + ".png");
                        UnityEngine.Object.DestroyImmediate(model);
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(host); PrefabUtility.UnloadPrefabContents(view); }
            }
        }

        private static void Capture(Camera camera, string path)
        {
            var rt = new RenderTexture(640, 640, 24);
            var texture = new Texture2D(640, 640, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                rt.Create();
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                RenderTexture.active = rt; texture.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
