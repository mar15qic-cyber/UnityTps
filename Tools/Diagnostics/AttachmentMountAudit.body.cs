// Read-only Unity execute_code method body. Replace __WEAPON_ID__ per invocation.
// Instantiates isolated prefab contents; never saves prefabs, scenes or calibration.
var weapons = Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog");
var catalog = Resources.Load<Game.Gameplay.Weapon.AttachmentAssetCatalog>("AttachmentAssetCatalog");
var weapon = weapons.Entries.First(e => e.itemId == "__WEAPON_ID__");
var isPistol = weapon.itemId.Contains("handgun") || weapon.itemId == "weapon.service_pistol";
var isCompact = isPistol || weapon.itemId.Contains("smg");
var entries = catalog.Entries.Where(e => e.HasModel && weapon.HasSlot(e.slot.ToString())
    && (e.slot != Game.Gameplay.Weapon.AttachmentSlotType.Muzzle
        || e.itemId == "attach.lpfp.muffler.01"
        || e.itemId == (isCompact ? "attach.lpw.muffler.01" : "attach.lpw.muffler.02"))).ToArray();
var rows = new System.Collections.Generic.List<object>();
var width = 360; var height = 230;
var sheet = new Texture2D(width * 4, height * Math.Max(1, entries.Length), TextureFormat.RGB24, false);
var dir = "E:/UnityProject/UnityFpsLowPoly/Temp/AttachmentMountAudit";
System.IO.Directory.CreateDirectory(dir);
var gunMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
var attMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
gunMat.SetColor("_BaseColor", new Color(.62f,.69f,.76f));
attMat.SetColor("_BaseColor", new Color(1f,.48f,.08f));
gunMat.SetFloat("_Cull",0); attMat.SetFloat("_Cull",0);
// Triangle projection: output x/y are footprint coordinates; z increases away from the gun.
Func<Vector3,int,Vector3> project = (p,slot) => slot == 1 ? new Vector3(p.y,p.z,-p.x)
    : new Vector3(p.x,p.z,slot == 4 ? -p.y : p.y);
Func<Vector3[],float,float,float[]> hits = (tri,u,v) => {
    var found = new System.Collections.Generic.List<float>();
    for (int k=0;k<tri.Length;k+=3) {
        var p=tri[k];var q=tri[k+1];var r=tri[k+2];
        var den=(q.y-r.y)*(p.x-r.x)+(r.x-q.x)*(p.y-r.y);
        if(Mathf.Abs(den)<1e-10f)continue;
        var a=((q.y-r.y)*(u-r.x)+(r.x-q.x)*(v-r.y))/den;
        var b=((r.y-p.y)*(u-r.x)+(p.x-r.x)*(v-r.y))/den;
        if(a>=-1e-5f&&b>=-1e-5f&&a+b<=1.00001f)found.Add(a*p.z+b*q.z+(1-a-b)*r.z);
    }
    return found.ToArray();
};
try {
for(int ei=0;ei<entries.Length;ei++) {
    var entry=entries[ei];
    for(int vi=0;vi<2;vi++) {
        var prefab=vi==0?weapon.definition.FirstPersonViewPrefab:weapon.definition.ThirdPersonViewPrefab;
        var root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(prefab));
        var allocated = new System.Collections.Generic.List<UnityEngine.Object>();
        try {
            var view=root.GetComponent<Game.Gameplay.Weapon.WeaponAttachmentView>();
            if(view==null)view=root.AddComponent<Game.Gameplay.Weapon.WeaponAttachmentView>();
            // Use production mounting and stock-sight suppression, including authored-frame conversion.
            view.ApplyAttachments(catalog,weapon.itemId,new[]{entry},false);
            var socket=view.GetSocketTransform(entry.slot);
            var mounted=view.FindSpawned(entry.itemId);
            if(socket==null||mounted==null){rows.Add(new{weapon=weapon.definition.DisplayName,attachment=entry.itemId,view=vi==0?"FP":"TP",missing=true});continue;}
            var restFrame=Quaternion.Inverse(root.transform.rotation)*socket.rotation;
            if(vi==0 && weapon.definition.FirstPersonAnimations.AimIdle!=null)
                weapon.definition.FirstPersonAnimations.AimIdle.SampleAnimation(root,0);
            var body=new System.Collections.Generic.List<Vector3>();
            var accessory=new System.Collections.Generic.List<Vector3>();
            var renderMeshes=new System.Collections.Generic.List<Mesh>();
            var renderMatrices=new System.Collections.Generic.List<Matrix4x4>();
            var renderMaterials=new System.Collections.Generic.List<Material>();
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true)) {
                var attached=renderer.transform.IsChildOf(mounted);
                var nm=renderer.name.ToLowerInvariant();
                if(!attached && (nm.Contains("arms")||nm.Contains("bullet")||nm.Contains("shell")||nm.Contains("knife"))) {renderer.enabled=false;continue;}
                if(!renderer.enabled||!renderer.gameObject.activeInHierarchy)continue;
                Mesh mesh=null;
                var skin=renderer as SkinnedMeshRenderer;
                if(skin!=null){mesh=new Mesh();skin.BakeMesh(mesh);allocated.Add(mesh);}
                else {var mf=renderer.GetComponent<MeshFilter>();if(mf!=null)mesh=mf.sharedMesh;}
                if(mesh==null)continue;
                renderMeshes.Add(mesh);renderMatrices.Add(renderer.transform.localToWorldMatrix);renderMaterials.Add(attached?attMat:gunMat);
                var vertices=mesh.vertices;var indices=mesh.triangles;
                var measureRotation=entry.slot==Game.Gameplay.Weapon.AttachmentSlotType.Tactical
                    ? Quaternion.Inverse(mounted.localRotation) : Quaternion.identity;
                var transformed=vertices.Select(p=>project(measureRotation*socket.InverseTransformPoint(renderer.transform.TransformPoint(p)),(int)entry.slot)).ToArray();
                var output=attached?accessory:body;
                foreach(var idx in indices)output.Add(transformed[idx]);
                renderer.sharedMaterials=renderer.sharedMaterials.Select(m=>attached?attMat:gunMat).ToArray();
                renderer.gameObject.layer=0;
            }
            var at=accessory.ToArray();var bt=body.ToArray();
            if(at.Length==0||bt.Length==0)throw new Exception("No mesh: "+weapon.itemId+" "+entry.itemId+" view "+vi+" at "+at.Length+" bt "+bt.Length);
            var baseZ=at.Min(p=>p.z);
            var foot=at.Where(p=>p.z<=baseZ+.0015f).ToArray();
            var lo=new Vector2(foot.Min(p=>p.x),foot.Min(p=>p.y));
            var hi=new Vector2(foot.Max(p=>p.x),foot.Max(p=>p.y));
            var gaps=new System.Collections.Generic.List<float>();var total=0;
            for(int ix=0;ix<11;ix++)for(int iy=0;iy<7;iy++) {
                var u=Mathf.Lerp(lo.x,hi.x,(ix+.5f)/11);var v=Mathf.Lerp(lo.y,hi.y,(iy+.5f)/7);
                var ah=hits(at,u,v);if(ah.Length==0)continue;
                var bottom=ah.Min();if(bottom>baseZ+.008f)continue;
                total++;
                var bh=hits(bt,u,v).Where(z=>Mathf.Abs(z-bottom)<.25f).ToArray();
                if(bh.Length>0)gaps.Add((bottom-bh.Max())*1000f);
            }
            gaps.Sort();
            rows.Add(new {weapon=weapon.definition.DisplayName,weaponId=weapon.itemId,attachment=entry.itemId,slot=entry.slot.ToString(),view=vi==0?"FP":"TP+Preview",samples=total,supported=gaps.Count,
                minMm=gaps.Count==0?(float?)null:gaps[0],medianMm=gaps.Count==0?(float?)null:gaps[gaps.Count/2],maxMm=gaps.Count==0?(float?)null:gaps[gaps.Count-1],
                nearContact=gaps.Count(g=>Mathf.Abs(g)<=2f),penetrating=gaps.Count(g=>g< -2f),floating=gaps.Count(g=>g>2f),
                baseMm=baseZ*1000f,footWidthMm=(hi.x-lo.x)*1000f,footDepthMm=(hi.y-lo.y)*1000f,
                mountPosition=mounted.localPosition.ToString("F6"),restFrame=restFrame.ToString("F6")});
            var preview=new UnityEditor.PreviewRenderUtility();
            try {
            var cam=preview.camera;
            cam.orthographic=true;cam.nearClipPlane=.01f;cam.farClipPlane=4;cam.clearFlags=CameraClearFlags.SolidColor;
            cam.backgroundColor=new Color(.055f,.075f,.095f);cam.cullingMask=1;cam.allowHDR=false;cam.allowMSAA=false;
            var accessoryBounds=new Bounds(socket.position,Vector3.zero);
            foreach(var r in mounted.GetComponentsInChildren<Renderer>())accessoryBounds.Encapsulate(r.bounds);
            var center=Vector3.Lerp(socket.position,accessoryBounds.center,.4f);
            cam.orthographicSize=Mathf.Max(.095f,accessoryBounds.size.magnitude*.5f);
            for(int angle=0;angle<2;angle++) {
                var look=angle==0?-socket.forward:(-socket.forward+socket.right*.6f-socket.up*.5f).normalized;
                cam.transform.position=center-look*1.5f;cam.transform.rotation=Quaternion.LookRotation(look,socket.up);
                preview.BeginPreview(new Rect(0,0,width,height),GUIStyle.none);
                for(int mi=0;mi<renderMeshes.Count;mi++)for(int sub=0;sub<renderMeshes[mi].subMeshCount;sub++)
                    preview.DrawMesh(renderMeshes[mi],renderMatrices[mi],renderMaterials[mi],sub);
                preview.Render(true,false);
                var rendered=preview.EndPreview();var previous=RenderTexture.active;
                var scaled=RenderTexture.GetTemporary(width,height,0);Graphics.Blit(rendered,scaled);RenderTexture.active=scaled;
                var tile=new Texture2D(width,height,TextureFormat.RGB24,false);
                tile.ReadPixels(new Rect(0,0,width,height),0,0);tile.Apply();RenderTexture.active=previous;RenderTexture.ReleaseTemporary(scaled);
                sheet.SetPixels((vi*2+angle)*width,(entries.Length-1-ei)*height,width,height,tile.GetPixels());
                UnityEngine.Object.DestroyImmediate(tile);
            }
            }finally{preview.Cleanup();}
        } finally {
            foreach(var o in allocated)if(o!=null)UnityEngine.Object.DestroyImmediate(o);
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
sheet.Apply();var path=dir+"/"+weapon.itemId+".png";
System.IO.File.WriteAllBytes(path,sheet.EncodeToPNG());
return new {weapon=weapon.definition.DisplayName,weaponId=weapon.itemId,previewEqualsTp=weapon.previewPrefab==weapon.definition.ThirdPersonViewPrefab,
    attachmentOrder=entries.Select(e=>new{e.itemId,e.displayName}).ToArray(),columns=new[]{"FP side","FP oblique","TP side","TP oblique"},path,rows};
}finally{UnityEngine.Object.DestroyImmediate(sheet);UnityEngine.Object.DestroyImmediate(gunMat);UnityEngine.Object.DestroyImmediate(attMat);}
