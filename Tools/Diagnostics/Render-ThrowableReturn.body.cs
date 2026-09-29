var weaponId = "weapon.m4";
var automatic = true;
var before = false;
var dir = "Logs/ThrowableReturn0929/" + (weaponId == "weapon.m4" ? "rifle" : "pistol") + "-" + (automatic ? "auto" : "manual") + "-" + (before ? "before" : "after");
System.IO.Directory.CreateDirectory(dir);
var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
System.Action<object,string,object> set = (o,n,x) => o.GetType().GetField(n,hidden).SetValue(o,x);
System.Action<object,string> call = (o,n) => o.GetType().GetMethod(n,hidden).Invoke(o,null);
var definition = UnityEngine.Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId==weaponId).definition;
var view = UnityEditor.PrefabUtility.LoadPrefabContents(UnityEditor.AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(view.scene,1UL<<60);
var meshes = new System.Collections.Generic.List<UnityEngine.Mesh>();
Game.Presentation.Animation.FPWeaponAnimator fp = null;
Game.Gameplay.Combat.ThrowableController throwable = null;
System.Action selection = null;
try
{
    var host = new UnityEngine.GameObject("PreviewHost"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host,view.scene);
    var camera = host.AddComponent<UnityEngine.Camera>(); camera.enabled=false; camera.scene=view.scene;
    camera.cameraType=UnityEngine.CameraType.Preview; camera.overrideSceneCullingMask=1UL<<60;
    camera.fieldOfView=45; camera.nearClipPlane=.01f; camera.clearFlags=UnityEngine.CameraClearFlags.SolidColor;
    camera.backgroundColor=new UnityEngine.Color(.25f,.3f,.35f);
    var owner = new UnityEngine.GameObject("InactivePreviewOwner"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner,view.scene); owner.SetActive(false);
    var actions = owner.AddComponent<Game.Gameplay.Action.ActionSystem>();
    var controller = owner.AddComponent<Game.Gameplay.Weapon.WeaponController>(); set(controller,"definition",definition);
    throwable=owner.AddComponent<Game.Gameplay.Combat.ThrowableController>();
    set(throwable,"_catalog",UnityEngine.Resources.Load<Game.Gameplay.Combat.ThrowableCatalog>("ThrowableCatalog"));
    set(throwable,"_actions",actions); call(throwable,"OnEnable");
    fp=view.GetComponent<Game.Presentation.Animation.FPWeaponAnimator>();
    var graph=view.GetComponent<Animancer.AnimancerComponent>(); graph.Animator.cullingMode=UnityEngine.AnimatorCullingMode.AlwaysAnimate;
    graph.Graph.UpdateMode=UnityEngine.Playables.DirectorUpdateMode.Manual;
    set(fp,"_animancer",graph); set(fp,"controller",controller); set(fp,"actionSystem",actions); set(fp,"_throwables",throwable);
    call(fp,"LoadClips");
    selection=()=>{ call(fp,"HandleThrowableSelection"); if(before && !throwable.IsEquipped) {set(fp,"_weaponDrawPending",false); call(fp,"PlayIdle");} };
    throwable.OnSelectionChanged+=selection;
    var motionRoot=new UnityEngine.GameObject("PreviewWeaponRoot"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(motionRoot,view.scene);
    view.transform.SetParent(motionRoot.transform,false);
    var motion=motionRoot.AddComponent<Game.Presentation.Camera.FPWeaponMotion>(); set(motion,"_weapon",controller); set(motion,"_viewCamera",camera);
    var driver=view.GetComponent<Game.Presentation.Animation.LPWGunPoseDriver>(); if(driver!=null) call(driver,"Awake");
    var left=view.GetComponent<Game.Presentation.Animation.FPLeftHandIK>();
    if(left!=null) {call(left,"Awake"); set(left,"_viewCamera",camera.transform);}
    var light=new UnityEngine.GameObject("Light"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(light,view.scene);
    light.transform.rotation=UnityEngine.Quaternion.Euler(35,-30,0); light.AddComponent<UnityEngine.Light>().type=UnityEngine.LightType.Directional;
    var originals=view.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true);
    var mirrors=new System.Collections.Generic.List<UnityEngine.MeshRenderer>();
    foreach(var source in originals)
    {
        var go=new UnityEngine.GameObject("Baked_"+source.name); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,view.scene);
        var mesh=new UnityEngine.Mesh(); meshes.Add(mesh); go.AddComponent<UnityEngine.MeshFilter>().sharedMesh=mesh;
        var renderer=go.AddComponent<UnityEngine.MeshRenderer>(); renderer.sharedMaterials=source.sharedMaterials; mirrors.Add(renderer);
    }
    set(throwable,"<IsEquipped>k__BackingField",true); set(throwable,"<SelectedType>k__BackingField",Game.Gameplay.Combat.ThrowableType.Frag); selection();
    if(before)
    {
        var config=view.GetComponentInChildren<Game.Presentation.Animation.FPThrowablePresentation>(true);
        config.Configure(config.Draw,config.Prepare,UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>("Assets/_Project/Review/PinCooperation/Frag-Throw.anim"));
    }
    var tick=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("TickThrowablePresentation",hidden);
    for(int i=0;i<36;i++) {graph.Evaluate(1f/60f); tick.Invoke(fp,new object[]{1f/60f,false});}
    var records=new System.Collections.Generic.List<object>();
    for(int frame=0;frame<120;frame++)
    {
        float t=frame/30f;
        if(frame==15)
        {
            if(automatic)
            {
                actions.TryStart(Game.Gameplay.Action.PlayerActionType.GrenadeThrow,throwable.ThrowActionSeconds);
                typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("HandleThrowStarted",hidden).Invoke(fp,new object[]{throwable.SelectedType});
            }
            else throwable.Unequip();
        }
        if(automatic && frame>=15) set(fp,"_throwStartedAt",UnityEngine.Time.time-(t-.5f));
        if(frame>15) actions.Tick(1f/30f);
        call(fp,"Update");
        graph.Evaluate(frame==15 ? 0 : 1f/30f);
        call(fp,"LateUpdate"); call(motion,"LateUpdate");
        if(driver!=null) call(driver,"LateUpdate"); if(left!=null) call(left,"LateUpdate");
        records.Add(new {time=t,clip=graph.States.Current.Clip.name,throwableActive=fp.IsThrowablePresentationActive,action=actions.CurrentAction.ToString()});
        var enabled=originals.Select(r=>r.enabled).ToArray();
        for(int i=0;i<originals.Length;i++)
        {
            var source=originals[i]; source.BakeMesh(meshes[i]);
            mirrors[i].transform.SetPositionAndRotation(source.transform.position,source.transform.rotation);
            mirrors[i].transform.localScale=source.transform.lossyScale;
            mirrors[i].enabled=source.enabled && source.gameObject.activeInHierarchy; source.enabled=false;
        }
        var rt=new UnityEngine.RenderTexture(640,360,24); var tex=new UnityEngine.Texture2D(640,360,UnityEngine.TextureFormat.RGB24,false);
        var previous=UnityEngine.RenderTexture.active;
        try
        {
            rt.Create(); camera.aspect=640f/360f;
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest {destination=rt});
            UnityEngine.RenderTexture.active=rt; tex.ReadPixels(new UnityEngine.Rect(0,0,640,360),0,0); tex.Apply();
            System.IO.File.WriteAllBytes(dir+"/frame-"+frame.ToString("D3")+".png",tex.EncodeToPNG());
        }
        finally
        {
            UnityEngine.RenderTexture.active=previous; rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
            for(int i=0;i<originals.Length;i++) originals[i].enabled=enabled[i];
        }
    }
    System.IO.File.WriteAllText(dir+"/states.json",Newtonsoft.Json.JsonConvert.SerializeObject(records));
    return dir;
}
finally
{
    if(throwable!=null) {if(selection!=null) throwable.OnSelectionChanged-=selection; call(throwable,"OnDisable");}
    if(fp!=null) call(fp,"ClearThrowablePresentation");
    foreach(var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
    view.transform.SetParent(null,false); UnityEditor.PrefabUtility.UnloadPrefabContents(view);
}
