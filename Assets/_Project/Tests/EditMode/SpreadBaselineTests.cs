using System.Reflection;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CP0 基线（Docs/13 检查点 3 交付）：现 WeaponController.ApplySpread 锥角统计断言——
    /// 锁定 CP4 动态散布改造前后的锥几何不变（方向分布、0 散布直通、边界不越界）。
    /// </summary>
    public sealed class SpreadBaselineTests
    {
        private static readonly MethodInfo ApplySpread = typeof(WeaponController).GetMethod(
            "ApplySpread", BindingFlags.NonPublic | BindingFlags.Static);

        static SpreadBaselineTests() => Assert.That(ApplySpread, Is.Not.Null, "ApplySpread 私有静态方法缺失");

        private static Vector3 Apply(Vector3 fwd, float spreadDeg)
            => (Vector3)ApplySpread.Invoke(null, new object[] { fwd, spreadDeg });

        [Test]
        public void ZeroSpread_ReturnsForwardExactly()
        {
            var fwd = Quaternion.Euler(10f, -25f, 0f) * Vector3.forward;
            var d = Apply(fwd, 0f);
            Assert.That(d, Is.EqualTo(fwd.normalized).Within(1e-5f));
        }

        [Test]
        public void Cone_AllWithinSpread_Angles_CoverCone()
        {
            var fwd = Vector3.forward;
            const float spread = 3f;
            const int n = 300;
            var rng = new System.Random(7);
            float maxAngle = 0f, sumAngle = 0f;
            int quarterCoverage = 0; // 四象限覆盖计数（防分布退化到单轴）
            var seen = new bool[4];
            for (int i = 0; i < n; i++)
            {
                // ApplySpread 内部用 UnityEngine.Random——统计断言不依赖注入
                var d = Apply(fwd, spread);
                float ang = Vector3.Angle(fwd, d);
                Assert.That(ang, Is.LessThanOrEqualTo(spread + 0.15f), "超出散布锥（数值容差）");
                maxAngle = Mathf.Max(maxAngle, ang);
                sumAngle += ang;
                int q = (d.x >= 0 ? 1 : 0) | (d.y >= 0 ? 2 : 0);
                if (!seen[q]) { seen[q] = true; quarterCoverage++; }
            }
            // 覆盖性：样本应触达锥边缘（均匀圆盘投影 → 平均角约 2/3·spread）
            Assert.That(maxAngle, Is.GreaterThan(spread * 0.9f), "未覆盖锥边缘（分布退化？）");
            Assert.That(sumAngle / n, Is.InRange(spread * 0.45f, spread * 0.85f), "平均角偏离均匀圆盘预期");
            Assert.That(quarterCoverage, Is.EqualTo(4), "四象限全覆盖失败");
        }

        [Test]
        public void Cone_SmallSpread_Normalized()
        {
            for (int i = 0; i < 50; i++)
            {
                var fwd = Random.onUnitSphere;
                var d = Apply(fwd, 0.5f);
                Assert.That(d.magnitude, Is.EqualTo(1f).Within(1e-4f), "输出未归一化");
                Assert.That(Vector3.Angle(fwd, d), Is.LessThanOrEqualTo(0.5f + 0.15f));
            }
        }
    }
}
