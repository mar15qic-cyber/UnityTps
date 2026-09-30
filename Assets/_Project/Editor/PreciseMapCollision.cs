using System;
using System.Linq;
using Game.Gameplay.Combat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    public static class PreciseMapCollision
    {
        public static void Apply(GameObject root)
        {
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                if (!collider.isTrigger) Object.DestroyImmediate(collider);
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
            }
            if (root.name.IndexOf("stair", StringComparison.OrdinalIgnoreCase) >= 0) AddStairRamp(root);
        }

        private static void AddStairRamp(GameObject root)
        {
            var filter=root.GetComponent<MeshFilter>(); if(filter==null||filter.sharedMesh==null)return;
            var bounds=filter.sharedMesh.bounds;
            bool alongZ=false;
            float low=bounds.min.x, high=bounds.max.x;
            var intersect=typeof(HandleUtility).GetMethod("IntersectRayMesh",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            float Surface(float coordinate)
            {
                var point=bounds.center;point.y=bounds.max.y+1;
                if(alongZ)point.z=coordinate;else point.x=coordinate;
                var args=new object[]{new Ray(point,Vector3.down),filter.sharedMesh,Matrix4x4.identity,default(RaycastHit)};
                return (bool)intersect.Invoke(null,args)?((RaycastHit)args[3]).point.y:float.NaN;
            }
            float ax=Surface(low+(high-low)*.02f), bx=Surface(low+(high-low)*.98f);
            alongZ=true; low=bounds.min.z;high=bounds.max.z;
            float az=Surface(low+(high-low)*.02f), bz=Surface(low+(high-low)*.98f);
            float dx=float.IsNaN(ax)||float.IsNaN(bx)?0:Mathf.Abs(ax-bx);
            float dz=float.IsNaN(az)||float.IsNaN(bz)?0:Mathf.Abs(az-bz);
            alongZ=dz>dx; low=alongZ?bounds.min.z:bounds.min.x;high=alongZ?bounds.max.z:bounds.max.x;
            float a=alongZ?az:ax,b=alongZ?bz:bx;
            low+=(high-low)*.02f;high-=(high-low)*.02f;
            Vector3 from=alongZ?new Vector3(bounds.center.x,a,low):new Vector3(low,a,bounds.center.z);
            Vector3 to=alongZ?new Vector3(bounds.center.x,b,high):new Vector3(high,b,bounds.center.z);
            var run=root.transform.TransformPoint(to)-root.transform.TransformPoint(from);
            float horizontal=new Vector2(run.x,run.z).magnitude;
            if(Mathf.Max(dx,dz)<.1f||float.IsNaN(a)||float.IsNaN(b)||Mathf.Abs(run.y)>horizontal)
            {
                // Vertical ladders named "Stairs" have no walkable tread gradient.
                var old=root.transform.Find("MovementRamp");if(old!=null)Object.DestroyImmediate(old.gameObject);
                return;
            }
            Vector3 P(float width,float length,float height)=>alongZ?new Vector3(width,height,length):new Vector3(length,height,width);
            float w0=alongZ?bounds.min.x:bounds.min.z,w1=alongZ?bounds.max.x:bounds.max.z;
            var mesh=new Mesh{name=filter.sharedMesh.name+"_MovementRamp",vertices=new[]{P(w0,low,a),P(w1,low,a),P(w0,high,b),P(w1,high,b)},triangles=new[]{0,2,1,1,2,3,1,2,0,3,2,1}};
            mesh.RecalculateNormals();mesh.RecalculateBounds();
            const string folder="Assets/_Project/Prefabs/Collision";
            System.IO.Directory.CreateDirectory(folder);
            string path=folder+"/"+mesh.name+".asset";
            var saved=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(saved==null){AssetDatabase.CreateAsset(mesh,path);saved=mesh;}else{EditorUtility.CopySerialized(mesh,saved);Object.DestroyImmediate(mesh);}
            var child=root.transform.Find("MovementRamp");
            if(child==null){var go=new GameObject("MovementRamp");go.transform.SetParent(root.transform,false);child=go.transform;}
            var ramp=child.GetComponent<MeshCollider>();if(ramp==null)ramp=child.gameObject.AddComponent<MeshCollider>();
            ramp.sharedMesh=saved;
            if(child.GetComponent<MapMovementBarrier>()==null)child.gameObject.AddComponent<MapMovementBarrier>();
        }

        public static void MarkBoundary(GameObject go)
        {
            if(go.GetComponent<MapMovementBarrier>()==null)go.AddComponent<MapMovementBarrier>();
            var renderer=go.GetComponent<MeshRenderer>();var filter=go.GetComponent<MeshFilter>();
            if(renderer==null||!renderer.enabled||filter==null)return;
            var child=go.transform.Find("VisibleShotSurface");
            if(child==null){var surface=new GameObject("VisibleShotSurface");surface.transform.SetParent(go.transform,false);child=surface.transform;}
            var collider=child.GetComponent<MeshCollider>();if(collider==null)collider=child.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh=filter.sharedMesh;
        }

        public static string RepairAll()
        {
            var original = SceneManagerSetup();
            int repaired = 0, barriers = 0;
            try
            {
                foreach (var name in new[] { "Arena", "Map_Stackyard", "Map_Depot55", "Map_Ridgeline", "Map_TrainingYard", "Map_NightRelay" })
                {
                    var scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/" + name + ".unity", OpenSceneMode.Single);
                    foreach (var go in scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Transform>(true)).Select(x => x.gameObject).ToArray())
                    {
                        if (go == null) continue;
                        var filter = go.GetComponent<MeshFilter>();
                        // All built-in primitive surfaces already have exact box geometry.
                        if (filter != null && filter.sharedMesh != null && AssetDatabase.GetAssetPath(filter.sharedMesh).StartsWith("Assets/")
                            && go.GetComponentInParent<Game.Gameplay.Player.InputReader>() == null)
                        {
                            // Only model roots own aggregate boxes; child meshes still each get exact collision.
                            if (go.GetComponent<Collider>() != null || PrefabUtility.IsAnyPrefabInstanceRoot(go)) { Apply(go); repaired++; }
                        }
                        if (go.name.StartsWith("Boundary", StringComparison.OrdinalIgnoreCase) || go.name.StartsWith("MapBoundary", StringComparison.OrdinalIgnoreCase))
                        {
                            if (go.GetComponent<Collider>() != null) { MarkBoundary(go); barriers++; }
                        }
                        foreach (var box in go.GetComponents<BoxCollider>())
                            box.size = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z));
                    }
                    EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
                }
            }
            finally { EditorSceneManager.RestoreSceneManagerSetup(original); }
            return "Exact mesh colliders: " + repaired + "; movement-only boundaries: " + barriers;
        }
        private static SceneSetup[] SceneManagerSetup() => EditorSceneManager.GetSceneManagerSetup();
    }
}
