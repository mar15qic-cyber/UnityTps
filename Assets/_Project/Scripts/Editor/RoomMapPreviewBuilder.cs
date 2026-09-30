#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    public static class RoomMapPreviewBuilder
    {
        public static void Build(string mapId, string sceneName)
        {
            var scene = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/"+sceneName+".unity");
            var previous = RenderTexture.active;
            var rt = new RenderTexture(768,432,24);
            Texture2D texture = null;
            try
            {
                Bounds bounds = new Bounds(Vector3.zero,Vector3.one); bool any=false;
                foreach(var root in scene.GetRootGameObjects())
                foreach(var renderer in root.GetComponentsInChildren<MeshRenderer>())
                {
                    if(renderer.GetComponentInParent<Canvas>()!=null || renderer.bounds.size.magnitude>250)continue;
                    if(!any){bounds=renderer.bounds;any=true;}else bounds.Encapsulate(renderer.bounds);
                }
                var go=new GameObject("PreviewCamera",typeof(Camera));SceneManager.MoveGameObjectToScene(go,scene);
                var camera=go.GetComponent<Camera>();camera.scene=scene;camera.targetTexture=rt;camera.enabled=false;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.06f,.09f,.10f);
                camera.orthographic=true;camera.orthographicSize=Mathf.Max(12,Mathf.Max(bounds.size.z,bounds.size.x/1.777f)*.58f);
                camera.transform.position=bounds.center+new Vector3(0,90,-65);camera.transform.LookAt(bounds.center);
                camera.nearClipPlane=.1f;camera.farClipPlane=300;camera.Render();RenderTexture.active=rt;
                texture=new Texture2D(768,432,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,768,432),0,0);texture.Apply();
                var dir="Assets/_Project/Resources/UI/Maps";Directory.CreateDirectory(dir);
                var path=dir+"/"+mapId+".png";File.WriteAllBytes(path,texture.EncodeToPNG());AssetDatabase.ImportAsset(path);
                var importer=(TextureImporter)AssetImporter.GetAtPath(path);importer.textureType=TextureImporterType.Sprite;importer.spriteImportMode=SpriteImportMode.Single;importer.mipmapEnabled=false;importer.SaveAndReimport();
            }
            finally
            {
                RenderTexture.active=previous;Object.DestroyImmediate(rt);if(texture!=null)Object.DestroyImmediate(texture);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
#endif
