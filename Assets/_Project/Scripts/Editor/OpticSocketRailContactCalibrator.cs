using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 2026-09-21 一次性校准器（用户拍板"全部按实测校准"）：
    /// 以烘焙网格顶点实测"枪体导轨顶在挂点局部系的 Y"，把 Attach_Optic 沿自身 +Y 平移
    /// 该值，使镜体底面精确落导轨顶（间隙=0）。挂载行 delta 与眼点行都是挂点局部系数据，
    /// 随挂点整体平移保持有效；SCAR 锁定行前向 x=0.08639714 不变。
    /// 排除规则只按渲染器自身节点名（链上溯会被 "arms"/"Armature" 里的 "arm" 误杀）。
    /// </summary>
    public static class OpticSocketRailContactCalibrator
    {
        private sealed class WeaponSpec
        {
            public string Id, FpPath, TpPath;
            public float FpY, TpY;
            public bool HasFp, HasTp;
        }

        private static readonly (string id, string fp, string tp)[] Specs =
        {
            ("weapon.m4",             "Assets/_Project/Prefabs/Weapons/FP_Rifle_View.prefab",          "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab"),
            ("weapon.ak",             "Assets/_Project/Prefabs/Weapons/FP_Rifle02_View.prefab",        "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_02.prefab"),
            ("weapon.rifle03",        "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab",        "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab"),
            ("weapon.service_pistol", "Assets/_Project/Prefabs/Weapons/FP_ServicePistol_View.prefab",  "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01.prefab"),
            ("weapon.handgun02",      "Assets/_Project/Prefabs/Weapons/FP_Handgun02_View.prefab",      "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_02.prefab"),
            ("weapon.handgun03",      "Assets/_Project/Prefabs/Weapons/FP_Handgun03_View.prefab",      "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_03.prefab"),
            ("weapon.handgun04",      "Assets/_Project/Prefabs/Weapons/FP_Handgun04_View.prefab",      "Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_04.prefab"),
            ("weapon.smg01",          "Assets/_Project/Prefabs/Weapons/FP_SMG01_View.prefab",          "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_01.prefab"),
            ("weapon.smg02",          "Assets/_Project/Prefabs/Weapons/FP_SMG02_View.prefab",          "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_02.prefab"),
            ("weapon.smg03",          "Assets/_Project/Prefabs/Weapons/FP_SMG03_View.prefab",          "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_03.prefab"),
            ("weapon.smg04",          "Assets/_Project/Prefabs/Weapons/FP_SMG04_View.prefab",          "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_04.prefab"),
            ("weapon.smg05",          "Assets/_Project/Prefabs/Weapons/FP_SMG05_View.prefab",          "Assets/_Project/Prefabs/Weapons/TP_Weapon_SMG_05.prefab"),
            ("weapon.shotgun01",      "Assets/_Project/Prefabs/Weapons/FP_Shotgun01_View.prefab",      "Assets/_Project/Prefabs/Weapons/TP_Weapon_Shotgun_01.prefab"),
        };

        [MenuItem("Tools/Attachments/Calibrate Optic Socket Rail Contact (13 weapons FP+TP)")]
        public static void Run()
        {
            var sb = new StringBuilder();
            int fpAdjusted = 0, tpAdjusted = 0;
            foreach (var (id, fpPath, tpPath) in Specs)
            {
                if (TryAdjust(fpPath, true, out var fpBefore, out var fpRailTop)) fpAdjusted++;
                sb.AppendLine($"FP {id}: railTop={fpRailTop:F4} socketY {fpBefore:F4} -> adjusted");
                if (TryAdjust(tpPath, false, out var tpBefore, out var tpRailTop)) tpAdjusted++;
                sb.AppendLine($"TP {id}: railTop={tpRailTop:F4} socketY {tpBefore:F4} -> adjusted");
            }
            AssetDatabase.SaveAssets();
            sb.AppendLine($"DONE fpAdjusted={fpAdjusted} tpAdjusted={tpAdjusted}");
            Debug.Log(sb.ToString());
            UnityEditor.EditorUtility.DisplayDialog("Optic Socket Rail Contact", sb.ToString(), "OK");
        }

        internal static bool TryAdjust(string path, bool fp, out float beforeY, out float railTop)
        {
            beforeY = 0f; railTop = 0f;
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform socket = null;
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                    if (t.name == "Attach_Optic") socket = t;
                if (socket == null) { Debug.LogError("[RailCalibrate] no socket: " + path); return false; }
                // Never overwrite a measured footprint/adapter with the old highest-vertex heuristic.
                var marker = socket.GetComponent<Game.Gameplay.Weapon.AttachmentSocket>();
                if (marker != null && marker.GeometryVerified) return true;

                railTop = MeasureRailTop(contents, socket, fp);
                if (!float.IsFinite(railTop)) { Debug.LogError("[RailCalibrate] no rail verts: " + path); return false; }

                beforeY = socket.localPosition.y;
                // 沿挂点自身 +Y 平移实测导轨顶值，使镜体底面（挂点设计面 y=0）落导轨顶。
                socket.position += socket.up * railTop;
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static float MeasureRailTop(GameObject root, Transform socket, bool fp)
        {
            var top = float.NegativeInfinity;
            var measured = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || IsExcluded(r.name)) continue;
                Mesh mesh = null; var baked = false;
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) continue;
                    mesh = new Mesh(); smr.BakeMesh(mesh, true); baked = true;
                }
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    mesh = mf.sharedMesh;
                }
                foreach (var v in mesh.vertices)
                {
                    var local = socket.InverseTransformPoint(r.transform.TransformPoint(v));
                    var inWin = fp
                        ? local.x >= -0.30f && local.x <= 0.30f && Mathf.Abs(local.z) <= 0.08f
                        : local.z >= -0.30f && local.z <= 0.30f && Mathf.Abs(local.x) <= 0.08f;
                    if (inWin) { measured = true; if (local.y > top) top = local.y; }
                }
                if (baked) UnityEngine.Object.DestroyImmediate(mesh);
            }
            return measured ? top : float.NaN;
        }

        private static bool IsExcluded(string nodeName)
        {
            var name = nodeName.ToLowerInvariant();
            return name.Contains("scope") || name.Contains("iron") || name.Contains("sight")
                || name.Contains("bullet") || name.Contains("silencer") || name.Contains("knife")
                || name.Contains("slider") || name.Contains("mag") || name.Contains("grip")
                || name.Contains("bipod") || name.Contains("arms");
        }
    }
}
