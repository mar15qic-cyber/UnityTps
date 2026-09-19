using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-18 实机问题7（TP 手枪竖直朝天）回归锁：TP 武器挂载语义=枪身结构统一 +Z 枪口
    /// 局部约定，朝向完全由 prefab 根局部位姿决定（TPWeaponMeshSwapper 不覆盖根变换）。
    /// 手枪根位姿必须与步枪参考值（实机验证正确的 AssaultRifle_01）逐分量一致。
    /// </summary>
    public sealed class TPWeaponMountPoseTests
    {
        private static readonly Vector3 RefPos = new(0.0087f, 0.1524f, 0.1093f);
        private static readonly Quaternion RefRot = new(-0.0480332f, 0.5607293f, 0.8256168f, 0.0404029f);

        private static (Vector3 pos, Quaternion rot) ReadRootPose(string prefabName)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Prefabs/Weapons/{prefabName}.prefab");
            Assert.That(root, Is.Not.Null, $"{prefabName} prefab 必须存在");
            return (root.transform.localPosition, root.transform.localRotation);
        }

        /// <summary>四元数逐分量比较（q/-q 同旋转归一；不用 Quaternion.Angle——近同一四元数时
        /// acos 放大器会把 1e-4 级分量差放大到近 1°，本测试锁的是烘焙值而非旋转语义）。</summary>
        private static void AssertSameRotation(Quaternion actual, Quaternion expected, string label)
        {
            float sign = actual.x * expected.x + actual.y * expected.y
                       + actual.z * expected.z + actual.w * expected.w >= 0f ? 1f : -1f;
            Assert.That(Mathf.Abs(actual.x - sign * expected.x), Is.LessThan(1e-3f), $"{label}.x");
            Assert.That(Mathf.Abs(actual.y - sign * expected.y), Is.LessThan(1e-3f), $"{label}.y");
            Assert.That(Mathf.Abs(actual.z - sign * expected.z), Is.LessThan(1e-3f), $"{label}.z");
            Assert.That(Mathf.Abs(actual.w - sign * expected.w), Is.LessThan(1e-3f), $"{label}.w");
        }

        [Test]
        public void RifleReferencePose_IsStable()
        {
            var (pos, rot) = ReadRootPose("TP_Weapon_AssaultRifle_01");
            Assert.That(Vector3.Distance(pos, RefPos), Is.LessThan(1e-3f), "步枪参考位姿被改动——请先更新本测试的参考值语义");
            AssertSameRotation(rot, RefRot, "步枪参考旋转");
        }

        [TestCase("TP_Weapon_Handgun_01")]
        [TestCase("TP_Weapon_Handgun_02")]
        [TestCase("TP_Weapon_Handgun_03")]
        [TestCase("TP_Weapon_Handgun_04")]
        public void HandgunRootPose_MatchesRifleReference(string prefabName)
        {
            var (pos, rot) = ReadRootPose(prefabName);
            AssertSameRotation(rot, RefRot,
                $"{prefabName} 根旋转必须与步枪参考一致（实机问题7：手枪竖直朝天的根因是根旋转烘焙错误）");
            Assert.That(Vector3.Distance(pos, RefPos), Is.LessThan(1e-3f),
                $"{prefabName} 根位置必须与步枪参考一致（同一右手骨握持约定）");
        }

        /// <summary>结构前提：全 TP 武器 prefab 的 Muzzle 标记必须在枪身局部 +Z 半球
        /// （枪口局部约定成立，根位姿统一才有意义）。</summary>
        [TestCase("TP_Weapon_AssaultRifle_01")]
        [TestCase("TP_Weapon_Handgun_01")]
        [TestCase("TP_Weapon_Handgun_04")]
        public void MuzzleMarker_IsForwardHemisphere(string prefabName)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Prefabs/Weapons/{prefabName}.prefab");
            var muzzle = root.transform.Find("Muzzle");
            Assert.That(muzzle, Is.Not.Null, $"{prefabName} 缺 Muzzle 标记");
            Vector3 dir = root.transform.InverseTransformPoint(muzzle.position).normalized;
            Assert.That(dir.z, Is.GreaterThan(0.9f),
                $"{prefabName} 枪口必须指向枪身局部 +Z（统一约定，当前 dir={dir:F3}）");
        }
    }
}
