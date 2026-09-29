using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Combat;
using Game.Presentation.Animation;
using Game.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    public static class FirstPersonPinAssetBuilder
    {
        private const string Folder = "Assets/_Project/Resources/ThrowableViews";
        [MenuItem("Tools/Review/Bake First Person Pull Rings")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Project/Resources", "ThrowableViews");
            var metal = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/RingMetal.mat");
            if (metal == null)
            {
                metal = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "RingMetal" };
                metal.SetColor("_BaseColor", new Color(.58f,.62f,.66f)); metal.SetFloat("_Metallic", .45f); metal.SetFloat("_Smoothness", .55f);
                AssetDatabase.CreateAsset(metal, Folder + "/RingMetal.mat");
            }
            var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            var weapon = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
            foreach (var type in new[] { ThrowableType.Frag, ThrowableType.Flash, ThrowableType.Smoke })
            {
                var source = catalog.Get(type).ModelPrefab;
                var mesh = source.GetComponent<MeshFilter>().sharedMesh;
                var groups = Islands(mesh);
                var ringIndices = groups.Single(g => g.Count == 256);
                var pinIndices = groups.Single(g => g.Count == 48);
                var detached = new HashSet<int>(ringIndices.Concat(pinIndices));
                var ringBounds = new Bounds(mesh.vertices[ringIndices[0]], Vector3.zero);
                foreach (int i in ringIndices) ringBounds.Encapsulate(mesh.vertices[i]);
                if (ringBounds.size.y < .025f || ringBounds.size.z < .025f) throw new InvalidOperationException("Unexpected ring geometry: " + type);
                var bodyMesh = SaveMesh(mesh, i => !detached.Contains(i), type + "Body");
                var ringMesh = SaveMesh(mesh, detached.Contains, type + "Ring");
                var rig = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(weapon.FirstPersonViewPrefab));
                GameObject body = null;
                try
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(FirstPersonThrowClipBuilder.ClipPath);
                    clip.SampleAnimation(rig, FPThrowablePinView.SeparationSeconds);
                    body = new GameObject(type.ToString());
                    body.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
                    body.AddComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
                    var ring = new GameObject("PullRing"); ring.transform.SetParent(body.transform, false);
                    ring.AddComponent<MeshFilter>().sharedMesh = ringMesh;
                    ring.AddComponent<MeshRenderer>().sharedMaterial = metal;
                    var grip = new GameObject("PinchContact"); grip.transform.SetParent(ring.transform, false);
                    // Pinch the lower arc so the loop projects above the fingers
                    // once extracted instead of being buried behind the knuckles.
                    grip.transform.localPosition = ringBounds.center + Vector3.down * .014f;
                    body.transform.SetParent(rig.transform.Find("Armature/arm_R/lower_arm_R/hand_R"), false);
                    FPWeaponAnimator.AlignHeldThrowable(body);
                    var left = rig.transform.Find("Armature/arm_L/lower_arm_L/hand_L");
                    var view = body.AddComponent<FPThrowablePinView>();
                    view.Configure(ring.transform, grip.transform, Vector3.zero, Quaternion.identity);
                    view.Bind(left); view.AlignContact(1f);
                    var finger = left.Find("finger_01_L/finger_02_L/finger_03_L");
                    view.Configure(ring.transform, grip.transform, finger.InverseTransformPoint(ring.transform.position), Quaternion.Inverse(finger.rotation) * ring.transform.rotation);
                    body.transform.SetParent(null, false);
                    body.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    PrefabUtility.SaveAsPrefabAsset(body, Folder + "/" + type + ".prefab");
                }
                finally { if (body != null) Object.DestroyImmediate(body); PrefabUtility.UnloadPrefabContents(rig); }
            }
            AssetDatabase.SaveAssets();
        }

        private static Mesh SaveMesh(Mesh source, Func<int,bool> selected, string name)
        {
            var tris = source.triangles; var keep = new List<int>();
            for (int i = 0; i < tris.Length; i += 3)
                if (selected(tris[i])) { keep.Add(tris[i]); keep.Add(tris[i+1]); keep.Add(tris[i+2]); }
            var indices = keep.Distinct().ToArray(); var map = indices.Select((v,i) => (v,i)).ToDictionary(p => p.v, p => p.i);
            var mesh = new Mesh { name = name, vertices = indices.Select(i => source.vertices[i]).ToArray(),
                normals = indices.Select(i => source.normals[i]).ToArray(), uv = indices.Select(i => source.uv[i]).ToArray(),
                triangles = keep.Select(i => map[i]).ToArray() };
            mesh.RecalculateBounds();
            string path = Folder + "/" + name + ".asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
            EditorUtility.CopySerialized(mesh, old); Object.DestroyImmediate(mesh); return old;
        }

        private static List<List<int>> Islands(Mesh mesh)
        {
            var vertices = mesh.vertices; var parents = Enumerable.Range(0, vertices.Length).ToArray();
            int Find(int i) => parents[i] == i ? i : parents[i] = Find(parents[i]);
            void Union(int a, int b) => parents[Find(a)] = Find(b);
            var welded = new Dictionary<Vector3Int,int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                var key = Vector3Int.RoundToInt(vertices[i] * 100000);
                if (welded.TryGetValue(key, out int other)) Union(i, other); else welded[key] = i;
            }
            var tris = mesh.triangles;
            for (int i = 0; i < tris.Length; i += 3) { Union(tris[i], tris[i+1]); Union(tris[i], tris[i+2]); }
            return Enumerable.Range(0, vertices.Length).GroupBy(Find).Select(g => g.ToList()).ToList();
        }
    }
}
