using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Editor
{
    public static class TPWeaponMountAudit
    {
        private const string PlayerPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";
        [MenuItem("Tools/Review/Audit TP Weapon Mounts")]
        public static void Run()
        {
            const string folder = "Assets/_Project/Prefabs/Weapons";
            var lines = new List<string>
            {
                "prefab,rootX,rootY,rootZ,rootPitch,rootYaw,rootRoll,muzzleX,muzzleY,muzzleZ,leftHandX,leftHandY,leftHandZ,meshMinX,meshMinY,meshMinZ,meshMaxX,meshMaxY,meshMaxZ,forwardDot"
            };
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileName(path).StartsWith("TP_Weapon_", StringComparison.Ordinal)) continue;
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Transform muzzle = root.transform.Find("Muzzle");
                    Transform leftHand = root.transform.Find("LeftHandTarget");
                    Bounds bounds = new Bounds();
                    bool hasMesh = false;
                    foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (filter.sharedMesh == null) continue;
                        foreach (Vector3 vertex in filter.sharedMesh.vertices)
                        {
                            Vector3 point = root.transform.InverseTransformPoint(filter.transform.TransformPoint(vertex));
                            if (!hasMesh) { bounds = new Bounds(point, Vector3.zero); hasMesh = true; }
                            else bounds.Encapsulate(point);
                        }
                    }
                    Vector3 muzzleLocal = muzzle != null ? root.transform.InverseTransformPoint(muzzle.position) : Vector3.zero;
                    Vector3 handLocal = leftHand != null ? root.transform.InverseTransformPoint(leftHand.position) : Vector3.zero;
                    float forward = muzzle != null ? Vector3.Dot(root.transform.forward, muzzle.forward) : -2f;
                    var data = new List<string> { Path.GetFileNameWithoutExtension(path) };
                    var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(path).transform;
                    Add(data, assetRoot.localPosition);
                    Add(data, assetRoot.localEulerAngles);
                    Add(data, muzzleLocal);
                    Add(data, handLocal);
                    Add(data, hasMesh ? bounds.min : Vector3.zero);
                    Add(data, hasMesh ? bounds.max : Vector3.zero);
                    data.Add(forward.ToString("F3", CultureInfo.InvariantCulture));
                    lines.Add(string.Join(",", data));
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            lines.Sort(1, lines.Count - 1, StringComparer.Ordinal);
            string output = Path.GetFullPath("Captures/TPWeaponMountAudit.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllLines(output, lines, new UTF8Encoding(true));
            Debug.Log($"[TPWeaponMountAudit] {lines.Count - 1} prefabs -> {output}");
        }

        private static void Add(List<string> row, Vector3 value)
        {
            row.Add(value.x.ToString("F4", CultureInfo.InvariantCulture));
            row.Add(value.y.ToString("F4", CultureInfo.InvariantCulture));
            row.Add(value.z.ToString("F4", CultureInfo.InvariantCulture));
        }

        [MenuItem("Tools/Review/Capture TP Weapon Mounts")]
        public static void CaptureMounts()
        {
            const string folder = "Assets/_Project/Prefabs/Weapons";
            string output = Path.GetFullPath("Captures/TPMounts");
            Directory.CreateDirectory(output);
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith("TP_Weapon_", StringComparison.Ordinal)) continue;
                GameObject player = PrefabUtility.LoadPrefabContents(PlayerPath);
                GameObject cameraObject = null;
                GameObject lightObject = null;
                RenderTexture render = null;
                Texture2D image = null;
                try
                {
                    UnityEditor.SceneManagement.EditorSceneManager.SetSceneCullingMask(player.scene, 1UL << 60);
                    var animator = player.transform.Find("TP_Model").GetComponent<Animator>();
                    Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                    var gunAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunAsset, player.scene);
                    gun.transform.SetParent(hand, false);
                    gun.transform.localPosition = gunAsset.transform.localPosition;
                    gun.transform.localRotation = gunAsset.transform.localRotation;
                    gun.transform.localScale = gunAsset.transform.localScale;
                    SetLayer(player.transform, 31);

                    cameraObject = new GameObject("TPMountAuditCamera");
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, player.scene);
                    var camera = cameraObject.AddComponent<Camera>();
                    camera.enabled = false;
                    camera.cameraType = CameraType.Preview;
                    camera.scene = player.scene;
                    camera.overrideSceneCullingMask = 1UL << 60;
                    camera.cullingMask = 1 << 31;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(.13f, .16f, .2f);
                    camera.fieldOfView = 36f;
                    camera.transform.position = player.transform.TransformPoint(new Vector3(2.7f, 1.55f, 1.7f));
                    camera.transform.LookAt(player.transform.TransformPoint(new Vector3(0f, 1.25f, .2f)));
                    lightObject = new GameObject("TPMountAuditLight");
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightObject, player.scene);
                    var light = lightObject.AddComponent<Light>();
                    light.type = LightType.Directional; light.intensity = 1.6f;
                    light.transform.rotation = Quaternion.Euler(40f, -45f, 0f);

                    render = new RenderTexture(900, 900, 24);
                    image = new Texture2D(900, 900, TextureFormat.RGB24, false);
                    render.Create();
                    RenderPipeline.SubmitRenderRequest(camera,
                        new UniversalRenderPipeline.SingleCameraRequest { destination = render });
                    RenderTexture.active = render;
                    image.ReadPixels(new Rect(0, 0, 900, 900), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
                }
                finally
                {
                    RenderTexture.active = null;
                    if (image != null) UnityEngine.Object.DestroyImmediate(image);
                    if (render != null) { render.Release(); UnityEngine.Object.DestroyImmediate(render); }
                    if (lightObject != null) UnityEngine.Object.DestroyImmediate(lightObject);
                    if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                    PrefabUtility.UnloadPrefabContents(player);
                }
            }
            Debug.Log("[TPWeaponMountAudit] captured 16 TP mounts -> " + output);
        }

        private static void SetLayer(Transform node, int layer)
        {
            node.gameObject.layer = layer;
            for (int i = 0; i < node.childCount; i++) SetLayer(node.GetChild(i), layer);
        }
    }
}
