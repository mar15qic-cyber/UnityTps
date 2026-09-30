var phase = "before";
var side = false;
var dir = "Logs/ThrowKinetics0928/" + phase + (side ? "/side" : "/front");
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
    var throwClip = (UnityEngine.AnimationClip)pose.FindProperty("throwablePresentationClip").objectReferenceValue;
    v.transform.position = pose.FindProperty("throwViewLocalPosition").vector3Value;
    v.transform.rotation = UnityEngine.Quaternion.Euler(pose.FindProperty("throwViewLocalEuler").vector3Value);
    var cam = host.AddComponent<UnityEngine.Camera>();
    cam.enabled = false; cam.scene = v.scene; cam.cameraType = UnityEngine.CameraType.Preview;
    cam.overrideSceneCullingMask = 1UL << 60; cam.fieldOfView = 45; cam.nearClipPlane = .01f;
    cam.backgroundColor = new UnityEngine.Color(.25f, .3f, .35f); cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
    var arms = v.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true).First(r => r.name == "arms");
    foreach (var r in v.GetComponentsInChildren<UnityEngine.Renderer>(true)) r.enabled = false;
    var grenade = (UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(
        UnityEngine.Resources.Load<Game.Gameplay.Combat.ThrowableCatalog>("ThrowableCatalog").Flash.ModelPrefab, v.scene);
    grenade.transform.SetParent(v.transform.Find("Armature/arm_R/lower_arm_R/hand_R"), false);
    grenade.transform.localRotation = (UnityEngine.Quaternion)typeof(Game.Presentation.Animation.FPWeaponAnimator).GetProperty("HeldThrowableRotation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null, null);
    grenade.transform.localPosition = (UnityEngine.Vector3)typeof(Game.Presentation.Animation.FPWeaponAnimator)
        .GetMethod("HeldThrowablePosition", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        .Invoke(null, new object[] { grenade });
    var display = new UnityEngine.GameObject("BakedArms");
    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(display, v.scene);
    display.transform.SetParent(host.transform, false);
    var filter = display.AddComponent<UnityEngine.MeshFilter>();
    display.AddComponent<UnityEngine.MeshRenderer>().sharedMaterials = arms.sharedMaterials;
    var light = new UnityEngine.GameObject("Light");
    light.transform.SetParent(host.transform, false); light.transform.rotation = UnityEngine.Quaternion.Euler(35, -30, 0);
    light.AddComponent<UnityEngine.Light>().type = UnityEngine.LightType.Directional;
    if (side) { host.transform.position = new UnityEngine.Vector3(1.15f,.06f,.52f); host.transform.LookAt(new UnityEngine.Vector3(.20f,-.10f,.40f)); cam.orthographic = true; cam.orthographicSize = .37f; }
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
    var prepareClip = (UnityEngine.AnimationClip)pose.FindProperty("throwablePrepareClip").objectReferenceValue;
    for (int frame = 0; frame < 144; frame++)
    {
        float t = frame / 60f;
        grenade.SetActive(t < 1.35f);
        if (t < 1f) prepareClip.SampleAnimation(v, UnityEngine.Mathf.Clamp01((t - .3f) / .36f) * prepareClip.length);
        else throwClip.SampleAnimation(v, UnityEngine.Mathf.Min(t - 1f, throwClip.length));
        var points = bones.Select(b => b.position).ToArray();
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


