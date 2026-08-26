using System.Reflection;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CP0 基线（CP4 签名更新版）：ApplySpread(forward, spread, rng) 锥角统计——
    /// 锁定动态散布接线后的锥几何不变（0 散布直通、锥内+边缘覆盖、归一化）。
    /// </summary>
    public sealed class SpreadBaselineTests
    {
        private static readonly MethodInfo ApplySpread = typeof(WeaponController).GetMethod(
            "ApplySpread", BindingFlags.NonPublic | BindingFlags.Static);

        static SpreadBaselineTests() => Assert.That(ApplySpread, Is.Not.Null, "ApplySpread 私有静态方法缺失");

        private static Vector3 Apply(Vector3 fwd, float spreadDeg, System.Random rng)
            => (Vector3)ApplySpread.Invoke(null, new object[] { fwd, spreadDeg, rng });

        [Test]
        public void ZeroSpread_ReturnsForwardExactly()
        {
            var rng = new System.Random(1);
            var fwd = Quaternion.Euler(10f, -25f, 0f) * Vector3.forward;
            var d = Apply(fwd, 0f, rng);
            Assert.That(d, Is.EqualTo(fwd.normalized).Within(1e-5f));
        }

        [Test]
        public void Cone_AllWithinSpread_CoverEdge_AllQuadrants()
        {
            var rng = new System.Random(7);
            var fwd = Vector3.forward;
            const float spread = 3f;
            const int n = 300;
            float maxAngle = 0f, sumAngle = 0f;
            var seen = new bool[4];
            for (int i = 0; i < n; i++)
            {
                var d = Apply(fwd, spread, rng);
                float ang = Vector3.Angle(fwd, d);
                Assert.That(ang, Is.LessThanOrEqualTo(spread + 0.15f), "超出散布锥");
                maxAngle = Mathf.Max(maxAngle, ang);
                sumAngle += ang;
                int q = (d.x >= 0 ? 1 : 0) | (d.y >= 0 ? 2 : 0);
                seen[q] = true;
            }
            Assert.That(maxAngle, Is.GreaterThan(spread * 0.9f), "未覆盖锥边缘");
            Assert.That(sumAngle / n, Is.InRange(spread * 0.45f, spread * 0.85f), "平均角偏离均匀圆盘预期");
            Assert.That(System.Array.TrueForAll(seen, v => v), "四象限全覆盖");
        }

        [Test]
        public void SmallSpread_Normalized_AndSeededDeterministic()
        {
            var fwd = Vector3.up;
            var a = Apply(fwd, 0.5f, new System.Random(42));
            var b = Apply(fwd, 0.5f, new System.Random(42));
            Assert.That(a, Is.EqualTo(b).Within(1e-6f), "同种子应逐位一致（网络回放前提）");
            for (int i = 0; i < 50; i++)
            {
                var d = Apply(Random.onUnitSphere, 0.5f, new System.Random(i));
                Assert.That(d.magnitude, Is.EqualTo(1f).Within(1e-4f), "输出未归一化");
            }
        }
    }
}
