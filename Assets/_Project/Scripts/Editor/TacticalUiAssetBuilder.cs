#if UNITY_EDITOR
using System.IO;
using System.Linq;
using Game.UI;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>Deterministic build-time assets. Original LPFP files are never modified.</summary>
    public static class TacticalUiAssetBuilder
    {
        private const string Output = "Assets/_Project/Resources/UI";
        [MenuItem("Tools/UI/Build Tactical Lobby Assets")]
        public static void Build()
        {
            Directory.CreateDirectory(Output + "/WeaponIcons");
            AssetDatabase.Refresh();
            EnsureRoomMaterials();
            var modelPath = "Assets/Low Poly FPS Pack/Third_Person_Character/Components/Meshes/Character/third_person_character_lpfp.fbx";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                instance.name = "LobbyCharacter";
                foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(behaviour);
                var animator = instance.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                PrefabUtility.SaveAsPrefabAsset(instance, Output + "/LobbyCharacter.prefab");
            }
            finally { Object.DestroyImmediate(instance); }
            var animationPath = "Assets/Low Poly FPS Pack/Third_Person_Character/Components/Animations/Rifle/third_person_character_lpfp@rifle.fbx";
            var clip = AssetDatabase.LoadAllAssetsAtPath(animationPath).OfType<AnimationClip>().Single(x => x.name == "idle_relaxed@rifle_tpc");
            var outputClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Output + "/LobbyRelaxed.anim");
            if (outputClip == null) AssetDatabase.CreateAsset(Object.Instantiate(clip), Output + "/LobbyRelaxed.anim");
            else EditorUtility.CopySerialized(clip, outputClip);
            var catalog = AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>("Assets/_Project/Resources/WeaponAssetCatalog.asset");
            foreach (var entry in catalog.Entries.Where(x => x.IsLpfp && x.definition != null && x.definition.ThirdPersonViewPrefab != null))
                BakeIcon(entry);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }

        public static void EnsureRoomMaterials()
        {
            foreach (var kind in new[] { "Lit", "Unlit" })
            {
                var path = Output + "/LobbyRoom" + kind + ".mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) continue;
                var shader = Shader.Find("Universal Render Pipeline/" + kind);
                if (shader == null) throw new System.InvalidOperationException("Missing room shader: " + kind);
                var material = new Material(shader) { name = "LobbyRoom" + kind };
                material.SetColor("_BaseColor", Color.white);
                AssetDatabase.CreateAsset(material, path);
            }
            AssetDatabase.SaveAssets();
        }

        private static void BakeIcon(WeaponAssetEntry entry)
        {
            var stage = new GameObject("IconBakeStage");
            stage.transform.position = new Vector3(2500, 2500, 2500);
            RenderTexture rt = null;
            Camera renderCamera = null;
            Texture2D image = null;
            var previous = RenderTexture.active;
            try
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(entry.definition.ThirdPersonViewPrefab, stage.transform);
                model.transform.localRotation = Quaternion.Euler(0, 90, 0);
                model.transform.localPosition = Vector3.zero;
                foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
                var renderers = model.GetComponentsInChildren<Renderer>();
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                var cameraObject = new GameObject("IconCamera", typeof(Camera));
                cameraObject.transform.SetParent(stage.transform, false);
                var camera = cameraObject.GetComponent<Camera>();
                renderCamera = camera;
                camera.transform.position = bounds.center + new Vector3(0, 0, -4);
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x / 2.4f) * 1.2f;
                camera.aspect = 2.4f; camera.nearClipPlane = 0.01f; camera.farClipPlane = 10;
                camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear; camera.allowHDR = false;
                var key = new GameObject("IconLight", typeof(Light));
                key.transform.SetParent(stage.transform, false);
                key.transform.rotation = Quaternion.Euler(35, -25, 0);
                var light = key.GetComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.7f; light.cullingMask = 1 << 31;
                rt = new RenderTexture(768, 320, 24, RenderTextureFormat.ARGB32);
                rt.Create(); camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                image = new Texture2D(768, 320, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, 768, 320), 0, 0); image.Apply();
                var path = Output + "/WeaponIcons/" + entry.itemId + ".png";
                File.WriteAllBytes(path, image.EncodeToPNG());
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true; importer.mipmapEnabled = false; importer.SaveAndReimport();
                entry.icon = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderCamera != null) renderCamera.targetTexture = null;
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (image != null) Object.DestroyImmediate(image);
                Object.DestroyImmediate(stage);
            }
        }
    }
}
#endif
