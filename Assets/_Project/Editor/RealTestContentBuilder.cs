using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Combat;
using Game.Presentation.Animation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    public static class RealTestContentBuilder
    {
        public static string BuildThrowables()
        {
            var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            catalog.Frag.FragMaxDamage = 90;
            for (int number = 2; number <= 3; number++)
            {
                var path = "Assets/_Project/Resources/Throwable_Frag" + number + ".asset";
                var definition = AssetDatabase.LoadAssetAtPath<ThrowableDefinition>(path);
                if (definition == null) { definition = Object.Instantiate(catalog.Frag); AssetDatabase.CreateAsset(definition, path); }
                definition.name = "Throwable_Frag" + number;
                definition.ModelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Low Poly FPS Pack/Prefabs/Models_Only/Grenades/Hand_Grenade_" + number + ".prefab");
                definition.FragMaxDamage = number == 2 ? 100 : 80;
                if (number == 2) catalog.Frag2 = definition; else catalog.Frag3 = definition;
                BuildView(definition, "Frag" + number);
                EditorUtility.SetDirty(definition);
            }
            foreach (var id in new[] { "throwable.frag", "throwable.frag_02", "throwable.frag_03", "throwable.flash", "throwable.smoke" })
                BakeIcon(catalog.Get(id).ModelPrefab, id);
            EditorUtility.SetDirty(catalog.Frag); EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssets();
            return "Five model icons baked; three frag variants configured: 90/100/80.";
        }

        private static void BuildView(ThrowableDefinition definition, string name)
        {
            var source = definition.ModelPrefab;
            var mesh = source.GetComponent<MeshFilter>().sharedMesh;
            var islands = (System.Collections.Generic.List<System.Collections.Generic.List<int>>)typeof(FirstPersonPinAssetBuilder)
                .GetMethod("Islands", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { mesh });
            var ring = islands.FirstOrDefault(g => g.Count == 256);
            var pin = islands.FirstOrDefault(g => g.Count == 48);
            if (ring == null || pin == null)
            {
                var plain=PrefabUtility.LoadPrefabContents("Assets/_Project/Resources/ThrowableViews/Frag.prefab");
                try
                {
                    plain.name=name; plain.GetComponent<MeshFilter>().sharedMesh=mesh;
                    plain.GetComponent<MeshRenderer>().sharedMaterials=source.GetComponent<MeshRenderer>().sharedMaterials;
                    Object.DestroyImmediate(plain.GetComponent<FPThrowablePinView>());
                    Object.DestroyImmediate(plain.transform.Find("PullRing").gameObject);
                    PrefabUtility.SaveAsPrefabAsset(plain,"Assets/_Project/Resources/ThrowableViews/"+name+".prefab");
                } finally { PrefabUtility.UnloadPrefabContents(plain); }
                return;
            }
            var detached = new System.Collections.Generic.HashSet<int>(ring.Concat(pin));
            var save = typeof(FirstPersonPinAssetBuilder).GetMethod("SaveMesh", BindingFlags.NonPublic | BindingFlags.Static);
            var body = (Mesh)save.Invoke(null, new object[] { mesh, new Func<int,bool>(i => !detached.Contains(i)), name + "Body" });
            var ringMesh = (Mesh)save.Invoke(null, new object[] { mesh, new Func<int,bool>(detached.Contains), name + "Ring" });
            var root = PrefabUtility.LoadPrefabContents("Assets/_Project/Resources/ThrowableViews/Frag.prefab");
            try
            {
                root.name = name;
                root.GetComponent<MeshFilter>().sharedMesh = body;
                root.GetComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
                root.transform.Find("PullRing").GetComponent<MeshFilter>().sharedMesh = ringMesh;
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/_Project/Resources/ThrowableViews/" + name + ".prefab");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void BakeIcon(GameObject prefab, string id)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D texture = null;
            var previous = RenderTexture.active;
            try
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                model.transform.rotation = Quaternion.Euler(0, -25, -15);
                var renderers = model.GetComponentsInChildren<Renderer>();
                var bounds = renderers[0].bounds; foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                var cameraObject = new GameObject("IconCamera"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>(); camera.cameraType = CameraType.Preview; camera.overrideSceneCullingMask=EditorSceneManager.GetSceneCullingMask(scene);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
                camera.orthographic = true; camera.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.y) * .62f;
                camera.transform.position = bounds.center + new Vector3(0, 0, -Mathf.Max(1, bounds.size.magnitude * 3));
                camera.transform.LookAt(bounds.center); camera.nearClipPlane = .01f; camera.farClipPlane = 20;
                foreach (var euler in new[] { new Vector3(35, -35, 0), new Vector3(20, 145, 0) })
                {
                    var lightObject = new GameObject("IconLight"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightObject, scene);
                    var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.5f; light.transform.rotation = Quaternion.Euler(euler);
                }
                target = new RenderTexture(384, 384, 24, RenderTextureFormat.ARGB32); camera.targetTexture = target; camera.Render(); camera.targetTexture=null;
                RenderTexture.active = target; texture = new Texture2D(384,384,TextureFormat.RGBA32,false);
                texture.ReadPixels(new Rect(0,0,384,384),0,0); texture.Apply();
                var path = "Assets/_Project/Resources/UI/WeaponIcons/" + id + ".png";
                File.WriteAllBytes(path, texture.EncodeToPNG()); AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path); importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single; importer.alphaIsTransparency = true; importer.mipmapEnabled = false; importer.SaveAndReimport();
            }
            finally
            {
                RenderTexture.active = previous; if (texture != null) Object.DestroyImmediate(texture);
                if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
