using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class VideoFollowupContentRepair
{
    public static void Run()
    {
        BuildMapMetadata();
        Game.EditorTools.NativeScopeReticleBuilder.Build();
        CenterAkClamps();
    }

    // Reproduce the exact camera used by RoomMapPreviewBuilder without replacing channel artwork.
    public static void BuildMapMetadata()
    {
        var entries = new List<MapRadarCatalog.Entry>();
        foreach (var id in new[] { "arena", "map_01", "map_02", "map_03", "map_04" })
        {
            var name = GameMapCatalog.ResolveSceneName(id);
            var scene = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/" + name + ".unity");
            try
            {
                Bounds bounds = default; bool any = false;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
                {
                    if (renderer.GetComponentInParent<Canvas>() != null || renderer.bounds.size.magnitude > 250) continue;
                    if (!any) { bounds = renderer.bounds; any = true; } else bounds.Encapsulate(renderer.bounds);
                }
                var offset = new Vector3(0, 90, -65);
                entries.Add(new MapRadarCatalog.Entry {
                    sceneName = name, image = Resources.Load<Sprite>("UI/Maps/" + id),
                    cameraPosition = bounds.center + offset, cameraRotation = Quaternion.LookRotation(-offset),
                    halfHeight = Mathf.Max(12, Mathf.Max(bounds.size.z, bounds.size.x / 1.777f) * .58f)
                });
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
        const string path = "Assets/_Project/Resources/MapRadarCatalog.asset";
        var catalog = AssetDatabase.LoadAssetAtPath<MapRadarCatalog>(path);
        if (catalog == null) { catalog = ScriptableObject.CreateInstance<MapRadarCatalog>(); AssetDatabase.CreateAsset(catalog, path); }
        catalog.entries = entries.ToArray(); EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssetIfDirty(catalog);
    }

    private static void CenterAkClamps()
    {
        var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
        foreach (var id in new[] { "attach.lpfp.optic.01", "attach.lpfp.optic.03" })
        {
            var entry = catalog.Find(id);
            var points = entry.prefab.GetComponentsInChildren<MeshFilter>(true).SelectMany(m => m.sharedMesh.vertices.Select(v =>
                entry.MountRotation * entry.prefab.transform.InverseTransformPoint(m.transform.TransformPoint(v)) + entry.mountOffset)).ToArray();
            var foot = points.Where(p => p.y < points.Min(v => v.y) + .0016f).ToArray();
            var target = new Vector3(0, 0, -(foot.Min(p => p.z) + foot.Max(p => p.z)) * .5f);
            catalog.Calibration.TryGet("weapon.m4", id, out var before, out var rotation, out var frame);
            catalog.Calibration.Set("weapon.m4", id, target, rotation);
            if (catalog.Calibration.TryGetOpticAim("weapon.m4", id, out var aim))
            {
                var delta = target - before;
                aim.EyePointLocal += delta;
                if (aim.HasAxisFront) aim.AxisFrontPointLocal += delta;
                if (aim.HasWindow) aim.WindowCenterLocal += delta;
                catalog.Calibration.SetOpticAim("weapon.m4", id, aim);
            }
        }
        AssetDatabase.SaveAssetIfDirty(catalog.Calibration);
    }
}
