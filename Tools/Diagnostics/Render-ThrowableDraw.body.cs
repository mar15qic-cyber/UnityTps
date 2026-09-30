var quick = false;
var side = false;
var dir = "Logs/ThrowableDraw0929/" + (quick ? "interrupt" : "cycle");
System.IO.Directory.CreateDirectory(dir);
var d = UnityEngine.Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
var v = UnityEditor.PrefabUtility.LoadPrefabContents(UnityEditor.AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(v.scene, 1UL << 60);
var host = new UnityEngine.GameObject("StaticThrowPreview");
UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, v.scene);
var meshes = new System.Collections.Generic.List<UnityEngine.Mesh>();
var materials = new System.Collections.Generic.List<UnityEngine.Material>();
try
{
    var pose = new UnityEditor.SerializedObject(v.GetComponent<Game.Presentation.Animation.FPWeaponAnimator>());
    v.transform.position = pose.FindProperty("throwViewLocalPosition").vector3Value;
    v.transform.rotation = UnityEngine.Quaternion.Euler(pose.FindProperty("throwViewLocalEuler").vector3Value);
    var cam = host.AddComponent<UnityEngine.Camera>();
    cam.enabled = false; cam.scene = v.scene; cam.cameraType = UnityEngine.CameraType.Preview;
    cam.overrideSceneCullingMask = 1UL << 60; cam.fieldOfView = 45; cam.nearClipPlane = .01f;
    cam.backgroundColor = new UnityEngine.Color(.25f, .3f, .35f); cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
    var arms = v.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true).First(r => r.name == "arms");
    foreach (var r in v.GetComponentsInChildren<UnityEngine.Renderer>(true)) r.enabled = false;
    var owner = new UnityEngine.GameObject("PreviewThrowableOwner");
    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, v.scene); owner.SetActive(false);
    var throwable = owner.AddComponent<Game.Gameplay.Combat.ThrowableController>();
    var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    typeof(Game.Gameplay.Combat.ThrowableController).GetField("_catalog",hidden).SetValue(throwable,UnityEngine.Resources.Load<Game.Gameplay.Combat.ThrowableCatalog>("ThrowableCatalog"));
    var fp = v.GetComponent<Game.Presentation.Animation.FPWeaponAnimator>();
    var graph = v.GetComponent<Animancer.AnimancerComponent>();
    graph.Animator.cullingMode = UnityEngine.AnimatorCullingMode.AlwaysAnimate;
    graph.Graph.UpdateMode = UnityEngine.Playables.DirectorUpdateMode.Manual;
    typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_animancer",hidden).SetValue(fp,graph);
    typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_throwables",hidden).SetValue(fp,throwable);
    typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_clipsReady",hidden).SetValue(fp,true);
    var selected=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("HandleThrowableSelection",hidden);
    var tick=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("TickThrowablePresentation",hidden);
    var late=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("LateUpdate",hidden);
    var throwStart=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("HandleThrowStarted",hidden);
    var startedAt=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_throwStartedAt",hidden);
    var heldField=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_heldThrowable",hidden);
    System.Action<int> select = index => {
        typeof(Game.Gameplay.Combat.ThrowableController).GetField("<IsEquipped>k__BackingField",hidden).SetValue(throwable,true);
        typeof(Game.Gameplay.Combat.ThrowableController).GetField("<SelectedType>k__BackingField",hidden).SetValue(throwable,(Game.Gameplay.Combat.ThrowableType)index);
        selected.Invoke(fp,null);
    };
    var initial=graph.Play(d.FirstPersonAnimations.Idle); initial.Time=0; initial.Speed=0; graph.Evaluate(0);
    var display = new UnityEngine.GameObject("BakedArms");
    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(display, v.scene);
    display.transform.SetParent(host.transform, false);
    var filter = display.AddComponent<UnityEngine.MeshFilter>();
    display.AddComponent<UnityEngine.MeshRenderer>().sharedMaterials = arms.sharedMaterials;
    var light = new UnityEngine.GameObject("Light");
    light.transform.SetParent(host.transform, false); light.transform.rotation = UnityEngine.Quaternion.Euler(35, -30, 0);
    light.AddComponent<UnityEngine.Light>().type = UnityEngine.LightType.Directional;
    if (side) { host.transform.position = new UnityEngine.Vector3(-1.05f,.16f,.22f); host.transform.LookAt(new UnityEngine.Vector3(0,-.12f,.43f)); cam.orthographic = true; cam.orthographicSize = .37f; }
    var jointNodes = new System.Collections.Generic.List<UnityEngine.Transform>();
    var links = new System.Collections.Generic.List<UnityEngine.LineRenderer>();
    var bones = new[] {v.transform.Find("Armature/arm_R"), v.transform.Find("Armature/arm_R/lower_arm_R"), v.transform.Find("Armature/arm_R/lower_arm_R/hand_R")};
    if(side) {
        var shader = UnityEngine.Shader.Find("Hidden/Internal-Colored");
        if(shader == null) throw new System.Exception("debug shader missing");
        for(int i=0;i<3;i++) {
            var mat = new UnityEngine.Material(shader); materials.Add(mat);
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); mat.SetInt("_ZWrite",0); mat.SetInt("_Cull",0); mat.renderQueue = 5000;
            var joint = new UnityEngine.GameObject("Joint_"+i); joint.transform.SetParent(host.transform,false);
            var line = joint.AddComponent<UnityEngine.LineRenderer>(); line.sharedMaterial=mat; line.positionCount=2; line.useWorldSpace=true;
            line.startWidth=line.endWidth=.016f; line.numCapVertices=8;
            line.startColor=line.endColor = i==0 ? UnityEngine.Color.cyan : i==1 ? new UnityEngine.Color(1f,.65f,0f) : UnityEngine.Color.magenta;
            links.Add(line);
        }
    }
    var samples = new System.Collections.Generic.List<object>();
    int throwFrame=quick?35:252;
    for (int frame = 0; frame < (quick?112:322); frame++)
    {
        float t=frame/60f;
        if(quick) {
            if(frame==0) select(0); if(frame==7) select(1); if(frame==13) select(2); if(frame==20) select(0); if(frame==32) select(1);
        } else {
            if(frame==0) select(0); if(frame==60) select(1); if(frame==120) select(2); if(frame==180) select(0);
        }
        if(frame==throwFrame) throwStart.Invoke(fp,new object[]{throwable.SelectedType});
        bool holding=!quick && frame>=228 && frame<throwFrame;
        tick.Invoke(fp,new object[]{1f/60f,holding});
        graph.Evaluate(frame==0 || frame==throwFrame ? 0 : 1f/60f);
        if(frame>=throwFrame) {
            float elapsed=(frame-throwFrame)/60f;
            startedAt.SetValue(fp,UnityEngine.Time.time-elapsed);
            var body=(UnityEngine.GameObject)heldField.GetValue(fp);
            if(body!=null && elapsed>=throwable.ReleaseDelaySeconds) body.SetActive(false);
        }
        late.Invoke(fp,null);
        var points = (side ? new[]{v.transform.Find("Armature/arm_L"),v.transform.Find("Armature/arm_L/lower_arm_L"),v.transform.Find("Armature/arm_L/lower_arm_L/hand_L")} : bones).Select(b => b.position).ToArray();
        samples.Add(new { t = t-1f, shoulder = new[]{points[0].x,points[0].y,points[0].z}, elbow = new[]{points[1].x,points[1].y,points[1].z}, wrist = new[]{points[2].x,points[2].y,points[2].z} });
        if(side) { for(int j=0;j<3;j++) { links[j].SetPosition(0,points[j]); links[j].SetPosition(1,j<2?points[j+1]:points[j]+UnityEngine.Vector3.up*.014f); } }
        var mesh = new UnityEngine.Mesh(); meshes.Add(mesh); arms.BakeMesh(mesh); filter.sharedMesh = mesh;
        display.transform.SetPositionAndRotation(arms.transform.position, arms.transform.rotation);
        display.transform.localScale = arms.transform.lossyScale;
        var rt = new UnityEngine.RenderTexture(960, 540, 24);
        var tex = new UnityEngine.Texture2D(960, 540, UnityEngine.TextureFormat.RGB24, false);
        var previous = UnityEngine.RenderTexture.active;
        try
        {
            rt.Create(); cam.aspect = 960f / 540f;
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = rt });
            UnityEngine.RenderTexture.active = rt; tex.ReadPixels(new UnityEngine.Rect(0, 0, 960, 540), 0, 0); tex.Apply();
            System.IO.File.WriteAllBytes(dir + "/frame-" + frame.ToString("D3") + ".png", tex.EncodeToPNG());
        }
        finally { UnityEngine.RenderTexture.active = previous; rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex); }
    }
    System.IO.File.WriteAllText(dir + "/joints.json", Newtonsoft.Json.JsonConvert.SerializeObject(samples));
    return dir;
}
finally
{
    foreach (var mat in materials) UnityEngine.Object.DestroyImmediate(mat);
    foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
    UnityEngine.Object.DestroyImmediate(host); UnityEditor.PrefabUtility.UnloadPrefabContents(v);
}


