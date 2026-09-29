var phase = "draft";
var side = false;
var dir = "Logs/PinKeyPoses0929/" + phase + (side ? "/side" : "/front");
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
        UnityEngine.Resources.Load<UnityEngine.GameObject>("ThrowableViews/Frag"), v.scene);
    UnityEditor.PrefabUtility.UnpackPrefabInstance(grenade, UnityEditor.PrefabUnpackMode.Completely, UnityEditor.InteractionMode.AutomatedAction);
    grenade.transform.SetParent(v.transform.Find("Armature/arm_R/lower_arm_R/hand_R"), false);
    grenade.transform.localRotation = (UnityEngine.Quaternion)typeof(Game.Presentation.Animation.FPWeaponAnimator).GetProperty("HeldThrowableRotation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null, null);
    grenade.transform.localPosition = (UnityEngine.Vector3)typeof(Game.Presentation.Animation.FPWeaponAnimator)
        .GetMethod("HeldThrowablePosition", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        .Invoke(null, new object[] { grenade });
    // Review grip indexing: orient the native ring/pin toward the supporting hand.
    grenade.transform.localRotation *= UnityEngine.Quaternion.Euler(0,180,0);
    var pin = grenade.GetComponent<Game.Presentation.Animation.FPThrowablePinView>();
    pin.Bind(v.transform.Find("Armature/arm_L/lower_arm_L/hand_L"));
    var display = new UnityEngine.GameObject("BakedArms");
    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(display, v.scene);
    display.transform.SetParent(host.transform, false);
    var filter = display.AddComponent<UnityEngine.MeshFilter>();
    display.AddComponent<UnityEngine.MeshRenderer>().sharedMaterials = arms.sharedMaterials;
    var light = new UnityEngine.GameObject("Light");
    light.transform.SetParent(host.transform, false); light.transform.rotation = UnityEngine.Quaternion.Euler(35, -30, 0);
    light.AddComponent<UnityEngine.Light>().type = UnityEngine.LightType.Directional;
    if (side) { host.transform.position = new UnityEngine.Vector3(-.85f,.13f,.16f); host.transform.LookAt(new UnityEngine.Vector3(0,-.17f,.43f)); cam.orthographic = true; cam.orthographicSize = .27f; }
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
    var ru = bones[0]; var re = bones[1]; var rw = bones[2];
    var lu = v.transform.Find("Armature/arm_L");
    var le = lu.Find("lower_arm_L"); var lw = le.Find("hand_L");
    var index = lw.Find("finger_01_L/finger_02_L/finger_03_L");
    var builder = typeof(Game.EditorTools.FirstPersonThrowClipBuilder);
    var gripMethod = builder.GetMethod("SetSupportGrip", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    var solve = builder.GetMethod("SolveArm", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    var ringParent = pin.Ring.parent;
    var ringPosition = pin.Ring.localPosition; var ringRotation = pin.Ring.localRotation;
    var evidence = new System.Collections.Generic.List<object>();
    for (int frame = 0; frame < 3; frame++)
    {
        float t = frame;
        pin.Ring.SetParent(ringParent, false);
        pin.Ring.SetLocalPositionAndRotation(ringPosition, ringRotation);
        // The three frames are pose decisions, not a new timing curve.
        throwClip.SampleAnimation(v, .10f);
        var rightWrist = v.transform.TransformPoint(new UnityEngine.Vector3(.145f, -.005f, .325f));
        ru.position = v.transform.TransformPoint(new UnityEngine.Vector3(.19f,-.12f,.008f));
        solve.Invoke(null, new object[] { ru, re, rw, rightWrist, v.transform.TransformPoint(new UnityEngine.Vector3(.38f,-.23f,.13f)) });
        rw.rotation = v.transform.rotation * UnityEngine.Quaternion.Euler(0,-12,frame == 0 ? 22 : 30)
            * UnityEngine.Quaternion.Euler(90,-20,-10);
        gripMethod.Invoke(null, new object[] { lw, frame == 0 ? .70f : 1f });
        // Relax the other three fingers as a group; keep the thumb-index pinch.
        lw.Find("middle_finger_01_L").localRotation = UnityEngine.Quaternion.Euler(0,0,90);
        lw.Find("middle_finger_01_L/middle_finger_02_L").localRotation = UnityEngine.Quaternion.Euler(0,180,270);
        lw.Find("middle_finger_01_L/middle_finger_02_L/middle_finger_03_L").localRotation = UnityEngine.Quaternion.Euler(0,0,270);
        lu.position = v.transform.TransformPoint(new UnityEngine.Vector3(-.18f,-.115f,.035f));
        // Aim the whole forearm/hand line diagonally inward, with the dorsum up.
        var palmNormal = (frame == 0 ? new UnityEngine.Vector3(-.16f,.78f,-.60f) : new UnityEngine.Vector3(-.1f,.82f,-.56f)).normalized;
        var fingersForward = new UnityEngine.Vector3(.85f,.10f,.51f).normalized;
        var palm = v.transform.rotation * UnityEngine.Quaternion.LookRotation(palmNormal,fingersForward);
        lw.rotation = palm;
        var targetContact = pin.Grip.position + (frame == 0 ? v.transform.TransformVector(new UnityEngine.Vector3(-.022f,-.003f,-.008f)) : UnityEngine.Vector3.zero);
        var targetWrist = lw.position + targetContact - index.TransformPoint(Game.Presentation.Animation.FPThrowablePinView.FingerContact);
        solve.Invoke(null, new object[] { lu, le, lw, targetWrist, v.transform.TransformPoint(new UnityEngine.Vector3(-.34f,-.20f,.16f)) });
        lw.rotation = palm;
        if (frame == 2)
        {
            // Preserve contact-relative ring transform at extraction, then move
            // the arm along the pull vector without an independent palm flip.
            pin.Ring.SetParent(index, true);
            var pull = grenade.transform.right * .06f;
            lu.position += v.transform.TransformVector(new UnityEngine.Vector3(-.004f,-.003f,0));
            solve.Invoke(null, new object[] { lu, le, lw, targetWrist + pull, v.transform.TransformPoint(new UnityEngine.Vector3(-.36f,-.22f,.15f)) });
            lw.rotation = v.transform.rotation * UnityEngine.Quaternion.AngleAxis(-5, UnityEngine.Vector3.forward) * UnityEngine.Quaternion.Inverse(v.transform.rotation) * palm;
        }
        evidence.Add(new { stage = frame, contactError = UnityEngine.Vector3.Distance(pin.Grip.position,index.TransformPoint(Game.Presentation.Animation.FPThrowablePinView.FingerContact)),
            wristBend = UnityEngine.Vector3.Angle(lw.up,lw.position-le.position),
            shoulder=lu.position.ToString("F5"), elbow=le.position.ToString("F5"), wrist=lw.position.ToString("F5"), palmNormal=lw.forward.ToString("F5"),
            pinOutAxis=grenade.transform.right.ToString("F5"),
            fingers=lw.GetComponentsInChildren<UnityEngine.Transform>().Where(b => b.name.Contains("_L")).Select(b => new {name=b.name, p=b.position.ToString("F5")}).ToArray(),
            ringParent=pin.Ring.parent.name,
            bones=v.GetComponentsInChildren<UnityEngine.Transform>(true).Where(b => UnityEditor.AnimationUtility.CalculateTransformPath(b,v.transform).StartsWith("Armature/arm_")).Select(b => new {path=UnityEditor.AnimationUtility.CalculateTransformPath(b,v.transform), position=new[]{b.localPosition.x,b.localPosition.y,b.localPosition.z}, rotation=new[]{b.localRotation.x,b.localRotation.y,b.localRotation.z,b.localRotation.w}}).ToArray() });
        var points = (side ? new[] { lu, le, lw } : bones).Select(b => b.position).ToArray();
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
    System.IO.File.WriteAllText(dir + "/evidence.json", Newtonsoft.Json.JsonConvert.SerializeObject(evidence, new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }));
    return dir;
}
finally
{
    foreach (var mat in materials) UnityEngine.Object.DestroyImmediate(mat);
    foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
    UnityEngine.Object.DestroyImmediate(host); UnityEditor.PrefabUtility.UnloadPrefabContents(v);
}


