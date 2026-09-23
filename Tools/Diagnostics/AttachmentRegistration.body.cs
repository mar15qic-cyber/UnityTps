// Read-only rigid registration using congruent triangles, validated against the whole TP body.
var catalog=Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog");
var results=new System.Collections.Generic.List<object>();
foreach(var w in catalog.Entries.Where(e=>e.definition!=null && !e.itemId.Contains("lpw") && e.HasSlot("Optic"))) {
var fp=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.FirstPersonViewPrefab));
var tp=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.ThirdPersonViewPrefab));
var baked=new Mesh();
try {
var bone=fp.transform.Find("Armature/weapon");
var sm=fp.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r=>!r.name.ToLower().Contains("arms")&&!r.name.ToLower().Contains("knife")).OrderByDescending(r=>r.sharedMesh.vertexCount).First();
var mf=tp.GetComponentsInChildren<MeshFilter>(true).OrderByDescending(r=>r.sharedMesh.vertexCount).First();
sm.BakeMesh(baked);
var fv=baked.vertices.Select(v=>bone.InverseTransformPoint(sm.transform.TransformPoint(v))).ToArray();
var tv=mf.sharedMesh.vertices.Select(v=>tp.transform.InverseTransformPoint(mf.transform.TransformPoint(v))).ToArray();
var ft=baked.triangles;var tt=mf.sharedMesh.triangles;
var best=float.PositiveInfinity;var bestRot=Quaternion.identity;var bestPos=Vector3.zero;
var trials=0;
// Congruent scalene triangles give a rigid pose without assuming matching vertex order.
for(int k=0;k<tt.Length && trials<80;k+=3) {
var a=tv[tt[k]];var b=tv[tt[k+1]];var c=tv[tt[k+2]];
var ab=(b-a).magnitude;var ac=(c-a).magnitude;var bc=(c-b).magnitude;
if(ab<.025f||ac<.025f||bc<.025f||Vector3.Cross(b-a,c-a).magnitude<.0003f)continue;
for(int j=0;j<ft.Length && trials<80;j+=3)for(int perm=0;perm<3 && trials<80;perm++) {
var x=fv[ft[j+perm]];var y=fv[ft[j+(perm+1)%3]];var z=fv[ft[j+(perm+2)%3]];
if(Mathf.Abs((y-x).magnitude-ab)>.00005f||Mathf.Abs((z-x).magnitude-ac)>.00005f||Mathf.Abs((z-y).magnitude-bc)>.00005f)continue;
var rot=Quaternion.LookRotation(y-x,Vector3.Cross(y-x,z-x))*Quaternion.Inverse(Quaternion.LookRotation(b-a,Vector3.Cross(b-a,c-a)));
var pos=x-rot*a;float error=0;int n=0;
for(int s=0;s<tv.Length;s+=Math.Max(1,tv.Length/100)) {var p=rot*tv[s]+pos;float d=float.PositiveInfinity;foreach(var q in fv)d=Mathf.Min(d,(q-p).sqrMagnitude);error+=d;n++;}
error=Mathf.Sqrt(error/n);trials++;if(error<best){best=error;bestRot=rot;bestPos=pos;}
if(best<.00005f)break;
}
if(best<.00005f)break;
}
var bounds=new Bounds(tv[0],Vector3.zero);foreach(var p in tv)bounds.Encapsulate(p);
results.Add(new{w.itemId,errorMm=best*1000,trials,rotation=new[]{bestRot.x,bestRot.y,bestRot.z,bestRot.w},position=new[]{bestPos.x,bestPos.y,bestPos.z},body=mf.name,min=bounds.min.ToString("F6"),max=bounds.max.ToString("F6"),sockets=tp.GetComponentsInChildren<Game.Gameplay.Weapon.AttachmentSocket>(true).Select(s=>new{slot=s.Slot.ToString(),p=tp.transform.InverseTransformPoint(s.transform.position).ToString("F6")}).ToArray()});
}finally{UnityEngine.Object.DestroyImmediate(baked);PrefabUtility.UnloadPrefabContents(fp);PrefabUtility.UnloadPrefabContents(tp);}
}
return results;
