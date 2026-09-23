var cat=Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog");var rows=new System.Collections.Generic.List<object>();
foreach(var w in cat.Entries.Where(e=>e.definition!=null&&!e.itemId.Contains("lpw")&&e.HasSlot("Optic"))) {
var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(w.definition.ThirdPersonViewPrefab));
try{
var triangles=new System.Collections.Generic.List<Vector3>();
foreach(var mf in root.GetComponentsInChildren<MeshFilter>(true)) {if(mf.name.ToLower().Contains("iron")||mf.name.ToLower().Contains("scope"))continue;var vs=mf.sharedMesh.vertices.Select(v=>root.transform.InverseTransformPoint(mf.transform.TransformPoint(v))).ToArray();foreach(var i in mf.sharedMesh.triangles)triangles.Add(vs[i]);}
var profiles=new System.Collections.Generic.List<object>();
for(float z=-.20f;z<=.301f;z+=.025f){var hits=new System.Collections.Generic.List<float>();for(int i=0;i<triangles.Count;i+=3){var a=triangles[i];var b=triangles[i+1];var c=triangles[i+2];var den=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);if(Mathf.Abs(den)<1e-10f)continue;var u=((b.z-c.z)*(-c.x)+(c.x-b.x)*(z-c.z))/den;var v=((c.z-a.z)*(-c.x)+(a.x-c.x)*(z-c.z))/den;if(u>=0&&v>=0&&u+v<=1)hits.Add(u*a.y+v*b.y+(1-u-v)*c.y);}if(hits.Count>0)profiles.Add(new{z=Mathf.Round(z*1000),top=Mathf.Round(hits.Max()*10000)/10,bottom=Mathf.Round(hits.Min()*10000)/10});}
var all=triangles.ToArray();var front=all.Max(p=>p.z);var tip=all.Where(p=>p.z>front-.0001f).ToArray();
rows.Add(new{w.itemId,profiles,tip=new{z=front,x=(tip.Min(p=>p.x)+tip.Max(p=>p.x))/2,y=(tip.Min(p=>p.y)+tip.Max(p=>p.y))/2}});
}finally{PrefabUtility.UnloadPrefabContents(root);}}
return rows;
