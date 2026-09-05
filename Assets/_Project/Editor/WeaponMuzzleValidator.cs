using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Presentation.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 枪口配置静态校验（Docs/13 检查点 1；2026-09-05 扩展）：对全部 16 把在役 FP 武器视图断言
    /// Muzzle 层/膛线方向/引用完整性/弹壳类型，并以原厂 FPSController 的
    /// Components 旋转为膛线基准（与校准同源，Day4.5 验证该方法）。
    /// 2026-09-05 扩展（新增 6 枪复制旧枪挂点事故复盘）：
    /// ① 覆盖 16 把正式 LPFP 武器（不含 LPW）；
    /// ② 新增 FP Muzzle 位置误差检查——原厂 Muzzleflash Particles 的 weapon 骨局部位置
    ///    与正式 View 的 Muzzle 同空间比较（≤1cm），杜绝"同族照搬坐标"静默通过；
    /// ③ 新增 TP Muzzle 位置检查——根局部空间贴枪模几何（z=前端 maxZ、y=主枪体 y 中点，≤2cm）；
    /// ④ 逐枪输出 PASS/FAIL 与位置/角度误差数值。
    /// 菜单触发：Tools/Weapon Muzzle Validator。
    /// </summary>
    public static class WeaponMuzzleValidator
    {
        private const string CtrlRoot = "Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Arms";
        private const string ViewRoot = "Assets/_Project/Prefabs/Weapons";
        private const float MaxPositionErrorMeters = 0.01f;   // FP Muzzle 位置误差上限
        private const float MaxTpPositionErrorMeters = 0.02f; // TP Muzzle 位置误差上限

        private static readonly (string ctrl, string view, string tp, string expectCasing)[] Jobs =
        {
            ("Assault_Rifle_01_Example_Prefab/Assault_Rifle_01_FPSController.prefab", "FP_Rifle_View", "TP_Weapon_AssaultRifle_01", null),
            ("Assault_Rifle_02_Example_Prefab/Assault_Rifle_02_FPSController.prefab", "FP_Rifle02_View", "TP_Weapon_AssaultRifle_02", null),
            ("Assault_Rifle_03_Example_Prefab/Assault_Rifle_03_FPSController.prefab", "FP_Rifle03_View", "TP_Weapon_AssaultRifle_03", null),
            ("SMG_01_Example_Prefab/SMG_01_FPSController.prefab", "FP_SMG01_View", "TP_Weapon_SMG_01", null),
            ("SMG_02_Example_Prefab/SMG_02_FPSController.prefab", "FP_SMG02_View", "TP_Weapon_SMG_02", null),
            ("Shotgun_01_Example_Prefab/Shotgun_01_FPSController.prefab", "FP_Shotgun01_View", "TP_Weapon_Shotgun_01",
                "Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Casing_Prefabs/Shotgun_Shell_Prefab.prefab"),
            ("Sniper_01_Example_Prefab/Sniper_01_FPSController.prefab", "FP_Sniper01_View", "TP_Weapon_Sniper_01",
                "Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Casing_Prefabs/Big_Casing_Prefab.prefab"),
            ("Sniper_02_Example_Prefab/Sniper_02_FPSController.prefab", "FP_Sniper02_View", "TP_Weapon_Sniper_02",
                "Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Casing_Prefabs/Big_Casing_Prefab.prefab"),
            ("Handgun_01_Example_Prefab/Handgun_01_FPSController.prefab", "FP_ServicePistol_View", "TP_Weapon_Handgun_01", null),
            ("Handgun_02_Example_Prefab/Handgun_02_FPSController.prefab", "FP_Handgun02_View", "TP_Weapon_Handgun_02", null),
            // ---- 2026-09-05 新增 6 把（枪口照搬旧枪事故批次）----
            ("SMG_03_Example_Prefab/SMG_03_FPSController.prefab", "FP_SMG03_View", "TP_Weapon_SMG_03", null),
            ("SMG_04_Example_Prefab/SMG_04_FPSController.prefab", "FP_SMG04_View", "TP_Weapon_SMG_04", null),
            ("SMG_05_Example_Prefab/SMG_05_FPSController.prefab", "FP_SMG05_View", "TP_Weapon_SMG_05", null),
            ("Handgun_03_Example_Prefab/Handgun_03_FPSController.prefab", "FP_Handgun03_View", "TP_Weapon_Handgun_03", null),
            ("Handgun_04_Example_Prefab/Handgun_04_FPSController.prefab", "FP_Handgun04_View", "TP_Weapon_Handgun_04", null),
            ("Sniper_03_Example_Prefab/Sniper_03_FPSController.prefab", "FP_Sniper03_View", "TP_Weapon_Sniper_03", null),
        };

        [MenuItem("Tools/Weapon Muzzle Validator")]
        public static void Run()
        {
            int pass = 0, fail = 0;
            var report = new StringBuilder();
            report.AppendLine("==== Weapon Muzzle Validator（在役 16 把，含位置校验）====");

            foreach (var job in Jobs)
            {
                var errors = new List<string>();
                var metrics = new List<string>();

                // 原厂基准：膛线（Components 旋转）+ 枪口位置（Muzzleflash Particles）
                Quaternion boreLocal;
                Vector3 muzzleLocal = Vector3.zero;
                var ctrlGo = PrefabUtility.LoadPrefabContents(CtrlRoot + "/" + job.ctrl);
                try
                {
                    var wb = FindDeep(ctrlGo.transform, "weapon");
                    var comps = FindDeep(ctrlGo.transform, "Components");
                    Transform particles = null;
                    foreach (var t in ctrlGo.GetComponentsInChildren<Transform>(true))
                        if (t.name.Contains("Muzzleflash Particles")) { particles = t; break; }
                    if (wb == null || comps == null || particles == null)
                    {
                        errors.Add("原厂节点缺失");
                        boreLocal = Quaternion.identity;
                    }
                    else
                    {
                        boreLocal = Quaternion.Inverse(wb.rotation) * comps.rotation;
                        muzzleLocal = wb.InverseTransformPoint(particles.position);
                    }
                }
                finally { PrefabUtility.UnloadPrefabContents(ctrlGo); }

                var viewPath = ViewRoot + "/" + job.view + ".prefab";
                var viewGo = PrefabUtility.LoadPrefabContents(viewPath);
                try
                {
                    var wb2 = FindDeep(viewGo.transform, "weapon");
                    if (wb2 == null) errors.Add("视图缺 weapon 骨");
                    var wv = viewGo.GetComponentInChildren<WeaponView>(true);
                    if (wv == null) { errors.Add("缺 WeaponView"); }
                    else
                    {
                        var so = new SerializedObject(wv);

                        // 1) Muzzle 引用与层
                        var muzzle = wv.Muzzle;
                        if (muzzle == null) errors.Add("muzzle 引用为空");
                        else
                        {
                            if (muzzle.gameObject.layer != 9) errors.Add("Muzzle 层=" + muzzle.gameObject.layer + "≠9");
                            // 2) 膛线夹角（与原厂 Components 前向，均换算到视图世界）
                            if (wb2 != null)
                            {
                                var expect = wb2.rotation * boreLocal * Vector3.forward;
                                float angle = Vector3.Angle(muzzle.forward, expect);
                                metrics.Add("角度=" + angle.ToString("F2") + "°");
                                if (angle > 3f) errors.Add("膛线夹角=" + angle.ToString("F2") + "°>3°");

                                // 2b) 位置误差（weapon 骨局部：原厂枪口 vs 正式 Muzzle）——照搬旧枪坐标在此暴露
                                if (muzzle.IsChildOf(wb2))
                                {
                                    float posErr = (wb2.InverseTransformPoint(muzzle.position) - muzzleLocal).magnitude;
                                    metrics.Add("位置误差=" + (posErr * 1000f).ToString("F1") + "mm");
                                    if (posErr > MaxPositionErrorMeters)
                                        errors.Add("Muzzle 位置误差=" + (posErr * 1000f).ToString("F1") + "mm（原厂基准 " +
                                            muzzleLocal.ToString("F4") + "，现值 " + wb2.InverseTransformPoint(muzzle.position).ToString("F4") + "）");
                                }
                                else errors.Add("Muzzle 不在 weapon 骨下（parent=" + muzzle.parent.name + "）");
                            }
                        }

                        // 3) ShellPort 独立且非空
                        var shellPort = so.FindProperty("shellPort").objectReferenceValue as Transform;
                        if (shellPort == null) errors.Add("shellPort 为空");
                        else if (muzzle != null && shellPort == muzzle) errors.Add("shellPort=Muzzle（未分离）");

                        // 4) SightReference 非空
                        if (so.FindProperty("sightReference").objectReferenceValue == null)
                            errors.Add("sightReference 为空");

                        // 5) 弹壳类型
                        var casing = so.FindProperty("shellCasingPrefab").objectReferenceValue as GameObject;
                        if (job.expectCasing == null)
                        {
                            if (casing == null) errors.Add("弹壳为空（非预期）");
                        }
                        else
                        {
                            var expectGo = AssetDatabase.LoadAssetAtPath<GameObject>(job.expectCasing);
                            if (casing == null || expectGo == null || casing.name != expectGo.name)
                                errors.Add("弹壳=" + (casing != null ? casing.name : "null") + " 期望=" + Path.GetFileNameWithoutExtension(job.expectCasing));
                        }

                        // 6) 火光/命中 Prefab 非空
                        if (so.FindProperty("muzzleFlashPrefab").objectReferenceValue == null) errors.Add("muzzleFlashPrefab 为空");
                        if (so.FindProperty("impactPrefab").objectReferenceValue == null) errors.Add("impactPrefab 为空");
                    }

                    // 7) 视图根层（应在 FirstPersonView=9）
                    if (viewGo.layer != 9) errors.Add("视图根层=" + viewGo.layer + "≠9");
                }
                finally { PrefabUtility.UnloadPrefabContents(viewGo); }

                // 8) TP Muzzle 位置（根局部空间贴枪模几何：z=前端、y=主枪体 y 中点）
                if (!string.IsNullOrEmpty(job.tp))
                {
                    var tpGo = AssetDatabase.LoadAssetAtPath<GameObject>(ViewRoot + "/" + job.tp + ".prefab");
                    if (tpGo == null) errors.Add("TP prefab 缺失:" + job.tp);
                    else CheckTpMuzzle(tpGo, errors, metrics);
                }

                if (errors.Count == 0)
                {
                    pass++;
                    report.AppendLine("[PASS] " + job.view + " → " + string.Join(" ", metrics));
                }
                else
                {
                    fail++;
                    report.AppendLine("[FAIL] " + job.view + " → " + string.Join("; ", errors) + "  [" + string.Join(" ", metrics) + "]");
                }
            }

            report.AppendLine($"==== 结果: {pass}/{Jobs.Length} PASS, {fail} FAIL ====");
            Debug.Log(report.ToString());
            if (fail > 0) Debug.LogError("[WeaponMuzzleValidator] 存在失败项，详见上方报告。");
        }

        /// <summary>TP Muzzle 位置校验：主枪体（与根同名 mesh）y 中点 + 联合网格前端 maxZ。</summary>
        private static void CheckTpMuzzle(GameObject tpGo, List<string> errors, List<string> metrics)
        {
            Transform root = tpGo.transform;
            Transform muzzle = root.Find("Muzzle");
            if (muzzle == null) { errors.Add("TP 缺 Muzzle 节点"); return; }

            MeshFilter body = null;
            float zMax = float.MinValue;
            foreach (var mf in tpGo.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    float z = root.InverseTransformPoint(mf.transform.TransformPoint(v)).z;
                    if (z > zMax) zMax = z;
                }
                if (mf.gameObject.name == tpGo.name) body = mf;
            }
            if (body == null || body.sharedMesh == null) { errors.Add("TP 未找到主枪体 mesh"); return; }

            float yMin = float.MaxValue, yMax = float.MinValue;
            foreach (var v in body.sharedMesh.vertices)
            {
                float y = root.InverseTransformPoint(body.transform.TransformPoint(v)).y;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            var expected = new Vector3(0f, (yMin + yMax) * 0.5f, zMax);
            float dz = Mathf.Abs(muzzle.localPosition.z - zMax);
            float dy = Mathf.Abs(muzzle.localPosition.y - (yMin + yMax) * 0.5f);
            metrics.Add("TP dz=" + (dz * 1000f).ToString("F0") + "mm dy=" + (dy * 1000f).ToString("F0") + "mm");
            if (dz > MaxTpPositionErrorMeters || dy > MaxTpPositionErrorMeters)
                errors.Add($"TP Muzzle 位置偏差 dz={(dz * 1000f):F0}mm dy={(dy * 1000f):F0}mm（期望≈({expected.x:F3},{expected.y:F3},{zMax:F3})，现值 {muzzle.localPosition.ToString("F4")}）");
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
