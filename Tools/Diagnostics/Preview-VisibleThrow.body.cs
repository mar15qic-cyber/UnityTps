var dir = "Logs/VisualFix0928/overhand-preview";
System.IO.Directory.CreateDirectory(dir);
var d = UnityEngine.Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
var v = UnityEditor.PrefabUtility.LoadPrefabContents(UnityEditor.AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(v.scene, 1UL << 60);
var host = new UnityEngine.GameObject("StaticThrowPreview");
UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, v.scene);
var meshes = new System.Collections.Generic.List<UnityEngine.Mesh>();
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
        UnityEngine.Resources.Load<Game.Gameplay.Combat.ThrowableCatalog>("ThrowableCatalog").Frag.ModelPrefab, v.scene);
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
    foreach (float t in new[] { 1f, 0f, .08f, .15f, .22f, .29f, .31f, .4f, .5f, .75f })
    {
        grenade.SetActive(t == 1f || t * throwClip.length < .35f);
        throwClip.SampleAnimation(v, t * throwClip.length);
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
            System.IO.File.WriteAllBytes(dir + "/baked-" + t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ".png", tex.EncodeToPNG());
        }
        finally { UnityEngine.RenderTexture.active = previous; rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex); }
    }
    return dir;
}
finally
{
    foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
    UnityEngine.Object.DestroyImmediate(host); UnityEditor.PrefabUtility.UnloadPrefabContents(v);
}


