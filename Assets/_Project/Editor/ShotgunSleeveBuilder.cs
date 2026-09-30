using System.Linq;
using Game.Presentation.Camera;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Keep the builder entry point so regeneration cannot restore rejected sleeves.
    public static class ShotgunSleeveBuilder
    {
        public const string MeshPath = "Assets/_Project/Prefabs/Weapons/ShotgunSleeves.asset";
        private const string SourcePath = "Assets/Low Poly FPS Pack/Components/Meshes/Arms/Shotgun_01/arms_shotgun_01.fbx";
        private static readonly string[] Views = {
            "Assets/_Project/Prefabs/Weapons/FP_Shotgun01_View.prefab",
            "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_Shotgun1_01_View.prefab",
            "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_Shotgun2_01_View.prefab",
            "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_Shotgun3_01_View.prefab",
            "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_Shotgun4_01_View.prefab",
            "Assets/_Project/Prefabs/Weapons/LPW/FP/FP_Shotgun5_01_View.prefab"
        };

        public static string Build()
        {
            var source = AssetDatabase.LoadAllAssetsAtPath(SourcePath).OfType<Mesh>().First(m => m.vertexCount == 2016);
            var sourceRenderer = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath)
                .GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.sharedMesh == source);
            foreach (var path in Views)
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var arms = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "arms");
                    arms.sharedMesh = source;
                    arms.localBounds = sourceRenderer.localBounds;
                    var camera = root.transform.Find("Armature/camera");
                    if (camera == null) throw new System.InvalidOperationException("Authored camera missing: " + path);
                    var profile = root.GetComponent<FPViewFramingProfile>() ?? root.AddComponent<FPViewFramingProfile>();
                    profile.Configure(root.transform.InverseTransformPoint(camera.position));
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            if (AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath) != null) AssetDatabase.DeleteAsset(MeshPath);
            AssetDatabase.SaveAssets();
            return "Restored original 2016-vertex arms and authored viewpoint on six project views; no sleeve extension.";
        }
    }
}
