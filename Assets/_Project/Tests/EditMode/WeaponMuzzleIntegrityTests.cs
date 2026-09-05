using Game.Presentation.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 枪口挂点完整性回归（Docs/23 表现迭代 2026-09-05）：锁定"新增枪复制旧枪 Muzzle"必然失败——
    /// FP 侧以原厂 FPSController 的 Muzzleflash Particles（weapon 骨局部）为唯一基准；
    /// TP 侧以枪模几何（根局部：z=前端 maxZ、y=主枪体 y 中点）为基准。
    /// 与 Tools/Weapon Muzzle Validator（16 把全量）互补，此处锁新增 6 把的校准结果。
    /// </summary>
    public sealed class WeaponMuzzleIntegrityTests
    {
        private const string CtrlRoot = "Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Arms";
        private const string ViewRoot = "Assets/_Project/Prefabs/Weapons";
        private const float MaxPosErrorMeters = 0.01f;
        private const float MaxTpErrorMeters = 0.02f;

        private static readonly (string ctrl, string fp, string tp)[] NewJobs =
        {
            ("SMG_03_Example_Prefab/SMG_03_FPSController.prefab", "FP_SMG03_View", "TP_Weapon_SMG_03"),
            ("SMG_04_Example_Prefab/SMG_04_FPSController.prefab", "FP_SMG04_View", "TP_Weapon_SMG_04"),
            ("SMG_05_Example_Prefab/SMG_05_FPSController.prefab", "FP_SMG05_View", "TP_Weapon_SMG_05"),
            ("Handgun_03_Example_Prefab/Handgun_03_FPSController.prefab", "FP_Handgun03_View", "TP_Weapon_Handgun_03"),
            ("Handgun_04_Example_Prefab/Handgun_04_FPSController.prefab", "FP_Handgun04_View", "TP_Weapon_Handgun_04"),
            ("Sniper_03_Example_Prefab/Sniper_03_FPSController.prefab", "FP_Sniper03_View", "TP_Weapon_Sniper_03"),
        };

        /// <summary>同族旧枪的 Muzzle weaponLocal（照搬源）——新枪不得等于这些值（模型不同）。</summary>
        private static readonly Vector3 Smg01MuzzleLocal = new(-0.0004f, 0.2637f, 0.3655f);
        private static readonly Vector3 Handgun02MuzzleLocal = new(-0.0004f, 0.1435f, 0.1328f);
        private static readonly Vector3 Sniper01MuzzleLocal = new(0.0000f, 0.5976f, 0.8228f);

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

        private static Vector3 GetFactoryMuzzleLocal(string ctrlRel)
        {
            var ctrl = PrefabUtility.LoadPrefabContents(CtrlRoot + "/" + ctrlRel);
            try
            {
                var wb = FindDeep(ctrl.transform, "weapon");
                Transform particles = null;
                foreach (var t in ctrl.GetComponentsInChildren<Transform>(true))
                    if (t.name.Contains("Muzzleflash Particles")) { particles = t; break; }
                Assert.That(wb, Is.Not.Null, $"{ctrlRel} 缺 weapon 骨");
                Assert.That(particles, Is.Not.Null, $"{ctrlRel} 缺 Muzzleflash Particles");
                return wb.InverseTransformPoint(particles.position);
            }
            finally { PrefabUtility.UnloadPrefabContents(ctrl); }
        }

        [Test]
        public void NewWeapons_FpMuzzle_MatchesFactoryFactoryBaseline()
        {
            foreach (var j in NewJobs)
            {
                Vector3 expected = GetFactoryMuzzleLocal(j.ctrl);
                var view = AssetDatabase.LoadAssetAtPath<GameObject>(ViewRoot + "/" + j.fp + ".prefab");
                Assert.That(view, Is.Not.Null, $"{j.fp} 缺失");
                var wb = FindDeep(view.transform, "weapon");
                var wv = view.GetComponentInChildren<WeaponView>(true);
                Assert.That(wv, Is.Not.Null, $"{j.fp} 缺 WeaponView");
                var muzzle = wv.Muzzle;
                Assert.That(muzzle, Is.Not.Null, $"{j.fp} muzzle 引用为空");
                Assert.That(muzzle.IsChildOf(wb), Is.True, $"{j.fp} Muzzle 必须在 weapon 骨下（现 parent={muzzle.parent?.name}）");
                Assert.That(muzzle.gameObject.layer, Is.EqualTo(9), $"{j.fp} Muzzle 层应为 FirstPersonView(9)");

                Vector3 actual = wb.InverseTransformPoint(muzzle.position);
                float err = Vector3.Distance(actual, expected);
                Assert.That(err, Is.LessThanOrEqualTo(MaxPosErrorMeters),
                    $"{j.fp} Muzzle 位置误差 {(err * 1000f):F1}mm（期望原厂 {expected.ToString("F4")}，现 {actual.ToString("F4")}）");
            }
        }

        [Test]
        public void NewWeapons_FpMuzzle_NotCopiedFromFamilyPredecessor()
        {
            // 照搬检测：模型不同 ⇒ 原厂基准不同 ⇒ 与旧枪坐标重合即失败（本次事故的回归锁）
            foreach (var j in NewJobs)
            {
                Vector3 actual;
                var view = AssetDatabase.LoadAssetAtPath<GameObject>(ViewRoot + "/" + j.fp + ".prefab");
                var wb = FindDeep(view.transform, "weapon");
                var wv = view.GetComponentInChildren<WeaponView>(true);
                actual = wb.InverseTransformPoint(wv.Muzzle.position);

                Vector3 expected = GetFactoryMuzzleLocal(j.ctrl);
                // 若原厂基准与旧枪恰好重合则跳过（同布局资产）；否则断言现值跟随本枪基准而非旧枪
                if ((expected - Smg01MuzzleLocal).magnitude < MaxPosErrorMeters && j.fp.Contains("SMG")) continue;
                if ((expected - Handgun02MuzzleLocal).magnitude < MaxPosErrorMeters && j.fp.Contains("Handgun")) continue;
                if ((expected - Sniper01MuzzleLocal).magnitude < MaxPosErrorMeters && j.fp.Contains("Sniper")) continue;

                if (j.fp.Contains("SMG"))
                    Assert.That(actual, Is.Not.EqualTo(Smg01MuzzleLocal).Within(MaxPosErrorMeters), $"{j.fp} 仍在照搬 SMG01 枪口坐标");
                else if (j.fp.Contains("Handgun"))
                    Assert.That(actual, Is.Not.EqualTo(Handgun02MuzzleLocal).Within(MaxPosErrorMeters), $"{j.fp} 仍在照搬 Handgun02 枪口坐标");
                else if (j.fp.Contains("Sniper"))
                    Assert.That(actual, Is.Not.EqualTo(Sniper01MuzzleLocal).Within(MaxPosErrorMeters), $"{j.fp} 仍在照搬 Sniper01 枪口坐标");
            }
        }

        [Test]
        public void NewWeapons_FpReferences_Complete()
        {
            foreach (var j in NewJobs)
            {
                var view = AssetDatabase.LoadAssetAtPath<GameObject>(ViewRoot + "/" + j.fp + ".prefab");
                var wv = view.GetComponentInChildren<WeaponView>(true);
                var so = new SerializedObject(wv);
                var shell = so.FindProperty("shellPort").objectReferenceValue as Transform;
                var sight = so.FindProperty("sightReference").objectReferenceValue as Transform;
                Assert.That(shell, Is.Not.Null, $"{j.fp} shellPort 为空");
                // Unity 假 null 语义：比较必须走 == 运算符而非 NUnit EqualTo（Transform.Equals 不走重载）
                Assert.That(shell != wv.Muzzle, Is.True, $"{j.fp} shellPort 未与 Muzzle 分离");
                Assert.That(shell.name.StartsWith("thumb") || shell.name.Contains("bullet"), Is.False,
                    $"{j.fp} shellPort 绑在骨骼/子弹节点上（{shell.name}）——抛壳口必须位于枪身");
                Assert.That(sight, Is.Not.Null, $"{j.fp} sightReference 为空");
            }
        }

        [Test]
        public void NewWeapons_TpMuzzle_MatchesWeaponGeometry()
        {
            foreach (var j in NewJobs)
            {
                var tp = AssetDatabase.LoadAssetAtPath<GameObject>(ViewRoot + "/" + j.tp + ".prefab");
                Assert.That(tp, Is.Not.Null, $"{j.tp} 缺失");
                var root = tp.transform;
                var muzzle = root.Find("Muzzle");
                Assert.That(muzzle, Is.Not.Null, $"{j.tp} 缺 Muzzle 节点");

                float zMax = float.MinValue;
                MeshFilter body = null;
                foreach (var mf in tp.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        float z = root.InverseTransformPoint(mf.transform.TransformPoint(v)).z;
                        if (z > zMax) zMax = z;
                    }
                    if (mf.gameObject.name == tp.name) body = mf;
                }
                Assert.That(body, Is.Not.Null, $"{j.tp} 未找到主枪体 mesh");
                float yMin = float.MaxValue, yMax = float.MinValue;
                foreach (var v in body.sharedMesh.vertices)
                {
                    float y = root.InverseTransformPoint(body.transform.TransformPoint(v)).y;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                }

                float dz = Mathf.Abs(muzzle.localPosition.z - zMax);
                float dy = Mathf.Abs(muzzle.localPosition.y - (yMin + yMax) * 0.5f);
                Assert.That(dz, Is.LessThanOrEqualTo(MaxTpErrorMeters),
                    $"{j.tp} Muzzle z 偏差 {(dz * 1000f):F0}mm（枪口应贴网格前端 {zMax:F4}，现 {muzzle.localPosition.z:F4}）");
                Assert.That(dy, Is.LessThanOrEqualTo(MaxTpErrorMeters),
                    $"{j.tp} Muzzle y 偏差 {(dy * 1000f):F0}mm（枪轴应取枪管中线 {(yMin + yMax) * 0.5f:F4}，现 {muzzle.localPosition.y:F4}）");
            }
        }
    }
}
