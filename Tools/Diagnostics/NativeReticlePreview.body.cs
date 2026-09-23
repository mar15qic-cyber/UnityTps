// Isolated EditMode UI rendering. Does not enter PlayMode or save project scenes.
var catalog=Game.Gameplay.Settings.NativeScopeReticleCatalog.Load();
var preview=new UnityEditor.PreviewRenderUtility();
var root=new GameObject("NativeReticlePreviewCanvas",typeof(RectTransform),typeof(Canvas));
preview.AddSingleGO(root);
var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;
var rt=root.GetComponent<RectTransform>();rt.sizeDelta=new Vector2(800,400);rt.localScale=Vector3.one*.01f;
var camera=preview.camera;camera.orthographic=true;camera.orthographicSize=2;
camera.nearClipPlane=.01f;camera.farClipPlane=20;camera.backgroundColor=new Color(.12f,.18f,.24f);camera.clearFlags=CameraClearFlags.SolidColor;
camera.transform.position=new Vector3(0,0,-10);camera.transform.rotation=Quaternion.identity;
canvas.worldCamera=camera;
var output=new Texture2D(800,400,TextureFormat.RGB24,false);
var meshes=new System.Collections.Generic.List<Mesh>();
var materials=new System.Collections.Generic.List<Material>();
var matrices=new System.Collections.Generic.List<Matrix4x4>();
try {
    for(int i=0;i<catalog.Entries.Length;i++){
        var e=catalog.Entries[i];
        var go=new GameObject(e.texture.name,typeof(RectTransform),typeof(UnityEngine.UI.RawImage));go.transform.SetParent(root.transform,false);
        var image=go.GetComponent<UnityEngine.UI.RawImage>();image.texture=e.texture;image.material=catalog.AdditiveMaterial;image.raycastTarget=false;
        image.rectTransform.sizeDelta=Vector2.one*170;
        image.rectTransform.anchoredPosition=new Vector2(-300+(i%4)*200,100-(i/4)*200)+(Vector2.one*.5f-e.aimUv)*170;
        image.Rebuild(UnityEngine.UI.CanvasUpdate.PreRender);
        var mesh=UnityEngine.Object.Instantiate(image.canvasRenderer.GetMesh());meshes.Add(mesh);
        var mat=new Material(catalog.AdditiveMaterial);mat.mainTexture=e.texture;materials.Add(mat);
        matrices.Add(image.transform.localToWorldMatrix);
    }
    Canvas.ForceUpdateCanvases();
    preview.BeginPreview(new Rect(0,0,800,400),GUIStyle.none);
    for(int i=0;i<meshes.Count;i++)preview.DrawMesh(meshes[i],matrices[i],materials[i],0);
    preview.Render(true,false);
    var rendered=preview.EndPreview();var old=RenderTexture.active;var temp=RenderTexture.GetTemporary(800,400,0);
    try {Graphics.Blit(rendered,temp);RenderTexture.active=temp;output.ReadPixels(new Rect(0,0,800,400),0,0);output.Apply();}
    finally {RenderTexture.active=old;RenderTexture.ReleaseTemporary(temp);}
    var path="E:/UnityProject/UnityFpsLowPoly/Temp/native-reticle-preview.png";System.IO.File.WriteAllBytes(path,output.EncodeToPNG());return path;
} finally {foreach(var m in meshes)UnityEngine.Object.DestroyImmediate(m);foreach(var m in materials)UnityEngine.Object.DestroyImmediate(m);UnityEngine.Object.DestroyImmediate(output);preview.Cleanup();}
