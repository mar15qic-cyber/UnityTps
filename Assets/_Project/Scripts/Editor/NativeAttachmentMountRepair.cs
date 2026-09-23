using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// Native mount repair. TP receiver coordinates are transferred to FP weapon-bone
    /// coordinates only after a nearest-surface registration check. Never infer an FP
    /// gun axis from prefab/world orientation, or reuse another weapon's sockets.
    /// Measurements: Tools/Diagnostics/AttachmentRegistration.body.cs (2026-09-22).
    /// </summary>
    public static class NativeAttachmentMountRepair
    {
        public sealed class Registration
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public Registration(Vector3 p, Quaternion r) { Position = p; Rotation = r; }
            public Vector3 Point(Vector3 p) => Rotation * p + Position;
        }
        public static readonly Dictionary<string, Registration> Registrations = new()
        {
            { "weapon.m4", new Registration(new Vector3(0.002951380f, 0.134166956f, 0.128509909f), new Quaternion(-0.286290616f, 0.000001212f, 0.000003070f, 0.958142800f)) },
            { "weapon.ak", new Registration(new Vector3(-0.000449271f, 0.091298014f, 0.042371504f), new Quaternion(-0.286294371f, 0.000000001f, 0.000002341f, 0.958141744f)) },
            { "weapon.service_pistol", new Registration(new Vector3(-0.000017286f, 0.019942116f, 0.023439556f), new Quaternion(-0.286294967f, -0.000000743f, -0.000000312f, 0.958141565f)) },
            { "weapon.rifle03", new Registration(new Vector3(0.001641152f, 0.114023149f, 0.054761470f), new Quaternion(-0.286294132f, -0.000000694f, 0.000000041f, 0.958141800f)) },
            { "weapon.smg01", new Registration(new Vector3(-0.000108830f, 0.062500280f, 0.101117596f), new Quaternion(-0.286293834f, -0.000000737f, -0.000000206f, 0.958141900f)) },
            { "weapon.smg02", new Registration(new Vector3(0.000096459f, 0.093822510f, 0.080569230f), new Quaternion(-0.286294550f, 0.000002661f, 0.000009007f, 0.958141600f)) },
            { "weapon.shotgun01", new Registration(new Vector3(0.000154814f, 0.050874062f, 0.077312940f), new Quaternion(-0.286294550f, -0.000000812f, -0.000000316f, 0.958141700f)) },
            { "weapon.handgun02", new Registration(new Vector3(-0.000044411f, 0.016790576f, 0.016555786f), new Quaternion(0.286294758f, 0.000000684f, 0.000000195f, -0.958141700f)) },
            { "weapon.handgun03", new Registration(new Vector3(-0.000031395f, 0.019680936f, 0.023246357f), new Quaternion(0.286293954f, 0.000000785f, 0.000000234f, -0.958141900f)) },
            { "weapon.handgun04", new Registration(new Vector3(0.000000055f, 0.030190438f, 0.006338729f), new Quaternion(-0.286293834f, -0.000000715f, -0.000000137f, 0.958142000f)) },
            { "weapon.smg03", new Registration(new Vector3(-0.000255899f, 0.014958471f, 0.008941650f), new Quaternion(-0.286295900f, -0.000000894f, -0.000000238f, 0.958141267f)) },
            { "weapon.smg04", new Registration(new Vector3(-0.000253979f, 0.062913220f, 0.040423445f), new Quaternion(-0.287282900f, -0.000000776f, -0.000000155f, 0.957845800f)) },
            { "weapon.smg05", new Registration(new Vector3(-0.000507444f, 0.057383444f, 0.052408360f), new Quaternion(-0.286293500f, -0.000000759f, -0.000000182f, 0.958142000f)) },
        };
        // Longitudinal seat centers on the receiver, NOT whole-gun/iron-sight centroids.
        private static readonly Dictionary<string, float> OpticZ = new()
        {
            {"weapon.m4",-.105f}, {"weapon.ak",-.0227f}, {"weapon.rifle03",-.067f},
            {"weapon.service_pistol",.006f}, {"weapon.handgun02",.006f},
            {"weapon.handgun03",.006f}, {"weapon.handgun04",.020f},
            {"weapon.smg01",.015f}, {"weapon.smg02",-.045f}, {"weapon.smg03",-.075f},
            {"weapon.smg04",.094f}, {"weapon.smg05",-.030f}, {"weapon.shotgun01",-.075f}
        };
        private static readonly HashSet<string> NeedsRail = new()
        {
            "weapon.m4","weapon.service_pistol","weapon.handgun02","weapon.handgun03",
            "weapon.handgun04","weapon.smg02","weapon.smg03","weapon.smg05"
        };
        private static readonly Dictionary<string, Vector2> TacticalYZ = new()
        {
            {"weapon.ak",new(.027f,.220f)}, {"weapon.rifle03",new(.024f,.210f)},
            {"weapon.smg01",new(.044f,.160f)}, {"weapon.smg03",new(.020f,.155f)},
            {"weapon.smg04",new(.070f,.113f)}, {"weapon.smg05",new(.022f,.196f)},
            {"weapon.shotgun01",new(.005f,.218f)}
        };
        private static readonly Dictionary<string,float> GripZ = new()
        { {"weapon.smg03",.137f} };
        private const string Folder = "Assets/_Project/Generated/AttachmentMounts";
        private static Quaternion Canonical => Quaternion.Euler(0,90,0);

        [MenuItem("Tools/Attachments/Repair Verified Native Mounts")]
        public static void Run()
        {
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            // Capture existing FP-local optical metadata before normalizing old calibration.
            var old = new Dictionary<string,Vector3>();
            foreach (var w in weapons.Entries.Where(e=>Registrations.ContainsKey(e.itemId)))
            {
                var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.FirstPersonViewPrefab));
                try {
                    var view=root.GetComponent<WeaponAttachmentView>() ?? root.AddComponent<WeaponAttachmentView>();
                    foreach(var e in catalog.Entries.Where(e=>e.slot==AttachmentSlotType.Optic && e.HasModel)) {
                        view.ApplyAttachments(catalog,w.itemId,new[]{e},false);
                        old[w.itemId+"|"+e.itemId]=view.FindSpawned(e.itemId).localPosition;
                    }
                    if(RegistrationError(w.itemId, root, w.definition.ThirdPersonViewPrefab) > .0005f)
                        throw new InvalidOperationException("Gun mesh changed; re-register before writing: "+w.itemId);
                } finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            // Mutate only catalog entries here; broad catalog/definition regeneration is unnecessary.
            catalog.EditorEntries.RemoveAll(e=>WeaponAttachmentStore.IsRetiredAttachmentId(e.itemId));
            var silencer=catalog.EditorEntries.First(e=>e.itemId=="attach.lpfp.muffler.01");
            foreach(var e in catalog.EditorEntries.Where(e=>e.slot==AttachmentSlotType.Muzzle)) {
                e.prefab=silencer.prefab; e.prefabPath=silencer.prefabPath; e.mountEuler=silencer.mountEuler;
                var points=EntryTriangles(e);
                e.mountOffset=new Vector3(-points.Max(p=>p.x)+.006f,
                    -(points.Max(p=>p.y)+points.Min(p=>p.y))*.5f,
                    -(points.Max(p=>p.z)+points.Min(p=>p.z))*.5f);
            }
            foreach(var e in catalog.EditorEntries.Where(e=>e.HasModel&&e.slot==AttachmentSlotType.Optic)) {
                var points=EntryTriangles(e);
                float bottom=points.Min(p=>p.y);
                var foot=points.Where(p=>p.y<bottom+.0016f).ToArray();
                float cx=(foot.Min(p=>p.x)+foot.Max(p=>p.x))*.5f;
                // The rail mates with the inner clamp ceiling, not the low side screws.
                float seat=Surface(points,cx,0,true);
                e.mountOffset=new Vector3(-cx,-seat,0);
            }
            foreach(var w in weapons.Entries.Where(e=>Registrations.ContainsKey(e.itemId))) {
                foreach(var e in catalog.Entries.Where(e=>e.HasModel)) {
                    catalog.Calibration.Set(w.itemId,e.itemId,Vector3.zero,Vector3.zero);
                    if(e.slot==AttachmentSlotType.Optic && catalog.Calibration.TryGetOpticAim(w.itemId,e.itemId,out var aim)) {
                        var delta=e.mountOffset-old[w.itemId+"|"+e.itemId];
                        aim.EyePointLocal+=delta;
                        if(aim.HasAxisFront)aim.AxisFrontPointLocal+=delta;
                        if(aim.HasWindow)aim.WindowCenterLocal+=delta;
                        catalog.Calibration.SetOpticAim(w.itemId,e.itemId,aim);
                    }
                }
                Repair(w.itemId,w.definition);
            }
            foreach(var w in weapons.Entries.Where(e=>e.itemId=="weapon.smg01"||e.itemId=="weapon.smg04"||e.itemId=="weapon.smg05"))
                w.slotCapabilities=w.slotCapabilities.Where(s=>s!="Underbarrel").ToArray();
            EditorUtility.SetDirty(weapons);EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssetIfDirty(weapons);
            AssetDatabase.SaveAssetIfDirty(catalog);
            AssetDatabase.SaveAssetIfDirty(catalog.Calibration);
            Debug.Log("[MountRepair] 13 native FP/TP pairs repaired; LPW suppressors retired. Client playtest still required.");
        }

        public static float RegistrationError(string id,GameObject fp,GameObject tp)
        {
            var reg=Registrations[id];var bone=fp.transform.Find("Armature/weapon");
            var skin=fp.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r=>!r.name.ToLowerInvariant().Contains("arms")&&!r.name.ToLowerInvariant().Contains("knife"))
                .OrderByDescending(r=>r.sharedMesh.vertexCount).First();
            var body=tp.GetComponentsInChildren<MeshFilter>(true).OrderByDescending(m=>m.sharedMesh.vertexCount).First();
            var mesh=new Mesh();
            try {
                skin.BakeMesh(mesh);
                var fv=mesh.vertices.Select(p=>bone.InverseTransformPoint(skin.transform.TransformPoint(p))).ToArray();
                var tv=body.sharedMesh.vertices.Select(p=>tp.transform.InverseTransformPoint(body.transform.TransformPoint(p))).ToArray();
                float max=0;
                for(int i=0;i<tv.Length;i+=Math.Max(1,tv.Length/160)) {
                    var p=reg.Point(tv[i]);
                    max=Mathf.Max(max,Mathf.Sqrt(fv.Min(q=>(q-p).sqrMagnitude)));
                }
                return max;
            } finally {Object.DestroyImmediate(mesh);}
        }

        public static void NormalizeOpticSeat(AttachmentAssetEntry entry)
        {
            if (!entry.HasModel || entry.slot != AttachmentSlotType.Optic) return;
            var points = EntryTriangles(entry);
            var foot = points.Where(p => p.y < points.Min(q => q.y) + .0016f).ToArray();
            float center = (foot.Min(p => p.x) + foot.Max(p => p.x)) * .5f;
            entry.mountOffset = new Vector3(-center, -Surface(points, center, 0, true), 0);
        }

        public static void Repair(string id,WeaponDefinition definition)
        {
            var tpPath=AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab);
            var fpPath=AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab);
            var tp=PrefabUtility.LoadPrefabContents(tpPath);
            var fp=PrefabUtility.LoadPrefabContents(fpPath);
            try {
                if(RegistrationError(id,fp,tp)>.0005f)throw new InvalidOperationException("Invalid FP/TP registration: "+id);
                var triangles=BodyTriangles(tp);
                var body=tp.GetComponentsInChildren<MeshFilter>(true).OrderByDescending(m=>m.sharedMesh.vertexCount).First();
                var bodyPoints=body.sharedMesh.vertices.Select(p=>tp.transform.InverseTransformPoint(body.transform.TransformPoint(p))).ToArray();
                float front=bodyPoints.Max(p=>p.z);
                var tip=bodyPoints.Where(p=>p.z>front-(id=="weapon.smg04"?.01f:.001f)).ToArray();
                var muzzle=new Vector3((tip.Min(p=>p.x)+tip.Max(p=>p.x))*.5f,(tip.Min(p=>p.y)+tip.Max(p=>p.y))*.5f,front);
                float z=OpticZ[id], top=float.NegativeInfinity;
                for(int i=0;i<=24;i++)for(int j=-1;j<=1;j++)
                    top=Mathf.Max(top,Surface(triangles,j*.009f,z-.05f+i*.1f/24,false));
                bool adapter=NeedsRail.Contains(id);
                var optic=new Vector3(0,top+(adapter?.003f:0),z);
                var poses=new Dictionary<AttachmentSlotType,(Vector3 p,Quaternion r)> {
                    {AttachmentSlotType.Optic,(optic,Canonical)}, {AttachmentSlotType.Muzzle,(muzzle,Canonical)}
                };
                if(TacticalYZ.TryGetValue(id,out var yz)) {
                    var side=triangles.Select(p=>new Vector3(p.y,p.x,p.z)).ToArray();
                    float x=Surface(side,yz.x,yz.y,false)-.001f;
                    poses[AttachmentSlotType.Tactical]=(new Vector3(x,yz.x,yz.y),
                        Quaternion.LookRotation(Vector3.forward,Vector3.right)*Canonical);
                } else if(id=="weapon.service_pistol") {
                    float y=Surface(triangles,0,.096f,true)+.001f;
                    poses[AttachmentSlotType.Tactical]=(new Vector3(0,y,.096f),
                        Quaternion.LookRotation(Vector3.forward,Vector3.down)*Canonical);
                }
                if(GripZ.TryGetValue(id,out var gz))
                    poses[AttachmentSlotType.Underbarrel]=(new Vector3(0,Surface(triangles,0,gz,true)+.001f,gz),Canonical);
                Mesh rail=adapter?BuildRail(id,triangles,optic):null;
                Write(tp,tp.transform,poses,null,rail);
                Write(fp,fp.transform.Find("Armature/weapon"),poses,Registrations[id],rail);
                foreach(var child in fp.GetComponentsInChildren<Transform>(true))
                    if(WeaponAttachmentView.IsOptionalDemoAccessory(child.name)||child.name=="rails")child.gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(tp,tpPath);
                PrefabUtility.SaveAsPrefabAsset(fp,fpPath);
            } finally {PrefabUtility.UnloadPrefabContents(tp);PrefabUtility.UnloadPrefabContents(fp);}
        }

        private static void Write(GameObject root,Transform parent,
            Dictionary<AttachmentSlotType,(Vector3 p,Quaternion r)> poses,Registration registration,Mesh rail)
        {
            foreach(var socket in root.GetComponentsInChildren<AttachmentSocket>(true))
                if(!poses.ContainsKey(socket.Slot))Object.DestroyImmediate(socket.gameObject);
            foreach(var pair in poses) {
                var socket=root.GetComponentsInChildren<AttachmentSocket>(true).FirstOrDefault(s=>s.Slot==pair.Key);
                if(socket==null)socket=new GameObject("Attach_"+pair.Key).AddComponent<AttachmentSocket>();
                socket.transform.SetParent(parent,false);
                socket.transform.localPosition=registration==null?pair.Value.p:registration.Point(pair.Value.p);
                socket.transform.localRotation=registration==null?pair.Value.r:registration.Rotation*pair.Value.r;
                var so=new SerializedObject(socket);so.FindProperty("slot").enumValueIndex=(int)pair.Key;
                so.FindProperty("geometryVerified").boolValue=true;so.ApplyModifiedPropertiesWithoutUndo();
                var prior=socket.transform.Find("OpticMountAdapter");if(prior!=null)Object.DestroyImmediate(prior.gameObject);
                if(pair.Key==AttachmentSlotType.Optic&&rail!=null) {
                    var adapter=new GameObject("OpticMountAdapter");adapter.transform.SetParent(socket.transform,false);
                    adapter.AddComponent<MeshFilter>().sharedMesh=rail;
                    adapter.AddComponent<MeshRenderer>().sharedMaterial=RailMaterial();
                    adapter.SetActive(false);
                }
            }
        }

        public static Vector3[] BodyTriangles(GameObject root)
        {
            var points=new List<Vector3>();
            foreach(var mf in root.GetComponentsInChildren<MeshFilter>(true)) {
                var n=mf.name.ToLowerInvariant();
                if(n.Contains("iron")||n.Contains("scope")||n.Contains("silencer")||mf.transform.GetComponentInParent<AttachmentSocket>()!=null)continue;
                var v=mf.sharedMesh.vertices.Select(p=>root.transform.InverseTransformPoint(mf.transform.TransformPoint(p))).ToArray();
                foreach(int i in mf.sharedMesh.triangles)points.Add(v[i]);
            }
            return points.ToArray();
        }
        private static Vector3[] EntryTriangles(AttachmentAssetEntry e)
        {
            var points=new List<Vector3>();
            foreach(var mf in e.prefab.GetComponentsInChildren<MeshFilter>(true)) {
                var v=mf.sharedMesh.vertices.Select(p=>e.MountRotation*e.prefab.transform.InverseTransformPoint(mf.transform.TransformPoint(p))).ToArray();
                foreach(int i in mf.sharedMesh.triangles)points.Add(v[i]);
            }
            return points.ToArray();
        }
        // Exact triangle intersections along local Y; no AABB/vertex-only proxy.
        public static float Surface(Vector3[] tri,float x,float z,bool bottom)
        {
            float hit=bottom?float.PositiveInfinity:float.NegativeInfinity;
            for(int i=0;i<tri.Length;i+=3) {
                var a=tri[i];var b=tri[i+1];var c=tri[i+2];
                float den=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
                if(Mathf.Abs(den)<1e-10f)continue;
                float u=((b.z-c.z)*(x-c.x)+(c.x-b.x)*(z-c.z))/den;
                float v=((c.z-a.z)*(x-c.x)+(a.x-c.x)*(z-c.z))/den;
                if(u<-.00001f||v<-.00001f||u+v>1.00001f)continue;
                float y=u*a.y+v*b.y+(1-u-v)*c.y;
                hit=bottom?Mathf.Min(hit,y):Mathf.Max(hit,y);
            }
            if(!float.IsFinite(hit))throw new InvalidOperationException($"No mounting surface at x={x}, z={z}");
            return hit;
        }

        private static void EnsureFolder()
        {
            if(!AssetDatabase.IsValidFolder("Assets/_Project/Generated"))AssetDatabase.CreateFolder("Assets/_Project","Generated");
            if(!AssetDatabase.IsValidFolder(Folder))AssetDatabase.CreateFolder("Assets/_Project/Generated","AttachmentMounts");
        }
        private static Material RailMaterial()
        {
            EnsureFolder();var path=Folder+"/Rail.mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(mat!=null)return mat;
            mat=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor",new Color(.075f,.080f,.085f));mat.SetFloat("_Smoothness",.25f);
            AssetDatabase.CreateAsset(mat,path);return mat;
        }
        private static Mesh BuildRail(string id,Vector3[] body,Vector3 seat)
        {
            EnsureFolder();var vertices=new List<Vector3>();var indices=new List<int>();
            // A low receiver adapter, underside follows the measured gun surface.
            // Cross-section is a dovetail: clamp tips clear the narrower lower neck.
            var inverse=Quaternion.Inverse(Canonical);
            for(int i=0;i<=12;i++) {
                float z=seat.z-.054f+i*.108f/12;
                foreach(float x in new[]{-.009f,.009f})
                    vertices.Add(inverse*(new Vector3(x,Surface(body,x,z,false)-.001f,z)-seat));
                foreach(float x in new[]{-.011f,.011f})
                    vertices.Add(inverse*(new Vector3(x,seat.y,z)-seat));
            }
            Action<int,int,int,int> quad=(a,b,c,d)=>{indices.AddRange(new[]{a,b,c,a,c,d});};
            for(int i=0;i<12;i++) {int a=i*4,b=a+4;
                quad(a,a+1,b+1,b);quad(a+2,b+2,b+3,a+3);
                quad(a,b,b+2,a+2);quad(a+1,a+3,b+3,b+1);
            }
            quad(0,2,3,1);quad(48,49,51,50);
            var path=Folder+"/"+id+".asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(mesh==null){mesh=new Mesh{name=id+" receiver adapter"};AssetDatabase.CreateAsset(mesh,path);}
            mesh.Clear();mesh.SetVertices(vertices);mesh.SetTriangles(indices,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);AssetDatabase.SaveAssetIfDirty(mesh);return mesh;
        }
    }
}
