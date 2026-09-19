using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// ADS 审计 A3 修复的数据锁（2026-09-19）：跨相机投影收敛的数学性质。
    /// 旧曳光把 shot.FiredDirection 平移到 FP 枪口 → 与真实弹着点只是平行线（伤害射线与
    /// 曳光线不汇聚）；FP(45°) 与世界(60°) 相机 FOV/位姿不同，同一世界点两套投影屏幕不重合。
    /// 本文件锁死 CameraProjection 纯数学：(1) 投影/反投影往返；(2) 跨相机匹配的屏幕连续性——
    /// 匹配点在 overlay 相机下的视口位置 == 源点在 source 相机下的视口位置（收敛性）；
    /// (3) 背后点拒绝；(4) 深度钳制。WeaponView.SpawnTracer 的收敛行为以这些原语为基础。
    /// </summary>
    public sealed class TracerScreenMatchTests
    {
        private static CameraProjection MakeCamera(Vector3 position, Quaternion rotation, float fov, float aspect)
        {
            return new CameraProjection
            {
                Position = position,
                Rotation = rotation,
                FovDegrees = fov,
                Aspect = aspect,
                NearClip = 0.01f,
                FarClip = 300f
            };
        }

        // 模拟实机双相机拓扑：世界相机（FOV 60）与 FP overlay 相机（FOV 45、后移 0.299m、侧偏）
        private static (CameraProjection world, CameraProjection fp) MakeRigPair()
        {
            var world = MakeCamera(new Vector3(10f, 2f, -3f), Quaternion.Euler(5f, 33f, 0f), 60f, 1.7777f);
            var fp = MakeCamera(
                world.Position + world.Rotation * new Vector3(-0.047f, 0.082f, -0.299f),
                world.Rotation,
                45f, 1.7777f);
            return (world, fp);
        }

        [Test]
        public void ProjectViewportRoundtrip_RecoversWorldPoint()
        {
            var cam = MakeCamera(new Vector3(1f, 2f, 3f), Quaternion.Euler(-7f, 40f, 2f), 55f, 1.6f);
            Vector3 point = cam.Position + cam.Rotation * new Vector3(0.3f, -0.2f, 25f);

            Vector3 viewport = cam.ProjectToViewport(point);
            Assert.That(viewport.z, Is.GreaterThan(0f));
            Vector3 recovered = cam.ViewportPointAtDepth(new Vector2(viewport.x, viewport.y), viewport.z);
            Assert.That(Vector3.Distance(recovered, point), Is.LessThan(1e-3f),
                "投影→视口→同深度反投影必须精确还原世界点");
        }

        [Test]
        public void MatchAcrossCameras_ConvergesToSameViewport_CrossFov()
        {
            var (world, fp) = MakeRigPair();
            // 模拟不同距离的真实弹着点（沿世界相机前向远方 + 侧偏）
            Vector3[] targets =
            {
                world.Position + (world.Rotation * new Vector3(0.05f, 0.03f, 1f)) * 5f,
                world.Position + (world.Rotation * new Vector3(-0.08f, 0.02f, 1f)) * 25f,
                world.Position + (world.Rotation * new Vector3(0.0f, 0f, 1f)) * 120f
            };
            foreach (var target in targets)
            {
                bool matched = CameraProjection.TryMatchAcrossCameras(world, fp, target,
                    0.25f, fp.FarClip * 0.9f, out Vector3 overlayPoint);
                Assert.That(matched, Is.True, $"远距目标 {target} 必须可匹配");
                Vector3 sourceViewport = world.ProjectToViewport(target);
                Vector3 overlayViewport = fp.ProjectToViewport(overlayPoint);
                Assert.That(Mathf.Abs(overlayViewport.x - sourceViewport.x), Is.LessThan(1e-3f),
                    "匹配点在 FP 相机的视口 x 必须等于源点在世界相机的视口 x（A3 收敛核心断言）");
                Assert.That(Mathf.Abs(overlayViewport.y - sourceViewport.y), Is.LessThan(1e-3f),
                    "匹配点在 FP 相机的视口 y 必须等于源点在世界相机的视口 y");
                // 深度沿 overlay 前向、且不小于下限（近裁剪保护）
                float depth = Vector3.Dot(overlayPoint - fp.Position, fp.Forward);
                Assert.That(depth, Is.GreaterThanOrEqualTo(0.25f - 1e-4f));
            }
        }

        [Test]
        public void MatchAcrossCameras_RejectsPointBehindSourceCamera()
        {
            var (world, fp) = MakeRigPair();
            Vector3 behind = world.Position - (world.Rotation * Vector3.forward) * 10f;
            bool matched = CameraProjection.TryMatchAcrossCameras(world, fp, behind, 0.25f, 100f, out _);
            Assert.That(matched, Is.False, "相机背后的点必须拒绝（不能产生镜像/翻转曳光）");
        }

        [Test]
        public void MatchAcrossCameras_ClampsDepthIntoOverlayClipRange()
        {
            var (world, fp) = MakeRigPair();
            // 极近目标：匹配深度必须被钳到 minDepth，不允许落在 overlay 近裁剪之内
            Vector3 veryClose = world.Position + (world.Rotation * Vector3.forward) * 0.05f;
            bool matched = CameraProjection.TryMatchAcrossCameras(world, fp, veryClose, 0.25f, 100f, out Vector3 overlayPoint);
            Assert.That(matched, Is.True);
            float depth = Vector3.Dot(overlayPoint - fp.Position, fp.Forward);
            Assert.That(depth, Is.GreaterThanOrEqualTo(0.25f - 1e-4f), "深度必须被钳到 minDepth");
        }

        [Test]
        public void MuzzleStart_ProjectsInsideOverlayView_DepthConsistent()
        {
            // 起点连续性：曳光近段起点=冻结枪口（FP 层内直接渲染），其 FP 视口位置
            // 与枪口世界位的 FP 投影一致（同相机渲染，天然连续——旧实现把起点交给
            // 世界相机渲染才产生平行错位）。
            var (world, fp) = MakeRigPair();
            Vector3 muzzleWorld = fp.Position + fp.Rotation * new Vector3(0.02f, -0.03f, 0.4f);
            Vector3 fpViewport = fp.ProjectToViewport(muzzleWorld);
            Assert.That(fpViewport.z, Is.GreaterThan(0f));
            Assert.That(fpViewport.x, Is.InRange(0f, 1f));
            Assert.That(fpViewport.y, Is.InRange(0f, 1f));
            float depth = Vector3.Dot(muzzleWorld - fp.Position, fp.Forward);
            Assert.That(depth, Is.EqualTo(fpViewport.z).Within(1e-3f));
        }
    }
}
