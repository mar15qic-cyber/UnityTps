using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Game.Presentation.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Unlike the factory particle marker or whole-gun bounds, the front face
    // locates the visible barrel aperture. No source package assets are edited.
    public static class LiveCombatMuzzleAudit
    {
        private const string Folder = "Assets/_Project/Prefabs/Weapons/";
        private static readonly Regex BodyName = new("^(assault_rifle|smg|handgun|sniper|sniper_rifle|shotgun)_\\d+$", RegexOptions.IgnoreCase);

        public static bool TryTip(GameObject view, Transform frame, bool firstPerson, out Vector3 local)
        {
            var authored = frame.Find("MuzzleExit");
            if (authored != null) { local = frame.InverseTransformPoint(authored.position); return true; }
            var points = new List<Vector3>();
            if (firstPerson)
            {
                foreach (var renderer in view.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!BodyName.IsMatch(renderer.name) || renderer.sharedMesh == null) continue;
                    var baked = new Mesh();
                    try
                    {
                        renderer.BakeMesh(baked);
                        foreach (var vertex in baked.vertices)
                            points.Add(frame.InverseTransformPoint(renderer.transform.TransformPoint(vertex)));
                    }
                    finally { UnityEngine.Object.DestroyImmediate(baked); }
                }
            }
            else
            {
                var mesh = view.GetComponent<MeshFilter>();
                if (mesh == null)
                    foreach (var candidate in view.GetComponentsInChildren<MeshFilter>(true))
                        if (candidate.name == view.name) { mesh = candidate; break; }
                if (mesh != null && mesh.sharedMesh != null)
                    foreach (var vertex in mesh.sharedMesh.vertices)
                        points.Add(frame.InverseTransformPoint(mesh.transform.TransformPoint(vertex)));
            }
            local = default;
            if (points.Count == 0) return false;
            float front = float.NegativeInfinity;
            foreach (var p in points) front = Mathf.Max(front, p.z);
            Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, front);
            Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, front);
            foreach (var p in points)
                if (p.z >= front - .0015f)
                { min.x = Mathf.Min(min.x, p.x); min.y = Mathf.Min(min.y, p.y);
                  max.x = Mathf.Max(max.x, p.x); max.y = Mathf.Max(max.y, p.y); }
            local = (min + max) * .5f;
            return true;
        }

        [MenuItem("Tools/Review/Audit Visible Barrel Tips")]
        public static void Audit() => Run(false);
        [MenuItem("Tools/Review/Repair Visible Barrel Tips")]
        public static void Repair() => Run(true);

        private static void Run(bool repair)
        {
            var report = new StringBuilder("view,tipX,tipY,tipZ,previousErrorMm,remainingErrorMm\n");
            int count = 0, failures = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { Folder.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetDirectoryName(path).Replace('\\', '/') + "/" != Folder) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                bool fp = name.StartsWith("FP_", StringComparison.Ordinal);
                if (!fp && !name.StartsWith("TP_Weapon_", StringComparison.Ordinal)) continue;
                var view = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var weaponView = view.GetComponentInChildren<WeaponView>(true);
                    var muzzle = fp ? weaponView != null ? weaponView.Muzzle : null : view.transform.Find("Muzzle");
                    Vector3 tip;
                    if (muzzle == null || !TryTip(view, muzzle, fp, out tip))
                    { failures++; Debug.LogError("[BarrelTipAudit] cannot resolve " + path); continue; }
                    Vector3 before = Vector3.zero;
                    var so = fp ? new SerializedObject(weaponView) : null;
                    if (fp) before = so.FindProperty("hasCalibratedBarrelTip").boolValue
                        ? so.FindProperty("barrelTipMuzzleLocal").vector3Value
                        : Vector3.back * so.FindProperty("tracerMuzzleInsetMeters").floatValue;
                    float error = Vector3.Distance(before, tip) * 1000f;
                    if (repair)
                    {
                        if (fp)
                        {
                            so.FindProperty("hasCalibratedBarrelTip").boolValue = true;
                            so.FindProperty("barrelTipMuzzleLocal").vector3Value = tip;
                            so.ApplyModifiedPropertiesWithoutUndo();
                        }
                        else muzzle.position = muzzle.TransformPoint(tip);
                        PrefabUtility.SaveAsPrefabAsset(view, path);
                    }
                    report.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1:F6},{2:F6},{3:F6},{4:F2},{5:F2}", name, tip.x, tip.y, tip.z, error, repair ? 0 : error));
                    count++;
                }
                finally { PrefabUtility.UnloadPrefabContents(view); }
            }
            Directory.CreateDirectory("Captures/CurrentAudit");
            File.WriteAllText("Captures/CurrentAudit/VisibleBarrelTips-" + (repair ? "repair" : "audit") + ".csv", report.ToString());
            Debug.Log($"[BarrelTipAudit] {count} views, {failures} unresolved, repair={repair}");
        }
    }
}
